using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using backend.Models;
using DuckDB.NET.Data;
using Dapper;
using backend.Strategies;

namespace backend.Services
{
    public class BacktestService
    {
        private readonly string _connectionString = "Data Source=quantscanner.duckdb";
        private readonly IndicatorService _indicatorService;
        private readonly IEnumerable<IStrategy> _strategies;

        public BacktestService(IndicatorService indicatorService, IEnumerable<IStrategy> strategies)
        {
            _indicatorService = indicatorService;
            _strategies = strategies;
        }

        public async Task<BacktestResult> RunBacktestAsync(string ticker, double stopLossPct, double targetPct, bool useDynamicExits = false)
        {
            var result = new BacktestResult { Ticker = ticker };
            
            List<DailyBar> dailyBars;
            using (var conn = new DuckDBConnection(_connectionString))
            {
                conn.Open();
                dailyBars = conn.Query<DailyBar>("SELECT * FROM DailyBars WHERE Ticker = $Ticker ORDER BY Date", new { Ticker = ticker }).ToList();
            }

            if (dailyBars.Count < 200) return result;

            var opens = dailyBars.Select(b => b.Open).ToArray();
            var closes = dailyBars.Select(b => b.Close).ToArray();
            var highs = dailyBars.Select(b => b.High).ToArray();
            var lows = dailyBars.Select(b => b.Low).ToArray();
            var volumes = dailyBars.Select(b => b.Volume).Select(v => (double)v).ToArray();
            var dates = dailyBars.Select(b => b.Date).ToArray();

            var obv = _indicatorService.CalculateObv(closes, volumes);
            var cmf = _indicatorService.CalculateCmf(closes, highs, lows, volumes);
            var atr14 = _indicatorService.CalculateAtr(highs, lows, closes, 14);

            double peak = closes[0];
            double maxDrawdown = 0;

            BacktestTrade? openTrade = null;
            bool triggerEntryOnNextBar = false;
            string pendingStrategy = "";

            for (int i = 200; i < closes.Length; i++)
            {
                double currentPrice = closes[i];
                if (currentPrice > peak) peak = currentPrice;
                double drawdown = (peak - currentPrice) / peak;
                if (drawdown > maxDrawdown) maxDrawdown = drawdown;

                // Pre-calculate indicators for exits using optimized overloads
                double poc = _indicatorService.CalculatePointOfControlAt(closes, volumes, i);
                double chandelierExit = _indicatorService.CalculateChandelierExitAt(highs, lows, atr14, i);

                if (openTrade != null)
                {
                    bool shouldExit = false;
                    double exitPrice = currentPrice;
                    if (useDynamicExits)
                    {
                        if (openTrade.Strategy == "HCT")
                            shouldExit = currentPrice < chandelierExit;
                        else
                            shouldExit = currentPrice < poc;
                        exitPrice = currentPrice;
                    }
                    else
                    {
                        double slLevel = openTrade.EntryPrice * (1 - stopLossPct);
                        double tpLevel = openTrade.EntryPrice * (1 + targetPct);
                        
                        bool hitSl = lows[i] <= slLevel;
                        bool hitTp = highs[i] >= tpLevel;
                        
                        if (hitSl && hitTp)
                        {
                            shouldExit = true;
                            exitPrice = Math.Min(opens[i], slLevel);
                        }
                        else if (hitSl)
                        {
                            shouldExit = true;
                            exitPrice = Math.Min(opens[i], slLevel);
                        }
                        else if (hitTp)
                        {
                            shouldExit = true;
                            exitPrice = Math.Max(opens[i], tpLevel);
                        }
                    }

                    if (shouldExit)
                    {
                        openTrade.ExitDate = dates[i];
                        openTrade.ExitPrice = exitPrice;
                        openTrade.ProfitPercentage = (exitPrice - openTrade.EntryPrice) / openTrade.EntryPrice * 100.0;
                        openTrade.IsWin = openTrade.ProfitPercentage > 0;
                        openTrade.DaysHeld = (int)(openTrade.ExitDate - openTrade.EntryDate).TotalDays;
                        result.Trades.Add(openTrade);
                        openTrade = null;
                    }
                }
                else
                {
                    if (triggerEntryOnNextBar)
                    {
                        openTrade = new BacktestTrade
                        {
                            Ticker = ticker,
                            Strategy = pendingStrategy,
                            EntryDate = dates[i],
                            EntryPrice = opens[i]
                        };
                        triggerEntryOnNextBar = false;
                        pendingStrategy = "";
                    }

                    var sliceCloses = closes.Take(i + 1).ToArray();
                    var sliceHighs = highs.Take(i + 1).ToArray();
                    var sliceLows = lows.Take(i + 1).ToArray();
                    var sliceVolumes = volumes.Take(i + 1).ToArray();
                    var sliceDates = dates.Take(i + 1).ToArray();
                    
                    double ytdVwap = _indicatorService.CalculateYtdVwap(sliceCloses, sliceHighs, sliceLows, sliceVolumes, sliceDates);
                    double zScore = _indicatorService.CalculateZScoreLast(sliceCloses);
                    double volPctRank = _indicatorService.CalculateVolatilityPercentileRank(atr14.Take(i + 1).ToArray());
                    
                    double max52WHigh = sliceHighs.TakeLast(250).Max();
                    double discount52W = (max52WHigh - currentPrice) / max52WHigh;

                    bool isHct = 
                        (currentPrice >= ytdVwap * 0.97 && currentPrice <= ytdVwap * 1.05 ||
                         currentPrice >= poc * 0.97 && currentPrice <= poc * 1.05) &&
                        obv[i] > obv[i - 20] &&
                        cmf[i] > 0 &&
                        volPctRank < 30 &&
                        zScore >= -1.0 && zScore <= 0.5 &&
                        currentPrice > chandelierExit;

                    bool isLrhr = 
                        zScore < -1.0 &&
                        discount52W >= 0.25 &&
                        cmf[i] > 0 && cmf[i - 5] <= 0 &&
                        obv[i] > obv[i - 10] &&
                        volPctRank < 50 &&
                        currentPrice > poc;

                    if (isHct || isLrhr)
                    {
                        triggerEntryOnNextBar = true;
                        pendingStrategy = isHct ? "HCT" : "LRHR";
                    }
                }
            }

            if (openTrade != null)
            {
                openTrade.ExitDate = dates[^1];
                openTrade.ExitPrice = closes[^1];
                openTrade.ProfitPercentage = (closes[^1] - openTrade.EntryPrice) / openTrade.EntryPrice * 100.0;
                openTrade.IsWin = openTrade.ProfitPercentage > 0;
                openTrade.DaysHeld = (int)(openTrade.ExitDate - openTrade.EntryDate).TotalDays;
                result.Trades.Add(openTrade);
            }

            result.TotalTrades = result.Trades.Count;
            result.WinningTrades = result.Trades.Count(t => t.IsWin);
            result.LosingTrades = result.TotalTrades - result.WinningTrades;
            result.WinRate = result.TotalTrades > 0 ? (double)result.WinningTrades / result.TotalTrades * 100.0 : 0;
            
            var wins = result.Trades.Where(t => t.IsWin).ToList();
            var losses = result.Trades.Where(t => !t.IsWin).ToList();
            
            result.AverageWinPercentage = wins.Any() ? wins.Average(t => t.ProfitPercentage) : 0;
            result.AverageLossPercentage = losses.Any() ? losses.Average(t => t.ProfitPercentage) : 0;
            
            double grossProfit = wins.Sum(t => t.ProfitPercentage);
            double grossLoss = Math.Abs(losses.Sum(t => t.ProfitPercentage));
            result.ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? 999 : 0);
            result.MaxDrawdown = maxDrawdown * 100.0;
            result.TotalReturn = result.Trades.Sum(t => t.ProfitPercentage);

            return result;
        }

        public async Task<BulkBacktestResponse> RunAllBacktestsAsync(double stopLossPct, double targetPct, bool useDynamicExits = false)
        {
            var response = new BulkBacktestResponse();
            
            // 1. Fetch all stock metadata
            List<StockMetadata> stocks;
            using (var conn = new DuckDBConnection(_connectionString))
            {
                conn.Open();
                stocks = conn.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();
            }

            // 2. Fetch all Daily Bars in one query
            var dailyGroups = new Dictionary<string, StockSeriesData>();
            using (var conn = new DuckDBConnection(_connectionString))
            {
                conn.Open();
                var rawDailyData = conn.Query<DailyBar>("SELECT Ticker, Open, High, Low, Close, Volume, Date FROM DailyBars");
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

                foreach (var kvp in rawDaily)
                {
                    var sorted = kvp.Value.OrderBy(c => c.Date).ToList();
                    var data = new StockSeriesData();
                    foreach (var c in sorted)
                    {
                        data.Opens.Add(c.Open);
                        data.Highs.Add(c.High);
                        data.Lows.Add(c.Low);
                        data.Closes.Add(c.Close);
                        data.Volumes.Add(c.Volume);
                        data.Dates.Add(c.Date);
                    }
                    dailyGroups[kvp.Key] = data;
                }
            }

            // 3. Pre-calculate historical returns for RsRank at each Date
            var historicalReturnsByDate = new Dictionary<DateTime, List<double>>();
            foreach (var kvp in dailyGroups)
            {
                var closes = kvp.Value.Closes;
                var dates = kvp.Value.Dates;
                for (int i = 60; i < closes.Count; i++)
                {
                    double ret = (closes[i] - closes[i - 60]) / closes[i - 60] * 100.0;
                    if (!historicalReturnsByDate.TryGetValue(dates[i], out var list))
                    {
                        list = new List<double>();
                        historicalReturnsByDate[dates[i]] = list;
                    }
                    list.Add(ret);
                }
            }
            foreach (var list in historicalReturnsByDate.Values)
            {
                list.Sort();
            }

            // 4. Initialize results collection for each strategy
            var strategySummaries = _strategies.Select(s => new StrategyPerformanceSummary { StrategyName = s.Name }).ToList();
            var tickerPerformancesBag = new ConcurrentDictionary<string, ConcurrentBag<TickerPerformance>>();
            foreach (var s in _strategies)
            {
                tickerPerformancesBag[s.Name] = new ConcurrentBag<TickerPerformance>();
            }

            // 5. Run backtest simulations in parallel across tickers in-memory
            Parallel.ForEach(dailyGroups, kvp =>
            {
                string ticker = kvp.Key;
                var data = kvp.Value;
                if (data.Closes.Count < 200) return;

                var opensArray = data.Opens.ToArray();
                var closesArray = data.Closes.ToArray();
                var highsArray = data.Highs.ToArray();
                var lowsArray = data.Lows.ToArray();
                var volumesArray = data.Volumes.ToArray();
                var datesArray = data.Dates.ToArray();

                // Pre-calculate full-series indicator arrays for this ticker
                double[] ema8 = _indicatorService.CalculateEma(closesArray, 8);
                double[] ema10 = _indicatorService.CalculateEma(closesArray, 10);
                double[] ema21 = _indicatorService.CalculateEma(closesArray, 21);
                double[] ema50 = _indicatorService.CalculateEma(closesArray, 50);
                double[] ema200 = _indicatorService.CalculateEma(closesArray, 200);
                double[] jnsar = _indicatorService.CalculateJnsar(closesArray, highsArray, lowsArray);
                var macd = _indicatorService.CalculateMacd(closesArray);
                double[] macdLine = macd.MacdLine;
                double[] macdSignal = macd.SignalLine;
                double[] atr14 = _indicatorService.CalculateAtr(highsArray, lowsArray, closesArray, 14);
                double[] rsi14 = _indicatorService.CalculateRsi(closesArray, 14);
                double[] obv = _indicatorService.CalculateObv(closesArray, volumesArray);
                double[] cmf = _indicatorService.CalculateCmf(closesArray, highsArray, lowsArray, volumesArray);

                // YTD VWAP pre-calculation
                double[] ytdVwap = new double[closesArray.Length];
                double cumPriceVolume = 0;
                double cumVolume = 0;
                int lastYear = -1;
                for (int i = 0; i < closesArray.Length; i++)
                {
                    int yr = datesArray[i].Year;
                    if (yr != lastYear)
                    {
                        cumPriceVolume = 0;
                        cumVolume = 0;
                        lastYear = yr;
                    }
                    double typicalPrice = (highsArray[i] + lowsArray[i] + closesArray[i]) / 3.0;
                    cumPriceVolume += typicalPrice * volumesArray[i];
                    cumVolume += volumesArray[i];
                    ytdVwap[i] = cumVolume > 0 ? cumPriceVolume / cumVolume : closesArray[i];
                }

                // Simulate each strategy
                foreach (var strategy in _strategies)
                {
                    BacktestTrade? openTrade = null;
                    bool triggerEntryOnNextBar = false;
                    var trades = new List<BacktestTrade>();
                    double peak = closesArray[0];
                    double maxDrawdown = 0;

                    for (int i = 200; i < closesArray.Length; i++)
                    {
                        double currentPrice = closesArray[i];
                        if (currentPrice > peak) peak = currentPrice;
                        double drawdown = (peak - currentPrice) / peak;
                        if (drawdown > maxDrawdown) maxDrawdown = drawdown;

                        // Pre-calculate index-specific indicators using optimized index-based overloads
                        double poc = _indicatorService.CalculatePointOfControlAt(closesArray, volumesArray, i);
                        double chandelierExit = _indicatorService.CalculateChandelierExitAt(highsArray, lowsArray, atr14, i);
                        double zScore = _indicatorService.CalculateZScoreAt(closesArray, i);
                        double volPctRank = _indicatorService.CalculateVolatilityPercentileRankAt(atr14, i);
                        
                        double rsRank = 50.0;
                        double ret3M = (closesArray[i] - closesArray[i - 60]) / closesArray[i - 60] * 100.0;
                        if (historicalReturnsByDate.TryGetValue(datesArray[i], out var list))
                        {
                            int idx = list.BinarySearch(ret3M);
                            if (idx < 0) idx = ~idx;
                            rsRank = list.Count > 1 ? (idx / (double)(list.Count - 1)) * 100.0 : 50.0;
                        }

                        double rsSharpe = _indicatorService.CalculateRollingSharpeAt(closesArray, i);

                        double maxH = highsArray[i];
                        int startH = Math.Max(0, i - 250 + 1);
                        for (int k = startH; k <= i; k++) { if (highsArray[k] > maxH) maxH = highsArray[k]; }
                        double discount52W = (maxH - currentPrice) / maxH;

                        // Create slices for OBV and CMF to match context requirements
                        var obvSlice = new double[i + 1];
                        Array.Copy(obv, 0, obvSlice, 0, i + 1);
                        var cmfSlice = new double[i + 1];
                        Array.Copy(cmf, 0, cmfSlice, 0, i + 1);

                        var context = new StrategyContext
                        {
                            CurrentPrice = currentPrice,
                            YtdVwap = ytdVwap[i],
                            Poc = poc,
                            Obv = obvSlice,
                            Cmf = cmfSlice,
                            VolPctRank = volPctRank,
                            RsRank = rsRank,
                            RsSharpe = rsSharpe,
                            ZScore = zScore,
                            ChandelierExit = chandelierExit,
                            Discount52W = discount52W,
                            Ema8 = ema8[i],
                            Ema10 = ema10[i],
                            Ema21 = ema21[i],
                            Ema50 = ema50[i],
                            Ema200 = ema200[i],
                            Rsi14 = rsi14[i],
                            MacdLine = macdLine[i],
                            MacdSignal = macdSignal[i],
                            Jnsar = jnsar[i]
                        };

                        if (openTrade != null)
                        {
                            bool shouldExit = false;
                            double exitPrice = currentPrice;
                            if (useDynamicExits)
                            {
                                shouldExit = strategy.ShouldExit(context, openTrade);
                                exitPrice = currentPrice;
                            }
                            else
                            {
                                double slLevel = openTrade.EntryPrice * (1 - stopLossPct);
                                double tpLevel = openTrade.EntryPrice * (1 + targetPct);
                                
                                bool hitSl = lowsArray[i] <= slLevel;
                                bool hitTp = highsArray[i] >= tpLevel;
                                
                                if (hitSl && hitTp)
                                {
                                    shouldExit = true;
                                    exitPrice = Math.Min(opensArray[i], slLevel);
                                }
                                else if (hitSl)
                                {
                                    shouldExit = true;
                                    exitPrice = Math.Min(opensArray[i], slLevel);
                                }
                                else if (hitTp)
                                {
                                    shouldExit = true;
                                    exitPrice = Math.Max(opensArray[i], tpLevel);
                                }
                            }

                            if (shouldExit)
                            {
                                openTrade.ExitDate = datesArray[i];
                                openTrade.ExitPrice = exitPrice;
                                openTrade.ProfitPercentage = (exitPrice - openTrade.EntryPrice) / openTrade.EntryPrice * 100.0;
                                openTrade.IsWin = openTrade.ProfitPercentage > 0;
                                openTrade.DaysHeld = (int)(openTrade.ExitDate - openTrade.EntryDate).TotalDays;
                                trades.Add(openTrade);
                                openTrade = null;
                            }
                        }
                        else
                        {
                            if (triggerEntryOnNextBar)
                            {
                                openTrade = new BacktestTrade
                                {
                                    Ticker = ticker,
                                    Strategy = strategy.Name,
                                    EntryDate = datesArray[i],
                                    EntryPrice = opensArray[i]
                                };
                                triggerEntryOnNextBar = false;
                            }

                            if (strategy.Evaluate(context))
                            {
                                triggerEntryOnNextBar = true;
                            }
                        }
                    }

                    if (openTrade != null)
                    {
                        openTrade.ExitDate = datesArray[^1];
                        openTrade.ExitPrice = closesArray[^1];
                        openTrade.ProfitPercentage = (closesArray[^1] - openTrade.EntryPrice) / openTrade.EntryPrice * 100.0;
                        openTrade.IsWin = openTrade.ProfitPercentage > 0;
                        openTrade.DaysHeld = (int)(openTrade.ExitDate - openTrade.EntryDate).TotalDays;
                        trades.Add(openTrade);
                    }

                    if (trades.Count > 0)
                    {
                        var wins = trades.Where(t => t.IsWin).ToList();
                        var losses = trades.Where(t => !t.IsWin).ToList();
                        double grossProfit = wins.Sum(t => t.ProfitPercentage);
                        double grossLoss = Math.Abs(losses.Sum(t => t.ProfitPercentage));
                        double profitFactor = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? 999.0 : 0.0);
                        double totalReturn = trades.Sum(t => t.ProfitPercentage);

                        var tp = new TickerPerformance
                        {
                            Ticker = ticker,
                            TotalTrades = trades.Count,
                            WinningTrades = wins.Count,
                            LosingTrades = losses.Count,
                            WinRate = (double)wins.Count / trades.Count * 100.0,
                            TotalReturn = totalReturn,
                            MaxDrawdown = maxDrawdown * 100.0,
                            ProfitFactor = profitFactor
                        };
                        tickerPerformancesBag[strategy.Name].Add(tp);
                    }
                }
            });

            // 6. Aggregate performance metrics for each strategy
            foreach (var summary in strategySummaries)
            {
                var list = tickerPerformancesBag[summary.StrategyName].ToList();
                summary.TickerPerformances = list.OrderByDescending(tp => tp.TotalReturn).ToList();

                summary.TotalTrades = list.Sum(tp => tp.TotalTrades);
                summary.WinningTrades = list.Sum(tp => tp.WinningTrades);
                summary.LosingTrades = list.Sum(tp => tp.LosingTrades);
                summary.WinRate = summary.TotalTrades > 0 ? (double)summary.WinningTrades / summary.TotalTrades * 100.0 : 0;
                
                // Aggregate averages and other totals
                summary.TotalReturn = list.Count > 0 ? list.Average(tp => tp.TotalReturn) : 0; // average return per ticker
                summary.MaxDrawdown = list.Count > 0 ? list.Max(tp => tp.MaxDrawdown) : 0; // worst case drawdown
                summary.ProfitFactor = list.Count > 0 ? list.Average(tp => tp.ProfitFactor) : 0;
            }

            response.Strategies = strategySummaries.OrderByDescending(s => s.TotalReturn).ToList();
            return response;
        }

        public async Task<List<BacktestTrade>> GenerateAllTradesAsync(double stopLossPct, double targetPct, bool useDynamicExits)
        {
            // 1. Fetch all StockMetadatas
            List<StockMetadata> stocks;
            using (var conn = new DuckDBConnection(_connectionString))
            {
                conn.Open();
                stocks = conn.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();
            }

            // 2. Fetch all Daily Bars in one query
            var dailyGroups = new Dictionary<string, StockSeriesData>();
            using (var conn = new DuckDBConnection(_connectionString))
            {
                conn.Open();
                var rawDailyData = conn.Query<DailyBar>("SELECT Ticker, Open, High, Low, Close, Volume, Date FROM DailyBars");
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

                foreach (var kvp in rawDaily)
                {
                    var sorted = kvp.Value.OrderBy(c => c.Date).ToList();
                    var data = new StockSeriesData();
                    foreach (var c in sorted)
                    {
                        data.Opens.Add(c.Open);
                        data.Highs.Add(c.High);
                        data.Lows.Add(c.Low);
                        data.Closes.Add(c.Close);
                        data.Volumes.Add(c.Volume);
                        data.Dates.Add(c.Date);
                    }
                    dailyGroups[kvp.Key] = data;
                }
            }

            // 3. Pre-calculate historical returns for RsRank at each Date
            var historicalReturnsByDate = new Dictionary<DateTime, List<double>>();
            foreach (var kvp in dailyGroups)
            {
                var closes = kvp.Value.Closes;
                var dates = kvp.Value.Dates;
                for (int i = 60; i < closes.Count; i++)
                {
                    double ret = (closes[i] - closes[i - 60]) / closes[i - 60] * 100.0;
                    if (!historicalReturnsByDate.TryGetValue(dates[i], out var list))
                    {
                        list = new List<double>();
                        historicalReturnsByDate[dates[i]] = list;
                    }
                    list.Add(ret);
                }
            }
            foreach (var list in historicalReturnsByDate.Values)
            {
                list.Sort();
            }

            var allTrades = new ConcurrentBag<BacktestTrade>();

            // 5. Run backtest simulations in parallel across tickers in-memory
            Parallel.ForEach(dailyGroups, kvp =>
            {
                string ticker = kvp.Key;
                var data = kvp.Value;
                if (data.Closes.Count < 200) return;

                var opensArray = data.Opens.ToArray();
                var closesArray = data.Closes.ToArray();
                var highsArray = data.Highs.ToArray();
                var lowsArray = data.Lows.ToArray();
                var volumesArray = data.Volumes.ToArray();
                var datesArray = data.Dates.ToArray();

                // Pre-calculate full-series indicator arrays for this ticker
                double[] ema8 = _indicatorService.CalculateEma(closesArray, 8);
                double[] ema10 = _indicatorService.CalculateEma(closesArray, 10);
                double[] ema21 = _indicatorService.CalculateEma(closesArray, 21);
                double[] ema50 = _indicatorService.CalculateEma(closesArray, 50);
                double[] ema200 = _indicatorService.CalculateEma(closesArray, 200);
                double[] jnsar = _indicatorService.CalculateJnsar(closesArray, highsArray, lowsArray);
                var macd = _indicatorService.CalculateMacd(closesArray);
                double[] macdLine = macd.MacdLine;
                double[] macdSignal = macd.SignalLine;
                double[] atr14 = _indicatorService.CalculateAtr(highsArray, lowsArray, closesArray, 14);
                double[] rsi14 = _indicatorService.CalculateRsi(closesArray, 14);
                double[] obv = _indicatorService.CalculateObv(closesArray, volumesArray);
                double[] cmf = _indicatorService.CalculateCmf(closesArray, highsArray, lowsArray, volumesArray);

                // YTD VWAP pre-calculation
                double[] ytdVwap = new double[closesArray.Length];
                double cumPriceVolume = 0;
                double cumVolume = 0;
                int lastYear = -1;
                for (int i = 0; i < closesArray.Length; i++)
                {
                    int yr = datesArray[i].Year;
                    if (yr != lastYear) { cumPriceVolume = 0; cumVolume = 0; lastYear = yr; }
                    double typicalPrice = (highsArray[i] + lowsArray[i] + closesArray[i]) / 3.0;
                    cumPriceVolume += typicalPrice * volumesArray[i];
                    cumVolume += volumesArray[i];
                    ytdVwap[i] = cumVolume > 0 ? cumPriceVolume / cumVolume : closesArray[i];
                }

                // Simulate each strategy
                foreach (var strategy in _strategies)
                {
                    BacktestTrade? openTrade = null;
                    bool triggerEntryOnNextBar = false;
                    double peak = closesArray[0];

                    for (int i = 200; i < closesArray.Length; i++)
                    {
                        double currentPrice = closesArray[i];
                        if (currentPrice > peak) peak = currentPrice;

                        // Pre-calculate index-specific indicators using optimized index-based overloads
                        double poc = _indicatorService.CalculatePointOfControlAt(closesArray, volumesArray, i);
                        double chandelierExit = _indicatorService.CalculateChandelierExitAt(highsArray, lowsArray, atr14, i);
                        double zScore = _indicatorService.CalculateZScoreAt(closesArray, i);
                        double volPctRank = _indicatorService.CalculateVolatilityPercentileRankAt(atr14, i);
                        
                        double rsRank = 50.0;
                        double ret3M = (closesArray[i] - closesArray[i - 60]) / closesArray[i - 60] * 100.0;
                        if (historicalReturnsByDate.TryGetValue(datesArray[i], out var list))
                        {
                            int idx = list.BinarySearch(ret3M);
                            if (idx < 0) idx = ~idx;
                            rsRank = list.Count > 1 ? (idx / (double)(list.Count - 1)) * 100.0 : 50.0;
                        }

                        double rsSharpe = _indicatorService.CalculateRollingSharpeAt(closesArray, i);

                        double maxH = highsArray[i];
                        int startH = Math.Max(0, i - 250 + 1);
                        for (int k = startH; k <= i; k++) { if (highsArray[k] > maxH) maxH = highsArray[k]; }
                        double discount52W = (maxH - currentPrice) / maxH;

                        // Create slices for OBV and CMF to match context requirements
                        var obvSlice = new double[i + 1];
                        Array.Copy(obv, 0, obvSlice, 0, i + 1);
                        var cmfSlice = new double[i + 1];
                        Array.Copy(cmf, 0, cmfSlice, 0, i + 1);

                        var context = new StrategyContext
                        {
                            CurrentPrice = currentPrice,
                            YtdVwap = ytdVwap[i],
                            Poc = poc,
                            Obv = obvSlice,
                            Cmf = cmfSlice,
                            VolPctRank = volPctRank,
                            RsRank = rsRank,
                            RsSharpe = rsSharpe,
                            ZScore = zScore,
                            ChandelierExit = chandelierExit,
                            Discount52W = discount52W,
                            Ema8 = ema8[i],
                            Ema10 = ema10[i],
                            Ema21 = ema21[i],
                            Ema50 = ema50[i],
                            Ema200 = ema200[i],
                            Rsi14 = rsi14[i],
                            MacdLine = macdLine[i],
                            MacdSignal = macdSignal[i],
                            Jnsar = jnsar[i]
                        };

                        if (openTrade != null)
                        {
                            bool shouldExit = false;
                            double exitPrice = currentPrice;
                            if (useDynamicExits)
                            {
                                shouldExit = strategy.ShouldExit(context, openTrade);
                                exitPrice = currentPrice;
                            }
                            else
                            {
                                double slLevel = openTrade.EntryPrice * (1 - stopLossPct);
                                double tpLevel = openTrade.EntryPrice * (1 + targetPct);
                                
                                bool hitSl = lowsArray[i] <= slLevel;
                                bool hitTp = highsArray[i] >= tpLevel;
                                
                                if (hitSl && hitTp)
                                {
                                    shouldExit = true;
                                    exitPrice = Math.Min(opensArray[i], slLevel);
                                }
                                else if (hitSl)
                                {
                                    shouldExit = true;
                                    exitPrice = Math.Min(opensArray[i], slLevel);
                                }
                                else if (hitTp)
                                {
                                    shouldExit = true;
                                    exitPrice = Math.Max(opensArray[i], tpLevel);
                                }
                            }

                            if (shouldExit)
                            {
                                openTrade.ExitDate = datesArray[i];
                                openTrade.ExitPrice = exitPrice;
                                openTrade.ProfitPercentage = (exitPrice - openTrade.EntryPrice) / openTrade.EntryPrice * 100.0;
                                openTrade.IsWin = openTrade.ProfitPercentage > 0;
                                openTrade.DaysHeld = (int)(openTrade.ExitDate - openTrade.EntryDate).TotalDays;
                                allTrades.Add(openTrade);
                                openTrade = null;
                            }
                        }
                        else
                        {
                            if (triggerEntryOnNextBar)
                            {
                                openTrade = new BacktestTrade
                                {
                                    Ticker = ticker,
                                    Strategy = strategy.Name,
                                    EntryDate = datesArray[i],
                                    EntryPrice = opensArray[i]
                                };
                                triggerEntryOnNextBar = false;
                            }

                            if (strategy.Evaluate(context))
                            {
                                triggerEntryOnNextBar = true;
                            }
                        }
                    }

                    if (openTrade != null)
                    {
                        openTrade.ExitDate = datesArray[^1];
                        openTrade.ExitPrice = closesArray[^1];
                        openTrade.ProfitPercentage = (closesArray[^1] - openTrade.EntryPrice) / openTrade.EntryPrice * 100.0;
                        openTrade.IsWin = openTrade.ProfitPercentage > 0;
                        openTrade.DaysHeld = (int)(openTrade.ExitDate - openTrade.EntryDate).TotalDays;
                        allTrades.Add(openTrade);
                    }
                }
            });

            return allTrades.OrderBy(t => t.EntryDate).ToList();
        }
    }
}
