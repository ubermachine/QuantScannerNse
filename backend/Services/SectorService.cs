using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using backend.Models;
using DuckDB.NET.Data;
using Dapper;

namespace backend.Services
{
    public class SectorService
    {
        private readonly string _connectionString = "Data Source=quantscanner.duckdb";
        private readonly YahooFinanceService _yahooFinance;
        private readonly IndicatorService _indicator;

        // NSE sector indices available on Yahoo Finance
        private static readonly Dictionary<string, string> SectorIndices = new()
        {
            ["^NSEI"] = "Nifty 50",
            ["^NSEBANK"] = "Nifty Bank",
            ["^CNXAUTO"] = "Nifty Auto",
            ["^CNXIT"] = "Nifty IT",
            ["^CNXPHARMA"] = "Nifty Pharma",
            ["^CNXMETAL"] = "Nifty Metal",
            ["^CNXENERGY"] = "Nifty Energy",
            ["^CNXFMCG"] = "Nifty FMCG",
            ["^CNXMEDIA"] = "Nifty Media",
            ["^CNXREALTY"] = "Nifty Realty",
            ["^CNXPSUBANK"] = "Nifty PSU Bank",
            ["^CNXFINANCE"] = "Nifty Financial Services",
            ["^CNXCONSUMER"] = "Nifty Consumer Durables",
            ["^CNXINFRA"] = "Nifty Infrastructure",
            ["^CNXCOMMODITIES"] = "Nifty Commodities",
            ["^CNXOILGAS"] = "Nifty Oil & Gas"
        };

        public SectorService(YahooFinanceService yahooFinance, IndicatorService indicator)
        {
            _yahooFinance = yahooFinance;
            _indicator = indicator;
        }

        /// <summary>
        /// Sync all sector index daily data from Yahoo Finance into SectorDailyBars.
        /// </summary>
        public async Task<int> SyncSectorDataAsync()
        {
            int total = 0;
            using var conn = new DuckDBConnection(_connectionString);
            conn.Open();

            // Ensure table exists
            conn.Execute(@"CREATE TABLE IF NOT EXISTS SectorDailyBars (
                Ticker VARCHAR, Date TIMESTAMP, Open DOUBLE, High DOUBLE, Low DOUBLE, Close DOUBLE, Volume BIGINT,
                PRIMARY KEY (Ticker, Date))");

            // Fetch and store sector data sequentially
            foreach (var kvp in SectorIndices)
            {
                var (dailyBars, _) = await _yahooFinance.FetchHistoricalDataAsync(kvp.Key, 500);
                if (dailyBars.Count == 0) continue;

                using var appender = conn.CreateAppender("SectorDailyBars");
                foreach (var bar in dailyBars)
                {
                    var row = appender.CreateRow();
                    row.AppendValue(kvp.Key);
                    row.AppendValue(bar.Date);
                    row.AppendValue(bar.Open);
                    row.AppendValue(bar.High);
                    row.AppendValue(bar.Low);
                    row.AppendValue(bar.Close);
                    row.AppendValue(bar.Volume);
                    row.EndRow();
                }
                appender.Close();
                total += dailyBars.Count;
            }

            return total;
        }

        /// <summary>
        /// Compute RRG metrics for all sectors and detect rotation.
        /// </summary>
        public SectorRotationResult GetSectorRotation()
        {
            using var conn = new DuckDBConnection(_connectionString);
            conn.Open();

            var allBars = conn.Query<DailyBar>("SELECT Ticker, Date, Close FROM SectorDailyBars ORDER BY Ticker, Date").ToList();
            if (allBars.Count == 0)
                return new SectorRotationResult { LastUpdated = DateTime.UtcNow };

            var grouped = allBars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());
            if (!grouped.TryGetValue("^NSEI", out var niftyBars) || niftyBars.Count < 50)
                return new SectorRotationResult { LastUpdated = DateTime.UtcNow };

            var niftyCloses = niftyBars.Select(b => b.Close).ToArray();
            var niftyDates = niftyBars.Select(b => b.Date).ToList();
            int histDays = Math.Min(60, niftyCloses.Length);

            var results = new List<SectorRRGPoint>();
            var suggestions = new List<RotationSuggestion>();

            foreach (var kvp in grouped)
            {
                if (kvp.Key == "^NSEI") continue;
                var bars = kvp.Value;
                if (bars.Count < 50) continue;

                var closes = bars.Select(b => b.Close).ToArray();
                var dates = bars.Select(b => b.Date).ToList();
                int minLen = Math.Min(closes.Length, niftyCloses.Length);

                // RS for every aligned day
                double[] rs = new double[minLen];
                for (int i = 0; i < minLen; i++)
                    rs[i] = closes[closes.Length - minLen + i] / niftyCloses[niftyCloses.Length - minLen + i];

                double[] rsSt = _indicator.CalculateEma(rs, 10);
                double[] rsLt = _indicator.CalculateEma(rs, 40);
                double[] mom = new double[rsSt.Length];
                for (int i = 0; i < rsSt.Length; i++) mom[i] = rsSt[i] - rsLt[i];

                int lookback = Math.Min(250, rsSt.Length);
                int last = rsSt.Length - 1;

                // Current z-scores
                var stSlice = rsSt.Skip(last - lookback + 1).Take(lookback).ToArray();
                double stMean = stSlice.Average();
                double stStd = Math.Sqrt(stSlice.Sum(v => Math.Pow(v - stMean, 2)) / lookback);
                double rsRatioZ = stStd > 0 ? (rsSt[last] - stMean) / stStd : 0;

                var momSlice = mom.Skip(last - lookback + 1).Take(lookback).ToArray();
                double momMean = momSlice.Average();
                double momStd = Math.Sqrt(momSlice.Sum(v => Math.Pow(v - momMean, 2)) / lookback);
                double rsMomZ = momStd > 0 ? (mom[last] - momMean) / momStd : 0;

                // Build history: compute quadrant for each of the last histDays
                var history = new List<QuadrantSnapshot>();
                string prevQ = "";
                int histStart = Math.Max(0, last - histDays);
                for (int h = histStart; h <= last; h++)
                {
                    double hRatio = 0, hMom = 0;
                    if (h >= lookback)
                    {
                        var hSt = rsSt.Skip(h - lookback + 1).Take(lookback).ToArray();
                        double hStM = hSt.Average();
                        double hStS = Math.Sqrt(hSt.Sum(v => Math.Pow(v - hStM, 2)) / lookback);
                        hRatio = hStS > 0 ? (rsSt[h] - hStM) / hStS : 0;

                        var hMomA = mom.Skip(h - lookback + 1).Take(lookback).ToArray();
                        double hMomM = hMomA.Average();
                        double hMomS = Math.Sqrt(hMomA.Sum(v => Math.Pow(v - hMomM, 2)) / lookback);
                        hMom = hMomS > 0 ? (mom[h] - hMomM) / hMomS : 0;
                    }

                    string q = (!(hRatio > 0) || hMom <= 0) ? (!(hRatio > 0) || hMom > 0 ? (!(hRatio <= 0) ? "Leading" : (!(hMom <= 0) ? "Lagging" : "Improving")) : "Weakening") : "Leading";
                    if (hRatio > 0 && hMom > 0) q = "Leading";
                    else if (hRatio > 0 && hMom <= 0) q = "Weakening";
                    else if (hRatio <= 0 && hMom <= 0) q = "Lagging";
                    else q = "Improving";

                    int dateIdx = dates.Count - (last - h) - 1;
                    if (dateIdx >= 0 && dateIdx < dates.Count)
                    {
                        history.Add(new QuadrantSnapshot { Date = dates[dateIdx], Quadrant = q });
                    }

                    prevQ = q;
                }

                // Detect recent transitions from the history
                string recentQ = history.Count > 0 ? history[^1].Quadrant : "";
                string beforeRecent = history.Count > 1 ? history[^2].Quadrant : "";
                bool isNewImproving = beforeRecent == "Lagging" && recentQ == "Improving";
                bool isNewWeakening = beforeRecent == "Leading" && recentQ == "Weakening";

                // Generate suggestion if meaningful transition detected
                if (isNewImproving)
                {
                    suggestions.Add(new RotationSuggestion
                    {
                        Sector = SectorIndices.TryGetValue(kvp.Key, out var n) ? n : kvp.Key,
                        Action = "BUY",
                        From = "Lagging",
                        To = "Improving",
                        DaysSinceChange = 1,
                        Reason = "Started turning around — watch for confirmation"
                    });
                }
                else if (recentQ == "Leading")
                {
                    // Check how long it's been Leading
                    int leadDays = 0;
                    for (int i = history.Count - 1; i >= 0 && history[i].Quadrant == "Leading"; i--)
                        leadDays++;

                    if (leadDays == 1 && beforeRecent == "Improving")
                    {
                        suggestions.Add(new RotationSuggestion
                        {
                            Sector = SectorIndices.TryGetValue(kvp.Key, out var n) ? n : kvp.Key,
                            Action = "BUY",
                            From = "Improving",
                            To = "Leading",
                            DaysSinceChange = 1,
                            Reason = "Confirmed new leader — rotated from Improving to Leading"
                        });
                    }
                }
                else if (recentQ == "Weakening")
                {
                    if (beforeRecent == "Leading")
                    {
                        suggestions.Add(new RotationSuggestion
                        {
                            Sector = SectorIndices.TryGetValue(kvp.Key, out var n) ? n : kvp.Key,
                            Action = "REDUCE",
                            From = "Leading",
                            To = "Weakening",
                            DaysSinceChange = 1,
                            Reason = "Losing momentum — consider reducing exposure"
                        });
                    }
                }
                else if (recentQ == "Lagging")
                {
                    int lagDays = 0;
                    for (int i = history.Count - 1; i >= 0 && history[i].Quadrant == "Lagging"; i--)
                        lagDays++;
                    if (lagDays >= 5)
                    {
                        suggestions.Add(new RotationSuggestion
                        {
                            Sector = SectorIndices.TryGetValue(kvp.Key, out var n) ? n : kvp.Key,
                            Action = "AVOID",
                            From = "Lagging",
                            To = "Lagging",
                            DaysSinceChange = lagDays,
                            Reason = "Persistent weakness — avoid until rotation signal appears"
                        });
                    }
                }

                results.Add(new SectorRRGPoint
                {
                    Ticker = kvp.Key,
                    Name = SectorIndices.TryGetValue(kvp.Key, out var name) ? name : kvp.Key,
                    RsRatio = Math.Round(rsRatioZ, 2),
                    RsMomentum = Math.Round(rsMomZ, 2),
                    Quadrant = recentQ,
                    Price = Math.Round(closes[^1], 2),
                    PriceChangePct = closes.Length >= 2
                        ? Math.Round((closes[^1] - closes[^2]) / closes[^2] * 100, 2)
                        : 0,
                    IsNewImproving = isNewImproving,
                    IsNewWeakening = isNewWeakening,
                    History = history
                });
            }

            int newImpCount = results.Count(r => r.IsNewImproving);
            int newWeakCount = results.Count(r => r.IsNewWeakening);
            bool rotationActive = newImpCount >= 2 && newWeakCount >= 2;

            return new SectorRotationResult
            {
                Sectors = results.OrderByDescending(r => r.RsMomentum).ToList(),
                Suggestions = suggestions,
                RotationActive = rotationActive,
                LastUpdated = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Backtest rotation signals: buy sector when Lagging→Improving, sell when Leading→Weakening.
        /// Computes RRG weekly (every 5 trading days), rebalances portfolio.
        /// </summary>
        public RotationBacktestResult RunRotationBacktest(RotationBacktestRequest request)
        {
            using var conn = new DuckDBConnection(_connectionString);
            conn.Open();

            var allBars = conn.Query<DailyBar>("SELECT Ticker, Date, Close FROM SectorDailyBars ORDER BY Ticker, Date").ToList();
            if (allBars.Count == 0) return new RotationBacktestResult();

            var grouped = allBars.GroupBy(b => b.Ticker).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Date).ToList());
            if (!grouped.TryGetValue("^NSEI", out var nifty) || nifty.Count < 250)
                return new RotationBacktestResult();

            var niftyCloses = nifty.Select(b => b.Close).ToArray();
            var niftyDates = nifty.Select(b => b.Date).ToList();

            // Determine date range
            DateTime start = request.StartDate ?? niftyDates[250];
            DateTime end = request.EndDate ?? niftyDates[^1];

            double cash = request.StartingCapital;
            double maxBalance = request.StartingCapital;
            double maxDrawdown = 0;
            var trades = new List<RotationBacktestTrade>();
            var curve = new List<EquityCurvePoint>();
            var niftyCurve = new List<EquityCurvePoint>();

            var activeSectors = new Dictionary<string, (DateTime entryDate, double entryPrice)>();

            // Walk through time weekly (every 5 trading days)
            for (int nIdx = 250; nIdx < niftyCloses.Length; nIdx += 5)
            {
                var currentDate = niftyDates[nIdx];
                if (currentDate < start) continue;
                if (currentDate > end) break;

                // Compute RRG for each sector at this point in time
                var signals = new List<(string ticker, double rsRatio, double rsMom, string quadrant)>();

                foreach (var kvp in grouped)
                {
                    if (kvp.Key == "^NSEI") continue;
                    var bars = kvp.Value;
                    if (bars.Count < 250) continue;

                    var closes = bars.Select(b => b.Close).ToArray();
                    int lastIdx = Math.Min(closes.Length - 1, nIdx);
                    int lookLen = Math.Min(lastIdx + 1, niftyCloses.Length);

                    if (lookLen < 250) continue;

                    // Compute RS for this point
                    double[] rs = new double[lookLen];
                    for (int i = 0; i < lookLen; i++)
                        rs[i] = closes[lastIdx - lookLen + 1 + i] / niftyCloses[lookLen - lookLen + i];

                    double[] rsSt = _indicator.CalculateEma(rs, 10);
                    double[] rsLt = _indicator.CalculateEma(rs, 40);
                    double[] mom = new double[rsSt.Length];
                    for (int i = 0; i < rsSt.Length; i++) mom[i] = rsSt[i] - rsLt[i];

                    int lb = Math.Min(250, rsSt.Length);
                    if (rsSt.Length < lb) continue;

                    int l = rsSt.Length - 1;
                    var stSl = rsSt.Skip(l - lb + 1).Take(lb).ToArray();
                    double stM = stSl.Average();
                    double stS = Math.Sqrt(stSl.Sum(v => Math.Pow(v - stM, 2)) / lb);
                    double rZ = stS > 0 ? (rsSt[l] - stM) / stS : 0;

                    var mSl = mom.Skip(l - lb + 1).Take(lb).ToArray();
                    double mM = mSl.Average();
                    double mS = Math.Sqrt(mSl.Sum(v => Math.Pow(v - mM, 2)) / lb);
                    double mZ = mS > 0 ? (mom[l] - mM) / mS : 0;

                    string q = rZ > 0 ? (mZ > 0 ? "Leading" : "Weakening") : (mZ > 0 ? "Improving" : "Lagging");

                    signals.Add((kvp.Key, rZ, mZ, q));
                }

                // Check exits: sell positions in Weakening or Lagging
                var toRemove = new List<string>();
                foreach (var kvp in activeSectors)
                {
                    var sig = signals.FirstOrDefault(s => s.ticker == kvp.Key);
                    if (sig.quadrant == "Weakening" || sig.quadrant == "Lagging")
                    {
                        // Sell
                        var bars = grouped[kvp.Key];
                        var close = bars[bars.Count - 1].Close;
                        double retPct = (close - kvp.Value.entryPrice) / kvp.Value.entryPrice * 100;
                        int days = (int)(currentDate - kvp.Value.entryDate).TotalDays;

                        double sellVal = close * (request.StartingCapital / 100.0 / activeSectors.Count);
                        cash += sellVal;

                        trades.Add(new RotationBacktestTrade
                        {
                            Sector = SectorIndices.TryGetValue(kvp.Key, out var n) ? n : kvp.Key,
                            Signal = "SELL",
                            Date = currentDate,
                            Price = close,
                            ReturnPct = Math.Round(retPct, 2),
                            DaysHeld = days
                        });
                        toRemove.Add(kvp.Key);
                    }
                }
                foreach (var t in toRemove) activeSectors.Remove(t);

                // Check entries: BUY sectors that just turned Improving (from Lagging)
                // Only if they were NOT Improving last week
                foreach (var sig in signals)
                {
                    if (activeSectors.ContainsKey(sig.ticker)) continue;
                    if (sig.quadrant != "Improving") continue;

                    // Check previous week's quadrant via historical lookup
                    if (nIdx < 255) continue;
                    // Simple heuristic: if it's now Improving and not already owned, and
                    // the RS-Momentum is positive (confirmed turn), buy
                    if (sig.rsMom > 0.5)
                    {
                        var bars = grouped[sig.ticker];
                        int bIdx = Math.Min(bars.Count - 1, nIdx);
                        var close = bars[bIdx].Close;
                        double posVal = request.StartingCapital * 0.10; // 10% per sector
                        if (cash >= posVal)
                        {
                            cash -= posVal;
                            activeSectors[sig.ticker] = (currentDate, close);
                            trades.Add(new RotationBacktestTrade
                            {
                                Sector = SectorIndices.TryGetValue(sig.ticker, out var n) ? n : sig.ticker,
                                Signal = "BUY",
                                Date = currentDate,
                                Price = close,
                                ReturnPct = 0,
                                DaysHeld = 0
                            });
                        }
                    }
                }

                // Portfolio valuation
                double portfolioVal = cash;
                foreach (var kvp in activeSectors)
                {
                    var bars = grouped[kvp.Key];
                    int bIdx = Math.Min(bars.Count - 1, nIdx);
                    double close = bars[bIdx].Close;
                    double posVal = request.StartingCapital * 0.10;
                    portfolioVal += posVal * (close / kvp.Value.entryPrice - 1);
                }

                maxBalance = Math.Max(maxBalance, portfolioVal);
                double dd = ((maxBalance - portfolioVal) / maxBalance) * 100;
                maxDrawdown = Math.Max(maxDrawdown, dd);

                curve.Add(new EquityCurvePoint { Date = currentDate, Balance = Math.Round(portfolioVal, 2), DrawdownPercent = Math.Round(dd, 2) });

                // Nifty benchmark
                double niftyVal = request.StartingCapital * (niftyCloses[nIdx] / niftyCloses[250]);
                niftyCurve.Add(new EquityCurvePoint { Date = currentDate, Balance = Math.Round(niftyVal, 2), DrawdownPercent = 0 });
            }

            double endingCapital = cash;
            foreach (var kvp in activeSectors)
            {
                var bars = grouped[kvp.Key];
                double close = bars[^1].Close;
                double retPct = (close - kvp.Value.entryPrice) / kvp.Value.entryPrice * 100;
                trades.Add(new RotationBacktestTrade
                {
                    Sector = SectorIndices.TryGetValue(kvp.Key, out var n) ? n : kvp.Key,
                    Signal = "CLOSE",
                    Date = niftyDates[^1],
                    Price = close,
                    ReturnPct = Math.Round(retPct, 2),
                    DaysHeld = (int)(niftyDates[^1] - kvp.Value.entryDate).TotalDays
                });
            }

            double totalRet = endingCapital - request.StartingCapital;
            double totalRetPct = totalRet / request.StartingCapital * 100;
            double niftyRet = niftyCloses[^1] / niftyCloses[250];
            double niftyRetPct = (niftyRet - 1) * 100;

            return new RotationBacktestResult
            {
                StartingCapital = request.StartingCapital,
                EndingCapital = Math.Round(endingCapital, 2),
                TotalReturn = Math.Round(totalRet, 2),
                ReturnPercent = Math.Round(totalRetPct, 2),
                NiftyReturn = Math.Round(niftyRetPct, 2),
                MaxDrawdown = Math.Round(maxDrawdown, 2),
                TotalTrades = trades.Count,
                Wins = trades.Count(t => t.ReturnPct > 0),
                Losses = trades.Count(t => t.ReturnPct < 0),
                WinRate = trades.Count > 0 ? Math.Round((double)trades.Count(t => t.ReturnPct > 0) / trades.Count * 100, 2) : 0,
                Trades = trades,
                EquityCurve = curve,
                NiftyCurve = niftyCurve
            };
        }
    }
}
