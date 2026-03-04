using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Abstraction for fetching evolving benchmark datasets from external sources.
    /// Implementations may fetch from GitHub raw URLs, OpenBenchmarking APIs, or other legal sources.
    /// </summary>
    public interface IBenchmarkDataProvider
    {
        /// <summary>
        /// Fetches the benchmark dataset asynchronously.
        /// Returns cached data if available and fresh, otherwise fetches from remote.
        /// </summary>
        Task<BenchmarkDatasetResult> GetDatasetAsync(CancellationToken ct = default);

        /// <summary>
        /// Forces a refresh from the remote source, bypassing cache freshness checks.
        /// </summary>
        Task<BenchmarkDatasetResult> RefreshAsync(CancellationToken ct = default);

        /// <summary>
        /// Provider name (e.g., "GitHub Raw", "OpenBenchmarking", "PassMark API").
        /// </summary>
        string ProviderName { get; }
    }

    /// <summary>
    /// Result of a benchmark dataset fetch operation.
    /// </summary>
    public class BenchmarkDatasetResult
    {
        /// <summary>The fetched dataset, or null if unavailable.</summary>
        public BenchmarkDataset? Dataset { get; set; }

        /// <summary>Whether the operation was successful.</summary>
        public bool Success => Dataset != null && string.IsNullOrEmpty(Error);

        /// <summary>Error message if fetch failed.</summary>
        public string? Error { get; set; }

        /// <summary>Whether data came from cache.</summary>
        public bool FromCache { get; set; }

        /// <summary>Cache age in days (null if not from cache).</summary>
        public double? CacheAgeDays { get; set; }

        /// <summary>HTTP status code from remote fetch (0 if not attempted or from cache).</summary>
        public int HttpStatus { get; set; }

        /// <summary>Time taken to fetch (for diagnostics).</summary>
        public TimeSpan FetchDuration { get; set; }
    }

    /// <summary>
    /// Root model for external benchmark dataset.
    /// Contains CPU/GPU benchmark entries with scores, percentiles, and rankings.
    /// </summary>
    public class BenchmarkDataset
    {
        /// <summary>Dataset version (semantic versioning recommended, e.g., "1.0.0").</summary>
        public string DatasetVersion { get; set; } = "";

        /// <summary>ISO 8601 publication timestamp.</summary>
        public string PublishedAt { get; set; } = "";

        /// <summary>Name of the dataset source (e.g., "PCDiagnosticPRO Market Benchmarks").</summary>
        public string SourceName { get; set; } = "";

        /// <summary>Total number of entries in the dataset (for reference).</summary>
        public int TotalEntries { get; set; }

        /// <summary>CPU benchmark entries keyed by normalized name.</summary>
        public List<CpuBenchmarkEntry> CpuEntries { get; set; } = new();

        /// <summary>GPU benchmark entries keyed by normalized name.</summary>
        public List<GpuBenchmarkEntry> GpuEntries { get; set; } = new();

        /// <summary>RAM baseline thresholds for percentile calculation.</summary>
        public RamBenchmarkBaseline? RamBaseline { get; set; }

        /// <summary>Storage baseline thresholds for percentile calculation.</summary>
        public StorageBenchmarkBaseline? StorageBaseline { get; set; }

        /// <summary>Total number of CPUs in the market reference (for rank display).</summary>
        public int TotalCpusInMarket { get; set; } = 2500;

        /// <summary>Total number of GPUs in the market reference (for rank display).</summary>
        public int TotalGpusInMarket { get; set; } = 1800;
    }

    /// <summary>
    /// Benchmark entry for a single CPU model.
    /// </summary>
    public class CpuBenchmarkEntry
    {
        /// <summary>Normalized CPU name (lowercase, no (R)/(TM), collapsed spaces).</summary>
        public string NormalizedName { get; set; } = "";

        /// <summary>Alternative names/patterns for matching (e.g., "ryzen 9 5900x", "5900x").</summary>
        public List<string> AlternativeNames { get; set; } = new();

        /// <summary>Raw benchmark score (0-100000+ scale, e.g., PassMark-style).</summary>
        public double RawScore { get; set; }

        /// <summary>Normalized score (0-100 scale for display).</summary>
        public double NormalizedScore { get; set; }

        /// <summary>Market percentile (0-100, where 100 = top performer).</summary>
        public double Percentile { get; set; }

        /// <summary>Approximate rank in the market (1 = best).</summary>
        public int Rank { get; set; }

        /// <summary>Typical core count for this CPU.</summary>
        public int Cores { get; set; }

        /// <summary>Typical thread count for this CPU.</summary>
        public int Threads { get; set; }

        /// <summary>Generation/release year (for age-based adjustments).</summary>
        public int ReleaseYear { get; set; }
    }

    /// <summary>
    /// Benchmark entry for a single GPU model.
    /// </summary>
    public class GpuBenchmarkEntry
    {
        /// <summary>Normalized GPU name (lowercase, no "NVIDIA GeForce", "AMD Radeon", etc.).</summary>
        public string NormalizedName { get; set; } = "";

        /// <summary>Alternative names/patterns for matching.</summary>
        public List<string> AlternativeNames { get; set; } = new();

        /// <summary>Raw benchmark score (0-50000+ scale, e.g., 3DMark-style).</summary>
        public double RawScore { get; set; }

        /// <summary>Normalized score (0-100 scale for display).</summary>
        public double NormalizedScore { get; set; }

        /// <summary>Market percentile (0-100, where 100 = top performer).</summary>
        public double Percentile { get; set; }

        /// <summary>Approximate rank in the market (1 = best).</summary>
        public int Rank { get; set; }

        /// <summary>VRAM in MB.</summary>
        public double VramMb { get; set; }

        /// <summary>Memory bandwidth in GB/s (if available).</summary>
        public double MemoryBandwidthGBps { get; set; }

        /// <summary>Generation/release year.</summary>
        public int ReleaseYear { get; set; }
    }

    /// <summary>
    /// Baseline thresholds for RAM percentile calculation.
    /// </summary>
    public class RamBenchmarkBaseline
    {
        /// <summary>Percentile mapping: GB threshold to percentile.</summary>
        public List<RamPercentileMapping> Mappings { get; set; } = new();
    }

    public class RamPercentileMapping
    {
        public double MinGb { get; set; }
        public double Percentile { get; set; }
    }

    /// <summary>
    /// Baseline thresholds for storage percentile calculation.
    /// </summary>
    public class StorageBenchmarkBaseline
    {
        /// <summary>HDD percentile (typically low).</summary>
        public double HddPercentile { get; set; } = 15.0;

        /// <summary>SATA SSD percentile.</summary>
        public double SataSsdPercentile { get; set; } = 50.0;

        /// <summary>NVMe SSD percentile.</summary>
        public double NvmePercentile { get; set; } = 85.0;

        /// <summary>Gen4/Gen5 NVMe percentile (high-end).</summary>
        public double NvmeGen4Percentile { get; set; } = 95.0;
    }
}
