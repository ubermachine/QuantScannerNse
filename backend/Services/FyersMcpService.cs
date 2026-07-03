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
        private Process _process;
        private StreamWriter _stdin;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pendingRequests = new();
        private long _requestId = 2; // Start from 3 as 1 is initialize, 2 is for any early checks
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _isInitialized = false;
        private string _lastLoginUrl = null;

        public FyersMcpService(ILogger<FyersMcpService> logger)
        {
            _logger = logger;
        }

        private async Task EnsureConnectedAsync()
        {
            if (_process != null && !_process.HasExited && _isInitialized)
            {
                return;
            }

            await _lock.WaitAsync();
            try
            {
                if (_process != null && !_process.HasExited && _isInitialized)
                {
                    return;
                }

                _logger.LogInformation("Starting FYERS MCP subprocess connection...");
                CleanupProcess();

                var startInfo = new ProcessStartInfo
                {
                    FileName = "npx.cmd",
                    Arguments = "mcp-remote https://mcp.fyers.in/mcp",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _process = new Process { StartInfo = startInfo };
                _process.Start();

                _stdin = _process.StandardInput;

                // Start reader thread for stdout
                _ = Task.Run(() => ReadStdoutAsync(_process.StandardOutput));
                _ = Task.Run(() => ReadStderrAsync(_process.StandardError));

                // Send initialize handshake
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

                // Wait for initialize response (timeout after 5 seconds)
                using var cts = new CancellationTokenSource(5000);
                using (cts.Token.Register(() => initTcs.TrySetCanceled()))
                {
                    await initTcs.Task;
                }

                // Send initialized notification
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
                while (!stdout.EndOfStream)
                {
                    var line = await stdout.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Try to parse as JSON-RPC response
                    try
                    {
                        var node = JsonNode.Parse(line);
                        if (node != null && node["id"] != null)
                        {
                            long id = node["id"].GetValue<long>();
                            if (_pendingRequests.TryRemove(id, out var tcs))
                            {
                                tcs.TrySetResult(line);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to parse stdout line as JSON: {Line}", line);
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
                while (!stderr.EndOfStream)
                {
                    var line = await stderr.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _logger.LogDebug("FYERS MCP Stderr: {Line}", line);
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
            // Map Yahoo ticker (e.g. BIOCON.NS) to FYERS format (e.g. NSE:BIOCON-EQ)
            string fyersSymbol = ticker;
            if (fyersSymbol.EndsWith(".NS"))
            {
                fyersSymbol = "NSE:" + fyersSymbol.Substring(0, fyersSymbol.Length - 3) + "-EQ";
            }

            try
            {
                await EnsureConnectedAsync();

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
                        arguments = new
                        {
                            symbol = fyersSymbol,
                            strikecount = 5
                        }
                    }
                };

                await _stdin.WriteLineAsync(JsonSerializer.Serialize(toolCall));
                await _stdin.FlushAsync();

                // Wait for response (timeout 5s)
                string responseJson;
                using (var cts = new CancellationTokenSource(5000))
                using (cts.Token.Register(() => tcs.TrySetCanceled()))
                {
                    responseJson = await tcs.Task;
                }

                var responseNode = JsonNode.Parse(responseJson);
                var result = responseNode?["result"];
                var isError = responseNode?["error"] != null || (result?["isError"] != null && result["isError"].GetValue<bool>());
                var textContent = result?["content"]?[0]?["text"]?.GetValue<string>();

                if (isError || (textContent != null && textContent.Contains("failed to get token from Redis")))
                {
                    // Not logged in! Let's get the login URL.
                    var loginUrl = await TriggerLoginFlowAsync();
                    return new FyersOptionsFlowData
                    {
                        NeedsLogin = true,
                        LoginUrl = loginUrl,
                        SqueezeStatus = "Neutral"
                    };
                }

                // We have option chain data! Parse and calculate
                if (textContent != null)
                {
                    var dataNode = JsonNode.Parse(textContent);
                    var optionsList = dataNode?["data"]?.AsArray();
                    if (optionsList != null && optionsList.Count > 0)
                    {
                        return CalculateOptionsMetrics(optionsList, ticker);
                    }
                }

                return new FyersOptionsFlowData { SqueezeStatus = "No Options Data" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query options flow for {Ticker}", ticker);
                // Return fallback data instead of crashing the scan
                return new FyersOptionsFlowData
                {
                    NeedsLogin = _lastLoginUrl != null,
                    LoginUrl = _lastLoginUrl,
                    SqueezeStatus = "Fyers Connection Error"
                };
            }
        }

        public async Task<string> TriggerLoginFlowAsync()
        {
            // Call the login tool to generate a new authentication link
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
                    name = "login",
                    arguments = new { }
                }
            };

            await _stdin.WriteLineAsync(JsonSerializer.Serialize(toolCall));
            await _stdin.FlushAsync();

            string responseJson;
            using (var cts = new CancellationTokenSource(5000))
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
            double totalPutOi = 0;
            double totalCallOi = 0;

            double totalCallPremium = 0;
            double totalPutPremium = 0;
            int callCount = 0;
            int putCount = 0;

            foreach (var opt in optionsList)
            {
                if (opt == null) continue;

                string optType = opt["option_type"]?.GetValue<string>();
                double strike = opt["strike_price"]?.GetValue<double>() ?? 0;
                double ltp = opt["ltp"]?.GetValue<double>() ?? 0;
                double oi = opt["oi"]?.GetValue<double>() ?? 0;

                if (optType == "PE")
                {
                    totalPutOi += oi;
                    totalPutPremium += ltp;
                    putCount++;
                }
                else if (optType == "CE")
                {
                    totalCallOi += oi;
                    totalCallPremium += ltp;
                    callCount++;
                }
            }

            // Put-Call Ratio
            double pcr = totalCallOi > 0 ? totalPutOi / totalCallOi : 0;

            // Premium Skew (Put vs Call pricing skew)
            double avgPutPremium = putCount > 0 ? totalPutPremium / putCount : 0;
            double avgCallPremium = callCount > 0 ? totalCallPremium / callCount : 0;
            double skew = avgCallPremium > 0 ? avgPutPremium / avgCallPremium : 1.0;

            // Short Squeeze / Sentiment Proxy
            // Squeeze status can be rated based on PCR and Option price skew
            string squeezeStatus = "Neutral";
            if (pcr > 1.3 && skew > 1.2)
            {
                // Puts are highly active and expensive (defensive/bearish hedge)
                squeezeStatus = "Bearish Hedge";
            }
            else if (pcr > 1.3 && skew < 0.9)
            {
                // High Put OI but cheap Put premiums (bearish crowding, potential squeeze fuel)
                squeezeStatus = "Squeeze Potential";
            }
            else if (pcr < 0.7 && skew < 0.8)
            {
                // Heavy Calls active and Call premiums expensive (bullish build-up)
                squeezeStatus = "Bullish Accumulation";
            }
            else if (pcr < 0.7 && skew > 1.2)
            {
                // Heavy Calls active but cheap premiums (potential bullish trap)
                squeezeStatus = "Call Unwinding";
            }

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
                if (_stdin != null)
                {
                    _stdin.Dispose();
                    _stdin = null;
                }
                if (_process != null)
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                    _process.Dispose();
                    _process = null;
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        public void Dispose()
        {
            CleanupProcess();
            _lock.Dispose();
        }
    }
}
