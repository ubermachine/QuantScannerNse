using System;
using System.Collections.Generic;
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

        public BacktestService(IndicatorService _indicatorService)
        {
            this._indicatorService = _indicatorService;
        }

        public async Task<PortfolioSimulationResult> RunPortfolioSimulationAsync(PortfolioRequest request)
        {
            using var connection = new DuckDBConnection(_connectionString);
            connection.Open();

            // Fetch metadata and daily/weekly bars from DuckDB (strongly-typed models to conform to project rules)
            var stocks = connection.Query<StockMetadata>("SELECT * FROM StockMetadatas").ToList();
            var rawDailyBars = connection.Query<DailyBar>("SELECT Ticker, Date, Open, High, Low, Close, Volume FROM DailyBars").ToList();
            var rawWeeklyBars = connection.Query<WeeklyBar>("SELECT Ticker, Date, Close FROM WeeklyBars").ToList();

            // Group bars in memory for fast lookup
            var dailyGroups = rawDailyBars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());
            var weeklyGroups = rawWeeklyBars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());

            var states = new List<BacktestStockState>();
            foreach (var stock in stocks)
            {
                if (!dailyGroups.TryGetValue(stock.Ticker, out var dBars) || dBars.Count < 200)
                    continue;

                var wBars = weeklyGroups.TryGetValue(stock.Ticker, out var wB) ? wB : new List<WeeklyBar>();

                var state = new BacktestStockState
                {
                    Ticker = stock.Ticker,
                    DailyBars = dBars,
                    WeeklyBars = wBars
                };

                states.Add(state);
            }

            // Precompute indicators across all stocks in parallel (highly optimized CPU usage)
            Parallel.ForEach(states, state =>
            {
                var closes = state.DailyBars.Select(b => b.Close).ToArray();
                var highs = state.DailyBars.Select(b => b.High).ToArray();
                var lows = state.DailyBars.Select(b => b.Low).ToArray();

                state.Ema8 = _indicatorService.CalculateEma(closes, 8);
                state.Ema10 = _indicatorService.CalculateEma(closes, 10);
                state.Ema21 = _indicatorService.CalculateEma(closes, 21);
                state.Ema50 = _indicatorService.CalculateEma(closes, 50);
                state.Ema200 = _indicatorService.CalculateEma(closes, 200);
                state.Jnsar = _indicatorService.CalculateJnsar(closes, highs, lows);
                state.Atr14 = _indicatorService.CalculateAtr(highs, lows, closes, 14);
                state.Atr22 = _indicatorService.CalculateAtr(highs, lows, closes, 22);

                if (state.WeeklyBars.Count > 0)
                {
                    var wCloses = state.WeeklyBars.Select(b => b.Close).ToArray();
                    state.WEma144 = _indicatorService.CalculateEma(wCloses, 144);
                    state.WEma233 = _indicatorService.CalculateEma(wCloses, 233);
                    var (macdLine, signalLine) = _indicatorService.CalculateMacd(wCloses);
                    state.WMacdLine = macdLine;
                    state.WMacdSignal = signalLine;
                }
            });

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
            var activePositions = new List<ActivePosition>();
            var trades = new List<PortfolioTrade>();
            var equityCurve = new List<EquityCurvePoint>();

            // Initialize pointers for chronological matching
            foreach (var state in states)
            {
                state.BarPointer = 0;
            }

            foreach (var currentDate in allDates)
            {
                // Advance pointers to the current date
                foreach (var state in states)
                {
                    while (state.BarPointer < state.DailyBars.Count && state.DailyBars[state.BarPointer].Date <= currentDate)
                    {
                        state.BarPointer++;
                    }
                }

                // 1. Check exits for active positions
                var exitedTickers = new HashSet<string>();
                var positionsToKeep = new List<ActivePosition>();

                foreach (var pos in activePositions)
                {
                    var state = states.First(s => s.Ticker == pos.Ticker);
                    int idx = state.BarPointer - 1;
                    if (idx < 0)
                    {
                        positionsToKeep.Add(pos);
                        continue;
                    }

                    var bar = state.DailyBars[idx];

                    // Check exit only on actual trading days for the stock
                    if (bar.Date == currentDate)
                    {
                        bool exitTriggered = false;
                        double exitPrice = 0;
                        string exitReason = "";

                        // Stop Loss first (conservative)
                        if (bar.Low <= pos.StopLoss)
                        {
                            exitTriggered = true;
                            exitPrice = Math.Min(pos.StopLoss, bar.Open);
                            exitReason = "Stop Loss";
                        }
                        // Target 1 next
                        else if (bar.High >= pos.Target1)
                        {
                            exitTriggered = true;
                            exitPrice = Math.Max(pos.Target1, bar.Open);
                            exitReason = "Target 1";
                        }

                        if (exitTriggered)
                        {
                            double exitPriceWithSlippage = exitPrice * (1.0 - request.SlippagePercent / 100.0);
                            double exitFee = exitPriceWithSlippage * pos.Shares * (request.TransactionCostPercent / 100.0);
                            double cashReceived = (exitPriceWithSlippage * pos.Shares) - exitFee;

                            cash += cashReceived;

                            double profit = cashReceived - pos.EntryCost;
                            double profitPercent = (exitPriceWithSlippage - pos.EntryPrice) / pos.EntryPrice * 100.0;

                            trades.Add(new PortfolioTrade
                            {
                                Ticker = pos.Ticker,
                                EntryDate = pos.EntryDate,
                                EntryPrice = Math.Round(pos.EntryPrice, 2),
                                ExitDate = currentDate,
                                ExitPrice = Math.Round(exitPriceWithSlippage, 2),
                                Shares = pos.Shares,
                                Profit = Math.Round(profit, 2),
                                ProfitPercent = Math.Round(profitPercent, 2),
                                ExitReason = exitReason
                            });

                            exitedTickers.Add(pos.Ticker);
                        }
                        else
                        {
                            positionsToKeep.Add(pos);
                        }
                    }
                    else
                    {
                        positionsToKeep.Add(pos);
                    }
                }

                activePositions = positionsToKeep;

                // 2. Update trailing stop loss for remaining active positions
                foreach (var pos in activePositions)
                {
                    var state = states.First(s => s.Ticker == pos.Ticker);
                    int idx = state.BarPointer - 1;
                    if (idx < 0) continue;

                    var bar = state.DailyBars[idx];
                    if (bar.Date == currentDate)
                    {
                        double newStopLoss = CalculateStopLossPrice(state, idx);
                        pos.StopLoss = Math.Max(pos.StopLoss, newStopLoss);
                    }
                }

                // Compute current portfolio equity (cash + open positions) for sizing
                double currentActiveValue = 0;
                foreach (var pos in activePositions)
                {
                    var state = states.First(s => s.Ticker == pos.Ticker);
                    int idx = state.BarPointer - 1;
                    if (idx >= 0)
                    {
                        currentActiveValue += state.DailyBars[idx].Close * pos.Shares;
                    }
                }
                double currentPortfolioValue = cash + currentActiveValue;

                // 3. Evaluate Buy Signals on current date
                var candidates = new List<(BacktestStockState State, int Index, double Return3M)>();

                foreach (var state in states)
                {
                    if (activePositions.Any(p => p.Ticker == state.Ticker) || exitedTickers.Contains(state.Ticker))
                        continue;

                    int idx = state.BarPointer - 1;
                    if (idx < 200) continue;

                    var bar = state.DailyBars[idx];
                    if (bar.Date == currentDate)
                    {
                        bool match = false;

                        // Trend-following strategy (HCT)
                        bool runHct = request.Strategy == "All" || request.Strategy.Contains("HCT", StringComparison.OrdinalIgnoreCase) || request.Strategy == "Both";
                        if (runHct)
                        {
                            if (bar.Close > state.Ema200[idx] &&
                                state.Ema8[idx] > state.Ema21[idx] &&
                                bar.Close > state.Ema10[idx] &&
                                bar.Close > state.Jnsar[idx])
                            {
                                double fib618 = _indicatorService.CalculateFib618(
                                    state.DailyBars.Take(idx + 1).Select(b => b.Close).ToArray(),
                                    state.DailyBars.Take(idx + 1).Select(b => b.High).ToArray(),
                                    state.DailyBars.Take(idx + 1).Select(b => b.Low).ToArray(),
                                    out _, out _
                                );

                                if (Math.Abs((bar.Close - fib618) / fib618) <= 0.02)
                                {
                                    match = true;
                                }
                            }
                        }

                        // Weekly swing reversal strategy (LRHR)
                        bool runLrhr = request.Strategy == "All" || request.Strategy.Contains("LRHR", StringComparison.OrdinalIgnoreCase) || request.Strategy == "Both";
                        if (!match && runLrhr)
                        {
                            double max52WHigh = state.DailyBars.Skip(Math.Max(0, idx - 249)).Take(Math.Min(idx + 1, 250)).Max(b => b.High);
                            double discount52W = (max52WHigh - bar.Close) / max52WHigh;

                            if (discount52W >= 0.30)
                            {
                                int w = state.GetWeeklyIndex(currentDate);
                                if (w >= 0 && w < state.WMacdLine.Length)
                                {
                                    if (state.WMacdLine[w] > state.WMacdSignal[w] &&
                                        (Math.Abs((bar.Close - state.WEma144[w]) / state.WEma144[w]) <= 0.05 ||
                                         Math.Abs((bar.Close - state.WEma233[w]) / state.WEma233[w]) <= 0.05))
                                    {
                                        match = true;
                                    }
                                }
                            }
                        }

                        if (match)
                        {
                            double return3M = (bar.Close - state.DailyBars[idx - 60].Close) / state.DailyBars[idx - 60].Close * 100.0;
                            candidates.Add((state, idx, return3M));
                        }
                    }
                }

                // Prioritize candidate signals by 3-Month Return (Relative Strength)
                var sortedCandidates = candidates.OrderByDescending(c => c.Return3M).ToList();

                // 4. Execute buys for prioritized candidate list
                foreach (var cand in sortedCandidates)
                {
                    if (activePositions.Count >= request.MaxPositions)
                        break;

                    var state = cand.State;
                    int idx = cand.Index;
                    var bar = state.DailyBars[idx];

                    var (t1, t2, sl) = CalculateVolatilityFibTargetsAtIndex(state, idx);

                    // Adjust initial stop loss to JNSAR and Chandelier Exit
                    if (bar.Close > state.Jnsar[idx] && sl < state.Jnsar[idx])
                    {
                        sl = state.Jnsar[idx];
                    }
                    double chandelier = CalculateChandelierExitAtIndex(state, idx);
                    if (bar.Close > chandelier && chandelier > sl)
                    {
                        sl = chandelier;
                    }

                    if (sl >= bar.Close - 0.01)
                    {
                        sl = bar.Close * 0.95;
                    }

                    double entryPrice = bar.Close * (1.0 + request.SlippagePercent / 100.0);
                    double shares = 0;

                    if (request.SizingModel == "Risk-Based")
                    {
                        double riskAmount = currentPortfolioValue * (request.RiskPerTradePercent / 100.0);
                        double priceRisk = entryPrice - sl;
                        if (priceRisk <= 0) priceRisk = entryPrice * 0.05;
                        shares = Math.Min(riskAmount / priceRisk, cash / entryPrice);
                    }
                    else // Equal sizing
                    {
                        double allocated = currentPortfolioValue * (request.PositionSizePercent / 100.0);
                        shares = Math.Min(allocated / entryPrice, cash / entryPrice);
                    }

                    int sharesInt = (int)shares;
                    if (sharesInt > 0)
                    {
                        double entryFee = entryPrice * sharesInt * (request.TransactionCostPercent / 100.0);
                        double totalCost = (entryPrice * sharesInt) + entryFee;

                        if (totalCost <= cash)
                        {
                            cash -= totalCost;
                            activePositions.Add(new ActivePosition
                            {
                                Ticker = state.Ticker,
                                EntryDate = currentDate,
                                EntryPrice = entryPrice,
                                Shares = sharesInt,
                                StopLoss = sl,
                                Target1 = t1,
                                Target2 = t2,
                                EntryCost = totalCost
                            });
                        }
                    }
                }

                // 5. Daily valuation
                double endActiveValue = 0;
                foreach (var pos in activePositions)
                {
                    var state = states.First(s => s.Ticker == pos.Ticker);
                    int idx = state.BarPointer - 1;
                    if (idx >= 0)
                    {
                        endActiveValue += state.DailyBars[idx].Close * pos.Shares;
                    }
                }
                double balance = cash + endActiveValue;
                maxBalance = Math.Max(maxBalance, balance);
                double drawdownPercent = ((maxBalance - balance) / maxBalance) * 100.0;
                maxDrawdown = Math.Max(maxDrawdown, drawdownPercent);

                equityCurve.Add(new EquityCurvePoint
                {
                    Date = currentDate,
                    Balance = Math.Round(balance, 2),
                    DrawdownPercent = Math.Round(drawdownPercent, 2)
                });
            }

            // Close remaining positions on the last day of the simulation
            if (activePositions.Count > 0 && allDates.Count > 0)
            {
                var lastDate = allDates[^1];
                foreach (var pos in activePositions)
                {
                    var state = states.First(s => s.Ticker == pos.Ticker);
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
                activePositions.Clear();
            }

            // Compute aggregate metrics
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

            // Calculate Sharpe Ratio from daily returns
            double sharpe = 0;
            if (equityCurve.Count > 1)
            {
                var dailyReturns = new List<double>();
                for (int i = 1; i < equityCurve.Count; i++)
                {
                    double prev = equityCurve[i - 1].Balance;
                    double curr = equityCurve[i].Balance;
                    if (prev > 0)
                    {
                        dailyReturns.Add((curr - prev) / prev);
                    }
                }

                if (dailyReturns.Count > 1)
                {
                    double avg = dailyReturns.Average();
                    double sumOfSquares = dailyReturns.Sum(r => Math.Pow(r - avg, 2));
                    double stdDev = Math.Sqrt(sumOfSquares / (dailyReturns.Count - 1));
                    if (stdDev > 0)
                    {
                        sharpe = (avg / stdDev) * Math.Sqrt(252); // Annualized
                    }
                }
            }

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

        private (double Target1, double Target2, double StopLoss) CalculateVolatilityFibTargetsAtIndex(BacktestStockState state, int idx)
        {
            if (idx < 10)
            {
                double cur = state.DailyBars[idx].Close;
                return (Math.Round(cur * 1.05, 2),
                        Math.Round(cur * 1.10, 2),
                        Math.Round(cur * 0.95, 2));
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
            {
                sl = state.Jnsar[idx];
            }
            double chandelier = CalculateChandelierExitAtIndex(state, idx);
            if (state.DailyBars[idx].Close > chandelier && chandelier > sl)
            {
                sl = chandelier;
            }
            return sl;
        }

        private double CalculateChandelierExitAtIndex(BacktestStockState state, int idx, int period = 22, double multiplier = 3.0)
        {
            if (idx < period - 1) return 0;
            double highestHigh = state.DailyBars[idx - period + 1].High;
            for (int j = idx - period + 2; j <= idx; j++)
            {
                if (state.DailyBars[j].High > highestHigh)
                {
                    highestHigh = state.DailyBars[j].High;
                }
            }
            double atr = state.Atr22[idx];
            return highestHigh - (atr * multiplier);
        }
    }

    public class BacktestStockState
    {
        public string Ticker { get; set; } = string.Empty;
        public List<DailyBar> DailyBars { get; set; } = new();
        public List<WeeklyBar> WeeklyBars { get; set; } = new();
        public int BarPointer { get; set; } = 0;

        // Precomputed daily indicators
        public double[] Ema8 { get; set; } = Array.Empty<double>();
        public double[] Ema10 { get; set; } = Array.Empty<double>();
        public double[] Ema21 { get; set; } = Array.Empty<double>();
        public double[] Ema50 { get; set; } = Array.Empty<double>();
        public double[] Ema200 { get; set; } = Array.Empty<double>();
        public double[] Jnsar { get; set; } = Array.Empty<double>();
        public double[] Atr14 { get; set; } = Array.Empty<double>();
        public double[] Atr22 { get; set; } = Array.Empty<double>();

        // Precomputed weekly indicators
        public double[] WEma144 { get; set; } = Array.Empty<double>();
        public double[] WEma233 { get; set; } = Array.Empty<double>();
        public double[] WMacdLine { get; set; } = Array.Empty<double>();
        public double[] WMacdSignal { get; set; } = Array.Empty<double>();

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
        public int Shares { get; set; }
        public double StopLoss { get; set; }
        public double Target1 { get; set; }
        public double Target2 { get; set; }
        public double EntryCost { get; set; }
    }
}
