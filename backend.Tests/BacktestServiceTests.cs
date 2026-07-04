using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using backend.Services;
using backend.Models;
using backend.Strategies;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace backend.Tests
{
    public class BacktestServiceTests
    {
        private readonly IndicatorService _indicatorService;

        public BacktestServiceTests()
        {
            _indicatorService = new IndicatorService();
        }

        [Fact]
        public void TestIndicatorServiceEma()
        {
            double[] data = { 10, 11, 12, 13, 14, 15, 16 };
            double[] ema = _indicatorService.CalculateEma(data, 3);
            
            Assert.Equal(data.Length, ema.Length);
            // Verify EMA increases with the trend
            Assert.True(ema[6] > ema[0]);
        }

        [Fact]
        public void TestIndicatorServiceZScoreAt()
        {
            // Create a series of length 60 with constant value, then a spike
            double[] closes = new double[60];
            for (int i = 0; i < 59; i++) closes[i] = 100.0;
            closes[59] = 150.0; // spike

            double zScore = _indicatorService.CalculateZScoreAt(closes, 59, 50);

            // With a large positive spike compared to a stable mean of 100, ZScore must be positive and significant
            Assert.True(zScore > 1.0);
        }

        [Fact]
        public void TestIndicatorServiceVolatilityPercentileRankAt()
        {
            double[] atr = new double[300];
            for (int i = 0; i < 299; i++) atr[i] = 2.0;
            atr[299] = 10.0; // extreme volatility spike at the end

            double rank = _indicatorService.CalculateVolatilityPercentileRankAt(atr, 299, 252);
            
            // The latest ATR is the absolute maximum in the 252-day window, so rank should be near 100%
            Assert.Equal(100.0, rank);
        }

        [Fact]
        public void TestIndicatorServiceChandelierExitAt()
        {
            double[] highs = new double[50];
            double[] lows = new double[50];
            double[] atr = new double[50];

            for (int i = 0; i < 50; i++)
            {
                highs[i] = 120.0;
                lows[i] = 110.0;
                atr[i] = 2.0;
            }

            // Chandelier Exit is highest high (120) - atr * multiplier (2 * 3 = 6) = 114
            double exit = _indicatorService.CalculateChandelierExitAt(highs, lows, atr, 49, 22, 3.0);
            
            Assert.Equal(114.0, exit);
        }

        [Fact]
        public async Task TestBacktestEngineBulkExecution()
        {
            // Test that the engine runs successfully on all symbols in the database
            var strategies = new List<IStrategy>
            {
                new HctStrategy(),
                new LrhrStrategy()
            };

            var backtestService = new BacktestService(_indicatorService, strategies);

            // Execute the bulk backtest with standard 5% stop loss and 15% target
            var response = await backtestService.RunAllBacktestsAsync(0.05, 0.15);

            Assert.NotNull(response);
            Assert.NotNull(response.Strategies);
            Assert.True(response.Strategies.Count > 0);

            // Verify properties of the strategy performance summaries
            foreach (var summary in response.Strategies)
            {
                Assert.NotEmpty(summary.StrategyName);
                Assert.True(summary.TotalTrades >= 0);
                Assert.True(summary.WinRate >= 0 && summary.WinRate <= 100);
                Assert.NotNull(summary.TickerPerformances);
            }
        }

        [Fact]
        public async Task TestBacktestEngineBulkExecutionWithDynamicExits()
        {
            var strategies = new List<IStrategy>
            {
                new HctStrategy(),
                new LrhrStrategy()
            };

            var backtestService = new BacktestService(_indicatorService, strategies);

            // Execute the bulk backtest with dynamic exits enabled
            var response = await backtestService.RunAllBacktestsAsync(0.05, 0.15, useDynamicExits: true);

            Assert.NotNull(response);
            Assert.NotNull(response.Strategies);
            Assert.True(response.Strategies.Count > 0);

            foreach (var summary in response.Strategies)
            {
                Assert.NotEmpty(summary.StrategyName);
                Assert.True(summary.TotalTrades >= 0);
                Assert.True(summary.WinRate >= 0 && summary.WinRate <= 100);
            }
        }

        [Fact]
        public async Task TestRunPortfolioSimulation()
        {
            var strategies = new List<IStrategy>
            {
                new HctStrategy(),
                new LrhrStrategy(),
                new JustNiftyStrategy(),
                new JustNiftyHctStrategy(),
                new JustNiftyLrhrStrategy()
            };

            var backtestService = new BacktestService(_indicatorService, strategies);

            // Fetch trades
            var fixedTrades = await backtestService.GenerateAllTradesAsync(0.05, 0.15, useDynamicExits: false);
            var dynamicTrades = await backtestService.GenerateAllTradesAsync(0.05, 0.15, useDynamicExits: true);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Portfolio Simulation Report (10 Lakh Initial Capital)");
            sb.AppendLine("This report simulates investing a starting capital of 1,000,000 INR (10 Lakh) across all strategies simultaneously using various position sizing models.");
            sb.AppendLine();

            // Run Sizing Model 1: Equal Weight (10% allocation per position, max 10 concurrent positions)
            SimulatePortfolio(fixedTrades, 1000000.0, 10, 0.10, "Model A: Fixed Exits (5% SL / 15% Target) - Equal 10% Sizing", sb);
            SimulatePortfolio(dynamicTrades, 1000000.0, 10, 0.10, "Model B: Dynamic Exits - Equal 10% Sizing", sb);

            // Run Sizing Model 2: Equal Weight (5% allocation per position, max 20 concurrent positions)
            SimulatePortfolio(fixedTrades, 1000000.0, 20, 0.05, "Model C: Fixed Exits (5% SL / 15% Target) - Equal 5% Sizing", sb);
            SimulatePortfolio(dynamicTrades, 1000000.0, 20, 0.05, "Model D: Dynamic Exits - Equal 5% Sizing", sb);

            // Write report to artifact folder
            string artifactPath = @"C:\Users\HP\.gemini\antigravity\brain\75da8333-68c8-4c69-95d5-e968839865b6\portfolio_simulation.md";
            System.IO.File.WriteAllText(artifactPath, sb.ToString());

            Assert.True(fixedTrades.Count > 0);
            Assert.True(dynamicTrades.Count > 0);
        }

        private void SimulatePortfolio(List<BacktestTrade> trades, double startingCapital, int maxPositions, double allocationPct, string label, System.Text.StringBuilder sb)
        {
            double cash = startingCapital;
            var activePositions = new List<ActivePosition>();
            int totalTradesTaken = 0;
            int wins = 0;
            int losses = 0;
            double peakPortfolioValue = startingCapital;
            double maxDrawdown = 0;

            // Group trades by EntryDate
            var tradesByDate = trades.GroupBy(t => t.EntryDate.Date).ToDictionary(g => g.Key, g => g.ToList());
            var dates = trades.Select(t => t.EntryDate.Date)
                              .Concat(trades.Select(t => t.ExitDate.Date))
                              .Distinct()
                              .OrderBy(d => d)
                              .ToList();

            foreach (var date in dates)
            {
                // 1. Close trades that exit on this date
                var exiting = activePositions.Where(p => p.ExitDate.Date <= date).ToList();
                foreach (var pos in exiting)
                {
                    double returnMultiplier = 1.0 + (pos.ProfitPercentage / 100.0);
                    double finalValue = pos.AllocatedCapital * returnMultiplier;
                    cash += finalValue;
                    
                    if (pos.ProfitPercentage > 0) wins++;
                    else losses++;

                    activePositions.Remove(pos);
                }

                // Calculate current portfolio value (cash + locked capital)
                double currentPortfolioValue = cash + activePositions.Sum(p => p.AllocatedCapital);
                if (currentPortfolioValue > peakPortfolioValue) peakPortfolioValue = currentPortfolioValue;
                double drawdown = (peakPortfolioValue - currentPortfolioValue) / peakPortfolioValue;
                if (drawdown > maxDrawdown) maxDrawdown = drawdown;

                // 2. Open new trades starting on this date
                if (tradesByDate.TryGetValue(date, out var candidates))
                {
                    foreach (var trade in candidates)
                    {
                        if (activePositions.Count >= maxPositions) break;

                        // Position sizing: allocate allocationPct of current portfolio value
                        double allocation = currentPortfolioValue * allocationPct;
                        if (cash >= allocation && allocation > 1000)
                        {
                            cash -= allocation;
                            activePositions.Add(new ActivePosition
                            {
                                Ticker = trade.Ticker,
                                Strategy = trade.Strategy,
                                EntryDate = trade.EntryDate,
                                ExitDate = trade.ExitDate,
                                EntryPrice = trade.EntryPrice,
                                ExitPrice = trade.ExitPrice,
                                ProfitPercentage = trade.ProfitPercentage,
                                AllocatedCapital = allocation
                            });
                            totalTradesTaken++;
                        }
                    }
                }
            }

            // Close any remaining active positions at their final price
            var remaining = activePositions.ToList();
            foreach (var pos in remaining)
            {
                double returnMultiplier = 1.0 + (pos.ProfitPercentage / 100.0);
                double finalValue = pos.AllocatedCapital * returnMultiplier;
                cash += finalValue;
                if (pos.ProfitPercentage > 0) wins++;
                else losses++;
                activePositions.Remove(pos);
            }

            double finalPortfolioValue = cash;
            double netProfitLoss = finalPortfolioValue - startingCapital;
            double percentageReturn = (netProfitLoss / startingCapital) * 100.0;
            double winRate = totalTradesTaken > 0 ? ((double)wins / totalTradesTaken) * 100.0 : 0.0;

            sb.AppendLine($"### {label}");
            sb.AppendLine($"- **Starting Capital**: {startingCapital:N2} INR");
            sb.AppendLine($"- **Final Portfolio Value**: {finalPortfolioValue:N2} INR");
            sb.AppendLine($"- **Net Profit/Loss**: {netProfitLoss:N2} INR ({(percentageReturn >= 0 ? "+" : "")}{percentageReturn:F2}%)");
            sb.AppendLine($"- **Total Trades Taken**: {totalTradesTaken}");
            sb.AppendLine($"- **Win Rate**: {winRate:F2}% (Wins: {wins}, Losses: {losses})");
            sb.AppendLine($"- **Max Portfolio Drawdown**: {maxDrawdown * 100.0:F2}%");
            sb.AppendLine();
        }

        private class ActivePosition
        {
            public string Ticker { get; set; } = string.Empty;
            public string Strategy { get; set; } = string.Empty;
            public DateTime EntryDate { get; set; }
            public DateTime ExitDate { get; set; }
            public double EntryPrice { get; set; }
            public double ExitPrice { get; set; }
            public double ProfitPercentage { get; set; }
            public double AllocatedCapital { get; set; }
        }
    }
}
