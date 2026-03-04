using System;

namespace PCDiagnosticPro.AI.Models
{
    public sealed class RunAnalysisHeader
    {
        public string RunId { get; set; } = "unknown";
        public DateTime? TimestampUtc { get; set; }
        public double CollectionPercent { get; set; }
        public int ErrorCount { get; set; }
        public int MissingDataCount { get; set; }
        public int CriticalAnomalyCount { get; set; }
        public string Summary { get; set; } = string.Empty;

        public string DateDisplay => TimestampUtc.HasValue
            ? TimestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "Unknown";
    }
}
