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
    /// Now includes precise decimal score and optional explanation.
    /// </summary>
    public class ScenarioScore
    {
        public string ScenarioId { get; set; } = "";
        public string Name { get; set; } = "";
        
        /// <summary>
        /// Score as integer (0-100) for backward compatibility.
        /// Set this via the setter which also updates PreciseScore.
        /// </summary>
        public int Score 
        { 
            get => (int)System.Math.Round(PreciseScore);
            set => PreciseScore = value;
        }

        /// <summary>
        /// Precise score with decimal (0.0-100.0) for granular display.
        /// </summary>
        public double PreciseScore { get; set; }

        public string Classification { get; set; } = "";

        /// <summary>
        /// Optional explanation of how the score was calculated.
        /// </summary>
        public ScoreExplanation? Explanation { get; set; }
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

        /// <summary>Version of the external performance dataset used (null if embedded/hardcoded).</summary>
        public string? DatasetVersion { get; set; }

        /// <summary>ISO 8601 publication date of the dataset used (null if embedded/hardcoded).</summary>
        public string? DatasetPublishedAt { get; set; }

        /// <summary>True when performance scoring is unavailable (RequireExternal mode, dataset failed).</summary>
        public bool IsUnavailable { get; set; }

        /// <summary>Reason why scoring is unavailable (only set when IsUnavailable=true).</summary>
        public string? UnavailableReason { get; set; }

        /// <summary>Full traceability info about the data source used for scoring.</summary>
        public DatasetSourceInfo SourceInfo { get; set; } = new();
    }

    /// <summary>
    /// Describes which data source was used for performance scoring.
    /// </summary>
    public enum DatasetSourceKind
    {
        /// <summary>External dataset from remote URL or cache.</summary>
        External,
        /// <summary>Embedded/hardcoded rules used as fallback.</summary>
        EmbeddedFallback,
        /// <summary>Scoring is unavailable (RequireExternal mode, dataset failed).</summary>
        Unavailable
    }

    /// <summary>
    /// Dataset mode policy for fallback behavior.
    /// </summary>
    public enum PerformanceDatasetMode
    {
        /// <summary>External dataset is required; scoring unavailable if it fails.</summary>
        RequireExternal,
        /// <summary>Allow fallback to embedded rules if external dataset fails.</summary>
        AllowFallbackEmbedded
    }

    /// <summary>
    /// Complete traceability information about the data source used for performance scoring.
    /// </summary>
    public class DatasetSourceInfo
    {
        /// <summary>Which source was used: External, EmbeddedFallback, or Unavailable.</summary>
        public DatasetSourceKind SourceKind { get; set; } = DatasetSourceKind.EmbeddedFallback;

        /// <summary>The configured dataset mode policy.</summary>
        public PerformanceDatasetMode Mode { get; set; } = PerformanceDatasetMode.RequireExternal;

        /// <summary>Whether a URL was configured in config.json.</summary>
        public bool UrlConfigured { get; set; }

        /// <summary>Host portion of the configured URL (no full URL for security).</summary>
        public string? UrlHost { get; set; }

        /// <summary>Dataset version string (external or embedded).</summary>
        public string VersionDisplay { get; set; } = "";

        /// <summary>Publication date of the dataset (external only).</summary>
        public string? PublishedAt { get; set; }

        /// <summary>Whether cache was used (hit).</summary>
        public bool CacheHit { get; set; }

        /// <summary>Age of the cache in days (null if no cache).</summary>
        public double? CacheAgeDays { get; set; }

        /// <summary>Whether the cache was expired beyond TTL.</summary>
        public bool CacheExpired { get; set; }

        /// <summary>Whether the cache was in grace period (expired but within 30-day grace).</summary>
        public bool CacheInGracePeriod { get; set; }

        /// <summary>Timestamp of last dataset refresh.</summary>
        public string? LastRefresh { get; set; }

        /// <summary>Whether a remote fetch was attempted.</summary>
        public bool RemoteFetchAttempted { get; set; }

        /// <summary>HTTP status of the remote fetch (0 if not attempted).</summary>
        public int RemoteFetchStatus { get; set; }

        /// <summary>Reason for remote fetch failure (null if successful).</summary>
        public string? RemoteFetchError { get; set; }

        /// <summary>Validation result: "pass" or failure reason.</summary>
        public string ValidationResult { get; set; } = "";

        /// <summary>Reason for fallback (null if primary source was used).</summary>
        public string? FallbackReason { get; set; }

        /// <summary>Display label for UI: "External Dataset", "Mode secours: règles internes", "Évaluation indisponible (dataset externe requis)".</summary>
        public string DisplayLabel { get; set; } = "";

        /// <summary>Short source line for the report evidence block.</summary>
        public string SourceLine { get; set; } = "";
    }

    /// <summary>
    /// Result of loading the performance dataset, including traceability.
    /// </summary>
    public class DatasetLoadResult
    {
        /// <summary>The loaded dataset (null if unavailable).</summary>
        public PerformanceDataset? Dataset { get; set; }

        /// <summary>Full traceability information.</summary>
        public DatasetSourceInfo SourceInfo { get; set; } = new();
    }
}
