using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using backend.Data;
using backend.Models;
using backend.Services;

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
        private readonly AppDbContext _context;
        private readonly IndicatorService _indicatorService;

        public ScanController(
            ScannerService scannerService,
            AppDbContext context,
            IndicatorService indicatorService)
        {
            _scannerService = scannerService;
            _context = context;
            _indicatorService = indicatorService;
        }

        [HttpGet("scan")]
        public async Task<ActionResult<ScanResponse>> Scan()
        {
            try
            {
                var scanResult = await _scannerService.ExecuteScanAsync();
                return Ok(scanResult);
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
                var bars = await _context.DailyBars
                    .Where(b => b.Ticker == ticker)
                    .OrderBy(b => b.Date)
                    .ToListAsync();

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
                
                double fib618 = _indicatorService.CalculateFib618(closes, highs, lows, out _, out _);

                var response = new HistoricalChartResponse
                {
                    Ticker = ticker
                };

                // Limit return to last 150 candles for the charts
                int countToTake = Math.Min(bars.Count, 150);
                int startIndex = bars.Count - countToTake;

                for (int i = startIndex; i < bars.Count; i++)
                {
                    response.Candles.Add(new ChartCandle
                    {
                        Date = bars[i].Date.ToString("yyyy-MM-dd"),
                        Open = Math.Round(bars[i].Open, 2),
                        High = Math.Round(bars[i].High, 2),
                        Low = Math.Round(bars[i].Low, 2),
                        Close = Math.Round(bars[i].Close, 2),
                        Volume = bars[i].Volume,
                        Ema8 = i < ema8.Length ? Math.Round(ema8[i], 2) : null,
                        Ema21 = i < ema21.Length ? Math.Round(ema21[i], 2) : null,
                        Ema200 = i < ema200.Length ? Math.Round(ema200[i], 2) : null,
                        Jnsar = i < jnsar.Length ? Math.Round(jnsar[i], 2) : null,
                        Fib618 = Math.Round(fib618, 2)
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
            var list = await _context.WatchlistItems.OrderByDescending(w => w.AddedAt).ToListAsync();
            return Ok(list);
        }

        [HttpPost("watchlist")]
        public async Task<IActionResult> AddToWatchlist([FromBody] WatchlistItem item)
        {
            if (string.IsNullOrEmpty(item.Ticker)) return BadRequest("Ticker is required.");

            var exists = await _context.WatchlistItems.AnyAsync(w => w.Ticker == item.Ticker);
            if (exists) return Conflict("Ticker is already in watchlist.");

            _context.WatchlistItems.Add(item);
            await _context.SaveChangesAsync();
            return Ok(new { message = "Added to watchlist successfully." });
        }

        [HttpDelete("watchlist/{ticker}")]
        public async Task<IActionResult> RemoveFromWatchlist(string ticker)
        {
            var item = await _context.WatchlistItems.FirstOrDefaultAsync(w => w.Ticker == ticker);
            if (item == null) return NotFound();

            _context.WatchlistItems.Remove(item);
            await _context.SaveChangesAsync();
            return Ok(new { message = "Removed from watchlist successfully." });
        }

        [HttpGet("diagnostics")]
        public async Task<IActionResult> GetDiagnostics()
        {
            var dailyCounts = await _context.DailyBars.GroupBy(b => b.Ticker).Select(g => new { Ticker = g.Key, Count = g.Count() }).ToListAsync();
            var weeklyCounts = await _context.WeeklyBars.GroupBy(b => b.Ticker).Select(g => new { Ticker = g.Key, Count = g.Count() }).ToListAsync();
            var metaCount = await _context.StockMetadatas.CountAsync();
            
            return Ok(new {
                metaCount,
                dailyCounts,
                weeklyCounts
            });
        }
    }
}
