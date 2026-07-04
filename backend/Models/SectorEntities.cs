using System;
using System.Collections.Generic;

namespace backend.Models
{
    public class SectorMetadata
    {
        public string Ticker { get; set; } = "";
        public string Name { get; set; } = "";
    }

    // Per-sector current RRG data
    public class SectorRRGPoint
    {
        public string Ticker { get; set; } = "";
        public string Name { get; set; } = "";
        public double RsRatio { get; set; }
        public double RsMomentum { get; set; }
        public string Quadrant { get; set; } = "";
        public double Price { get; set; }
        public double PriceChangePct { get; set; }
        public bool IsNewImproving { get; set; }
        public bool IsNewWeakening { get; set; }
        public List<QuadrantSnapshot> History { get; set; } = new(); // last ~30 days
    }

    // One day's quadrant state
    public class QuadrantSnapshot
    {
        public DateTime Date { get; set; }
        public string Quadrant { get; set; } = "";
    }

    // Rotation suggestion / alert
    public class RotationSuggestion
    {
        public string Sector { get; set; } = "";
        public string Action { get; set; } = ""; // "BUY", "REDUCE", "AVOID"
        public string From { get; set; } = "";   // previous quadrant
        public string To { get; set; } = "";     // current quadrant
        public int DaysSinceChange { get; set; }
        public string Reason { get; set; } = "";
    }

    // Full rotation response
    public class SectorRotationResult
    {
        public List<SectorRRGPoint> Sectors { get; set; } = new();
        public List<RotationSuggestion> Suggestions { get; set; } = new();
        public bool RotationActive { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
