using System.Collections.Generic;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// System category for the final verdict (deterministic from hardware tiers).
    /// </summary>
    public static class SystemCategory
    {
        public const string EntryLevel = "Entry-Level";
        public const string MidRange = "Mid-Range";
        public const string UpperMid = "Upper Mid";
        public const string HighEnd = "High-End";
        public const string WorkstationGrade = "Workstation Grade";
    }

    /// <summary>
    /// Normalized hardware profile with tier labels and raw values for scoring.
    /// </summary>
    public class HardwareProfile
    {
        public string CpuTier { get; set; } = "Unknown";
        public string GpuTier { get; set; } = "Unknown";
        public string RamTier { get; set; } = "Unknown";
        public string StorageTier { get; set; } = "Unknown";

        public string? CpuModel { get; set; }
        public int CpuCores { get; set; }
        public int CpuThreads { get; set; }
        public double CpuBaseGhz { get; set; }
        public double CpuBoostGhz { get; set; }

        public string? GpuModel { get; set; }
        public double GpuVramMb { get; set; }

        public double RamGb { get; set; }
        public int RamSpeedMhz { get; set; }
        public bool DualChannel { get; set; }

        /// <summary>HDD, SATA_SSD, or NVMe</summary>
        public string StorageKind { get; set; } = "Unknown";

        /// <summary>True when CPU tier was resolved from a known name pattern; false when heuristic or Unmatched.</summary>
        public bool CpuNameMatched { get; set; } = true;
        /// <summary>True when GPU tier was resolved from a known name pattern; false when heuristic or Unmatched.</summary>
        public bool GpuNameMatched { get; set; } = true;
    }

    /// <summary>
    /// Single usage scenario score and classification.
    /// </summary>
    public class ScenarioScore
    {
        public string ScenarioId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Score { get; set; }
        public string Classification { get; set; } = "";
    }

    /// <summary>
    /// Classification bands: Not Recommended (&lt;40), Acceptable (40-55), Good (56-70), Excellent (&gt;70).
    /// </summary>
    public static class ScenarioClassification
    {
        public const string NotRecommended = "Not Recommended";
        public const string Acceptable = "Acceptable";
        public const string Good = "Good";
        public const string Excellent = "Excellent";
    }

    /// <summary>
    /// Bottleneck analysis result: primary limiting factor and upgrade priorities.
    /// </summary>
    public class BottleneckResult
    {
        public string PrimaryLimitingFactor { get; set; } = "None significant";
        public List<UpgradePriority> UpgradePriorityRank { get; set; } = new();
    }

    public class UpgradePriority
    {
        public int Rank { get; set; }
        public string Component { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Final verdict: system category and realistic expectation summary.
    /// </summary>
    public class VerdictSummary
    {
        public string Category { get; set; } = SystemCategory.EntryLevel;
        public string RealisticExpectationSummary { get; set; } = "";
    }

    /// <summary>
    /// Full result from the Performance Evaluation Engine.
    /// </summary>
    public class PerformanceEvaluationResult
    {
        public HardwareProfile Profile { get; set; } = new();
        public List<ScenarioScore> ScenarioScores { get; set; } = new();
        public BottleneckResult Bottleneck { get; set; } = new();
        public VerdictSummary Verdict { get; set; } = new();

        /// <summary>Single 0-100 score for backward compatibility (e.g. average of scenario scores).</summary>
        public int Score { get; set; }
    }
}
