using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using backend.Models;

namespace backend.Services
{
    public class FyersMcpService : IDisposable
    {
        private readonly ILogger<FyersMcpService> _logger;
        private Process? _process;
        private StreamWriter? _stdin;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pendingRequests = new();
        private long _requestId = 2;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SemaphoreSlim _queryLock = new(1, 1);
        private bool _isInitialized = false;
        private string? _lastLoginUrl = null;

        // ── Circuit breaker: skip FYERS calls for 60s after a connection failure ──
        private DateTime _circuitOpenUntil = DateTime.MinValue;
        private const int CircuitBreakSeconds = 60;

        private TaskCompletionSource? _proxyReadyTcs;

        public FyersMcpService(ILogger<FyersMcpService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Returns true if the circuit breaker is open (connection recently failed).
        /// Callers should check this before attempting any query.
        /// </summary>
        public bool IsCircuitOpen => DateTime.UtcNow < _circuitOpenUntil;

        private void TripCircuitBreaker()
        {
            _circuitOpenUntil = DateTime.UtcNow.AddSeconds(CircuitBreakSeconds);
            _logger.LogWarning(
                "FYERS circuit breaker tripped – skipping all FYERS calls for {Seconds}s",
                CircuitBreakSeconds);
        }

        private async Task EnsureConnectedAsync()
        {
            // Fast-fail if circuit is open
            if (IsCircuitOpen)
                throw new InvalidOperationException("FYERS circuit breaker is open");

            bool hasExited = true;
            if (_process != null)
            {
                try { hasExited = _process.HasExited; }
                catch (InvalidOperationException) { hasExited = true; }
            }

            if (_process != null && !hasExited && _isInitialized)
                return;

            await _lock.WaitAsync();
            try
            {
                // Re-check after acquiring lock
                if (IsCircuitOpen)
                    throw new InvalidOperationException("FYERS circuit breaker is open");

                hasExited = true;
                if (_process != null)
                {
                    try { hasExited = _process.HasExited; }
                    catch (InvalidOperationException) { hasExited = true; }
                }
                if (_process != null && !hasExited && _isInitialized)
                    return;

                _logger.LogInformation("Starting FYERS MCP subprocess connection...");
                CleanupProcess();

                _proxyReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                var startInfo = new ProcessStartInfo
                {
                    FileName = isWindows ? "cmd.exe" : "npx",
                    Arguments = isWindows ? "/c npx -y mcp-remote https://mcp.fyers.in/mcp" : "-y mcp-remote https://mcp.fyers.in/mcp",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _process = new Process { StartInfo = startInfo };
                _process.Start();

                _stdin = _process.StandardInput;

                _ = Task.Run(() => ReadStdoutAsync(_process.StandardOutput));
                _ = Task.Run(() => ReadStderrAsync(_process.StandardError));

                // Wait for the proxy to establish (15s timeout)
                using (var proxyCts = new CancellationTokenSource(15000))
                {
                    using (proxyCts.Token.Register(() => _proxyReadyTcs.TrySetCanceled()))
                    {
                        try
                        {
                            await _proxyReadyTcs.Task;
                            _logger.LogInformation("FYERS MCP proxy is ready. Sending initialize request...");
                        }
                        catch (TaskCanceledException)
                        {
                            _logger.LogWarning("Timeout waiting for FYERS MCP proxy to establish. Proceeding to initialize anyway...");
                        }
                    }
                }

                var initRequest = new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { },
                        clientInfo = new { name = "dotnet-mcp-client", version = "1.0" }
                    }
                };

                var initTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingRequests[1] = initTcs;

                await _stdin.WriteLineAsync(JsonSerializer.Serialize(initRequest));
                await _stdin.FlushAsync();

                // Wait for init response (30s timeout)
                using var cts = new CancellationTokenSource(30000);
                using (cts.Token.Register(() => initTcs.TrySetCanceled()))
                {
                    await initTcs.Task;
                }

                var initializedNotification = new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized"
                };

                await _stdin.WriteLineAsync(JsonSerializer.Serialize(initializedNotification));
                await _stdin.FlushAsync();

                _isInitialized = true;
                _logger.LogInformation("FYERS MCP connection initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect/initialize FYERS MCP server.");
                CleanupProcess();
                TripCircuitBreaker();
                throw;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task ReadStdoutAsync(StreamReader stdout)
        {
            try
            {
                string? line;
                while ((line = await stdout.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var node = JsonNode.Parse(line);
                        if (node != null && node["id"] != null)
                        {
                            long id = node["id"]!.GetValue<long>();
                            if (_pendingRequests.TryRemove(id, out var tcs))
                                tcs.TrySetResult(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse stdout line as JSON: {Line}", line);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading FYERS MCP stdout stream.");
            }
        }

        private async Task ReadStderrAsync(StreamReader stderr)
        {
            try
            {
                string? line;
                while ((line = await stderr.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    _logger.LogWarning("FYERS MCP Stderr: {Line}", line);

                    if (line.Contains("Proxy established successfully") || line.Contains("Local STDIO server running"))
                    {
                        _proxyReadyTcs?.TrySetResult();
                    }

                    if (line.Contains("SSE stream disconnected") || line.Contains("TypeError: terminated"))
                    {
                        _logger.LogError("FYERS MCP SSE stream disconnected! Resetting connection state to trigger auto-reconnection on next query.");
                        CleanupProcess();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading FYERS MCP stderr stream.");
            }
        }

        public async Task<FyersOptionsFlowData> QueryOptionsFlowAsync(string ticker)
        {
            // ── Circuit breaker fast-path ──────────────────────────────────────────
            if (IsCircuitOpen)
            {
                return new FyersOptionsFlowData
                {
                    NeedsLogin = _lastLoginUrl != null,
                    LoginUrl = _lastLoginUrl,
                    SqueezeStatus = "Fyers Unavailable"
                };
            }

            var candidateSymbols = new List<string>();
            string stdSymbol = ticker;
            if (stdSymbol.EndsWith(".NS"))
                stdSymbol = stdSymbol[..^3];
            
            candidateSymbols.Add("NSE:" + stdSymbol + "-EQ");
            
            // Known symbol mismatches/renames
            if (stdSymbol.Equals("LTF", StringComparison.OrdinalIgnoreCase))
            {
                candidateSymbols.Add("NSE:L&TFH-EQ");
                candidateSymbols.Add("NSE:L_TFH-EQ");
            }
            else if (stdSymbol.Equals("M&M", StringComparison.OrdinalIgnoreCase) || stdSymbol.Equals("M_M", StringComparison.OrdinalIgnoreCase))
            {
                candidateSymbols.Add("NSE:M&M-EQ");
                candidateSymbols.Add("NSE:M_M-EQ");
            }

            await _queryLock.WaitAsync();
            try
            {
                await EnsureConnectedAsync();

                bool hadAuthError = false;

                foreach (var fyersSymbol in candidateSymbols)
                {
                    _logger.LogInformation("Querying option chain with candidate: {FyersSymbol}", fyersSymbol);
                    
                    long currentCallId = Interlocked.Increment(ref _requestId);
                    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingRequests[currentCallId] = tcs;

                    var toolCall = new
                    {
                        jsonrpc = "2.0",
                        id = currentCallId,
                        method = "tools/call",
                        @params = new
                        {
                            name = "get_option_chain",
                            arguments = new { symbol = fyersSymbol, strikecount = 5 }
                        }
                    };

                    await _stdin!.WriteLineAsync(JsonSerializer.Serialize(toolCall));
                    await _stdin.FlushAsync();

                    string responseJson;
                    using (var cts = new CancellationTokenSource(8000))
                    using (cts.Token.Register(() => tcs.TrySetCanceled()))
                    {
                        responseJson = await tcs.Task;
                    }

                    _logger.LogDebug("FYERS MCP Response JSON: {Response}", responseJson);

                    var responseNode = JsonNode.Parse(responseJson);
                    var errorNode = responseNode?["error"];
                    var result = responseNode?["result"];
                    var isResultError = result?["isError"]?.GetValue<bool>() == true;
                    var textContent = result?["content"]?[0]?["text"]?.GetValue<string>();

                    bool isAuthError = false;

                    if (errorNode != null)
                    {
                        var errorMsg = errorNode["message"]?.GetValue<string>() ?? "";
                        _logger.LogWarning("FYERS MCP error returned for {Symbol}: {Error}", fyersSymbol, errorMsg);
                        
                        if (errorMsg.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                            errorMsg.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                            errorMsg.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                            errorMsg.Contains("redis", StringComparison.OrdinalIgnoreCase))
                        {
                            isAuthError = true;
                        }
                    }

                    if (textContent != null && (
                        textContent.Contains("failed to get token from Redis") ||
                        textContent.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                        textContent.Contains("login required", StringComparison.OrdinalIgnoreCase)))
                    {
                        isAuthError = true;
                    }

                    if (isAuthError)
                    {
                        hadAuthError = true;
                        break; // Stop checking candidates, we need authentication
                    }

                    // If query succeeded and returned option data
                    if (errorNode == null && !isResultError && textContent != null)
                    {
                        try
                        {
                            var dataNode = JsonNode.Parse(textContent);
                            var optionsList = dataNode?["data"]?["optionsChain"] as JsonArray;
                            if (optionsList != null && optionsList.Count > 0)
                            {
                                _logger.LogInformation("Successfully retrieved option data for candidate {FyersSymbol}", fyersSymbol);
                                return CalculateOptionsMetrics(optionsList, ticker);
                            }
                            else
                            {
                                _logger.LogWarning("FYERS MCP returned empty options list for {Symbol}. Content: {Content}", fyersSymbol, textContent);
                            }
                        }
                        catch (Exception parseEx)
                        {
                            _logger.LogWarning(parseEx, "Failed to parse option data content for candidate {Symbol}", fyersSymbol);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("FYERS MCP query failed for {Symbol}. isResultError={IsResultError}, errorNode={ErrorNode}, textContent={TextContent}", 
                            fyersSymbol, isResultError, errorNode?.ToJsonString(), textContent);
                    }
                }

                if (hadAuthError)
                {
                    var loginUrl = await TriggerLoginFlowAsyncInternal();
                    return new FyersOptionsFlowData
                    {
                        NeedsLogin = true,
                        LoginUrl = loginUrl,
                        SqueezeStatus = "Neutral"
                    };
                }

                _logger.LogWarning("All option chain candidates failed for ticker {Ticker} without auth errors.", ticker);
                return new FyersOptionsFlowData { SqueezeStatus = "No Options Data" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query options flow for {Ticker}", ticker);
                return new FyersOptionsFlowData
                {
                    NeedsLogin = _lastLoginUrl != null,
                    LoginUrl = _lastLoginUrl,
                    SqueezeStatus = "Fyers Connection Error"
                };
            }
            finally
            {
                _queryLock.Release();
            }
        }

        public async Task<string?> TriggerLoginFlowAsync()
        {
            await _queryLock.WaitAsync();
            try
            {
                return await TriggerLoginFlowAsyncInternal();
            }
            finally
            {
                _queryLock.Release();
            }
        }

        private async Task<string?> TriggerLoginFlowAsyncInternal()
        {
            // Reset circuit breaker – user explicitly requested a login attempt
            _circuitOpenUntil = DateTime.MinValue;
            _logger.LogInformation("FYERS login requested – resetting circuit breaker and connecting...");

            try
            {
                await EnsureConnectedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to FYERS MCP for login flow");
                return null;
            }

            long currentCallId = Interlocked.Increment(ref _requestId);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[currentCallId] = tcs;

            var toolCall = new
            {
                jsonrpc = "2.0",
                id = currentCallId,
                method = "tools/call",
                @params = new { name = "login", arguments = new { } }
            };

            await _stdin!.WriteLineAsync(JsonSerializer.Serialize(toolCall));
            await _stdin.FlushAsync();

            string responseJson;
            using (var cts = new CancellationTokenSource(15000)) // 15s for login flow
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                responseJson = await tcs.Task;
            }

            var responseNode = JsonNode.Parse(responseJson);
            var textContent = responseNode?["result"]?["content"]?[0]?["text"]?.GetValue<string>();

            if (textContent != null && textContent.Contains("Please visit this URL to login: "))
            {
                var url = textContent.Replace("Please visit this URL to login: ", "").Trim();
                _lastLoginUrl = url;
                return url;
            }

            return _lastLoginUrl;
        }

        private FyersOptionsFlowData CalculateOptionsMetrics(JsonArray optionsList, string ticker)
        {
            double totalPutOi = 0, totalCallOi = 0;
            double totalCallPremium = 0, totalPutPremium = 0;
            int callCount = 0, putCount = 0;

            foreach (var opt in optionsList)
            {
                if (opt == null) continue;
                string? optType = opt["option_type"]?.GetValue<string>();
                double ltp = opt["ltp"]?.GetValue<double>() ?? 0;
                double oi  = opt["oi"]?.GetValue<double>()  ?? 0;

                if (optType == "PE")      { totalPutOi  += oi; totalPutPremium  += ltp; putCount++;  }
                else if (optType == "CE") { totalCallOi += oi; totalCallPremium += ltp; callCount++; }
            }

            double pcr = totalCallOi > 0 ? totalPutOi / totalCallOi : 0;
            double avgPutPrem  = putCount  > 0 ? totalPutPremium  / putCount  : 0;
            double avgCallPrem = callCount > 0 ? totalCallPremium / callCount : 0;
            double skew = avgCallPrem > 0 ? avgPutPrem / avgCallPrem : 1.0;

            string squeezeStatus = "Neutral";
            if      (pcr > 1.3 && skew > 1.2) squeezeStatus = "Bearish Hedge";
            else if (pcr > 1.3 && skew < 0.9) squeezeStatus = "Squeeze Potential";
            else if (pcr < 0.7 && skew < 0.8) squeezeStatus = "Bullish Accumulation";
            else if (pcr < 0.7 && skew > 1.2) squeezeStatus = "Call Unwinding";

            return new FyersOptionsFlowData
            {
                Pcr = Math.Round(pcr, 2),
                Skew = Math.Round(skew, 2),
                SqueezeStatus = squeezeStatus,
                NeedsLogin = false
            };
        }

        private void CleanupProcess()
        {
            try
            {
                _isInitialized = false;
                if (_stdin != null) { _stdin.Dispose(); _stdin = null; }
                if (_process != null)
                {
                    if (!_process.HasExited) _process.Kill();
                    _process.Dispose();
                    _process = null;
                }
            }
            catch { /* Ignore cleanup errors */ }
        }

        public void Dispose()
        {
            CleanupProcess();
            _lock.Dispose();
            _queryLock.Dispose();
        }
    }
}
