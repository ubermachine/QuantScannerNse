using System;
using System.Collections.Generic;

namespace backend.Models
{
    public class RotationBacktestRequest
    {
        public double StartingCapital { get; set; } = 1000000;
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }

    public class RotationBacktestResult
    {
        public double StartingCapital { get; set; }
        public double EndingCapital { get; set; }
        public double TotalReturn { get; set; }
        public double ReturnPercent { get; set; }
        public double NiftyReturn { get; set; } // buy & hold Nifty benchmark
        public double MaxDrawdown { get; set; }
        public double SharpeRatio { get; set; }
        public int TotalTrades { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public double WinRate { get; set; }
        public double ProfitFactor { get; set; }
        public List<RotationBacktestTrade> Trades { get; set; } = new();
        public List<EquityCurvePoint> EquityCurve { get; set; } = new();
        public List<EquityCurvePoint> NiftyCurve { get; set; } = new(); // benchmark
    }

    public class RotationBacktestTrade
    {
        public string Sector { get; set; } = "";
        public string Signal { get; set; } = ""; // BUY or SELL
        public DateTime Date { get; set; }
        public double Price { get; set; }
        public double ReturnPct { get; set; }
        public int DaysHeld { get; set; }
    }
}
