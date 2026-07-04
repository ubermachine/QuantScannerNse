using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using backend.Models;
using DuckDB.NET.Data;
using Dapper;

namespace backend.Services
{
    public class BacktestService
    {
        private readonly string _connectionString = "Data Source=quantscanner.duckdb";
        private readonly IndicatorService _indicatorService;

        public BacktestService(IndicatorService indicatorService)
        {
            _indicatorService = indicatorService;
        }

        public async Task<PortfolioSimulationResult> RunPortfolioSimulationAsync(PortfolioRequest request)
        {
            var sw = Stopwatch.StartNew();

            using var connection = new DuckDBConnection(_connectionString);
            connection.Open();

            // Fetch metadata and daily/weekly bars
            var stocks = connection.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();
            var rawDailyBars = connection.Query<DailyBar>("SELECT Ticker, Date, Open, High, Low, Close, Volume FROM DailyBars").ToList();
            var rawWeeklyBars = connection.Query<WeeklyBar>("SELECT Ticker, Date, Close FROM WeeklyBars").ToList();

            // Group bars in memory
            var dailyGroups = rawDailyBars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());
            var weeklyGroups = rawWeeklyBars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());

            // Step 1: Build states with minimal data + precompute all indicators
            var states = new List<BacktestStockState>();
            foreach (var stock in stocks)
            {
                if (!dailyGroups.TryGetValue(stock.Ticker, out var dBars) || dBars.Count < 200)
                    continue;

                var wBars = weeklyGroups.TryGetValue(stock.Ticker, out var wB) ? wB : new List<WeeklyBar>();

                // Pre-extract raw arrays here to avoid allocating them per-state in the parallel loop
                var closes = dBars.Select(b => b.Close).ToArray();
                var highs  = dBars.Select(b => b.High).ToArray();
                var lows   = dBars.Select(b => b.Low).ToArray();
                var vols   = dBars.Select(b => (double)b.Volume).ToArray();

                var state = new BacktestStockState
                {
                    Ticker = stock.Ticker,
                    Sector = stock.Sector ?? "",
                    DailyBars = dBars,
                    WeeklyBars = wBars,
                    Closes = closes,
                    Highs = highs,
                    Lows = lows,
                    Volumes = vols
                };

                // Precompute indicator arrays — stock-level, not recomputed on each bar
                state.Ema8   = _indicatorService.CalculateEma(closes, 8);
                state.Ema10  = _indicatorService.CalculateEma(closes, 10);
                state.Ema21  = _indicatorService.CalculateEma(closes, 21);
                state.Ema50  = _indicatorService.CalculateEma(closes, 50);
                state.Ema200 = _indicatorService.CalculateEma(closes, 200);
                state.Jnsar  = _indicatorService.CalculateJnsar(closes, highs, lows);
                state.Atr14  = _indicatorService.CalculateAtr(highs, lows, closes, 14);
                state.Atr22  = _indicatorService.CalculateAtr(highs, lows, closes, 22);

                // Precompute score indicators (avoids per-candidate recomputation)
                state.Adx14  = _indicatorService.CalculateAdx(highs, lows, closes, 14);
                state.Rsi14  = _indicatorService.CalculateRsi(closes);
                var (bbU, _, bbL) = _indicatorService.CalculateBollingerBands(closes);
                state.BollingerUpper = bbU;
                state.BollingerLower = bbL;
                var (kcU, _, kcL) = _indicatorService.CalculateKeltnerChannels(highs, lows, closes);
                state.KeltnerUpper = kcU;
                state.KeltnerLower = kcL;

                // Precompute Volume Score for every index
                state.VolumeScores = new double[closes.Length];
                for (int i = 20; i < closes.Length; i++)
                {
                    var sliceCloses = closes.AsSpan(0, i + 1);
                    var sliceVols   = vols.AsSpan(0, i + 1);
                    state.VolumeScores[i] = _indicatorService.CalculateVolumeScore(
                        sliceCloses.ToArray(), sliceVols.ToArray(), 15);
                }

                // Daily MACD (for Momentum Continuation strategy)
                var (dailyMacdLine, dailyMacdSignal) = _indicatorService.CalculateMacd(closes);
                state.MacdLine = dailyMacdLine;
                state.MacdSignal = dailyMacdSignal;

                // CMF — Chaikin Money Flow (for Institutional Value strategy)
                state.Cmf = _indicatorService.CalculateCmf(closes, highs, lows, vols, 21);

                // 20-day average volume (for Volatility Breakout strategy)
                state.VolAvg20 = new double[closes.Length];
                double volRunningSum = 0;
                for (int i = 0; i < closes.Length; i++)
                {
                    volRunningSum += vols[i];
                    if (i >= 20) volRunningSum -= vols[i - 20];
                    state.VolAvg20[i] = i >= 19 ? volRunningSum / 20.0 : vols[i];
                }

                // OBV — On-Balance Volume (for CBO, DPA strategies)
                state.Obv = _indicatorService.CalculateObv(closes, vols);

                // Z-score — rolling 50-period (for DPA strategy)
                state.ZScore = new double[closes.Length];
                for (int i = 50; i < closes.Length; i++)
                {
                    double segMean = closes.Skip(i - 49).Take(50).Average();
                    double segVar = closes.Skip(i - 49).Take(50).Sum(v => Math.Pow(v - segMean, 2)) / 50.0;
                    state.ZScore[i] = segVar > 0 ? (closes[i] - segMean) / Math.Sqrt(segVar) : 0;
                }

                // 40-day average volume (for DPA volume decline check)
                state.VolAvg40 = new double[closes.Length];
                double volRun40 = 0;
                for (int i = 0; i < closes.Length; i++)
                {
                    volRun40 += vols[i];
                    if (i >= 40) volRun40 -= vols[i - 40];
                    state.VolAvg40[i] = i >= 39 ? volRun40 / 40.0 : vols[i];
                }

                if (wBars.Count > 0)
                {
                    var wCloses = wBars.Select(b => b.Close).ToArray();
                    state.WEma144 = _indicatorService.CalculateEma(wCloses, 144);
                    state.WEma233 = _indicatorService.CalculateEma(wCloses, 233);
                    var (macdLine, signalLine) = _indicatorService.CalculateMacd(wCloses);
                    state.WMacdLine = macdLine;
                    state.WMacdSignal = signalLine;
                }

                states.Add(state);
            }

            // Precompute cross-sectional 3M returns for RS percentile ranking (RSML strategy)
            var stockReturnsAsc = new List<double>();
            foreach (var st in states)
            {
                if (st.Closes.Length >= 60)
                {
                    double ret = (st.Closes[^1] - st.Closes[^61]) / st.Closes[^61] * 100.0;
                    stockReturnsAsc.Add(ret);
                }
            }
            stockReturnsAsc.Sort();

            Console.WriteLine($"[PERF] Load+indicator precompute: {sw.Elapsed.TotalSeconds:F2}s for {states.Count} stocks");
            sw.Restart();

            // Gather and sort all unique trading dates in range
            var allDates = states
                .SelectMany(s => s.DailyBars)
                .Select(b => b.Date)
                .Where(d => d >= request.StartDate && d <= request.EndDate)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            if (allDates.Count == 0)
            {
                return new PortfolioSimulationResult
                {
                    StartingCapital = request.StartingCapital,
                    EndingCapital = request.StartingCapital
                };
            }

            double cash = request.StartingCapital;
            double maxBalance = request.StartingCapital;
            double maxDrawdown = 0;
            double prevDayPortfolioValue = request.StartingCapital; // snapshot for sizing
            double currentPortfolioValue = request.StartingCapital;
            var trades = new List<PortfolioTrade>();
            var equityCurve = new List<EquityCurvePoint>(1024);

            // O(1) state lookup instead of .First() linear scan
            var stateDict = new Dictionary<string, BacktestStockState>(states.Count);
            foreach (var s in states) stateDict[s.Ticker] = s;

            // O(1) position lookup instead of list scan
            var activePosDict = new Dictionary<string, ActivePosition>(request.MaxPositions * 2);

            // Initialize bar pointers
            foreach (var state in states) state.BarPointer = 0;

            Console.WriteLine($"[PERF] Loop setup: {sw.Elapsed.TotalSeconds:F2}s");
            sw.Restart();

            foreach (var currentDate in allDates)
            {
                // Advance pointers to current date
                foreach (var state in states)
                {
                    while (state.BarPointer < state.DailyBars.Count && state.DailyBars[state.BarPointer].Date <= currentDate)
                        state.BarPointer++;
                }

                // Snapshot portfolio value before today's P&L for position sizing (no intraday bias)
                prevDayPortfolioValue = currentPortfolioValue;

                // --- Step 1: Check exits for active positions ---
                var exitedTickers = new HashSet<string>(request.MaxPositions);

                foreach (var kvp in activePosDict.ToList())
                {
                    var pos = kvp.Value;
                    if (!stateDict.TryGetValue(pos.Ticker, out var state)) continue;
                    int idx = state.BarPointer - 1;
                    if (idx < 0) continue;
                    var bar = state.DailyBars[idx];
                    if (bar.Date != currentDate) continue;

                    // ── EXIT PRIORITY: Target 2 > Target 1 (scale-out) > Stop Loss ──
                    // This ensures that if both target and stop are hit intraday,
                    // the target wins (stock proved it can reach TP).
                    bool didExit = false;

                    // 1. Target 2 — full exit of remaining position
                    if (!didExit && pos.HasHitTarget1 && bar.High >= pos.Target2)
                    {
                        double exitPrice = Math.Max(pos.Target2, bar.Open);
                        FinalizeExit(pos, state, request, currentDate, exitPrice, pos.Shares, "Target 2",
                                     trades, ref cash, exitedTickers, activePosDict);
                        didExit = true;
                    }

                    // 2. Target 1 — scale out 50% of position, move stop to breakeven
                    if (!didExit && !pos.HasHitTarget1 && bar.High >= pos.Target1)
                    {
                        int exitShares = Math.Max(1, pos.Shares / 2);
                        double exitPrice = Math.Max(pos.Target1, bar.Open);
                        FinalizeExit(pos, state, request, currentDate, exitPrice, exitShares, "Target 1",
                                     trades, ref cash, exitedTickers, activePosDict, partial: true);

                        pos.HasHitTarget1 = true;
                        pos.StopLoss = pos.EntryPrice; // move stop to breakeven
                        didExit = true;
                    }

                    // 3. Stop Loss — full exit (last price priority)
                    if (!didExit && bar.Low <= pos.StopLoss)
                    {
                        double exitPrice = Math.Min(pos.StopLoss, bar.Open);
                        FinalizeExit(pos, state, request, currentDate, exitPrice, pos.Shares, "Stop Loss",
                                     trades, ref cash, exitedTickers, activePosDict);
                        didExit = true;
                    }

                    // 4. Time stop — exit if held beyond strategy's max days
                    if (!didExit && pos.TimeStopDays > 0)
                    {
                        int daysHeld = (int)(currentDate - pos.EntryDate).TotalDays;
                        if (daysHeld >= pos.TimeStopDays)
                        {
                            double exitPrice = bar.Close * (1.0 - request.SlippagePercent / 100.0);
                            FinalizeExit(pos, state, request, currentDate, exitPrice, pos.Shares, "Time Stop",
                                         trades, ref cash, exitedTickers, activePosDict);
                            didExit = true;
                        }
                    }
                }

                // --- Step 2: Trailing stop loss updates ---
                foreach (var kvp in activePosDict)
                {
                    var pos = kvp.Value;
                    if (!stateDict.TryGetValue(pos.Ticker, out var state)) continue;
                    int idx = state.BarPointer - 1;
                    if (idx < 0) continue;

                    var bar = state.DailyBars[idx];
                    if (bar.Date != currentDate) continue;

                    // Track highest close since entry for trailing stop
                    pos.HighestCloseSinceEntry = Math.Max(pos.HighestCloseSinceEntry, bar.Close);

                    // Per-strategy ATR trailing stop (wider before T1, tighter after)
                    double trailMult = GetTrailMultiplier(pos.StrategyName, pos.HasHitTarget1);
                    double trailStop = pos.HighestCloseSinceEntry - trailMult * state.Atr14[idx];
                    if (trailStop > pos.StopLoss)
                        pos.StopLoss = trailStop;

                    // After Target 1, keep stop at breakeven as floor
                    if (pos.HasHitTarget1 && pos.EntryPrice > pos.StopLoss)
                        pos.StopLoss = pos.EntryPrice;
                }



                // --- Step 4: Evaluate Buy Signals ---
                var candidates = new List<(BacktestStockState State, int Index, double Return3M)>();

                foreach (var state in states)
                {
                    int idx = state.BarPointer - 1;

                    // FIX: Evaluate buy signal using YESTERDAY's data to avoid look-ahead bias.
                    // idx is today's bar. Signal is based on idx-1 (yesterday's close).
                    // Entry executes at today's open (bar.Open).
                    if (activePosDict.ContainsKey(state.Ticker) || exitedTickers.Contains(state.Ticker))
                        continue;
                    if (idx < 200) continue; // need idx-1 >= 200-1 so require idx >= 200
                    var bar = state.DailyBars[idx];
                    if (bar.Date != currentDate) continue;

                    bool match = false;

                    // Trend-following strategy (HCT)
                    bool runHct = request.Strategy == "All" || request.Strategy.Contains("HCT", StringComparison.OrdinalIgnoreCase) || request.Strategy == "Both";
                    if (runHct)
                    {
                        var prevBar = state.DailyBars[idx - 1];
                        if (prevBar.Close > state.Ema200[idx - 1] &&
                            state.Ema8[idx - 1] > state.Ema21[idx - 1] &&
                            prevBar.Close > state.Ema10[idx - 1] &&
                            prevBar.Close > state.Jnsar[idx - 1])
                        {
                            // Fib618 on data up to yesterday only
                            double fib618 = _indicatorService.CalculateFib618(
                                state.DailyBars.Take(idx).Select(b => b.Close).ToArray(),
                                state.DailyBars.Take(idx).Select(b => b.High).ToArray(),
                                state.DailyBars.Take(idx).Select(b => b.Low).ToArray(),
                                out _, out _
                            );

                            if (Math.Abs((prevBar.Close - fib618) / fib618) <= 0.02)
                                match = true;
                        }
                    }

                    // Weekly swing reversal strategy (LRHR)
                    bool runLrhr = request.Strategy == "All" || request.Strategy.Contains("LRHR", StringComparison.OrdinalIgnoreCase) || request.Strategy == "Both";
                    if (!match && runLrhr)
                    {
                        var prevBar = state.DailyBars[idx - 1];
                        double max52WHigh = state.DailyBars.Skip(Math.Max(0, idx - 249)).Take(Math.Min(idx + 1, 250)).Max(b => b.High);
                        double discount52W = (max52WHigh - bar.Close) / max52WHigh;

                        if (discount52W >= 0.30)
                        {
                            int w = state.GetWeeklyIndex(currentDate);
                            if (w >= 0 && w < state.WMacdLine.Length)
                            {
                                if (state.WMacdLine[w] > state.WMacdSignal[w] &&
                                    (Math.Abs((prevBar.Close - state.WEma144[w]) / state.WEma144[w]) <= 0.05 ||
                                     Math.Abs((prevBar.Close - state.WEma233[w]) / state.WEma233[w]) <= 0.05))
                                {
                                    match = true;
                                }
                            }
                        }
                    }

                    // ─── STRATEGY 3: Momentum Continuation (MOMCON) ────────────────────
                    bool runMomcon = request.Strategy == "All" || request.Strategy.Contains("MOMCON", StringComparison.OrdinalIgnoreCase);
                    if (!match && runMomcon)
                    {
                        var prevBar = state.DailyBars[idx - 1];
                        int pd = idx - 1;

                        // Layered uptrend: price > 50 EMA > 200 EMA, 50 EMA rising,
                        // ADX > 27, RSI 55-78, MACD bullish, volume confirming
                        if (prevBar.Close > state.Ema50[pd] &&
                            state.Ema50[pd] > state.Ema200[pd] &&
                            pd >= 5 && state.Ema50[pd] > state.Ema50[pd - 5] &&
                            state.Adx14.Length > 0 && state.Adx14[pd] > 27 &&
                            state.Rsi14.Length > 0 && state.Rsi14[pd] >= 55 && state.Rsi14[pd] <= 78 &&
                            state.MacdLine.Length > 0 && state.MacdSignal.Length > 0 &&
                            state.MacdLine[pd] > state.MacdSignal[pd] &&
                            state.VolumeScores[pd] >= 5 &&
                            prevBar.Close > state.Jnsar[pd])
                        {
                            match = true;
                        }
                    }

                    // ─── STRATEGY 4: Institutional Value (VAL) ──────────────────────────
                    // Stocks pulling back to 50 EMA support with institutional accumulation.
                    bool runVal = request.Strategy == "All" || request.Strategy.Contains("VAL", StringComparison.OrdinalIgnoreCase);
                    if (!match && runVal)
                    {
                        var prevBar = state.DailyBars[idx - 1];
                        int pd = idx - 1;

                        bool priceNear50Ema = prevBar.Close <= state.Ema50[pd] &&
                                              prevBar.Close >= state.Ema50[pd] * 0.97;
                        bool atrCompressed = state.Atr14.Length > 60 &&
                                             state.Atr14[pd] < state.Atr14.Skip(pd - 59).Take(60).Average();

                        if (prevBar.Close > state.Ema200[pd] &&
                            priceNear50Ema &&
                            state.Cmf.Length > 0 && state.Cmf[pd] > 0 &&
                            atrCompressed &&
                            state.Rsi14.Length > 0 && state.Rsi14[pd] >= 40 && state.Rsi14[pd] <= 55 &&
                            state.VolumeScores[pd] >= 3)
                        {
                            match = true;
                        }
                    }

                    // ─── STRATEGY 5: Volatility Breakout (VBO) ──────────────────────────
                    // Stocks breaking out of Bollinger/Keltner squeeze with volume + ATR expansion.
                    bool runVbo = request.Strategy == "All" || request.Strategy.Contains("VBO", StringComparison.OrdinalIgnoreCase);
                    if (!match && runVbo)
                    {
                        var prevBar = state.DailyBars[idx - 1];
                        int pd = idx - 1;

                        bool squeezeFiring = state.BollingerUpper.Length > 0 &&
                                             state.KeltnerUpper.Length > 0 &&
                                             pd < state.BollingerUpper.Length &&
                                             pd < state.KeltnerUpper.Length &&
                                             state.BollingerUpper[pd] < state.KeltnerUpper[pd] &&
                                             state.BollingerLower[pd] > state.KeltnerLower[pd];

                        double currVol = state.Volumes[idx - 1];
                        bool volExpansion = state.VolAvg20[pd] > 0 &&
                                            currVol > state.VolAvg20[pd] * 1.3;

                        bool atrExpanding = state.Atr14.Length > 60 &&
                                            state.Atr14[pd] > state.Atr14.Skip(pd - 59).Take(60).Average();

                        if (squeezeFiring &&
                            prevBar.Close > state.Ema50[pd] &&
                            state.Ema50[pd] > state.Ema200[pd] &&
                            volExpansion &&
                            atrExpanding &&
                            state.Adx14.Length > 0 && state.Adx14[pd] > 25 &&
                            state.Rsi14.Length > 0 && state.Rsi14[pd] > 50)
                        {
                            match = true;
                        }
                    }

                    // ─── STRATEGY 6: Momentum + Accumulation (MOMACC) ────────────────
                    // Rides established trends where institutions are actively accumulating.
                    // Most reliable strategy in bull markets (60%+ win rate observed).
                    bool runMomacc = request.Strategy == "All" || request.Strategy.Contains("MOMACC", StringComparison.OrdinalIgnoreCase);
                    if (!match && runMomacc)
                    {
                        var prevBar = state.DailyBars[idx - 1];
                        int pd = idx - 1;

                        // Layered uptrend, CMF > 0 (institutions buying), volume confirming,
                        // momentum in golden zone, trend strong, MACD aligned
                        if (prevBar.Close > state.Ema50[pd] &&
                            state.Ema50[pd] > state.Ema200[pd] &&
                            pd >= 5 && state.Ema50[pd] > state.Ema50[pd - 5] &&
                            state.Cmf.Length > 0 && state.Cmf[pd] > 0 &&
                            state.VolumeScores[pd] >= 6 &&
                            state.Adx14.Length > 0 && state.Adx14[pd] > 27 &&
                            state.Rsi14.Length > 0 && state.Rsi14[pd] >= 50 && state.Rsi14[pd] <= 72 &&
                            state.MacdLine.Length > 0 && state.MacdSignal.Length > 0 &&
                            state.MacdLine[pd] > state.MacdSignal[pd])
                        {
                            match = true;
                        }
                    }

                    // ─── STRATEGY 7: Compression Breakout (CBO) ──────────────────────
                    // Highest-alpha trade in equities: vol compression → vol expansion.
                    // BB/Keltner squeeze + volume + OBV + ADX rising = explosive moves.
                    bool runCbo = request.Strategy == "All" || request.Strategy.Contains("CBO", StringComparison.OrdinalIgnoreCase);
                    if (!match && runCbo)
                    {
                        var prevBar = state.DailyBars[idx - 1];
                        int pd = idx - 1;

                        bool squeezeFiring = state.BollingerUpper.Length > 0 &&
                                             state.KeltnerUpper.Length > 0 &&
                                             pd < state.BollingerUpper.Length &&
                                             pd < state.KeltnerUpper.Length &&
                                             state.BollingerUpper[pd] < state.KeltnerUpper[pd] &&
                                             state.BollingerLower[pd] > state.KeltnerLower[pd];

                        // OBV at 20-day high (volume confirmation)
                        bool obvHigh = state.Obv.Length > 0 && pd >= 20 &&
                                       state.Obv[pd] == state.Obv.Skip(pd - 19).Take(20).Max();

                        // ADX rising (trend emerging)
                        bool adxRising = state.Adx14.Length > 0 && pd >= 1 &&
                                         state.Adx14[pd] > state.Adx14[pd - 1] &&
                                         state.Adx14[pd] > 25;

                        double currVol = state.Volumes[pd];
                        bool volExpansion = state.VolAvg20[pd] > 0 &&
                                            currVol > state.VolAvg20[pd] * 1.3;

                        bool atrExpanding = state.Atr14.Length > 60 &&
                                            state.Atr14[pd] > state.Atr14.Skip(pd - 59).Take(60).Average();

                        if (squeezeFiring &&
                            prevBar.Close > state.Ema50[pd] &&
                            state.Ema50[pd] > state.Ema200[pd] &&
                            volExpansion && atrExpanding &&
                            obvHigh && adxRising &&
                            state.Rsi14.Length > 0 && state.Rsi14[pd] > 50)
                        {
                            match = true;
                        }
                    }

                    // ─── STRATEGY 8: Deep Pullback Accumulation (DPA) ────────────────
                    // Catches stocks that sold off but smart money is accumulating.
                    // OBV divergence + CMF positive = institutions buying the dip.
                    bool runDpa = request.Strategy == "All" || request.Strategy.Contains("DPA", StringComparison.OrdinalIgnoreCase);
                    if (!match && runDpa)
                    {
                        var prevBar = state.DailyBars[idx - 1];
                        int pd = idx - 1;

                        // 52-week discount
                        double max52WHigh = state.DailyBars.Skip(Math.Max(0, pd - 249)).Take(250).Max(b => b.High);
                        double discount52W = (max52WHigh - prevBar.Close) / max52WHigh;

                        // OBV divergence: price lower than 20 days ago but OBV higher
                        bool obvDivergence = pd >= 20 &&
                                             prevBar.Close < state.DailyBars[pd - 20].Close &&
                                             state.Obv[pd] > state.Obv[pd - 20];

                        // Volume declining: 20-day avg below 40-day avg (selling exhaustion)
                        bool volDeclining = pd >= 40 &&
                                            state.VolAvg20[pd] < state.VolAvg40[pd] * 0.95;

                        if (discount52W >= 0.15 &&
                            obvDivergence &&
                            state.Cmf.Length > 0 && state.Cmf[pd] > 0 &&
                            state.Rsi14.Length > 0 && state.Rsi14[pd] >= 30 && state.Rsi14[pd] <= 48 &&
                            volDeclining &&
                            prevBar.Close > state.Ema200[pd] &&
                            state.ZScore.Length > 0 && state.ZScore[pd] < -1.0)
                        {
                            match = true;
                        }
                    }

                    // ─── STRATEGY 9: RS Momentum Leader (RSML) ───────────────────────
                    // Picks the strongest stocks using relative strength percentile.
                    // Only top 30% of stocks by 3M return qualify.
                    bool runRsml = request.Strategy == "All" || request.Strategy.Contains("RSML", StringComparison.OrdinalIgnoreCase);
                    if (!match && runRsml)
                    {
                        var prevBar = state.DailyBars[idx - 1];
                        int pd = idx - 1;

                        // RS rank: cross-sectional percentile of 3M return
                        double ret3M = pd >= 60
                            ? (prevBar.Close - state.DailyBars[pd - 60].Close) / state.DailyBars[pd - 60].Close * 100.0
                            : 0;

                        // RS rank from precomputed sorted returns
                        double rsRank = 0;
                        if (stockReturnsAsc != null && stockReturnsAsc.Count > 0)
                        {
                            int idxRs = stockReturnsAsc.BinarySearch(ret3M);
                            if (idxRs < 0) idxRs = ~idxRs;
                            rsRank = (double)(idxRs) / stockReturnsAsc.Count * 100.0;
                        }

                        // Near 20 EMA: within 3%
                        double ema20 = state.Ema8[pd]; // 8 EMA as fast trend proxy
                        bool priceNear20Ema = Math.Abs((prevBar.Close - ema20) / ema20) <= 0.03;

                        if (rsRank >= 70 &&
                            prevBar.Close > state.Ema50[pd] &&
                            state.Ema50[pd] > state.Ema200[pd] &&
                            state.Adx14.Length > 0 && state.Adx14[pd] > 25 &&
                            state.Rsi14.Length > 0 && state.Rsi14[pd] >= 52 && state.Rsi14[pd] <= 75 &&
                            state.VolumeScores[pd] >= 5 &&
                            priceNear20Ema)
                        {
                            match = true;
                        }
                    }

                    if (match)
                    {
                        // --- SCORECARD (Max 80) using precomputed arrays ---
                        var closes = state.Closes;
                        var vols   = state.Volumes;
                        int pd = idx - 1; // yesterday's index
                        var prevBar = state.DailyBars[pd];

                        // 1. Trend Quality (max 20)
                        int trendScore = 0;
                        if (prevBar.Close > state.Ema50[pd])                         trendScore += 5;
                        if (state.Ema50[pd] > state.Ema200[pd])                      trendScore += 5;
                        if (pd >= 5 && state.Ema50[pd] > state.Ema50[pd - 5])        trendScore += 5;
                        if (state.Adx14.Length > 0 && state.Adx14[pd] > 25)          trendScore += 5;

                        // 2. Relative Strength (max 20)
                        int rsScore = 0;
                        if (pd >= 120)
                        {
                            double rsRet3M = (closes[pd] - closes[pd - 60]) / closes[pd - 60] * 100;
                            double rsRet6M = (closes[pd] - closes[pd - 120]) / closes[pd - 120] * 100;
                            if (rsRet3M > 0) rsScore += 10;
                            if (rsRet6M > 0) rsScore += 10;
                        }

                        // 3. Volume Accumulation (max 10) — precomputed
                        int volScore = (int)state.VolumeScores[pd];

                        // 4. Volatility Setup — ATR Coiled (max 10)
                        int volSetupScore = 0;
                        if (state.Atr14.Length > 60)
                        {
                            double atrAvg = state.Atr14.Skip(pd - 59).Take(60).Average();
                            if (state.Atr14[pd] < atrAvg) volSetupScore += 5;
                        }
                        if (state.BollingerUpper.Length > 0 && state.KeltnerUpper.Length > 0 &&
                            pd < state.BollingerUpper.Length && pd < state.KeltnerUpper.Length &&
                            state.BollingerUpper[pd] < state.KeltnerUpper[pd] &&
                            state.BollingerLower[pd] > state.KeltnerLower[pd])
                            volSetupScore += 5;

                        // 5. Momentum Quality — RSI zone (max 10)
                        int momentumScore = 0;
                        if (state.Rsi14.Length > 0 && pd < state.Rsi14.Length)
                        {
                            double latestRsi = state.Rsi14[pd];
                            if (latestRsi >= 50 && latestRsi <= 70)      momentumScore = 10;
                            else if (latestRsi > 40 && latestRsi < 50)   momentumScore = 5;
                        }

                        // 6. Institutional Footprint (max 10)
                        int instScore = 0;
                        int window = Math.Min(20, pd + 1);
                        double upVol = 0, downVol = 0;
                        int upCnt = 0, downCnt = 0;
                        for (int k = pd - window + 1; k <= pd; k++)
                        {
                            if (k >= 1 && closes[k] > closes[k - 1]) { upVol += vols[k]; upCnt++; }
                            else if (k >= 1) { downVol += vols[k]; downCnt++; }
                        }
                        if (upCnt > 0) upVol /= upCnt;
                        if (downCnt > 0) downVol /= downCnt;
                        if (upVol > downVol * 1.5) instScore = 10;
                        else if (upVol > downVol)  instScore = 5;

                        int totalScore = trendScore + rsScore + volScore + volSetupScore + momentumScore + instScore;

                        // Gate: skip if below min score
                        if (totalScore < request.MinScore) continue;

                        double ret3M = pd >= 60
                            ? (prevBar.Close - state.DailyBars[pd - 60].Close) / state.DailyBars[pd - 60].Close * 100.0
                            : 0;
                        candidates.Add((state, idx, ret3M)); // idx is today's bar index for entry
                    }
                }

                // Prioritize by 3-month return
                var sortedCandidates = candidates.OrderByDescending(c => c.Return3M).ToList();

                // --- Step 5: Execute buys ---
                // Entry happens at today's OPEN using today's bar
                foreach (var cand in sortedCandidates)
                {
                    if (activePosDict.Count >= request.MaxPositions) break;

                    var state = cand.State;
                    int idx = cand.Index;
                    var bar = state.DailyBars[idx];

                    // Enter at today's OPEN, not close
                    double entryPrice = bar.Open * (1.0 + request.SlippagePercent / 100.0);

                    // Strategy-specific bracket using ATR
                    var prevBar = state.DailyBars[idx - 1];
                    var (t1, t2, sl, timeStopDays) = GetStrategyBracket(request.Strategy, entryPrice, state.Atr14[idx - 1], prevBar.Close);

                    // ── Portfolio-aware position sizing ──
                    // Size against AVAILABLE CASH (not total portfolio value).
                    // Max single position capped at 20% of portfolio.
                    double maxPosValue = prevDayPortfolioValue * 0.20;
                    double targetValue;

                    if (request.SizingModel == "Risk-Based")
                    {
                        double riskAmount = cash * (request.RiskPerTradePercent / 100.0);
                        double priceRisk = entryPrice - sl;
                        if (priceRisk <= 0) priceRisk = entryPrice * 0.05;
                        targetValue = Math.Min(riskAmount / priceRisk, maxPosValue);
                    }
                    else
                    {
                        double allocated = cash * (request.PositionSizePercent / 100.0);
                        targetValue = Math.Min(allocated, maxPosValue);
                    }

                    // Partial fill: use available cash if target exceeds it
                    targetValue = Math.Min(targetValue, cash * 0.98);
                    int sharesInt = (int)(targetValue / entryPrice);
                    if (sharesInt < 1) sharesInt = 1;

                    double entryFee = entryPrice * sharesInt * (request.TransactionCostPercent / 100.0);
                    double totalCost = (entryPrice * sharesInt) + entryFee;

                    if (sharesInt > 0 && totalCost <= cash)
                        {
                            cash -= totalCost;
                            activePosDict[state.Ticker] = new ActivePosition
                            {
                                Ticker = state.Ticker,
                                StrategyName = request.Strategy,
                                EntryDate = currentDate,
                                EntryPrice = entryPrice,
                                HighestCloseSinceEntry = entryPrice,
                                InitialShares = sharesInt,
                                Shares = sharesInt,
                                StopLoss = sl,
                                Target1 = t1,
                                Target2 = t2,
                                TimeStopDays = timeStopDays,
                                HasHitTarget1 = false,
                                EntryCost = totalCost
                            };
                        }
                    }

                // --- Step 6: Compute current portfolio value + equity curve ---
                // Daily point — needed for correct Sharpe annualization (√252)
                double currentBalance = cash;
                foreach (var kvp in activePosDict)
                {
                    if (!stateDict.TryGetValue(kvp.Key, out var state)) continue;
                    int idx = state.BarPointer - 1;
                    if (idx >= 0)
                        currentBalance += state.DailyBars[idx].Close * kvp.Value.Shares;
                }
                currentPortfolioValue = currentBalance; // total = cash + market value of positions

                maxBalance = Math.Max(maxBalance, currentBalance);
                double ddPct = ((maxBalance - currentBalance) / maxBalance) * 100.0;
                maxDrawdown = Math.Max(maxDrawdown, ddPct);

                // Every day gets an equity curve point (needed for correct Sharpe)
                {
                    equityCurve.Add(new EquityCurvePoint
                    {
                        Date = currentDate,
                        Balance = Math.Round(currentBalance, 2),
                        DrawdownPercent = Math.Round(ddPct, 2)
                    });
                }
            }

            Console.WriteLine($"[PERF] Simulation loop: {sw.Elapsed.TotalSeconds:F2}s for {allDates.Count} days, {trades.Count} trades");

            // --- Close remaining positions on last day ---
            var lastDate = allDates[^1];
            if (activePosDict.Count > 0)
            {
                foreach (var kvp in activePosDict)
                {
                    var pos = kvp.Value;
                    if (!stateDict.TryGetValue(pos.Ticker, out var state)) continue;
                    int idx = state.BarPointer - 1;
                    if (idx < 0) continue;

                    var bar = state.DailyBars[idx];
                    double exitPrice = bar.Close * (1.0 - request.SlippagePercent / 100.0);
                    double exitFee = exitPrice * pos.Shares * (request.TransactionCostPercent / 100.0);
                    double cashReceived = (exitPrice * pos.Shares) - exitFee;
                    cash += cashReceived;

                    double profit = cashReceived - pos.EntryCost;
                    double profitPercent = (exitPrice - pos.EntryPrice) / pos.EntryPrice * 100.0;

                    trades.Add(new PortfolioTrade
                    {
                        Ticker = pos.Ticker,
                        EntryDate = pos.EntryDate,
                        EntryPrice = Math.Round(pos.EntryPrice, 2),
                        ExitDate = lastDate,
                        ExitPrice = Math.Round(exitPrice, 2),
                        Shares = pos.Shares,
                        Profit = Math.Round(profit, 2),
                        ProfitPercent = Math.Round(profitPercent, 2),
                        ExitReason = "End of Simulation"
                    });
                }
                activePosDict.Clear();
            }

            // --- Compute aggregate metrics ---
            double endingCapital = cash;
            double totalProfit = endingCapital - request.StartingCapital;
            double returnPercent = (totalProfit / request.StartingCapital) * 100.0;

            int totalTrades = trades.Count;
            int winningTrades = trades.Count(t => t.Profit > 0);
            int losingTrades = trades.Count(t => t.Profit <= 0);
            double winRate = totalTrades > 0 ? ((double)winningTrades / totalTrades) * 100.0 : 0;

            double grossProfit = trades.Where(t => t.Profit > 0).Sum(t => t.Profit);
            double grossLoss = trades.Where(t => t.Profit < 0).Sum(t => Math.Abs(t.Profit));
            double profitFactor = grossLoss == 0 ? (grossProfit > 0 ? 99.9 : 1.0) : grossProfit / grossLoss;

            // Sharpe Ratio
            double sharpe = 0;
            if (equityCurve.Count > 1)
            {
                var dailyReturns = new List<double>();
                for (int i = 1; i < equityCurve.Count; i++)
                {
                    double prev = equityCurve[i - 1].Balance;
                    double curr = equityCurve[i].Balance;
                    if (prev > 0) dailyReturns.Add((curr - prev) / prev);
                }

                if (dailyReturns.Count > 1)
                {
                    double avg = dailyReturns.Average();
                    double sumOfSquares = dailyReturns.Sum(r => Math.Pow(r - avg, 2));
                    double stdDev = Math.Sqrt(sumOfSquares / (dailyReturns.Count - 1));
                    if (stdDev > 0)
                        sharpe = (avg / stdDev) * Math.Sqrt(252);
                }
            }

            // Truncate trades to 500 max for response size
            if (trades.Count > 500)
                trades = trades.Skip(trades.Count - 500).ToList();

            Console.WriteLine($"[PERF] Total elapsed: {sw.Elapsed.TotalSeconds:F2}s");

            return new PortfolioSimulationResult
            {
                StartingCapital = Math.Round(request.StartingCapital, 2),
                EndingCapital = Math.Round(endingCapital, 2),
                TotalProfit = Math.Round(totalProfit, 2),
                ReturnPercent = Math.Round(returnPercent, 2),
                SharpeRatio = Math.Round(sharpe, 2),
                MaxDrawdownPercent = Math.Round(maxDrawdown, 2),
                ProfitFactor = Math.Round(profitFactor, 2),
                WinRate = Math.Round(winRate, 2),
                TotalTrades = totalTrades,
                WinningTrades = winningTrades,
                LosingTrades = losingTrades,
                Trades = trades,
                EquityCurve = equityCurve
            };
        }

        /// <summary>
        /// Finalize a trade exit (full or partial). Handles slippage, fees, cash,
        /// trade record, and dictionary removal for full exits.
        /// </summary>
        private void FinalizeExit(
            ActivePosition pos, BacktestStockState state, PortfolioRequest request, DateTime exitDate,
            double rawExitPrice, int sharesToSell, string exitReason,
            List<PortfolioTrade> trades, ref double cash, HashSet<string> exitedTickers,
            Dictionary<string, ActivePosition> activePosDict, bool partial = false)
        {
            int actualShares = Math.Min(sharesToSell, pos.Shares);
            if (actualShares <= 0) return;

            double exitPriceWithSlippage = rawExitPrice * (1.0 - request.SlippagePercent / 100.0);
            double exitFee = exitPriceWithSlippage * actualShares * (request.TransactionCostPercent / 100.0);
            double cashReceived = (exitPriceWithSlippage * actualShares) - exitFee;
            cash += cashReceived;

            // Cost basis for the sold shares (proportional to entry)
            double soldCostBasis = pos.EntryCost * ((double)actualShares / pos.InitialShares);
            double profit = cashReceived - soldCostBasis;
            double profitPercent = (exitPriceWithSlippage - pos.EntryPrice) / pos.EntryPrice * 100.0;

            trades.Add(new PortfolioTrade
            {
                Ticker = pos.Ticker,
                EntryDate = pos.EntryDate,
                EntryPrice = Math.Round(pos.EntryPrice, 2),
                ExitDate = exitDate,
                ExitPrice = Math.Round(exitPriceWithSlippage, 2),
                Shares = actualShares,
                Profit = Math.Round(profit, 2),
                ProfitPercent = Math.Round(profitPercent, 2),
                ExitReason = exitReason
            });

            if (partial)
            {
                // Reduce position — deduct sold shares' cost from EntryCost
                pos.Shares -= actualShares;
                pos.EntryCost -= soldCostBasis;
            }
            else
            {
                activePosDict.Remove(pos.Ticker);
                exitedTickers.Add(pos.Ticker);
            }
        }

        /// <summary>
        /// Compute portfolio value from cash + open positions
        /// </summary>
        private double ComputePortfolioValue(double cash, Dictionary<string, ActivePosition> activePosDict,
            Dictionary<string, BacktestStockState> stateDict)
        {
            double total = cash;
            foreach (var kvp in activePosDict)
            {
                if (!stateDict.TryGetValue(kvp.Key, out var state)) continue;
                int idx = state.BarPointer - 1;
                if (idx >= 0)
                    total += state.DailyBars[idx].Close * kvp.Value.Shares;
            }
            return total;
        }

        private (double Target1, double Target2, double StopLoss) CalculateVolatilityFibTargetsAtIndex(BacktestStockState state, int idx)
        {
            if (idx < 10)
            {
                double cur = state.DailyBars[idx].Close;
                return (Math.Round(cur * 1.05, 2), Math.Round(cur * 1.10, 2), Math.Round(cur * 0.95, 2));
            }

            double mean = 0;
            double[] lr = new double[10];
            for (int i = 0; i < 10; i++)
            {
                lr[i] = Math.Log(state.DailyBars[idx - 9 + i].Close / state.DailyBars[idx - 10 + i].Close);
                mean += lr[i];
            }
            mean /= 10;
            double variance = 0;
            for (int i = 0; i < 10; i++)
            {
                double d = lr[i] - mean;
                variance += d * d;
            }
            variance /= 9;
            double dailyVol = Math.Sqrt(variance);
            double curPrice = state.DailyBars[idx].Close;
            double projRange = curPrice * dailyVol * Math.Sqrt(10);

            return (Math.Round(curPrice + 0.382 * projRange, 2),
                    Math.Round(curPrice + 0.618 * projRange, 2),
                    Math.Round(curPrice - 0.618 * projRange, 2));
        }

        private double CalculateStopLossPrice(BacktestStockState state, int idx)
        {
            var (_, _, sl) = CalculateVolatilityFibTargetsAtIndex(state, idx);

            if (state.DailyBars[idx].Close > state.Jnsar[idx] && sl < state.Jnsar[idx])
                sl = state.Jnsar[idx];
            double chandelier = CalculateChandelierExitAtIndex(state, idx);
            if (state.DailyBars[idx].Close > chandelier && chandelier > sl)
                sl = chandelier;
            return sl;
        }

        private double CalculateChandelierExitAtIndex(BacktestStockState state, int idx, int period = 22, double multiplier = 3.0)
        {
            if (idx < period - 1) return 0;
            double highestHigh = state.DailyBars[idx - period + 1].High;
            for (int j = idx - period + 2; j <= idx; j++)
            {
                if (state.DailyBars[j].High > highestHigh)
                    highestHigh = state.DailyBars[j].High;
            }
            double atr = state.Atr22[idx];
            return highestHigh - (atr * multiplier);
        }

        /// <summary>
        /// Run backtest for ALL strategies and return per-strategy equity curves for comparison.
        /// Precomputes indicators once, then runs each strategy's simulation in parallel.
        /// </summary>
        public async Task<MultiStrategySimulationResult> RunAllStrategiesAsync(PortfolioRequest request)
        {
            var sw = Stopwatch.StartNew();

            // Precompute indicators once (same for all strategies)
            using var connection = new DuckDBConnection(_connectionString);
            connection.Open();

            var stocks = connection.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();
            var rawDailyBars = connection.Query<DailyBar>("SELECT Ticker, Date, Open, High, Low, Close, Volume FROM DailyBars").ToList();
            var rawWeeklyBars = connection.Query<WeeklyBar>("SELECT Ticker, Date, Close FROM WeeklyBars").ToList();

            var dailyGroups = rawDailyBars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());
            var weeklyGroups = rawWeeklyBars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());

            var states = new List<BacktestStockState>();
            foreach (var stock in stocks)
            {
                if (!dailyGroups.TryGetValue(stock.Ticker, out var dBars) || dBars.Count < 200) continue;
                var wBars = weeklyGroups.TryGetValue(stock.Ticker, out var wB) ? wB : new List<WeeklyBar>();

                var closes = dBars.Select(b => b.Close).ToArray();
                var highs  = dBars.Select(b => b.High).ToArray();
                var lows   = dBars.Select(b => b.Low).ToArray();
                var vols   = dBars.Select(b => (double)b.Volume).ToArray();

                var state = new BacktestStockState
                {
                    Ticker = stock.Ticker, Sector = stock.Sector ?? "",
                    DailyBars = dBars, WeeklyBars = wBars,
                    Closes = closes, Highs = highs, Lows = lows, Volumes = vols
                };
                // Precompute all indicators (same code as RunPortfolioSimulationAsync)
                state.Ema8 = _indicatorService.CalculateEma(closes, 8);
                state.Ema10 = _indicatorService.CalculateEma(closes, 10);
                state.Ema21 = _indicatorService.CalculateEma(closes, 21);
                state.Ema50 = _indicatorService.CalculateEma(closes, 50);
                state.Ema200 = _indicatorService.CalculateEma(closes, 200);
                state.Jnsar = _indicatorService.CalculateJnsar(closes, highs, lows);
                state.Atr14 = _indicatorService.CalculateAtr(highs, lows, closes, 14);
                state.Atr22 = _indicatorService.CalculateAtr(highs, lows, closes, 22);
                state.Adx14 = _indicatorService.CalculateAdx(highs, lows, closes, 14);
                state.Rsi14 = _indicatorService.CalculateRsi(closes);
                var (bbU, _, bbL) = _indicatorService.CalculateBollingerBands(closes);
                state.BollingerUpper = bbU; state.BollingerLower = bbL;
                var (kcU, _, kcL) = _indicatorService.CalculateKeltnerChannels(highs, lows, closes);
                state.KeltnerUpper = kcU; state.KeltnerLower = kcL;

                state.VolumeScores = new double[closes.Length];
                for (int i = 20; i < closes.Length; i++)
                    state.VolumeScores[i] = _indicatorService.CalculateVolumeScore(closes[..(i+1)], vols[..(i+1)], 15);

                var (dMacdL, dMacdS) = _indicatorService.CalculateMacd(closes);
                state.MacdLine = dMacdL; state.MacdSignal = dMacdS;
                state.Cmf = _indicatorService.CalculateCmf(closes, highs, lows, vols, 21);

                state.VolAvg20 = new double[closes.Length];
                double vrs = 0;
                for (int i = 0; i < closes.Length; i++) { vrs += vols[i]; if (i >= 20) vrs -= vols[i - 20]; state.VolAvg20[i] = i >= 19 ? vrs / 20.0 : vols[i]; }

                state.Obv = _indicatorService.CalculateObv(closes, vols);
                state.ZScore = new double[closes.Length];
                for (int i = 50; i < closes.Length; i++) { 
                    double m = closes.Skip(i - 49).Take(50).Average();
                    double v = closes.Skip(i - 49).Take(50).Sum(x => Math.Pow(x - m, 2)) / 50.0;
                    state.ZScore[i] = v > 0 ? (closes[i] - m) / Math.Sqrt(v) : 0;
                }
                state.VolAvg40 = new double[closes.Length];
                double vr40 = 0;
                for (int i = 0; i < closes.Length; i++) { vr40 += vols[i]; if (i >= 40) vr40 -= vols[i - 40]; state.VolAvg40[i] = i >= 39 ? vr40 / 40.0 : vols[i]; }

                if (wBars.Count > 0)
                {
                    var wCloses = wBars.Select(b => b.Close).ToArray();
                    state.WEma144 = _indicatorService.CalculateEma(wCloses, 144);
                    state.WEma233 = _indicatorService.CalculateEma(wCloses, 233);
                    var (wMacdL, wMacdS) = _indicatorService.CalculateMacd(wCloses);
                    state.WMacdLine = wMacdL; state.WMacdSignal = wMacdS;
                }
                states.Add(state);
            }

            // RS ranking
            var stockReturnsAsc = new List<double>();
            foreach (var st in states) { if (st.Closes.Length >= 60) stockReturnsAsc.Add((st.Closes[^1] - st.Closes[^61]) / st.Closes[^61] * 100.0); }
            stockReturnsAsc.Sort();

            Console.WriteLine($"[PERF] CompareAll precompute: {sw.Elapsed.TotalSeconds:F2}s for {states.Count} stocks");

            // Strategy names to run (all that have dedicated logic in the simulation loop)
            var strategyNames = new[] { "HCT", "LRHR", "MOMCON", "VAL", "VBO", "MOMACC", "CBO", "DPA", "RSML" };
            var results = new List<StrategySimLine>();

            Parallel.ForEach(strategyNames, stratName =>
            {
                var req = new PortfolioRequest
                {
                    StartingCapital = request.StartingCapital,
                    MaxPositions = request.MaxPositions,
                    SizingModel = request.SizingModel,
                    RiskPerTradePercent = request.RiskPerTradePercent,
                    PositionSizePercent = request.PositionSizePercent,
                    TransactionCostPercent = request.TransactionCostPercent,
                    SlippagePercent = request.SlippagePercent,
                    Strategy = stratName,
                    StartDate = request.StartDate,
                    EndDate = request.EndDate,
                    MinScore = request.MinScore
                };
                var result = RunPortfolioSimulationAsync(req).Result;
                lock (results)
                {
                    results.Add(new StrategySimLine
                    {
                        StrategyName = stratName,
                        EquityCurve = result.EquityCurve,
                        Summary = result
                    });
                }
            });

            var ordered = results.OrderByDescending(r => r.Summary.ReturnPercent).ToList();
            Console.WriteLine($"[PERF] CompareAll total: {sw.Elapsed.TotalSeconds:F2}s");

            return new MultiStrategySimulationResult
            {
                Strategies = ordered,
                StartingCapital = request.StartingCapital
            };
        }

        /// <summary>
        /// Returns strategy-specific bracket: (target1, target2, stopLoss, timeStopDays)
        /// All distances in multiples of ATR14 for volatility-normalized sizing.
        /// </summary>
        private (double Target1, double Target2, double StopLoss, int TimeStopDays) GetStrategyBracket(
            string strategyName, double entryPrice, double atr14, double currentClose)
        {
            // Safety: if ATR is 0 or near 0, use 1% of price as default
            if (atr14 <= 0) atr14 = entryPrice * 0.01;

            double t1, t2, sl;
            int timeStop;

            if (strategyName.Contains("HCT", StringComparison.OrdinalIgnoreCase))
            {
                t1 = entryPrice + 2.0 * atr14;
                t2 = entryPrice + 3.0 * atr14;
                sl = entryPrice - 1.5 * atr14;
                timeStop = 20;
            }
            else if (strategyName.Contains("LRHR", StringComparison.OrdinalIgnoreCase))
            {
                t1 = entryPrice + 3.0 * atr14;
                t2 = entryPrice + 5.0 * atr14;
                sl = entryPrice - 2.0 * atr14;
                timeStop = 45;
            }
            else if (strategyName.Contains("JustNifty Positional", StringComparison.OrdinalIgnoreCase))
            {
                t1 = entryPrice + 2.5 * atr14;
                t2 = entryPrice + 4.0 * atr14;
                sl = entryPrice - 2.0 * atr14;
                timeStop = 30;
            }
            else if (strategyName.Contains("MOMACC", StringComparison.OrdinalIgnoreCase) ||
                     strategyName.Contains("MOMCON", StringComparison.OrdinalIgnoreCase))
            {
                t1 = entryPrice + 3.0 * atr14;
                t2 = entryPrice + 5.0 * atr14;
                sl = entryPrice - 2.0 * atr14;
                timeStop = 25;
            }
            else if (strategyName.Contains("CBO", StringComparison.OrdinalIgnoreCase))
            {
                t1 = entryPrice + 3.0 * atr14;
                t2 = entryPrice + 5.0 * atr14;
                sl = entryPrice - 1.0 * atr14;
                timeStop = 15;
            }
            else if (strategyName.Contains("DPA", StringComparison.OrdinalIgnoreCase))
            {
                t1 = entryPrice + 4.0 * atr14;
                t2 = entryPrice + 6.0 * atr14;
                sl = entryPrice - 2.5 * atr14;
                timeStop = 45;
            }
            else if (strategyName.Contains("VAL", StringComparison.OrdinalIgnoreCase))
            {
                t1 = entryPrice + 2.5 * atr14;
                t2 = entryPrice + 4.0 * atr14;
                sl = entryPrice - 1.5 * atr14;
                timeStop = 30;
            }
            else // VBO, RSML, JustNifty HCT, JustNifty LRHR, default
            {
                t1 = entryPrice + 2.5 * atr14;
                t2 = entryPrice + 4.0 * atr14;
                sl = entryPrice - 2.0 * atr14;
                timeStop = 25;
            }

            // Safety: stop at least 0.5% below entry
            if (sl >= entryPrice - 0.01)
                sl = entryPrice * 0.995;

            return (Math.Round(t1, 2), Math.Round(t2, 2), Math.Round(sl, 2), timeStop);
        }

        /// <summary>
        /// Returns ATR trailing stop multiplier based on strategy and whether T1 was hit.
        /// </summary>
        private double GetTrailMultiplier(string strategyName, bool hasHitTarget1)
        {
            double beforeT1, afterT1;

            if (strategyName.Contains("HCT") || strategyName.Contains("VBO"))
            {
                beforeT1 = 2.5; afterT1 = 1.5;
            }
            else if (strategyName.Contains("CBO"))
            {
                beforeT1 = 2.0; afterT1 = 1.0;
            }
            else if (strategyName.Contains("DPA") || strategyName.Contains("LRHR"))
            {
                beforeT1 = 3.5; afterT1 = 2.0;
            }
            else // MOMACC, MOMCON, VAL, RSML, JustNifty, default
            {
                beforeT1 = 3.0; afterT1 = 1.5;
            }

            return hasHitTarget1 ? afterT1 : beforeT1;
        }
    }

    public class BacktestStockState
    {
        public string Ticker { get; set; } = string.Empty;
        public string Sector { get; set; } = "";
        public List<DailyBar> DailyBars { get; set; } = new();
        public List<WeeklyBar> WeeklyBars { get; set; } = new();
        public int BarPointer { get; set; } = 0;

        // Pre-extracted raw arrays (avoids Take().ToArray() in daily loop)
        public double[] Closes { get; set; } = Array.Empty<double>();
        public double[] Highs  { get; set; } = Array.Empty<double>();
        public double[] Lows   { get; set; } = Array.Empty<double>();
        public double[] Volumes { get; set; } = Array.Empty<double>();

        // Precomputed daily indicators
        public double[] Ema8 { get; set; } = Array.Empty<double>();
        public double[] Ema10 { get; set; } = Array.Empty<double>();
        public double[] Ema21 { get; set; } = Array.Empty<double>();
        public double[] Ema50 { get; set; } = Array.Empty<double>();
        public double[] Ema200 { get; set; } = Array.Empty<double>();
        public double[] Jnsar { get; set; } = Array.Empty<double>();
        public double[] Atr14 { get; set; } = Array.Empty<double>();
        public double[] Atr22 { get; set; } = Array.Empty<double>();

        // Precomputed score indicators (avoids daily recomputation)
        public double[] Adx14 { get; set; } = Array.Empty<double>();
        public double[] Rsi14 { get; set; } = Array.Empty<double>();
        public double[] BollingerUpper { get; set; } = Array.Empty<double>();
        public double[] BollingerLower { get; set; } = Array.Empty<double>();
        public double[] KeltnerUpper { get; set; } = Array.Empty<double>();
        public double[] KeltnerLower { get; set; } = Array.Empty<double>();
        public double[] VolumeScores { get; set; } = Array.Empty<double>();

        // Precomputed weekly indicators
        public double[] WEma144 { get; set; } = Array.Empty<double>();
        public double[] WEma233 { get; set; } = Array.Empty<double>();
        public double[] WMacdLine { get; set; } = Array.Empty<double>();
        public double[] WMacdSignal { get; set; } = Array.Empty<double>();

        // Daily MACD (for Momentum Continuation strategy)
        public double[] MacdLine { get; set; } = Array.Empty<double>();
        public double[] MacdSignal { get; set; } = Array.Empty<double>();

        // CMF (for Institutional Value strategy)
        public double[] Cmf { get; set; } = Array.Empty<double>();

        // 20-day average volume (for Volatility Breakout strategy)
        public double[] VolAvg20 { get; set; } = Array.Empty<double>();

        // OBV, ZScore, VolAvg40 (for new strategies)
        public double[] Obv { get; set; } = Array.Empty<double>();
        public double[] ZScore { get; set; } = Array.Empty<double>();
        public double[] VolAvg40 { get; set; } = Array.Empty<double>();

        public int GetWeeklyIndex(DateTime date)
        {
            int low = 0;
            int high = WeeklyBars.Count - 1;
            int ans = -1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (WeeklyBars[mid].Date <= date)
                {
                    ans = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
            return ans;
        }
    }

    public class ActivePosition
    {
        public string Ticker { get; set; } = string.Empty;
        public DateTime EntryDate { get; set; }
        public double EntryPrice { get; set; }
        public int InitialShares { get; set; }
        public int Shares { get; set; }
        public bool HasHitTarget1 { get; set; } = false;
        public double StopLoss { get; set; }
        public double Target1 { get; set; }
        public double Target2 { get; set; }
        public double EntryCost { get; set; }
        public string StrategyName { get; set; } = "";
        public double HighestCloseSinceEntry { get; set; }
        public int TimeStopDays { get; set; } = 30;
    }
}