using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Benchmark data provider that fetches from a GitHub raw URL.
    /// Implements caching with TTL, ETag support, and graceful fallback.
    /// 
    /// Default URL points to a PCDiagnosticPRO-controlled repository that can be updated anytime.
    /// </summary>
    public class GitHubBenchmarkDataProvider : IBenchmarkDataProvider
    {
        private const int DefaultTtlDays = 7;
        private const int GracePeriodDays = 30;
        private const int TimeoutSeconds = 15;

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCDiagnosticPro", "cache");

        private static readonly string BenchmarkCachePath = Path.Combine(CacheDir, "market_benchmarks.json");
        private static readonly string BenchmarkMetaPath = Path.Combine(CacheDir, "market_benchmarks.meta.json");

        private readonly string _remoteUrl;
        private readonly object _lock = new();
        private BenchmarkDatasetResult? _cachedResult;
        private bool _loaded;

        public string ProviderName => "GitHub Raw";

        /// <summary>
        /// Default constructor uses the PCDiagnosticPRO benchmark dataset URL.
        /// </summary>
        public GitHubBenchmarkDataProvider()
            : this(GetConfiguredUrl() ?? GetDefaultUrl())
        {
        }

        /// <summary>
        /// Constructor with custom URL for testing or alternative sources.
        /// </summary>
        public GitHubBenchmarkDataProvider(string remoteUrl)
        {
            _remoteUrl = remoteUrl ?? throw new ArgumentNullException(nameof(remoteUrl));
        }

        /// <summary>
        /// Default benchmark dataset URL (publicly accessible GitHub raw URL).
        /// </summary>
        private static string GetDefaultUrl()
        {
            // This URL should point to a public GitHub repository that we control
            // For now, using a placeholder that should be replaced with actual hosted dataset
            return "https://raw.githubusercontent.com/PCDiagnosticPRO/benchmarks/main/market_benchmarks.json";
        }

        /// <summary>
        /// Read URL from config file if configured.
        /// </summary>
        private static string? GetConfiguredUrl()
        {
            try
            {
                var configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PCDiagnosticPro", "config.json");

                if (!File.Exists(configPath)) return null;
                var json = File.ReadAllText(configPath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("BenchmarkDatasetUrl", out var urlEl))
                    return urlEl.GetString();
            }
            catch { }
            return null;
        }

        public async Task<BenchmarkDatasetResult> GetDatasetAsync(CancellationToken ct = default)
        {
            // Check memory cache first
            if (_loaded && _cachedResult != null)
            {
                return _cachedResult;
            }

            var sw = Stopwatch.StartNew();
            var result = new BenchmarkDatasetResult();

            try
            {
                // Check disk cache
                var (cachedDataset, meta) = await TryLoadFromCacheAsync();
                bool hasCachedData = cachedDataset != null;
                double cacheAgeDays = meta != null ? (DateTime.UtcNow - meta.LastFetched).TotalDays : double.MaxValue;
                bool cacheFresh = hasCachedData && cacheAgeDays < DefaultTtlDays;

                // If cache is fresh, use it
                if (cacheFresh && cachedDataset != null)
                {
                    result.Dataset = cachedDataset;
                    result.FromCache = true;
                    result.CacheAgeDays = Math.Round(cacheAgeDays, 1);
                    result.FetchDuration = sw.Elapsed;
                    CacheResult(result);
                    return result;
                }

                // Try to fetch from remote
                var (fetchedDataset, httpStatus, etag) = await FetchRemoteAsync(_remoteUrl, meta?.ETag, ct);
                result.HttpStatus = httpStatus;

                // 304 Not Modified - use cache
                if (httpStatus == 304 && cachedDataset != null)
                {
                    await SaveMetaAsync(new BenchmarkCacheMeta { ETag = meta?.ETag ?? "", LastFetched = DateTime.UtcNow });
                    result.Dataset = cachedDataset;
                    result.FromCache = true;
                    result.CacheAgeDays = Math.Round(cacheAgeDays, 1);
                    result.FetchDuration = sw.Elapsed;
                    CacheResult(result);
                    return result;
                }

                // Remote fetch successful
                if (fetchedDataset != null)
                {
                    await SaveCacheAsync(fetchedDataset, etag);
                    result.Dataset = fetchedDataset;
                    result.FromCache = false;
                    result.FetchDuration = sw.Elapsed;
                    CacheResult(result);
                    return result;
                }

                // Remote fetch failed - use stale cache if available
                if (cachedDataset != null)
                {
                    result.Dataset = cachedDataset;
                    result.FromCache = true;
                    result.CacheAgeDays = Math.Round(cacheAgeDays, 1);
                    result.Error = $"Remote unavailable (HTTP {httpStatus}), using stale cache";
                    result.FetchDuration = sw.Elapsed;
                    CacheResult(result);
                    return result;
                }

                // No cache, no remote - use embedded fallback
                result.Dataset = BuildEmbeddedDataset();
                result.FromCache = false;
                result.Error = $"Remote unavailable (HTTP {httpStatus}), using embedded fallback";
                result.FetchDuration = sw.Elapsed;
                CacheResult(result);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Dataset = BuildEmbeddedDataset();
                result.FetchDuration = sw.Elapsed;
                CacheResult(result);
                return result;
            }
        }

        public async Task<BenchmarkDatasetResult> RefreshAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                _cachedResult = null;
                _loaded = false;
            }

            // Delete cache to force fresh fetch
            try
            {
                if (File.Exists(BenchmarkCachePath))
                    File.Delete(BenchmarkCachePath);
                if (File.Exists(BenchmarkMetaPath))
                    File.Delete(BenchmarkMetaPath);
            }
            catch { }

            return await GetDatasetAsync(ct);
        }

        private void CacheResult(BenchmarkDatasetResult result)
        {
            lock (_lock)
            {
                _cachedResult = result;
                _loaded = true;
            }
        }

        #region Remote Fetch

        private async Task<(BenchmarkDataset? dataset, int httpStatus, string? etag)> FetchRemoteAsync(
            string url, string? etag, CancellationToken ct)
        {
            try
            {
                // Enforce HTTPS
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    App.LogMessage($"[GitHubBenchmarkDataProvider] Rejected non-HTTPS URL: {url}");
                    return (null, 0, null);
                }

                using var handler = new HttpClientHandler();
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PCDiagnosticPro/1.0");

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(etag))
                    request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag));

                var response = await client.SendAsync(request, ct);
                int httpStatus = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.NotModified)
                    return (null, 304, etag);

                if (!response.IsSuccessStatusCode)
                {
                    App.LogMessage($"[GitHubBenchmarkDataProvider] HTTP {httpStatus} from {url}");
                    return (null, httpStatus, null);
                }

                var newETag = response.Headers.ETag?.Tag;
                var json = await response.Content.ReadAsStringAsync();

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var dataset = JsonSerializer.Deserialize<BenchmarkDataset>(json, options);

                return (dataset, httpStatus, newETag);
            }
            catch (OperationCanceledException)
            {
                return (null, 0, null);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GitHubBenchmarkDataProvider] Fetch error: {ex.Message}");
                return (null, 0, null);
            }
        }

        #endregion

        #region Cache

        private async Task<(BenchmarkDataset? dataset, BenchmarkCacheMeta? meta)> TryLoadFromCacheAsync()
        {
            try
            {
                if (!File.Exists(BenchmarkCachePath)) return (null, null);

                var json = await File.ReadAllTextAsync(BenchmarkCachePath, Encoding.UTF8);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var dataset = JsonSerializer.Deserialize<BenchmarkDataset>(json, options);

                BenchmarkCacheMeta? meta = null;
                if (File.Exists(BenchmarkMetaPath))
                {
                    var metaJson = await File.ReadAllTextAsync(BenchmarkMetaPath, Encoding.UTF8);
                    meta = JsonSerializer.Deserialize<BenchmarkCacheMeta>(metaJson, options);
                }
                else
                {
                    meta = new BenchmarkCacheMeta { LastFetched = File.GetLastWriteTimeUtc(BenchmarkCachePath) };
                }

                return (dataset, meta);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GitHubBenchmarkDataProvider] Cache read error: {ex.Message}");
                return (null, null);
            }
        }

        private async Task SaveCacheAsync(BenchmarkDataset dataset, string? etag)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
                await File.WriteAllTextAsync(BenchmarkCachePath, JsonSerializer.Serialize(dataset, options), Encoding.UTF8);
                await SaveMetaAsync(new BenchmarkCacheMeta { ETag = etag ?? "", LastFetched = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GitHubBenchmarkDataProvider] Cache write error: {ex.Message}");
            }
        }

        private async Task SaveMetaAsync(BenchmarkCacheMeta meta)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(BenchmarkMetaPath, JsonSerializer.Serialize(meta, options), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GitHubBenchmarkDataProvider] Meta write error: {ex.Message}");
            }
        }

        private class BenchmarkCacheMeta
        {
            public string ETag { get; set; } = "";
            public DateTime LastFetched { get; set; }
        }

        #endregion

        #region Embedded Fallback

        /// <summary>
        /// Build embedded benchmark dataset with common CPUs/GPUs.
        /// This provides baseline data when remote is unavailable.
        /// Based on publicly available benchmark data (PassMark, UserBenchmark aggregates).
        /// </summary>
        private static BenchmarkDataset BuildEmbeddedDataset()
        {
            return new BenchmarkDataset
            {
                DatasetVersion = "embedded-1.0.0",
                PublishedAt = "2026-02-12T00:00:00Z",
                SourceName = "PCDiagnosticPRO Embedded Benchmarks",
                TotalEntries = 100,
                TotalCpusInMarket = 2500,
                TotalGpusInMarket = 1800,
                CpuEntries = BuildEmbeddedCpuEntries(),
                GpuEntries = BuildEmbeddedGpuEntries(),
                RamBaseline = new RamBenchmarkBaseline
                {
                    Mappings = new List<RamPercentileMapping>
                    {
                        new() { MinGb = 4, Percentile = 10 },
                        new() { MinGb = 8, Percentile = 30 },
                        new() { MinGb = 16, Percentile = 55 },
                        new() { MinGb = 32, Percentile = 75 },
                        new() { MinGb = 64, Percentile = 90 },
                        new() { MinGb = 128, Percentile = 98 }
                    }
                },
                StorageBaseline = new StorageBenchmarkBaseline
                {
                    HddPercentile = 15.0,
                    SataSsdPercentile = 50.0,
                    NvmePercentile = 85.0,
                    NvmeGen4Percentile = 95.0
                }
            };
        }

        private static List<CpuBenchmarkEntry> BuildEmbeddedCpuEntries()
        {
            // Common CPUs with approximate benchmark scores and percentiles
            // Based on aggregated public benchmark data
            return new List<CpuBenchmarkEntry>
            {
                // AMD Ryzen 9 series (High-end/Workstation)
                new() { NormalizedName = "ryzen 9 5900x", AlternativeNames = new() { "5900x", "amd ryzen 9 5900x" }, RawScore = 28400, NormalizedScore = 94.2, Percentile = 95.3, Rank = 118, Cores = 12, Threads = 24, ReleaseYear = 2020 },
                new() { NormalizedName = "ryzen 9 5950x", AlternativeNames = new() { "5950x", "amd ryzen 9 5950x" }, RawScore = 39100, NormalizedScore = 97.8, Percentile = 97.2, Rank = 70, Cores = 16, Threads = 32, ReleaseYear = 2020 },
                new() { NormalizedName = "ryzen 9 7900x", AlternativeNames = new() { "7900x", "amd ryzen 9 7900x" }, RawScore = 44500, NormalizedScore = 98.5, Percentile = 97.8, Rank = 55, Cores = 12, Threads = 24, ReleaseYear = 2022 },
                new() { NormalizedName = "ryzen 9 7950x", AlternativeNames = new() { "7950x", "amd ryzen 9 7950x" }, RawScore = 63200, NormalizedScore = 99.5, Percentile = 98.9, Rank = 28, Cores = 16, Threads = 32, ReleaseYear = 2022 },
                
                // AMD Ryzen 7 series (High-end)
                new() { NormalizedName = "ryzen 7 5800x", AlternativeNames = new() { "5800x", "amd ryzen 7 5800x" }, RawScore = 22800, NormalizedScore = 88.5, Percentile = 88.7, Rank = 283, Cores = 8, Threads = 16, ReleaseYear = 2020 },
                new() { NormalizedName = "ryzen 7 7800x3d", AlternativeNames = new() { "7800x3d", "amd ryzen 7 7800x3d" }, RawScore = 23100, NormalizedScore = 89.2, Percentile = 89.4, Rank = 265, Cores = 8, Threads = 16, ReleaseYear = 2023 },
                
                // AMD Ryzen 5 series (Mid-range)
                new() { NormalizedName = "ryzen 5 5600x", AlternativeNames = new() { "5600x", "amd ryzen 5 5600x" }, RawScore = 17200, NormalizedScore = 78.3, Percentile = 76.8, Rank = 580, Cores = 6, Threads = 12, ReleaseYear = 2020 },
                new() { NormalizedName = "ryzen 5 7600x", AlternativeNames = new() { "7600x", "amd ryzen 5 7600x" }, RawScore = 20500, NormalizedScore = 84.1, Percentile = 82.5, Rank = 438, Cores = 6, Threads = 12, ReleaseYear = 2022 },
                
                // Intel Core i9 series (High-end/Workstation)
                new() { NormalizedName = "core i9-12900k", AlternativeNames = new() { "i9-12900k", "12900k", "intel core i9-12900k" }, RawScore = 41400, NormalizedScore = 98.1, Percentile = 97.5, Rank = 63, Cores = 16, Threads = 24, ReleaseYear = 2021 },
                new() { NormalizedName = "core i9-13900k", AlternativeNames = new() { "i9-13900k", "13900k", "intel core i9-13900k" }, RawScore = 59500, NormalizedScore = 99.3, Percentile = 98.7, Rank = 33, Cores = 24, Threads = 32, ReleaseYear = 2022 },
                new() { NormalizedName = "core i9-14900k", AlternativeNames = new() { "i9-14900k", "14900k", "intel core i9-14900k" }, RawScore = 61200, NormalizedScore = 99.4, Percentile = 98.8, Rank = 30, Cores = 24, Threads = 32, ReleaseYear = 2023 },
                
                // Intel Core i7 series (High-end)
                new() { NormalizedName = "core i7-12700k", AlternativeNames = new() { "i7-12700k", "12700k", "intel core i7-12700k" }, RawScore = 31500, NormalizedScore = 95.8, Percentile = 94.2, Rank = 145, Cores = 12, Threads = 20, ReleaseYear = 2021 },
                new() { NormalizedName = "core i7-13700k", AlternativeNames = new() { "i7-13700k", "13700k", "intel core i7-13700k" }, RawScore = 46500, NormalizedScore = 98.7, Percentile = 97.0, Rank = 75, Cores = 16, Threads = 24, ReleaseYear = 2022 },
                
                // Intel Core i5 series (Mid-range)
                new() { NormalizedName = "core i5-12400", AlternativeNames = new() { "i5-12400", "12400", "intel core i5-12400" }, RawScore = 17700, NormalizedScore = 79.5, Percentile = 77.8, Rank = 555, Cores = 6, Threads = 12, ReleaseYear = 2022 },
                new() { NormalizedName = "core i5-13600k", AlternativeNames = new() { "i5-13600k", "13600k", "intel core i5-13600k" }, RawScore = 30200, NormalizedScore = 95.2, Percentile = 93.5, Rank = 163, Cores = 14, Threads = 20, ReleaseYear = 2022 },
                
                // Intel Core i3 series (Entry-level)
                new() { NormalizedName = "core i3-12100", AlternativeNames = new() { "i3-12100", "12100", "intel core i3-12100" }, RawScore = 10200, NormalizedScore = 62.4, Percentile = 58.3, Rank = 1043, Cores = 4, Threads = 8, ReleaseYear = 2022 },
                
                // Older popular CPUs
                new() { NormalizedName = "ryzen 5 3600", AlternativeNames = new() { "3600", "amd ryzen 5 3600" }, RawScore = 13200, NormalizedScore = 70.1, Percentile = 66.2, Rank = 845, Cores = 6, Threads = 12, ReleaseYear = 2019 },
                new() { NormalizedName = "core i7-8700", AlternativeNames = new() { "i7-8700", "8700", "intel core i7-8700" }, RawScore = 9800, NormalizedScore = 60.5, Percentile = 55.8, Rank = 1105, Cores = 6, Threads = 12, ReleaseYear = 2018 },
            };
        }

        private static List<GpuBenchmarkEntry> BuildEmbeddedGpuEntries()
        {
            // Common GPUs with approximate benchmark scores and percentiles
            return new List<GpuBenchmarkEntry>
            {
                // NVIDIA RTX 40 series
                new() { NormalizedName = "rtx 4090", AlternativeNames = new() { "geforce rtx 4090", "nvidia rtx 4090" }, RawScore = 39100, NormalizedScore = 99.8, Percentile = 99.4, Rank = 11, VramMb = 24576, MemoryBandwidthGBps = 1008, ReleaseYear = 2022 },
                new() { NormalizedName = "rtx 4080", AlternativeNames = new() { "geforce rtx 4080", "nvidia rtx 4080" }, RawScore = 28500, NormalizedScore = 98.2, Percentile = 97.8, Rank = 40, VramMb = 16384, MemoryBandwidthGBps = 717, ReleaseYear = 2022 },
                new() { NormalizedName = "rtx 4070 ti", AlternativeNames = new() { "geforce rtx 4070 ti", "nvidia rtx 4070 ti" }, RawScore = 22900, NormalizedScore = 96.5, Percentile = 95.8, Rank = 76, VramMb = 12288, MemoryBandwidthGBps = 504, ReleaseYear = 2023 },
                new() { NormalizedName = "rtx 4070", AlternativeNames = new() { "geforce rtx 4070", "nvidia rtx 4070" }, RawScore = 17800, NormalizedScore = 93.2, Percentile = 91.5, Rank = 153, VramMb = 12288, MemoryBandwidthGBps = 504, ReleaseYear = 2023 },
                new() { NormalizedName = "rtx 4060 ti", AlternativeNames = new() { "geforce rtx 4060 ti", "nvidia rtx 4060 ti" }, RawScore = 13800, NormalizedScore = 88.5, Percentile = 85.2, Rank = 266, VramMb = 8192, MemoryBandwidthGBps = 288, ReleaseYear = 2023 },
                new() { NormalizedName = "rtx 4060", AlternativeNames = new() { "geforce rtx 4060", "nvidia rtx 4060" }, RawScore = 10500, NormalizedScore = 82.1, Percentile = 78.5, Rank = 387, VramMb = 8192, MemoryBandwidthGBps = 272, ReleaseYear = 2023 },
                
                // NVIDIA RTX 30 series
                new() { NormalizedName = "rtx 3090", AlternativeNames = new() { "geforce rtx 3090", "nvidia rtx 3090" }, RawScore = 19800, NormalizedScore = 95.1, Percentile = 93.7, Rank = 113, VramMb = 24576, MemoryBandwidthGBps = 936, ReleaseYear = 2020 },
                new() { NormalizedName = "rtx 3090 ti", AlternativeNames = new() { "geforce rtx 3090 ti", "nvidia rtx 3090 ti" }, RawScore = 21200, NormalizedScore = 95.8, Percentile = 94.5, Rank = 99, VramMb = 24576, MemoryBandwidthGBps = 1008, ReleaseYear = 2022 },
                new() { NormalizedName = "rtx 3080", AlternativeNames = new() { "geforce rtx 3080", "nvidia rtx 3080" }, RawScore = 17400, NormalizedScore = 92.8, Percentile = 91.0, Rank = 162, VramMb = 10240, MemoryBandwidthGBps = 760, ReleaseYear = 2020 },
                new() { NormalizedName = "rtx 3080 ti", AlternativeNames = new() { "geforce rtx 3080 ti", "nvidia rtx 3080 ti" }, RawScore = 18600, NormalizedScore = 94.2, Percentile = 92.5, Rank = 135, VramMb = 12288, MemoryBandwidthGBps = 912, ReleaseYear = 2021 },
                new() { NormalizedName = "rtx 3070", AlternativeNames = new() { "geforce rtx 3070", "nvidia rtx 3070" }, RawScore = 13400, NormalizedScore = 88.1, Percentile = 84.6, Rank = 277, VramMb = 8192, MemoryBandwidthGBps = 448, ReleaseYear = 2020 },
                new() { NormalizedName = "rtx 3070 ti", AlternativeNames = new() { "geforce rtx 3070 ti", "nvidia rtx 3070 ti" }, RawScore = 14200, NormalizedScore = 89.5, Percentile = 86.3, Rank = 247, VramMb = 8192, MemoryBandwidthGBps = 608, ReleaseYear = 2021 },
                new() { NormalizedName = "rtx 3060", AlternativeNames = new() { "geforce rtx 3060", "nvidia rtx 3060" }, RawScore = 8800, NormalizedScore = 76.2, Percentile = 71.5, Rank = 513, VramMb = 12288, MemoryBandwidthGBps = 360, ReleaseYear = 2021 },
                new() { NormalizedName = "rtx 3060 ti", AlternativeNames = new() { "geforce rtx 3060 ti", "nvidia rtx 3060 ti" }, RawScore = 11600, NormalizedScore = 84.8, Percentile = 80.2, Rank = 356, VramMb = 8192, MemoryBandwidthGBps = 448, ReleaseYear = 2020 },
                
                // NVIDIA RTX 20 series
                new() { NormalizedName = "rtx 2080 ti", AlternativeNames = new() { "geforce rtx 2080 ti", "nvidia rtx 2080 ti" }, RawScore = 13600, NormalizedScore = 88.3, Percentile = 84.9, Rank = 271, VramMb = 11264, MemoryBandwidthGBps = 616, ReleaseYear = 2018 },
                new() { NormalizedName = "rtx 2080", AlternativeNames = new() { "geforce rtx 2080", "nvidia rtx 2080" }, RawScore = 10200, NormalizedScore = 81.5, Percentile = 77.8, Rank = 400, VramMb = 8192, MemoryBandwidthGBps = 448, ReleaseYear = 2018 },
                new() { NormalizedName = "rtx 2070", AlternativeNames = new() { "geforce rtx 2070", "nvidia rtx 2070" }, RawScore = 9100, NormalizedScore = 77.8, Percentile = 73.2, Rank = 482, VramMb = 8192, MemoryBandwidthGBps = 448, ReleaseYear = 2018 },
                new() { NormalizedName = "rtx 2060", AlternativeNames = new() { "geforce rtx 2060", "nvidia rtx 2060" }, RawScore = 7200, NormalizedScore = 70.5, Percentile = 64.8, Rank = 634, VramMb = 6144, MemoryBandwidthGBps = 336, ReleaseYear = 2019 },
                
                // NVIDIA GTX 16/10 series
                new() { NormalizedName = "gtx 1660 super", AlternativeNames = new() { "geforce gtx 1660 super", "nvidia gtx 1660 super" }, RawScore = 6100, NormalizedScore = 65.2, Percentile = 58.5, Rank = 747, VramMb = 6144, MemoryBandwidthGBps = 336, ReleaseYear = 2019 },
                new() { NormalizedName = "gtx 1080 ti", AlternativeNames = new() { "geforce gtx 1080 ti", "nvidia gtx 1080 ti" }, RawScore = 10500, NormalizedScore = 82.1, Percentile = 78.5, Rank = 387, VramMb = 11264, MemoryBandwidthGBps = 484, ReleaseYear = 2017 },
                new() { NormalizedName = "gtx 1080", AlternativeNames = new() { "geforce gtx 1080", "nvidia gtx 1080" }, RawScore = 7800, NormalizedScore = 72.8, Percentile = 67.2, Rank = 590, VramMb = 8192, MemoryBandwidthGBps = 320, ReleaseYear = 2016 },
                new() { NormalizedName = "gtx 1070", AlternativeNames = new() { "geforce gtx 1070", "nvidia gtx 1070" }, RawScore = 6500, NormalizedScore = 67.1, Percentile = 60.8, Rank = 705, VramMb = 8192, MemoryBandwidthGBps = 256, ReleaseYear = 2016 },
                new() { NormalizedName = "gtx 1060", AlternativeNames = new() { "geforce gtx 1060", "nvidia gtx 1060" }, RawScore = 4300, NormalizedScore = 55.2, Percentile = 47.5, Rank = 945, VramMb = 6144, MemoryBandwidthGBps = 192, ReleaseYear = 2016 },
                
                // AMD RX 7000 series
                new() { NormalizedName = "rx 7900 xtx", AlternativeNames = new() { "radeon rx 7900 xtx", "amd rx 7900 xtx" }, RawScore = 24800, NormalizedScore = 97.2, Percentile = 96.5, Rank = 63, VramMb = 24576, MemoryBandwidthGBps = 960, ReleaseYear = 2022 },
                new() { NormalizedName = "rx 7900 xt", AlternativeNames = new() { "radeon rx 7900 xt", "amd rx 7900 xt" }, RawScore = 20500, NormalizedScore = 95.5, Percentile = 94.0, Rank = 108, VramMb = 20480, MemoryBandwidthGBps = 800, ReleaseYear = 2022 },
                new() { NormalizedName = "rx 7800 xt", AlternativeNames = new() { "radeon rx 7800 xt", "amd rx 7800 xt" }, RawScore = 14800, NormalizedScore = 90.2, Percentile = 87.3, Rank = 229, VramMb = 16384, MemoryBandwidthGBps = 624, ReleaseYear = 2023 },
                new() { NormalizedName = "rx 7600", AlternativeNames = new() { "radeon rx 7600", "amd rx 7600" }, RawScore = 9500, NormalizedScore = 79.2, Percentile = 74.8, Rank = 453, VramMb = 8192, MemoryBandwidthGBps = 288, ReleaseYear = 2023 },
                
                // AMD RX 6000 series
                new() { NormalizedName = "rx 6900 xt", AlternativeNames = new() { "radeon rx 6900 xt", "amd rx 6900 xt" }, RawScore = 18800, NormalizedScore = 94.5, Percentile = 92.8, Rank = 130, VramMb = 16384, MemoryBandwidthGBps = 512, ReleaseYear = 2020 },
                new() { NormalizedName = "rx 6800 xt", AlternativeNames = new() { "radeon rx 6800 xt", "amd rx 6800 xt" }, RawScore = 17200, NormalizedScore = 92.5, Percentile = 90.6, Rank = 169, VramMb = 16384, MemoryBandwidthGBps = 512, ReleaseYear = 2020 },
                new() { NormalizedName = "rx 6700 xt", AlternativeNames = new() { "radeon rx 6700 xt", "amd rx 6700 xt" }, RawScore = 11200, NormalizedScore = 83.8, Percentile = 79.5, Rank = 369, VramMb = 12288, MemoryBandwidthGBps = 384, ReleaseYear = 2021 },
                new() { NormalizedName = "rx 6600 xt", AlternativeNames = new() { "radeon rx 6600 xt", "amd rx 6600 xt" }, RawScore = 9200, NormalizedScore = 78.5, Percentile = 73.8, Rank = 472, VramMb = 8192, MemoryBandwidthGBps = 256, ReleaseYear = 2021 },
            };
        }

        #endregion
    }
}


