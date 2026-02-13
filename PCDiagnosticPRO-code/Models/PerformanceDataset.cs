using System.Collections.Generic;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Root model for the external performance dataset (remote JSON).
    /// All scoring tables, thresholds, patterns, and scenario rules are stored here.
    /// </summary>
    public class PerformanceDataset
    {
        /// <summary>Semantic version of the dataset (e.g. "1.0.0").</summary>
        public string DatasetVersion { get; set; } = "";

        /// <summary>ISO 8601 publication timestamp.</summary>
        public string PublishedAt { get; set; } = "";

        /// <summary>CPU name-matching patterns with tier order (1=Entry .. 4=High-end, 5=Workstation).</summary>
        public List<PatternRule> CpuPatterns { get; set; } = new();

        /// <summary>GPU name-matching patterns with tier order.</summary>
        public List<PatternRule> GpuPatterns { get; set; } = new();

        /// <summary>CPU heuristic rules for fallback when no name pattern matches.</summary>
        public CpuHeuristicRules CpuHeuristicRules { get; set; } = new();

        /// <summary>GPU VRAM-based tier thresholds (MB).</summary>
        public GpuVramThresholds GpuVramThresholds { get; set; } = new();

        /// <summary>RAM tier thresholds (GB).</summary>
        public RamTierRules RamTierRules { get; set; } = new();

        /// <summary>Storage kind to tier order mapping.</summary>
        public StorageTierRules StorageTierRules { get; set; } = new();

        /// <summary>Per-scenario scoring rules keyed by scenario ID.</summary>
        public Dictionary<string, ScenarioRule> ScenarioRules { get; set; } = new();

        /// <summary>Classification thresholds for scenario scores.</summary>
        public ClassificationThresholds ClassificationThresholds { get; set; } = new();

        /// <summary>High-end guard floor rules.</summary>
        public FloorRules Floors { get; set; } = new();

        /// <summary>
        /// Market benchmark requirements per scenario (keyed by scenario ID).
        /// When present, scoring uses specs-vs-market comparison instead of base+bonus formulas.
        /// This allows scores to reflect real-world capability ("Can You Run It" style).
        /// </summary>
        public Dictionary<string, MarketBenchmark>? MarketBenchmarks { get; set; }
    }

    /// <summary>
    /// A pattern-to-tier mapping rule (e.g. "ryzen 9" → tier order 4).
    /// </summary>
    public class PatternRule
    {
        /// <summary>Substring to search for (case-insensitive) in the normalized hardware name.</summary>
        public string Pattern { get; set; } = "";

        /// <summary>Tier order: 1=Entry, 2=Mid-range, 3=Upper Mid, 4=High-end, 5=Workstation.</summary>
        public int TierOrder { get; set; }
    }

    /// <summary>
    /// CPU core/thread heuristic thresholds used when no name pattern matches.
    /// </summary>
    public class CpuHeuristicRules
    {
        public int HighEndMinCores { get; set; } = 12;
        public int HighEndMinThreads { get; set; } = 24;
        public int UpperMidMinCores { get; set; } = 10;
        public int UpperMidMinThreads { get; set; } = 16;
        public int MidRangeMinCores { get; set; } = 6;
        public int MidRangeMaxCores { get; set; } = 8;
        public int EntryMinCores { get; set; } = 4;
        public int EntryMinThreads { get; set; } = 4;
    }

    /// <summary>
    /// GPU VRAM-based tier thresholds (in MB).
    /// </summary>
    public class GpuVramThresholds
    {
        public double HighEndMinMb { get; set; } = 8192;
        public double UpperMidMinMb { get; set; } = 4096;
        public double MidRangeMinMb { get; set; } = 2048;
        public double EntryMinMb { get; set; } = 1024;
    }

    /// <summary>
    /// RAM tier thresholds (in GB).
    /// </summary>
    public class RamTierRules
    {
        public double HighEndMinGb { get; set; } = 32;
        public double MidRangeMinGb { get; set; } = 16;
        public double EntryMinGb { get; set; } = 8;
        public double EntryFloorGb { get; set; } = 4;
    }

    /// <summary>
    /// Storage kind to tier order mapping.
    /// </summary>
    public class StorageTierRules
    {
        public int NvmeTier { get; set; } = 4;
        public int SataSsdTier { get; set; } = 2;
        public int HddTier { get; set; } = 1;
    }

    /// <summary>
    /// Scoring rule for a single usage scenario.
    /// </summary>
    public class ScenarioRule
    {
        /// <summary>Base score before bonuses.</summary>
        public int Base { get; set; }

        /// <summary>Ordered list of bonus conditions. Evaluated top-to-bottom; each adds Points if Condition is met.</summary>
        public List<ScenarioBonus> Bonuses { get; set; } = new();
    }

    /// <summary>
    /// A single bonus condition within a scenario rule.
    /// Condition is a simple expression like "CpuTierOrder>=3", "RamGb>=16", "StorageKind==NVMe", "GpuVramMb>=6144", "CpuThreads>=16".
    /// </summary>
    public class ScenarioBonus
    {
        /// <summary>
        /// Condition string. Supported forms:
        /// - "CpuTierOrder>=N", "GpuTierOrder>=N"
        /// - "RamGb>=N", "GpuVramMb>=N", "CpuThreads>=N"
        /// - "StorageKind==HDD", "StorageKind==NVMe", "StorageKind==SATA_SSD"
        /// </summary>
        public string Condition { get; set; } = "";

        /// <summary>Points to add (can be negative for penalties).</summary>
        public int Points { get; set; }

        /// <summary>
        /// Optional: if true, this bonus is only applied when the previous bonus in the list was NOT applied (else-if chain).
        /// Default false (independent bonus).
        /// </summary>
        public bool ElseIf { get; set; }
    }

    /// <summary>
    /// Classification threshold boundaries.
    /// Score &lt; NotRecommendedBelow → "Not Recommended"
    /// Score &lt; AcceptableBelow → "Acceptable"
    /// Score &lt; GoodBelow → "Good"
    /// Score >= GoodBelow → "Excellent"
    /// </summary>
    public class ClassificationThresholds
    {
        public int NotRecommendedBelow { get; set; } = 40;
        public int AcceptableBelow { get; set; } = 55;
        public int GoodBelow { get; set; } = 70;
    }

    /// <summary>
    /// Floor rules for high-end configurations.
    /// </summary>
    public class FloorRules
    {
        /// <summary>Condition that must be met to apply scenario floors.</summary>
        public FloorCondition HighEndCondition { get; set; } = new();

        /// <summary>Minimum scores per scenario ID when HighEndCondition is met.</summary>
        public Dictionary<string, int> ScenarioFloors { get; set; } = new();
    }

    /// <summary>
    /// Market benchmark entry for a single usage scenario.
    /// Defines min / recommended / ultra hardware requirements based on current market (2025-2026).
    /// Scoring interpolates: below min → 0-39, at min → 40, at recommended → 70, at/above ultra → 100.
    /// </summary>
    public class MarketBenchmark
    {
        /// <summary>Localized display name for the scenario.</summary>
        public string Label { get; set; } = "";

        /// <summary>What this scenario measures (e.g. "Jeux AAA récents en 1440p 60 FPS, ultra").</summary>
        public string Description { get; set; } = "";

        /// <summary>Hardware requirements at min / recommended / ultra levels + component weights.</summary>
        public ScenarioRequirements Requirements { get; set; } = new();
    }

    /// <summary>
    /// Hardware requirements for a scenario at three levels (min / recommended / ultra).
    /// Each component (CPU, GPU, RAM, Storage) has a weight that determines its contribution to the final score.
    /// Score per component = interpolate(actual_value, min_value, recommended_value, ultra_value) → mapped to 0-100.
    /// Final score = weighted average of component scores.
    /// </summary>
    public class ScenarioRequirements
    {
        // ── Minimum (below this = Not Recommended, score ~0-39) ──
        public int MinCpuCores { get; set; }
        public int MinCpuThreads { get; set; }
        public double MinRamGb { get; set; }
        public double MinGpuVramMb { get; set; }
        public int MinGpuTierOrder { get; set; }
        public int MinStorageTier { get; set; }

        // ── Recommended (comfortable, score ~70) ──
        public int RecommendedCpuCores { get; set; }
        public int RecommendedCpuThreads { get; set; }
        public double RecommendedRamGb { get; set; }
        public double RecommendedGpuVramMb { get; set; }
        public int RecommendedGpuTierOrder { get; set; }
        public int RecommendedStorageTier { get; set; }

        // ── Ultra (handles the most demanding workloads, score 100) ──
        public int UltraCpuCores { get; set; }
        public int UltraCpuThreads { get; set; }
        public double UltraRamGb { get; set; }
        public double UltraGpuVramMb { get; set; }
        public int UltraGpuTierOrder { get; set; }
        public int UltraStorageTier { get; set; }

        // ── CPU Frequency (GHz) — optional, for frequency-sensitive scenarios ──
        /// <summary>Minimum CPU frequency in GHz (0 = not applicable).</summary>
        public double MinCpuGhz { get; set; }
        /// <summary>Recommended CPU frequency in GHz.</summary>
        public double RecommendedCpuGhz { get; set; }
        /// <summary>Ultra/optimal CPU frequency in GHz.</summary>
        public double UltraCpuGhz { get; set; }

        // ── Video acceleration — bonus for scenarios requiring video playback ──
        /// <summary>If true, bonus points are awarded when GPU has video acceleration capability.</summary>
        public bool RequiresVideoAcceleration { get; set; }

        // ── LLM/AI RAM threshold — bonus for AI inference scenarios ──
        /// <summary>Minimum RAM in GB for LLM workloads (0 = not applicable). Awards bonus when met.</summary>
        public double MinRamGbForLLM { get; set; }

        // ── Component weights (must sum to ~1.0) ──
        /// <summary>CPU weight in final score (cores + threads combined).</summary>
        public double WeightCpu { get; set; } = 0.25;
        /// <summary>GPU weight in final score (tier + VRAM combined).</summary>
        public double WeightGpu { get; set; } = 0.35;
        /// <summary>RAM weight in final score.</summary>
        public double WeightRam { get; set; } = 0.25;
        /// <summary>Storage weight in final score.</summary>
        public double WeightStorage { get; set; } = 0.15;
    }

    /// <summary>
    /// Condition for determining if a system qualifies as high-end for floor application.
    /// </summary>
    public class FloorCondition
    {
        /// <summary>GPU name patterns that qualify (e.g. "3090"). Any match qualifies.</summary>
        public List<string> GpuPatterns { get; set; } = new();

        /// <summary>Minimum VRAM in MB (alternative to GpuPatterns match).</summary>
        public double MinVramMb { get; set; }

        /// <summary>Minimum CPU cores required.</summary>
        public int MinCores { get; set; }

        /// <summary>Minimum RAM in GB required.</summary>
        public double MinRamGb { get; set; }
    }
}
