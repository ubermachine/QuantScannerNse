using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using DuckDB.NET.Data;
using Dapper;
using backend.Models;
using backend.Services;
using backend.Strategies;

namespace backend.Controllers
{
    public static class SyncStatus
    {
        public static bool IsRunning { get; set; } = false;
        public static string CurrentTicker { get; set; } = string.Empty;
        public static int ProgressPercent { get; set; } = 0;
        public static string LastSyncMessage { get; set; } = "Not started.";
    }

    [ApiController]
    [Route("api")]
    public class ScanController : ControllerBase
    {
        private readonly ScannerService _scannerService;
        private readonly string _conn = "Data Source=quantscanner.duckdb";
        private readonly IndicatorService _indicatorService;
        private readonly FyersMcpService _fyersMcpService;
        private readonly IEnumerable<IStrategy> _strategies;

        public ScanController(
            ScannerService scannerService,
            IndicatorService indicatorService,
            FyersMcpService fyersMcpService,
            IEnumerable<IStrategy> strategies)
        {
            _scannerService = scannerService;
            _indicatorService = indicatorService;
            _fyersMcpService = fyersMcpService;
            _strategies = strategies;
        }

        [HttpGet("scan")]
        public async Task<ActionResult<ScanResponse>> Scan([FromQuery] string strategyName = null)
        {
            try
            {
                var scanResult = await _scannerService.ExecuteScanAsync(strategyName);
                return Ok(scanResult);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("scanner/strategies")]
        public ActionResult<IEnumerable<string>> GetStrategies()
        {
            return Ok(_strategies.Select(s => s.Name).ToList());
        }

        [HttpGet("fyers/login")]
        public async Task<IActionResult> GetFyersLoginUrl()
        {
            try
            {
                var loginUrl = await _fyersMcpService.TriggerLoginFlowAsync();
                return Ok(new { loginUrl });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("fyers/status")]
        public async Task<IActionResult> GetFyersStatus()
        {
            try
            {
                var flow = await _fyersMcpService.QueryOptionsFlowAsync("RELIANCE.NS");
                return Ok(new { needsLogin = flow.NeedsLogin });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message, needsLogin = true });
            }
        }

        [HttpGet("fyers/options/{ticker}")]
        public async Task<IActionResult> GetFyersOptions(string ticker)
        {
            try
            {
                var optionsFlow = await _fyersMcpService.QueryOptionsFlowAsync(ticker);
                return Ok(optionsFlow);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("sync")]
        public IActionResult StartSync([FromServices] IServiceScopeFactory scopeFactory)
        {
            if (SyncStatus.IsRunning)
            {
                return BadRequest(new { message = "Sync is already in progress." });
            }

            SyncStatus.IsRunning = true;
            SyncStatus.ProgressPercent = 0;
            SyncStatus.CurrentTicker = "Initializing...";
            SyncStatus.LastSyncMessage = "Sync started.";

            // Run sync in the background with a dedicated DI scope
            _ = Task.Run(async () =>
            {
                try
                {
                    using (var scope = scopeFactory.CreateScope())
                    {
                        var scannerService = scope.ServiceProvider.GetRequiredService<ScannerService>();
                        await scannerService.SyncHistoricalDataAsync((ticker, progress) =>
                        {
                            SyncStatus.CurrentTicker = ticker;
                            SyncStatus.ProgressPercent = progress;
                            return Task.CompletedTask;
                        });
                    }
                    SyncStatus.IsRunning = false;
                    SyncStatus.LastSyncMessage = $"Sync completed at {DateTime.Now:HH:mm:ss}";
                    SyncStatus.ProgressPercent = 100;
                }
                catch (Exception ex)
                {
                    SyncStatus.IsRunning = false;
                    SyncStatus.LastSyncMessage = $"Sync failed: {ex.Message}";
                }
            });

            return Ok(new { message = "Sync process kicked off in background." });
        }

        [HttpGet("sync/status")]
        public IActionResult GetSyncStatus()
        {
            return Ok(new
            {
                isRunning = SyncStatus.IsRunning,
                currentTicker = SyncStatus.CurrentTicker,
                progressPercent = SyncStatus.ProgressPercent,
                message = SyncStatus.LastSyncMessage
            });
        }

        [HttpGet("chart/{ticker}")]
        public async Task<ActionResult<HistoricalChartResponse>> GetChartData(string ticker)
        {
            try
            {
                using var conn = new DuckDBConnection(_conn); conn.Open(); var bars = conn.Query<DailyBar>("SELECT * FROM DailyBars WHERE Ticker = $Ticker ORDER BY Date", new { Ticker = ticker }).ToList();

                if (bars.Count == 0)
                {
                    return NotFound(new { message = $"No chart data found for ticker {ticker}" });
                }

                // Calculate indicators for overlays
                var closes = bars.Select(b => b.Close).ToArray();
                var highs = bars.Select(b => b.High).ToArray();
                var lows = bars.Select(b => b.Low).ToArray();

                var ema8 = _indicatorService.CalculateEma(closes, 8);
                var ema21 = _indicatorService.CalculateEma(closes, 21);
                var ema200 = _indicatorService.CalculateEma(closes, 200);
                var jnsar = _indicatorService.CalculateJnsar(closes, highs, lows);
                var (macdLine, macdSignal) = _indicatorService.CalculateMacd(closes);
                
                double fib618 = _indicatorService.CalculateFib618(closes, highs, lows, out _, out _);

                var response = new HistoricalChartResponse
                {
                    Ticker = ticker
                };

                // Limit return to last 150 candles for the charts
                int countToTake = Math.Min(bars.Count, 150);
                int startIndex = bars.Count - countToTake;

                double SafeRound(double value, int decimals)
                {
                    if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
                    return Math.Round(value, decimals);
                }

                for (int i = startIndex; i < bars.Count; i++)
                {
                    response.Candles.Add(new ChartCandle
                    {
                        Date = bars[i].Date.ToString("yyyy-MM-dd"),
                        Open = SafeRound(bars[i].Open, 2),
                        High = SafeRound(bars[i].High, 2),
                        Low = SafeRound(bars[i].Low, 2),
                        Close = SafeRound(bars[i].Close, 2),
                        Volume = bars[i].Volume,
                        Ema8 = i < ema8.Length ? SafeRound(ema8[i], 2) : null,
                        Ema21 = i < ema21.Length ? SafeRound(ema21[i], 2) : null,
                        Ema200 = i < ema200.Length ? SafeRound(ema200[i], 2) : null,
                        Jnsar = i < jnsar.Length ? SafeRound(jnsar[i], 2) : null,
                        Fib618 = SafeRound(fib618, 2),
                        MacdLine = i < macdLine.Length ? SafeRound(macdLine[i], 2) : null,
                        MacdSignal = i < macdSignal.Length ? SafeRound(macdSignal[i], 2) : null,
                        MacdHistogram = (i < macdLine.Length && i < macdSignal.Length) ? SafeRound(macdLine[i] - macdSignal[i], 2) : null
                    });
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("watchlist")]
        public async Task<ActionResult<IEnumerable<WatchlistItem>>> GetWatchlist()
        {
            using var conn = new DuckDBConnection(_conn);
            conn.Open();
            var list = conn.Query<WatchlistItem>("SELECT * FROM WatchlistItems").ToList();
            return Ok(list);
        }

        [HttpPost("watchlist")]
        public async Task<IActionResult> AddToWatchlist([FromBody] WatchlistItem item)
        {
            if (string.IsNullOrEmpty(item.Ticker)) return BadRequest("Ticker is required.");
            using var conn = new DuckDBConnection(_conn);
            conn.Open();
            conn.Execute("INSERT INTO WatchlistItems (Ticker, EntryPrice) VALUES ($Ticker, $EntryPrice) ON CONFLICT (Ticker) DO UPDATE SET EntryPrice = excluded.EntryPrice", new { Ticker = item.Ticker, EntryPrice = item.EntryPrice });
            return Ok(new { message = "Added to watchlist successfully." });
        }

        [HttpDelete("watchlist/{ticker}")]
        public async Task<IActionResult> RemoveFromWatchlist(string ticker)
        {
            using var conn = new DuckDBConnection(_conn);
            conn.Open();
            conn.Execute("DELETE FROM WatchlistItems WHERE Ticker = $Ticker", new { Ticker = ticker });
            return Ok(new { message = "Removed from watchlist successfully." });
        }

        [HttpGet("diagnostics")]
        public async Task<IActionResult> GetDiagnostics()
        {
            using var conn = new DuckDBConnection(_conn); conn.Open(); var dailyCounts = conn.Query("SELECT Ticker, COUNT(*) as Count FROM DailyBars GROUP BY Ticker").ToList();
            var weeklyCounts = conn.Query("SELECT Ticker, COUNT(*) as Count FROM WeeklyBars GROUP BY Ticker").ToList();
            var metaCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM StockMetadatas");
            
            return Ok(new {
                metaCount,
                dailyCounts,
                weeklyCounts
            });
        }
    }
}
