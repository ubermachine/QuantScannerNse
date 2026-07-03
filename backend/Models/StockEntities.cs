using System;
using System.Collections.Generic;

namespace backend.Models
{
    public class StockMetadata
    {
        public string Ticker { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Sector { get; set; } = string.Empty;
    }

    public class DailyBar
    {
        public int Id { get; set; }
        public string Ticker { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
    }

    public class WeeklyBar
    {
        public int Id { get; set; }
        public string Ticker { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
    }

    public class WatchlistItem
    {
        public string Ticker { get; set; } = string.Empty;
        public double EntryPrice { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    }

    // API Data Transfer Objects (DTOs)
    public class ScanResponse
    {
        public string MarketRegime { get; set; } = "UNKNOWN"; // BULLISH or BEARISH
        public double IndexClose { get; set; }
        public double IndexEma200 { get; set; }
        public List<StockScanResult> Results { get; set; } = new();
    }

    public class StockScanResult
    {
        public string Ticker { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Sector { get; set; } = string.Empty;
        public double Price { get; set; }
        public int Score { get; set; } // Consolidated Score 0 - 100
        
        // Strategy Matches
        public bool IsHctMatch { get; set; }
        public bool IsLrhrMatch { get; set; }
        public string Strategy { get; set; } = "None"; // "HCT", "LRHR", "Both", "None"

        // Technical Indicators
        public double Jnsar { get; set; }
        public double DistanceToJnsar { get; set; }
        public double Ema200 { get; set; }
        public double Ema50 { get; set; }
        public double Fib618 { get; set; }
        public double Atr14 { get; set; }
        public bool IsVolatilityCoiled { get; set; }
        public double ProximityTo52WHigh { get; set; }
        public double VolumeScore { get; set; }
        
        // Fundamentals
        public double EpsGrowthYoY { get; set; }
        public double DebtToEquity { get; set; }

        // Trade Levels
        public double StopLoss { get; set; }
        public double Target1 { get; set; }
        public double Target2 { get; set; }

        // Scorecard Breakdown (to show in detail panel)
        public int TrendScore { get; set; } // Max 20
        public int RelativeStrengthScore { get; set; } // Max 20
        public int ProximityScore { get; set; } // Max 15
        public int VolumeAccumulationScore { get; set; } // Max 10
        public int VolatilitySetupScore { get; set; } // Max 10
        public int FundamentalsScore { get; set; } // Max 15
        public int InstitutionalFootprintScore { get; set; } // Max 10

        // FYERS Options Flow Enrichment
        public FyersOptionsFlowData? FyersOptionsFlow { get; set; }
    }

    public class FyersOptionsFlowData
    {
        public double Pcr { get; set; }
        public double Skew { get; set; }
        public string SqueezeStatus { get; set; } = "Neutral";
        public bool NeedsLogin { get; set; }
        public string? LoginUrl { get; set; }
    }


    public class HistoricalChartResponse
    {
        public string Ticker { get; set; } = string.Empty;
        public List<ChartCandle> Candles { get; set; } = new();
    }

    public class ChartCandle
    {
        public string Date { get; set; } = string.Empty; // yyyy-MM-dd
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
        
        // Calculated overlays
        public double? Ema8 { get; set; }
        public double? Ema21 { get; set; }
        public double? Ema200 { get; set; }
        public double? Jnsar { get; set; }
        public double? Fib618 { get; set; }
    }
}
