using System;
using System.Collections.Generic;

namespace backend.Models
{
    public class PortfolioRequest
    {
        public double StartingCapital { get; set; } = 1000000;
        public int MaxPositions { get; set; } = 10;
        public string SizingModel { get; set; } = "Equal"; // "Equal" or "Risk-Based"
        public double RiskPerTradePercent { get; set; } = 1.0;
        public double PositionSizePercent { get; set; } = 10.0;
        public double TransactionCostPercent { get; set; } = 0.05; // Brokerage & taxes in % (e.g. 0.05%)
        public double SlippagePercent { get; set; } = 0.10; // Slippage in % (e.g. 0.10%)
        public string Strategy { get; set; } = "Both"; // "HCT", "LRHR", "Both"
        public DateTime StartDate { get; set; } = DateTime.MinValue;
        public DateTime EndDate { get; set; } = DateTime.MaxValue;
    }

    public class PortfolioSimulationResult
    {
        public double StartingCapital { get; set; }
        public double EndingCapital { get; set; }
        public double TotalProfit { get; set; }
        public double ReturnPercent { get; set; }
        public double SharpeRatio { get; set; }
        public double MaxDrawdownPercent { get; set; }
        public double ProfitFactor { get; set; }
        public double WinRate { get; set; }
        public int TotalTrades { get; set; }
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public List<PortfolioTrade> Trades { get; set; } = new();
        public List<EquityCurvePoint> EquityCurve { get; set; } = new();
    }

    public class PortfolioTrade
    {
        public string Ticker { get; set; } = string.Empty;
        public DateTime EntryDate { get; set; }
        public double EntryPrice { get; set; }
        public DateTime ExitDate { get; set; }
        public double ExitPrice { get; set; }
        public int Shares { get; set; }
        public double Profit { get; set; }
        public double ProfitPercent { get; set; }
        public string ExitReason { get; set; } = string.Empty; // "Stop Loss", "Target 1", "Target 2", "End of Simulation", etc.
    }

    public class EquityCurvePoint
    {
        public DateTime Date { get; set; }
        public double Balance { get; set; }
        public double DrawdownPercent { get; set; }
    }
}
