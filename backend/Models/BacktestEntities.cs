using System;
using System.Collections.Generic;

namespace backend.Models
{
    public class BacktestTrade
    {
        public string Ticker { get; set; } = string.Empty;
        public string Strategy { get; set; } = string.Empty;
        public DateTime EntryDate { get; set; }
        public double EntryPrice { get; set; }
        public DateTime ExitDate { get; set; }
        public double ExitPrice { get; set; }
        public double ProfitPercentage { get; set; }
        public bool IsWin { get; set; }
        public int DaysHeld { get; set; }
    }

    public class BacktestResult
    {
        public string Ticker { get; set; } = string.Empty;
        public int TotalTrades { get; set; }
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public double WinRate { get; set; }
        public double AverageWinPercentage { get; set; }
        public double AverageLossPercentage { get; set; }
        public double ProfitFactor { get; set; }
        public double MaxDrawdown { get; set; }
        public double TotalReturn { get; set; }
        public List<BacktestTrade> Trades { get; set; } = new();
    }
    
    public class BacktestRequest
    {
        public string Ticker { get; set; } = string.Empty;
        public double StopLossPct { get; set; } = 0.05;
        public double TargetPct { get; set; } = 0.15;
        public bool UseDynamicExits { get; set; } = false;
    }

    public class StrategyPerformanceSummary
    {
        public string StrategyName { get; set; } = string.Empty;
        public int TotalTrades { get; set; }
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public double WinRate { get; set; }
        public double AverageWinPercentage { get; set; }
        public double AverageLossPercentage { get; set; }
        public double ProfitFactor { get; set; }
        public double MaxDrawdown { get; set; }
        public double TotalReturn { get; set; }
        public List<TickerPerformance> TickerPerformances { get; set; } = new();
    }

    public class TickerPerformance
    {
        public string Ticker { get; set; } = string.Empty;
        public int TotalTrades { get; set; }
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public double WinRate { get; set; }
        public double TotalReturn { get; set; }
        public double MaxDrawdown { get; set; }
        public double ProfitFactor { get; set; }
    }

    public class BulkBacktestResponse
    {
        public List<StrategyPerformanceSummary> Strategies { get; set; } = new();
    }
}

