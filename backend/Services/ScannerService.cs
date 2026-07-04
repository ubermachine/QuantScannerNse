using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DuckDB.NET.Data;
using Dapper;
using backend.Models;
using Microsoft.AspNetCore.SignalR;
using backend.Strategies;

namespace backend.Services
{
    public class ScannerService
    {
        private readonly string _connectionString = "Data Source=quantscanner.duckdb";
        private readonly YahooFinanceService _yahooFinanceService;
        private readonly IndicatorService _indicatorService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly FyersMcpService _fyersMcpService;
        private readonly IEnumerable<IStrategy> _strategies;

        private static Dictionary<string, StockSeriesData>? _cachedDailyGroups;
        private static Dictionary<string, WeeklySeriesData>? _cachedWeeklyGroups;
        private static DateTime _cacheTime = DateTime.MinValue;
        private static readonly object _cacheLock = new object();

        public ScannerService(
            YahooFinanceService yahooFinanceService,
            IndicatorService indicatorService,
            IServiceScopeFactory scopeFactory,
            FyersMcpService fyersMcpService,
            IEnumerable<IStrategy> strategies)
        {
            _yahooFinanceService = yahooFinanceService;
            _indicatorService = indicatorService;
            _scopeFactory = scopeFactory;
            _fyersMcpService = fyersMcpService;
            _strategies = strategies;
        }

        public async Task<ScanResponse> ExecuteScanAsync(string selectedStrategy = null)
        {
            var response = new ScanResponse();

            // 1. Check Market Regime
            var (indexPrice, indexEma200) = await _yahooFinanceService.FetchIndexDataAsync();
            response.IndexClose = Math.Round(indexPrice, 2);
            response.IndexEma200 = Math.Round(indexEma200, 2);
            response.MarketRegime = indexPrice >= indexEma200 ? "BULLISH" : "BEARISH";

            // Get Nifty 50 returns for relative strength calculations
            double indexReturn3M = 0;
            double indexReturn6M = 0;
            using var earlyConn = new DuckDBConnection(_connectionString);
            earlyConn.Open();
            var indexBars = earlyConn.Query<DailyBar>("SELECT * FROM DailyBars WHERE Ticker = '^NSEI' ORDER BY Date").ToList();

            if (indexBars.Count > 120)
            {
                var indexCloses = indexBars.Select(b => b.Close).ToArray();
                indexReturn3M = _indicatorService.CalculateReturn(indexCloses, 60); // ~60 trading days = 3M
                indexReturn6M = _indicatorService.CalculateReturn(indexCloses, 120); // ~120 trading days = 6M
            }
            else
            {
                // Fallbacks if ^NSEI is not fully synced in DB
                indexReturn3M = 3.0; // historical average proxy
                indexReturn6M = 6.0;
            }

            // 2. Fetch all tickers in our database
            var stocks = earlyConn.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();

            Dictionary<string, StockSeriesData> dailyGroups;
            Dictionary<string, WeeklySeriesData> weeklyGroups;

            lock (_cacheLock)
            {
                dailyGroups = _cachedDailyGroups!;
                weeklyGroups = _cachedWeeklyGroups!;
            }

            if (dailyGroups == null || weeklyGroups == null)
            {
                var newDailyGroups = new Dictionary<string, StockSeriesData>();
                var newWeeklyGroups = new Dictionary<string, WeeklySeriesData>();

                var connection = new DuckDBConnection(_connectionString);
                connection.Open();

                try
                {
                    // Fetch Daily Bars raw
                    var rawDailyData = connection.Query<DailyBar>("SELECT Ticker, High, Low, Close, Volume, Date FROM DailyBars");
                    var rawDaily = new Dictionary<string, List<DailyBar>>();
                    foreach (var row in rawDailyData)
                    {
                        if (string.IsNullOrEmpty(row.Ticker)) continue;
                        if (!rawDaily.TryGetValue(row.Ticker, out var list))
                        {
                            list = new List<DailyBar>();
                            rawDaily[row.Ticker] = list;
                        }
                        list.Add(row);
                    }

                    // Sort in memory (takes < 1ms in CPU)
                    foreach (var kvp in rawDaily)
                    {
                        var sorted = kvp.Value.OrderBy(c => c.Date).ToList();
                        var data = new StockSeriesData();
                        foreach (var c in sorted)
                        {
                            data.Highs.Add(c.High);
                            data.Lows.Add(c.Low);
                            data.Closes.Add(c.Close);
                            data.Volumes.Add(c.Volume);
                            data.Dates.Add(c.Date);
                        }
                        newDailyGroups[kvp.Key] = data;
                    }

                    // Fetch Weekly Bars raw
                    var rawWeeklyData = connection.Query<WeeklyBar>("SELECT Ticker, Close, Date FROM WeeklyBars");
                    var rawWeekly = new Dictionary<string, List<WeeklyBar>>();
                    
                    foreach (var row in rawWeeklyData)
                    {
                        if (string.IsNullOrEmpty(row.Ticker)) continue;
                        if (!rawWeekly.TryGetValue(row.Ticker, out var list))
                        {
                            list = new List<WeeklyBar>();
                            rawWeekly[row.Ticker] = list;
                        }
                        list.Add(row);
                    }

                    foreach (var kvp in rawWeekly)
                    {
                        var sorted = kvp.Value.OrderBy(c => c.Date).ToList();
                        var data = new WeeklySeriesData();
                        foreach (var c in sorted)
                        {
                            data.Closes.Add(c.Close);
                        }
                        newWeeklyGroups[kvp.Key] = data;
                    }
                }
                finally
                {
                    connection.Dispose();
                }

                lock (_cacheLock)
                {
                    _cachedDailyGroups = newDailyGroups;
                    _cachedWeeklyGroups = newWeeklyGroups;
                    _cacheTime = DateTime.UtcNow;
                }

                dailyGroups = newDailyGroups;
                weeklyGroups = newWeeklyGroups;
            }

            var resultsBag = new ConcurrentBag<StockScanResult>();

            // Pre-calculate 3M Returns for RS Percentile Ranking
            var stockReturns = new Dictionary<string, double>();
            foreach (var stock in stocks)
            {
                if (dailyGroups.TryGetValue(stock.Ticker, out var dData) && dData.Closes.Count >= 60)
                {
                    stockReturns[stock.Ticker] = _indicatorService.CalculateReturn(dData.Closes.ToArray(), 60);
                }
            }
            var orderedReturns = stockReturns.Values.OrderBy(v => v).ToList();
            int totalReturns = orderedReturns.Count;

            // 3. Scan stocks in parallel using memory dictionary lookups (takes < 20ms total across cores!)
            Parallel.ForEach(stocks, stock =>
            {
                try
                {
                    if (!dailyGroups.TryGetValue(stock.Ticker, out var dailyData) ||
                        !weeklyGroups.TryGetValue(stock.Ticker, out var weeklyData))
                    {
                        return; // Equivalent to continue inside Parallel.ForEach
                    }

                    if (dailyData.Closes.Count < 200 || weeklyData.Closes.Count < 10) return;

                    var dailyCloses = dailyData.Closes.ToArray();
                    var dailyHighs = dailyData.Highs.ToArray();
                    var dailyLows = dailyData.Lows.ToArray();
                    var dailyVolumes = dailyData.Volumes.ToArray();
                    var dailyDates = dailyData.Dates.ToArray();
                    
                    var weeklyCloses = weeklyData.Closes.ToArray();

                    double currentPrice = dailyCloses[^1];

                    // --- TECHNICAL CALCULATIONS ---
                    var ema50 = _indicatorService.CalculateEma(dailyCloses, 50);
                    var ema200 = _indicatorService.CalculateEma(dailyCloses, 200);
                    var ema8 = _indicatorService.CalculateEma(dailyCloses, 8);
                    var ema10 = _indicatorService.CalculateEma(dailyCloses, 10);
                    var ema21 = _indicatorService.CalculateEma(dailyCloses, 21);
                    var jnsar = _indicatorService.CalculateJnsar(dailyCloses, dailyHighs, dailyLows);
                    
                    // Weekly EMAs for LRHR
                    var ema144w = _indicatorService.CalculateEma(weeklyCloses, 144);
                    var ema233w = _indicatorService.CalculateEma(weeklyCloses, 233);
                    var (wMacdLine, wMacdSignal) = _indicatorService.CalculateMacd(weeklyCloses);
                    var dailyMacd = _indicatorService.CalculateMacd(dailyCloses);
                    var macdLine = dailyMacd.MacdLine;
                    var macdSignal = dailyMacd.SignalLine;

                    // ATR Volatility Contraction
                    var atr14 = _indicatorService.CalculateAtr(dailyHighs, dailyLows, dailyCloses, 14);
                    var atr14Avg60 = atr14.Length > 60 ? atr14.TakeLast(60).Average() : atr14[^1];
                    bool isAtrCoiled = atr14[^1] < atr14Avg60;

                    // Quant-Grade Indicators
                    var rsi = _indicatorService.CalculateRsi(dailyCloses);
                    var adx = _indicatorService.CalculateAdx(dailyHighs, dailyLows, dailyCloses);
                    var (upperB, middleB, lowerB) = _indicatorService.CalculateBollingerBands(dailyCloses);
                    var (upperK, middleK, lowerK) = _indicatorService.CalculateKeltnerChannels(dailyHighs, dailyLows, dailyCloses);
                    double zScore = _indicatorService.CalculateZScoreLast(dailyCloses);
                    
                    bool isSqueezeFiring = upperB[^1] < upperK[^1] && lowerB[^1] > lowerK[^1];

                    // Prop Desk / Institutional Indicators
                    double poc = _indicatorService.CalculatePointOfControl(dailyCloses, dailyVolumes);
                    double ytdVwap = _indicatorService.CalculateYtdVwap(dailyCloses, dailyHighs, dailyLows, dailyVolumes, dailyDates);
                    double chandelierExit = _indicatorService.CalculateChandelierExit(dailyHighs, dailyLows, dailyCloses);
                    
                    var obv = _indicatorService.CalculateObv(dailyCloses, dailyVolumes);
                    var cmf = _indicatorService.CalculateCmf(dailyCloses, dailyHighs, dailyLows, dailyVolumes);
                    var volPctRank = _indicatorService.CalculateVolatilityPercentileRank(atr14);
                    var rsSharpe = _indicatorService.CalculateRollingSharpe(dailyCloses);

                    double rsRank = 0;
                    if (stockReturns.TryGetValue(stock.Ticker, out var ret))
                    {
                        int index = orderedReturns.BinarySearch(ret);
                        if (index < 0) index = ~index;
                        rsRank = totalReturns > 1 ? (index / (double)(totalReturns - 1)) * 100.0 : 50.0;
                    }

                    // 52-Week High Proximity
                    double max52WHigh = dailyHighs.TakeLast(250).Max();
                    double discount52W = (max52WHigh - currentPrice) / max52WHigh;

                    // Dynamic Fib Rebounds
                    double fib618 = _indicatorService.CalculateFib618(dailyCloses, dailyHighs, dailyLows, out double lastSwingHigh, out double lastSwingLow);

                    // Volatility targets
                    var (target1, target2, stopLoss) = _indicatorService.CalculateVolatilityFibTargets(dailyCloses);

                    // Adjust stop loss to protect JNSAR if Close > JNSAR
                    if (currentPrice > jnsar[^1] && stopLoss < jnsar[^1])
                    {
                        stopLoss = jnsar[^1]; // Trail close to JNSAR
                    }
                    
                    // The Chandelier Exit is now our primary trailing stop for prop desk logic.
                    // If it's tighter than the default stop loss, we use it to limit risk.
                    if (currentPrice > chandelierExit && chandelierExit > stopLoss)
                    {
                        stopLoss = chandelierExit;
                    }

                    // --- SCORECARD SCORING ENGINE (Prop-Desk Grade, Tightened) ---

                    // 1. TREND QUALITY (Max 20) — requires strong multi-timeframe alignment
                    int trendScore = 0;
                    if (currentPrice > ema50[^1]) trendScore += 5;
                    if (ema50[^1] > ema200[^1]) trendScore += 5;
                    if (ema50[^1] > ema50[ema50.Length - 5]) trendScore += 5; // 50 EMA actively rising
                    if (adx[^1] > 25) trendScore += 5; // ADX trend strength confirmation

                    // 2. RELATIVE STRENGTH (Max 20) — must outperform the index
                    int rsScore = 0;
                    double stockReturn3M = _indicatorService.CalculateReturn(dailyCloses, 60);
                    double stockReturn6M = _indicatorService.CalculateReturn(dailyCloses, 120);
                    if (stockReturn3M > indexReturn3M) rsScore += 10;
                    if (stockReturn6M > indexReturn6M) rsScore += 10;

                    // 3. 52-WEEK PROXIMITY (Max 10) — tightened. Reward only stocks within 5% of 52W high.
                    int proximityScore = 0;
                    if (discount52W <= 0.05) proximityScore = 10;        // Tight VCP — within 5% of high
                    else if (discount52W <= 0.10) proximityScore = 6;    // Acceptable — within 10%
                    // Stocks >10% below 52W high get 0 proximity score (they have overhead resistance)

                    // 4. VOLUME ACCUMULATION (Max 10)
                    int volScore = _indicatorService.CalculateVolumeScore(dailyCloses, dailyVolumes, 15);
                    int volumeAccumulationScore = volScore;

                    // 5. VOLATILITY SETUP (Max 10) — coiling and squeeze firing
                    int volatilitySetupScore = isAtrCoiled ? 5 : 0;
                    if (isSqueezeFiring) volatilitySetupScore += 5;

                    // 6. MOMENTUM GATE (Max 10) — RSI between 50-70 is the golden zone: not exhausted, not dead
                    int fundamentalsScore = 0; // Repurposed to Momentum Quality score
                    double latestRsi = rsi[^1];
                    if (latestRsi >= 50 && latestRsi <= 70) fundamentalsScore += 10; // Golden zone
                    else if (latestRsi > 40 && latestRsi < 50) fundamentalsScore += 5;  // Recovering
                    // RSI < 40 = downtrend (0), RSI > 70 = overbought (0)

                    // 7. INSTITUTIONAL FOOTPRINT (Max 10)
                    // Up-day avg volume must be materially higher than down-day avg volume (1.5x threshold)
                    int instScore = 0;
                    double avgUpVol = 0, avgDownVol = 0;
                    int upCount = 0, downCount = 0;
                    int lookbackWindow = Math.Min(20, dailyCloses.Length - 1);
                    for (int i = dailyCloses.Length - lookbackWindow; i < dailyCloses.Length; i++)
                    {
                        if (dailyCloses[i] > dailyCloses[i - 1]) { avgUpVol += dailyVolumes[i]; upCount++; }
                        else { avgDownVol += dailyVolumes[i]; downCount++; }
                    }
                    if (upCount > 0) avgUpVol /= upCount;
                    if (downCount > 0) avgDownVol /= downCount;
                    if (avgUpVol > avgDownVol * 1.5) instScore = 10; // Must be 1.5x — not just marginally higher
                    else if (avgUpVol > avgDownVol) instScore = 5;
                    int institutionalFootprintScore = instScore;

                    // Aggregate score (Max = 80)
                    // NOTE: proximityScore intentionally excluded — it creates a structural bias toward stocks near
                    // their 52W highs, preventing LRHR deep-value setups from competing on score.
                    // Proximity is enforced directly inside the HCT/LRHR strategy conditions instead.
                    int totalScore = trendScore + rsScore + volumeAccumulationScore + volatilitySetupScore + fundamentalsScore + institutionalFootprintScore;

                    double epsGrowth = 15.0; // kept for DTO compatibility
                    double debtToEquity = 0.5; // kept for DTO compatibility

                    var strategyContext = new StrategyContext
                    {
                        CurrentPrice = currentPrice,
                        YtdVwap = ytdVwap,
                        Poc = poc,
                        Obv = obv,
                        Cmf = cmf,
                        VolPctRank = volPctRank,
                        RsRank = rsRank,
                        RsSharpe = rsSharpe,
                        ZScore = zScore,
                        ChandelierExit = chandelierExit,
                        Discount52W = discount52W,
                        
                        // New variables passed in
                        Ema8 = ema8[^1],
                        Ema10 = ema10[^1],
                        Ema21 = ema21[^1],
                        Ema50 = ema50[^1],
                        Ema200 = ema200[^1],
                        Rsi14 = rsi[^1],
                        MacdLine = macdLine[^1],
                        MacdSignal = macdSignal[^1],
                        Jnsar = jnsar[^1]
                    };

                    bool isHct = false;
                    bool isLrhr = false;
                    string strategyName = "None";
                    string logic = "";
                    var matchedStrategies = new List<string>();

                    foreach (var strategy in _strategies)
                    {
                        if (!string.IsNullOrEmpty(selectedStrategy) && selectedStrategy != "All" &&
                            !strategy.Name.Contains(selectedStrategy, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (strategy.Evaluate(strategyContext))
                        {
                            matchedStrategies.Add(strategy.Name);
                            logic += strategy.GetLogic(strategyContext) + " ";
                            
                            if (strategy.Name.Contains("HCT")) isHct = true;
                            if (strategy.Name.Contains("LRHR")) isLrhr = true;
                        }
                    }

                    if (matchedStrategies.Count == 0) strategyName = "None";
                    else if (matchedStrategies.Count == 1) strategyName = matchedStrategies[0];
                    else strategyName = string.Join(" + ", matchedStrategies);
                    
                    // Prop-desk conviction thresholds (tightened from 75/50 to 65/45)
                    string conviction = totalScore >= 65 ? "High" : (totalScore >= 45 ? "Medium" : "Low");
                    
                    // Override conviction with quant gates
                    if (volPctRank > 80) conviction = "Low";  // Volatility expansion / panic
                    else if (rsRank < 20) conviction = "Low"; // Extreme laggard
                    else if (isSqueezeFiring && totalScore >= 55 && rsSharpe > 1.0) conviction = "High"; // Coiled breakout with good risk-adjusted returns
                    else if (currentPrice > poc && currentPrice > ytdVwap && totalScore >= 55 && cmf[^1] > 0.1) conviction = "High"; // Strong institutional alignment

                    if (isSqueezeFiring) logic += "Bollinger bands contracted inside Keltner Channels (Squeeze Firing). ";
                    if (cmf[^1] > 0.1) logic += $"Strong Institutional Accumulation (CMF = {Math.Round(cmf[^1], 2)}). ";
                    if (currentPrice > poc) logic += "Trading above the 150-day Volume Point of Control (HVN support). ";
                    if (currentPrice > ytdVwap) logic += "Trending above Institutional YTD VWAP. ";
                    if (zScore > 2.5) logic += "WARNING: Statistically overextended (Z-Score > 2.5). ";
                    
                    if (string.IsNullOrEmpty(logic)) logic = "No distinct structural edge.";

                    double SafeRound(double value, int decimals)
                    {
                        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
                        return Math.Round(value, decimals);
                    }

                    resultsBag.Add(new StockScanResult
                    {
                        Ticker = stock.Ticker,
                        Name = stock.Name,
                        Sector = stock.Sector,
                        Price = SafeRound(currentPrice, 2),
                        Score = totalScore,
                        IsHctMatch = isHct,
                        IsLrhrMatch = isLrhr,
                        Strategy = strategyName,
                        Conviction = conviction,
                        Logic = logic.Trim(),
                        Jnsar = SafeRound(jnsar[^1], 2),
                        DistanceToJnsar = SafeRound(((currentPrice - jnsar[^1]) / (jnsar[^1] == 0 ? 1 : jnsar[^1])) * 100.0, 2),
                        Ema200 = SafeRound(ema200[^1], 2),
                        Ema50 = SafeRound(ema50[^1], 2),
                        Fib618 = SafeRound(fib618, 2),
                        Atr14 = SafeRound(atr14[^1], 2),
                        IsVolatilityCoiled = isAtrCoiled,
                        IsSqueezeFiring = isSqueezeFiring,
                        Rsi14 = SafeRound(rsi[^1], 2),
                        Adx14 = SafeRound(adx[^1], 2),
                        ZScore = SafeRound(zScore, 2),
                        ProximityTo52WHigh = SafeRound(discount52W * 100.0, 2),
                        VolumeScore = volumeAccumulationScore,
                        PointOfControl = SafeRound(poc, 2),
                        YtdVwap = SafeRound(ytdVwap, 2),
                        ChandelierExit = SafeRound(chandelierExit, 2),
                        Obv = SafeRound(obv.Length > 0 ? obv[^1] : 0, 0),
                        Cmf = SafeRound(cmf.Length > 0 ? cmf[^1] : 0, 2),
                        VolatilityPctRank = SafeRound(volPctRank, 1),
                        RsSharpe = SafeRound(rsSharpe, 2),
                        RsPercentileRank = SafeRound(rsRank, 1),
                        EpsGrowthYoY = epsGrowth,
                        DebtToEquity = debtToEquity,
                        StopLoss = SafeRound(stopLoss, 2),
                        Target1 = SafeRound(target1, 2),
                        Target2 = SafeRound(target2, 2),
                        
                        // Score breakdown
                        TrendScore = trendScore,
                        RelativeStrengthScore = rsScore,
                        ProximityScore = proximityScore,
                        VolumeAccumulationScore = volumeAccumulationScore,
                        VolatilitySetupScore = volatilitySetupScore,
                        FundamentalsScore = fundamentalsScore,
                        InstitutionalFootprintScore = institutionalFootprintScore
                    });
                }
                catch (Exception)
                {
                    // Fail silently for corrupted tickers
                }
            });

            var results = resultsBag.OrderByDescending(r => r.Score).ToList();

            // NOTE: Fyers Options Flow is NOT fetched here during scan.
            // Per platform rules, it is fetched on-demand ONLY when the user
            // explicitly clicks "Populate Options Data" on a specific symbol.
            // Fetching eagerly here blocks the scan for 8s per matched stock.

            response.Results = results;
            return response;
        }

        public async Task SyncHistoricalDataAsync(Func<string, int, Task> onProgress)
        {
            ClearCache(); // Invalidate cache at the start of sync
            // First sync Index Data (^NSEI)
            var (indexBars, _) = await _yahooFinanceService.FetchHistoricalDataAsync("^NSEI");
            if (indexBars.Count > 0)
            {
                await SaveDailyBarsAsync("^NSEI", indexBars);
            }

            using var syncConn = new DuckDBConnection(_connectionString);
            syncConn.Open();
            var stocks = syncConn.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();
            int total = stocks.Count;
            int counter = 0;

            using var sharedContext = new DuckDBConnection(_connectionString);
            sharedContext.Open();

            // Use SemaphoreSlim to run up to 50 concurrent download tasks from Yahoo Finance
            var semaphore = new SemaphoreSlim(50);
            var dbSemaphore = new SemaphoreSlim(1);
            var tasks = stocks.Select(async stock =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // 1. Download Price Bars (daily and weekly)
                    var (daily, weekly) = await _yahooFinanceService.FetchHistoricalDataAsync(stock.Ticker);

                    // 2. Commit to database sequentially using the shared connection to prevent locking overhead
                    await dbSemaphore.WaitAsync();
                    try
                    {
                        if (daily.Count > 0)
                        {
                            await SaveDailyBarsInternalAsync(sharedContext, stock.Ticker, daily);
                        }
                        if (weekly.Count > 0)
                        {
                            await SaveWeeklyBarsInternalAsync(sharedContext, stock.Ticker, weekly);
                        }
                    }
                    finally
                    {
                        dbSemaphore.Release();
                    }

                    int currentCount = Interlocked.Increment(ref counter);
                    int progress = (int)((double)currentCount / total * 100.0);
                    await onProgress(stock.Ticker, progress);

                    // Very slight throttle per task to keep connection pool healthy
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error syncing {stock.Ticker}: {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            ClearCache(); // Invalidate cache at the end of sync to reload fresh data on next scan
        }

        public static void ClearCache()
        {
            lock (_cacheLock)
            {
                _cachedDailyGroups = null;
                _cachedWeeklyGroups = null;
            }
        }

        private async Task SaveDailyBarsInternalAsync(DuckDBConnection context, string ticker, List<DailyBar> freshBars)
        {
            var existingDates = context.Query<DateTime>("SELECT Date FROM DailyBars WHERE Ticker = $Ticker", new { Ticker = ticker }).ToHashSet();

            var newBars = freshBars.Where(b => !existingDates.Contains(b.Date)).ToList();
            if (newBars.Count > 0)
            {
                using var appender = context.CreateAppender("DailyBars");
                foreach (var b in newBars)
                {
                    var row = appender.CreateRow();
                    row.AppendValue(b.Ticker);
                    row.AppendValue(b.Date);
                    row.AppendValue(b.Open);
                    row.AppendValue(b.High);
                    row.AppendValue(b.Low);
                    row.AppendValue(b.Close);
                    row.AppendValue(b.Volume);
                    row.EndRow();
                }
                appender.Close();
            }
        }

        private async Task SaveWeeklyBarsInternalAsync(DuckDBConnection context, string ticker, List<WeeklyBar> freshBars)
        {
            var existingDates = context.Query<DateTime>("SELECT Date FROM WeeklyBars WHERE Ticker = $Ticker", new { Ticker = ticker }).ToHashSet();

            var newBars = freshBars.Where(b => !existingDates.Contains(b.Date)).ToList();
            if (newBars.Count > 0)
            {
                using var appender = context.CreateAppender("WeeklyBars");
                foreach (var b in newBars)
                {
                    var row = appender.CreateRow();
                    row.AppendValue(b.Ticker);
                    row.AppendValue(b.Date);
                    row.AppendValue(b.Open);
                    row.AppendValue(b.High);
                    row.AppendValue(b.Low);
                    row.AppendValue(b.Close);
                    row.AppendValue(b.Volume);
                    row.EndRow();
                }
                appender.Close();
            }
        }

        private async Task SaveDailyBarsAsync(string ticker, List<DailyBar> freshBars)
        {
            using var conn = new DuckDBConnection(_connectionString); conn.Open();
            await SaveDailyBarsInternalAsync(conn, ticker, freshBars);
        }

        private async Task SaveWeeklyBarsAsync(string ticker, List<WeeklyBar> freshBars)
        {
            using var conn = new DuckDBConnection(_connectionString); conn.Open();
            await SaveWeeklyBarsInternalAsync(conn, ticker, freshBars);
        }
    }

    public class StockSeriesData
    {
        public List<double> Opens { get; } = new();
        public List<double> Closes { get; } = new();
        public List<double> Highs { get; } = new();
        public List<double> Lows { get; } = new();
        public List<double> Volumes { get; } = new();
        public List<DateTime> Dates { get; } = new();
    }

    public class WeeklySeriesData
    {
        public List<double> Closes { get; } = new();
    }

    public class RawCandle
    {
        public DateTime Date { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public double Volume { get; set; }
    }

    public class RawWeeklyCandle
    {
        public DateTime Date { get; set; }
        public double Close { get; set; }
    }
}
