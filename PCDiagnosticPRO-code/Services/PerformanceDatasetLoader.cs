using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Loads, caches, and validates the external PerformanceDataset from a remote HTTPS URL.
    /// Implements explicit source selection policy (RequireExternal / AllowFallbackEmbedded)
    /// with full traceability (DatasetLoadResult + DatasetSourceInfo).
    ///
    /// Cache path: %LocalAppData%\PCDiagnosticPro\cache\performance_dataset.json
    /// Metadata:   %LocalAppData%\PCDiagnosticPro\cache\performance_dataset.meta.json
    /// TTL: 7 days. Grace period for RequireExternal: 30 days. ETag/If-None-Match supported.
    /// </summary>
    public static class PerformanceDatasetLoader
    {
        private static readonly object _lock = new();
        private static DatasetLoadResult? _cachedResult;
        private static bool _loaded;

        private const int DefaultTtlDays = 7;
        private const int GracePeriodDays = 30;

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCDiagnosticPro", "cache");

        private static readonly string CachePath = Path.Combine(CacheDir, "performance_dataset.json");
        private static readonly string MetaPath = Path.Combine(CacheDir, "performance_dataset.meta.json");

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCDiagnosticPro", "config.json");

        /// <summary>
        /// Returns the currently loaded dataset (may be null if unavailable).
        /// Thread-safe; loads once per application lifetime.
        /// DEPRECATED: Use LoadResult for full traceability.
        /// </summary>
        public static PerformanceDataset? Current => LoadResult.Dataset;

        /// <summary>
        /// Returns the full DatasetLoadResult with traceability.
        /// Thread-safe; loads once per application lifetime.
        /// </summary>
        public static DatasetLoadResult LoadResult
        {
            get
            {
                if (!_loaded)
                {
                    lock (_lock)
                    {
                        if (!_loaded)
                        {
                            _cachedResult = LoadDataset();
                            _loaded = true;
                        }
                    }
                }
                return _cachedResult!;
            }
        }

        /// <summary>
        /// Forces a reload on the next access (e.g. after a new scan).
        /// </summary>
        public static void Invalidate()
        {
            lock (_lock)
            {
                _cachedResult = null;
                _loaded = false;
            }
        }

        /// <summary>
        /// Invalidates the cache and forces an immediate reload from remote (or cache/embedded) on the next access.
        /// Call this when the user requests "Rafraîchir les exigences"; then re-evaluate or re-open the report to see updated scores.
        /// </summary>
        public static void InvalidateAndReload()
        {
            Invalidate();
            _ = LoadResult; // Force reload now
        }

        /// <summary>
        /// Core loading logic with explicit source selection, fallback policy, and traceability.
        /// </summary>
        private static DatasetLoadResult LoadDataset()
        {
            var info = new DatasetSourceInfo();
            string? url = ReadConfigUrl();
            var mode = ReadConfigMode();

            info.Mode = mode;
            info.UrlConfigured = !string.IsNullOrWhiteSpace(url);
            if (info.UrlConfigured && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                info.UrlHost = uri.Host;

            try
            {
                // 1. Check cache
                var cachedDataset = TryLoadFromCache(out var meta);
                bool hasCachedData = cachedDataset != null;
                double cacheAgeDays = meta != null ? (DateTime.UtcNow - meta.LastFetched).TotalDays : double.MaxValue;
                bool cacheFresh = hasCachedData && cacheAgeDays < DefaultTtlDays;
                bool cacheInGrace = hasCachedData && !cacheFresh && cacheAgeDays < GracePeriodDays;
                bool cacheExpired = hasCachedData && cacheAgeDays >= DefaultTtlDays;

                if (hasCachedData)
                {
                    info.CacheHit = true;
                    info.CacheAgeDays = Math.Round(cacheAgeDays, 1);
                    info.CacheExpired = cacheExpired;
                    info.CacheInGracePeriod = cacheInGrace;
                    info.LastRefresh = meta?.LastFetched.ToString("o");
                }

                // ─────────────────────────────────────
                // CASE A: No URL configured
                // ─────────────────────────────────────
                if (!info.UrlConfigured)
                {
                    // No external URL: use cache if present, otherwise embedded fallback (so out-of-box shows scores).
                    // RequireExternal only forces Unavailable when a URL *is* configured but load/validation fails.
                    if (mode == PerformanceDatasetMode.RequireExternal)
                    {
                        // If we have a cached dataset from a previous session, allow it (stale or fresh)
                        if (hasCachedData)
                        {
                            info.SourceKind = DatasetSourceKind.External;
                            info.VersionDisplay = cachedDataset!.DatasetVersion;
                            info.PublishedAt = cachedDataset.PublishedAt;
                            info.ValidationResult = "pass (cached, no URL currently)";
                            info.FallbackReason = cacheExpired ? "URL non configurée ; cache expiré utilisé (grace)" : null;
                            info.DisplayLabel = cacheExpired ? "Dataset externe (cache expiré)" : "Dataset externe (cache)";
                            info.SourceLine = $"Source: External Dataset | {cachedDataset.DatasetVersion} | cache";
                            LogDebugBlock(info);
                            return new DatasetLoadResult { Dataset = cachedDataset, SourceInfo = info };
                        }
                        // No cache, no URL → embedded fallback (scores visible; label indicates mode secours)
                        return BuildEmbeddedFallbackResult(info, "Aucune URL configurée, aucun cache");
                    }
                    else // AllowFallbackEmbedded
                    {
                        if (hasCachedData)
                        {
                            info.SourceKind = DatasetSourceKind.External;
                            info.VersionDisplay = cachedDataset!.DatasetVersion;
                            info.PublishedAt = cachedDataset.PublishedAt;
                            info.ValidationResult = "pass (cached, no URL)";
                            info.DisplayLabel = "Dataset externe (cache)";
                            info.SourceLine = $"Source: External Dataset | {cachedDataset.DatasetVersion} | cache";
                            LogDebugBlock(info);
                            return new DatasetLoadResult { Dataset = cachedDataset, SourceInfo = info };
                        }
                        // No cache → embedded fallback
                        return BuildEmbeddedFallbackResult(info, "Aucune URL configurée, aucun cache");
                    }
                }

                // ─────────────────────────────────────
                // CASE B: URL configured
                // ─────────────────────────────────────

                // B1. Fresh cache → use it directly
                if (cacheFresh && cachedDataset != null)
                {
                    info.SourceKind = DatasetSourceKind.External;
                    info.VersionDisplay = cachedDataset.DatasetVersion;
                    info.PublishedAt = cachedDataset.PublishedAt;
                    info.ValidationResult = "pass (cache fresh)";
                    info.DisplayLabel = "Dataset externe (cache)";
                    info.SourceLine = $"Source: External Dataset | {cachedDataset.DatasetVersion} | {info.UrlHost} | cache fresh";
                    LogDebugBlock(info);
                    return new DatasetLoadResult { Dataset = cachedDataset, SourceInfo = info };
                }

                // B2. Enforce HTTPS
                if (!url!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    App.LogMessage($"[PerformanceDatasetLoader] Rejected non-HTTPS URL: {url}");
                    info.RemoteFetchError = "URL non-HTTPS rejetée";
                    return HandleFetchFailure(info, mode, cachedDataset, cacheInGrace, "URL non-HTTPS rejetée");
                }

                // B3. Fetch remote
                info.RemoteFetchAttempted = true;
                var fetched = FetchRemote(url, meta?.ETag, out var newETag, out var httpStatus);
                info.RemoteFetchStatus = httpStatus;

                // B3a. 304 Not Modified
                if (httpStatus == 304 && cachedDataset != null)
                {
                    SaveMeta(new CacheMeta { ETag = meta?.ETag ?? "", LastFetched = DateTime.UtcNow });
                    info.SourceKind = DatasetSourceKind.External;
                    info.VersionDisplay = cachedDataset.DatasetVersion;
                    info.PublishedAt = cachedDataset.PublishedAt;
                    info.ValidationResult = "pass (304 Not Modified)";
                    info.CacheExpired = false;
                    info.DisplayLabel = "Dataset externe (304)";
                    info.SourceLine = $"Source: External Dataset | {cachedDataset.DatasetVersion} | {info.UrlHost} | 304";
                    LogDebugBlock(info);
                    return new DatasetLoadResult { Dataset = cachedDataset, SourceInfo = info };
                }

                // B3b. Remote fetched successfully
                if (fetched != null)
                {
                    var errors = PerformanceDatasetValidator.Validate(fetched);
                    if (errors.Count == 0)
                    {
                        // Valid remote dataset
                        SaveCache(fetched, newETag);
                        info.SourceKind = DatasetSourceKind.External;
                        info.VersionDisplay = fetched.DatasetVersion;
                        info.PublishedAt = fetched.PublishedAt;
                        info.ValidationResult = "pass";
                        info.CacheHit = false; // fresh from remote
                        info.LastRefresh = DateTime.UtcNow.ToString("o");
                        info.DisplayLabel = "Dataset externe (remote)";
                        info.SourceLine = $"Source: External Dataset | {fetched.DatasetVersion} | {info.UrlHost} | HTTP {httpStatus}";
                        LogDebugBlock(info);
                        return new DatasetLoadResult { Dataset = fetched, SourceInfo = info };
                    }
                    else
                    {
                        // Remote fetched but validation failed
                        info.RemoteFetchError = $"Validation échouée: {string.Join("; ", errors)}";
                        info.ValidationResult = $"fail ({string.Join("; ", errors)})";
                        App.LogMessage($"[PerformanceDatasetLoader] Remote dataset validation failed: {info.ValidationResult}");
                    }
                }
                else
                {
                    // Remote fetch failed (network error, non-2xx, etc.)
                    if (string.IsNullOrEmpty(info.RemoteFetchError))
                        info.RemoteFetchError = $"HTTP {httpStatus}";
                }

                // B4. Remote failed or invalid → apply fallback policy
                return HandleFetchFailure(info, mode, cachedDataset, cacheInGrace, info.RemoteFetchError ?? "Fetch échoué");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerformanceDatasetLoader] Unexpected error: {ex.Message}");
                info.RemoteFetchError = ex.Message;

                // Last resort: try stale cache
                try
                {
                    var fallback = TryLoadFromCache(out _);
                    if (fallback != null)
                    {
                        if (mode == PerformanceDatasetMode.RequireExternal)
                        {
                            info.SourceKind = DatasetSourceKind.External;
                            info.VersionDisplay = fallback.DatasetVersion;
                            info.PublishedAt = fallback.PublishedAt;
                            info.ValidationResult = "pass (exception fallback cache)";
                            info.FallbackReason = $"Exception: {ex.Message}";
                            info.DisplayLabel = "Dataset externe (cache, exception)";
                            info.SourceLine = $"Source: External Dataset | {fallback.DatasetVersion} | cache (exception)";
                            LogDebugBlock(info);
                            return new DatasetLoadResult { Dataset = fallback, SourceInfo = info };
                        }
                        else
                        {
                            info.SourceKind = DatasetSourceKind.External;
                            info.VersionDisplay = fallback.DatasetVersion;
                            info.PublishedAt = fallback.PublishedAt;
                            info.ValidationResult = "pass (exception fallback cache)";
                            info.FallbackReason = $"Exception: {ex.Message}";
                            info.DisplayLabel = "Dataset externe (cache, exception)";
                            info.SourceLine = $"Source: External Dataset | {fallback.DatasetVersion} | cache (exception)";
                            LogDebugBlock(info);
                            return new DatasetLoadResult { Dataset = fallback, SourceInfo = info };
                        }
                    }
                }
                catch { }

                // No cache either
                if (mode == PerformanceDatasetMode.RequireExternal)
                {
                    info.SourceKind = DatasetSourceKind.Unavailable;
                    info.FallbackReason = $"Exception: {ex.Message}";
                    info.DisplayLabel = "Évaluation indisponible (dataset externe requis)";
                    info.SourceLine = "Source: Indisponible";
                    LogDebugBlock(info);
                    return new DatasetLoadResult { Dataset = null, SourceInfo = info };
                }
                return BuildEmbeddedFallbackResult(info, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the case where remote fetch failed or validation failed.
        /// Applies the configured fallback policy.
        /// </summary>
        private static DatasetLoadResult HandleFetchFailure(
            DatasetSourceInfo info,
            PerformanceDatasetMode mode,
            PerformanceDataset? cachedDataset,
            bool cacheInGrace,
            string failureReason)
        {
            if (mode == PerformanceDatasetMode.RequireExternal)
            {
                // RequireExternal: allow stale cache only within grace period
                if (cachedDataset != null && cacheInGrace)
                {
                    info.SourceKind = DatasetSourceKind.External;
                    info.VersionDisplay = cachedDataset.DatasetVersion;
                    info.PublishedAt = cachedDataset.PublishedAt;
                    info.ValidationResult = "pass (stale cache, grace period)";
                    info.FallbackReason = failureReason;
                    info.DisplayLabel = $"Dataset externe (cache expiré — grace {info.CacheAgeDays:F0}j/{GracePeriodDays}j)";
                    info.SourceLine = $"Source: External Dataset | {cachedDataset.DatasetVersion} | cache expiré (grace)";
                    LogDebugBlock(info);
                    return new DatasetLoadResult { Dataset = cachedDataset, SourceInfo = info };
                }

                // RequireExternal: cache too old or absent → scoring unavailable
                info.SourceKind = DatasetSourceKind.Unavailable;
                info.FallbackReason = failureReason;
                info.DisplayLabel = "Évaluation indisponible (dataset externe requis)";
                info.SourceLine = "Source: Indisponible";
                LogDebugBlock(info);
                return new DatasetLoadResult { Dataset = null, SourceInfo = info };
            }
            else // AllowFallbackEmbedded
            {
                // AllowFallbackEmbedded: use stale cache if present
                if (cachedDataset != null)
                {
                    info.SourceKind = DatasetSourceKind.External;
                    info.VersionDisplay = cachedDataset.DatasetVersion;
                    info.PublishedAt = cachedDataset.PublishedAt;
                    info.ValidationResult = "pass (stale cache)";
                    info.FallbackReason = failureReason;
                    info.DisplayLabel = $"Dataset externe (cache expiré — {info.CacheAgeDays:F0}j)";
                    info.SourceLine = $"Source: External Dataset | {cachedDataset.DatasetVersion} | cache expiré";
                    LogDebugBlock(info);
                    return new DatasetLoadResult { Dataset = cachedDataset, SourceInfo = info };
                }

                // No cache → embedded fallback
                return BuildEmbeddedFallbackResult(info, failureReason);
            }
        }

        /// <summary>
        /// Builds an embedded fallback result with traceability.
        /// Returns the built-in embedded dataset so market benchmark scoring works even without external data.
        /// </summary>
        private static DatasetLoadResult BuildEmbeddedFallbackResult(DatasetSourceInfo info, string reason)
        {
            info.SourceKind = DatasetSourceKind.EmbeddedFallback;
            info.VersionDisplay = $"embedded ({PerformanceEvaluationEngine.TableVersion})";
            info.FallbackReason = reason;
            info.ValidationResult = "n/a (embedded)";
            info.DisplayLabel = $"Mode secours : règles internes";
            info.SourceLine = $"Source: Embedded Fallback | embedded ({PerformanceEvaluationEngine.TableVersion}) | {reason}";
            LogDebugBlock(info);
            return new DatasetLoadResult { Dataset = BuildEmbeddedDataset(), SourceInfo = info };
        }

        /// <summary>
        /// Build the embedded/default PerformanceDataset with market benchmarks.
        /// This ensures that even without an external URL or cache, scoring uses market-based comparison.
        /// Updated: 2026-02. Based on 2025-2026 market hardware landscape.
        /// </summary>
        private static PerformanceDataset BuildEmbeddedDataset()
        {
            return new PerformanceDataset
            {
                DatasetVersion = $"embedded ({PerformanceEvaluationEngine.TableVersion})",
                PublishedAt = "2026-02-12T00:00:00Z",
                MarketBenchmarks = new Dictionary<string, MarketBenchmark>
                {
                    // Bureau: bar very low so any decent PC (8 GB RAM, 4 cores) gets 100%. High-end (95 GB, 24 GB VRAM, Ryzen 9) = 100%.
                    ["office"] = new MarketBenchmark
                    {
                        Label = "Bureau / Navigation",
                        Description = "Office 365, navigateur 20+ onglets, vidéo conférence",
                        Requirements = new ScenarioRequirements
                        {
                            MinCpuCores = 2, MinCpuThreads = 4, MinRamGb = 4, MinGpuVramMb = 0, MinGpuTierOrder = 0, MinStorageTier = 1,
                            RecommendedCpuCores = 4, RecommendedCpuThreads = 8, RecommendedRamGb = 8, RecommendedGpuVramMb = 0, RecommendedGpuTierOrder = 1, RecommendedStorageTier = 2,
                            UltraCpuCores = 4, UltraCpuThreads = 8, UltraRamGb = 8, UltraGpuVramMb = 0, UltraGpuTierOrder = 0, UltraStorageTier = 2,
                            WeightCpu = 0.30, WeightGpu = 0.05, WeightRam = 0.35, WeightStorage = 0.30
                        }
                    },
                    ["multitasking"] = new MarketBenchmark
                    {
                        Label = "Multitâche",
                        Description = "IDE + navigateur + Slack + Excel + Docker simultanément",
                        Requirements = new ScenarioRequirements
                        {
                            MinCpuCores = 4, MinCpuThreads = 8, MinRamGb = 8, MinGpuVramMb = 0, MinGpuTierOrder = 0, MinStorageTier = 1,
                            RecommendedCpuCores = 8, RecommendedCpuThreads = 16, RecommendedRamGb = 16, RecommendedGpuVramMb = 0, RecommendedGpuTierOrder = 1, RecommendedStorageTier = 2,
                            UltraCpuCores = 12, UltraCpuThreads = 24, UltraRamGb = 32, UltraGpuVramMb = 0, UltraGpuTierOrder = 2, UltraStorageTier = 4,
                            WeightCpu = 0.40, WeightGpu = 0.05, WeightRam = 0.40, WeightStorage = 0.15
                        }
                    },
                    ["gaming_1080p"] = new MarketBenchmark
                    {
                        Label = "Jeu 1080p",
                        Description = "Jeux AAA récents (2024-2026) en 1080p 60 FPS, paramètres élevés",
                        Requirements = new ScenarioRequirements
                        {
                            MinCpuCores = 4, MinCpuThreads = 8, MinRamGb = 8, MinGpuVramMb = 4096, MinGpuTierOrder = 2, MinStorageTier = 2,
                            RecommendedCpuCores = 6, RecommendedCpuThreads = 12, RecommendedRamGb = 16, RecommendedGpuVramMb = 8192, RecommendedGpuTierOrder = 3, RecommendedStorageTier = 4,
                            UltraCpuCores = 8, UltraCpuThreads = 16, UltraRamGb = 32, UltraGpuVramMb = 12288, UltraGpuTierOrder = 4, UltraStorageTier = 4,
                            WeightCpu = 0.25, WeightGpu = 0.45, WeightRam = 0.20, WeightStorage = 0.10
                        }
                    },
                    ["gaming_1440p"] = new MarketBenchmark
                    {
                        Label = "Jeu 1440p",
                        Description = "Jeux AAA récents en 1440p 60 FPS, paramètres élevés à ultra",
                        Requirements = new ScenarioRequirements
                        {
                            MinCpuCores = 6, MinCpuThreads = 12, MinRamGb = 16, MinGpuVramMb = 8192, MinGpuTierOrder = 3, MinStorageTier = 2,
                            RecommendedCpuCores = 8, RecommendedCpuThreads = 16, RecommendedRamGb = 16, RecommendedGpuVramMb = 12288, RecommendedGpuTierOrder = 4, RecommendedStorageTier = 4,
                            UltraCpuCores = 12, UltraCpuThreads = 24, UltraRamGb = 32, UltraGpuVramMb = 16384, UltraGpuTierOrder = 5, UltraStorageTier = 4,
                            WeightCpu = 0.20, WeightGpu = 0.55, WeightRam = 0.15, WeightStorage = 0.10
                        }
                    },
                    ["gaming_4k"] = new MarketBenchmark
                    {
                        Label = "Jeu 4K",
                        Description = "Jeux AAA en 4K 60 FPS, paramètres ultra (plus exigeant que 1440p)",
                        Requirements = new ScenarioRequirements
                        {
                            MinCpuCores = 8, MinCpuThreads = 16, MinRamGb = 16, MinGpuVramMb = 12288, MinGpuTierOrder = 4, MinStorageTier = 2,
                            RecommendedCpuCores = 8, RecommendedCpuThreads = 16, RecommendedRamGb = 32, RecommendedGpuVramMb = 16384, RecommendedGpuTierOrder = 5, RecommendedStorageTier = 4,
                            UltraCpuCores = 12, UltraCpuThreads = 24, UltraRamGb = 32, UltraGpuVramMb = 24576, UltraGpuTierOrder = 5, UltraStorageTier = 4,
                            WeightCpu = 0.20, WeightGpu = 0.60, WeightRam = 0.12, WeightStorage = 0.08
                        }
                    },
                    ["4k_editing"] = new MarketBenchmark
                    {
                        Label = "Montage vidéo 4K",
                        Description = "DaVinci Resolve / Premiere Pro, timeline 4K multicouche, rendu GPU",
                        Requirements = new ScenarioRequirements
                        {
                            MinCpuCores = 6, MinCpuThreads = 12, MinRamGb = 16, MinGpuVramMb = 4096, MinGpuTierOrder = 2, MinStorageTier = 2,
                            RecommendedCpuCores = 8, RecommendedCpuThreads = 16, RecommendedRamGb = 32, RecommendedGpuVramMb = 8192, RecommendedGpuTierOrder = 3, RecommendedStorageTier = 4,
                            UltraCpuCores = 16, UltraCpuThreads = 32, UltraRamGb = 64, UltraGpuVramMb = 16384, UltraGpuTierOrder = 4, UltraStorageTier = 4,
                            WeightCpu = 0.30, WeightGpu = 0.30, WeightRam = 0.25, WeightStorage = 0.15
                        }
                    },
                    ["streaming_gaming"] = new MarketBenchmark
                    {
                        Label = "Streaming + Jeu",
                        Description = "Streaming OBS 1080p60 + jeu AAA simultané",
                        Requirements = new ScenarioRequirements
                        {
                            MinCpuCores = 6, MinCpuThreads = 12, MinRamGb = 16, MinGpuVramMb = 6144, MinGpuTierOrder = 2, MinStorageTier = 2,
                            RecommendedCpuCores = 8, RecommendedCpuThreads = 16, RecommendedRamGb = 32, RecommendedGpuVramMb = 8192, RecommendedGpuTierOrder = 3, RecommendedStorageTier = 4,
                            UltraCpuCores = 12, UltraCpuThreads = 24, UltraRamGb = 32, UltraGpuVramMb = 12288, UltraGpuTierOrder = 4, UltraStorageTier = 4,
                            WeightCpu = 0.35, WeightGpu = 0.35, WeightRam = 0.20, WeightStorage = 0.10
                        }
                    },
                    ["vms"] = new MarketBenchmark
                    {
                        Label = "Machines virtuelles",
                        Description = "2-3 VMs simultanées (dev, test, serveur) avec ressources dédiées",
                        Requirements = new ScenarioRequirements
                        {
                            MinCpuCores = 4, MinCpuThreads = 8, MinRamGb = 16, MinGpuVramMb = 0, MinGpuTierOrder = 0, MinStorageTier = 2,
                            RecommendedCpuCores = 8, RecommendedCpuThreads = 16, RecommendedRamGb = 32, RecommendedGpuVramMb = 0, RecommendedGpuTierOrder = 1, RecommendedStorageTier = 4,
                            UltraCpuCores = 16, UltraCpuThreads = 32, UltraRamGb = 64, UltraGpuVramMb = 0, UltraGpuTierOrder = 1, UltraStorageTier = 4,
                            WeightCpu = 0.40, WeightGpu = 0.00, WeightRam = 0.45, WeightStorage = 0.15
                        }
                    },
                    ["ai_inference"] = new MarketBenchmark
                    {
                        Label = "IA (inférence)",
                        Description = "LLM locaux (Llama 7-13B), Stable Diffusion, inférence ML en local",
                        Requirements = new ScenarioRequirements
                        {
                            MinCpuCores = 4, MinCpuThreads = 8, MinRamGb = 16, MinGpuVramMb = 6144, MinGpuTierOrder = 2, MinStorageTier = 2,
                            RecommendedCpuCores = 8, RecommendedCpuThreads = 16, RecommendedRamGb = 32, RecommendedGpuVramMb = 12288, RecommendedGpuTierOrder = 3, RecommendedStorageTier = 4,
                            UltraCpuCores = 12, UltraCpuThreads = 24, UltraRamGb = 64, UltraGpuVramMb = 24576, UltraGpuTierOrder = 4, UltraStorageTier = 4,
                            WeightCpu = 0.15, WeightGpu = 0.55, WeightRam = 0.25, WeightStorage = 0.05
                        }
                    }
                }
            };
        }

        #region Remote fetch

        private static PerformanceDataset? FetchRemote(string url, string? etag, out string? newETag, out int httpStatus)
        {
            newETag = null;
            httpStatus = 0;

            try
            {
                using var handler = new HttpClientHandler();
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PCDiagnosticPro/1.0");

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(etag))
                    request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag));

                var response = client.Send(request);
                httpStatus = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.NotModified)
                    return null;

                if (!response.IsSuccessStatusCode)
                {
                    App.LogMessage($"[PerformanceDatasetLoader] HTTP {httpStatus} from {url}");
                    return null;
                }

                newETag = response.Headers.ETag?.Tag;
                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<PerformanceDataset>(json, options);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerformanceDatasetLoader] Fetch error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Cache

        private static PerformanceDataset? TryLoadFromCache(out CacheMeta? meta)
        {
            meta = null;
            try
            {
                if (!File.Exists(CachePath)) return null;

                var json = File.ReadAllText(CachePath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var dataset = JsonSerializer.Deserialize<PerformanceDataset>(json, options);

                if (File.Exists(MetaPath))
                {
                    var metaJson = File.ReadAllText(MetaPath);
                    meta = JsonSerializer.Deserialize<CacheMeta>(metaJson, options);
                }
                else
                {
                    // Use file write time as proxy
                    meta = new CacheMeta { LastFetched = File.GetLastWriteTimeUtc(CachePath) };
                }

                return dataset;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerformanceDatasetLoader] Cache read error: {ex.Message}");
                return null;
            }
        }

        private static void SaveCache(PerformanceDataset dataset, string? etag)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
                File.WriteAllText(CachePath, JsonSerializer.Serialize(dataset, options));
                SaveMeta(new CacheMeta { ETag = etag ?? "", LastFetched = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerformanceDatasetLoader] Cache write error: {ex.Message}");
            }
        }

        private static void SaveMeta(CacheMeta meta)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta, options));
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerformanceDatasetLoader] Meta write error: {ex.Message}");
            }
        }

        #endregion

        #region Config

        private static string? ReadConfigUrl()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                var json = File.ReadAllText(ConfigPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("PerformanceDatasetUrl", out var urlEl))
                    return urlEl.GetString();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerformanceDatasetLoader] Config read error: {ex.Message}");
            }
            return null;
        }

        private static PerformanceDatasetMode ReadConfigMode()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return PerformanceDatasetMode.RequireExternal;
                var json = File.ReadAllText(ConfigPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("PerformanceDatasetMode", out var modeEl))
                {
                    var modeStr = modeEl.GetString();
                    if (string.Equals(modeStr, "AllowFallbackEmbedded", StringComparison.OrdinalIgnoreCase))
                        return PerformanceDatasetMode.AllowFallbackEmbedded;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerformanceDatasetLoader] Config mode read error: {ex.Message}");
            }
            return PerformanceDatasetMode.RequireExternal;
        }

        #endregion

        #region Debug Logging

        /// <summary>
        /// Logs a single structured debug block with all traceability information.
        /// </summary>
        private static void LogDebugBlock(DatasetSourceInfo info)
        {
            var block = $@"[PerformanceDatasetLoader] ── Source Selection ──
  DatasetMode        = {info.Mode}
  UrlConfigured      = {info.UrlConfigured} ({info.UrlHost ?? "n/a"})
  CacheHit           = {info.CacheHit} (age: {info.CacheAgeDays?.ToString("F1") ?? "n/a"}d, expired: {info.CacheExpired}, grace: {info.CacheInGracePeriod})
  RemoteFetchAttempt = {info.RemoteFetchAttempted} (status: {info.RemoteFetchStatus}, error: {info.RemoteFetchError ?? "none"})
  ValidationResult   = {info.ValidationResult}
  SourceUsed         = {info.SourceKind}
  VersionDisplay     = {info.VersionDisplay}
  PublishedAt        = {info.PublishedAt ?? "n/a"}
  FallbackReason     = {info.FallbackReason ?? "none"}
  DisplayLabel       = {info.DisplayLabel}";

            App.LogMessage(block);
        }

        #endregion

        #region Cache metadata

        private class CacheMeta
        {
            public string ETag { get; set; } = "";
            public DateTime LastFetched { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Validates a PerformanceDataset for required fields, types, and ranges.
    /// </summary>
    public static class PerformanceDatasetValidator
    {
        private static readonly HashSet<string> RequiredScenarioIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "office", "multitasking", "gaming_1080p", "gaming_1440p", "gaming_4k",
            "4k_editing", "streaming_gaming", "vms", "ai_inference"
        };

        /// <summary>
        /// Validate a dataset. Returns empty list if valid, or a list of error descriptions.
        /// </summary>
        public static List<string> Validate(PerformanceDataset? dataset)
        {
            var errors = new List<string>();
            if (dataset == null) { errors.Add("Dataset is null"); return errors; }

            if (string.IsNullOrWhiteSpace(dataset.DatasetVersion))
                errors.Add("DatasetVersion is required");
            if (string.IsNullOrWhiteSpace(dataset.PublishedAt))
                errors.Add("PublishedAt is required");

            // CpuPatterns
            if (dataset.CpuPatterns == null || dataset.CpuPatterns.Count == 0)
                errors.Add("CpuPatterns must be non-empty");
            else
                ValidatePatterns(dataset.CpuPatterns, "CpuPatterns", errors);

            // GpuPatterns
            if (dataset.GpuPatterns == null || dataset.GpuPatterns.Count == 0)
                errors.Add("GpuPatterns must be non-empty");
            else
                ValidatePatterns(dataset.GpuPatterns, "GpuPatterns", errors);

            // CpuHeuristicRules
            if (dataset.CpuHeuristicRules == null)
                errors.Add("CpuHeuristicRules is required");
            else
            {
                var h = dataset.CpuHeuristicRules;
                if (h.HighEndMinCores <= 0) errors.Add("CpuHeuristicRules.HighEndMinCores must be > 0");
                if (h.EntryMinCores <= 0) errors.Add("CpuHeuristicRules.EntryMinCores must be > 0");
            }

            // GpuVramThresholds
            if (dataset.GpuVramThresholds == null)
                errors.Add("GpuVramThresholds is required");
            else
            {
                var g = dataset.GpuVramThresholds;
                if (g.HighEndMinMb <= 0) errors.Add("GpuVramThresholds.HighEndMinMb must be > 0");
                if (g.EntryMinMb <= 0) errors.Add("GpuVramThresholds.EntryMinMb must be > 0");
            }

            // RamTierRules
            if (dataset.RamTierRules == null)
                errors.Add("RamTierRules is required");
            else
            {
                if (dataset.RamTierRules.HighEndMinGb <= 0) errors.Add("RamTierRules.HighEndMinGb must be > 0");
            }

            // StorageTierRules
            if (dataset.StorageTierRules == null)
                errors.Add("StorageTierRules is required");

            // ClassificationThresholds
            if (dataset.ClassificationThresholds == null)
                errors.Add("ClassificationThresholds is required");
            else
            {
                var c = dataset.ClassificationThresholds;
                if (c.NotRecommendedBelow <= 0 || c.NotRecommendedBelow > 100)
                    errors.Add("ClassificationThresholds.NotRecommendedBelow must be 1-100");
                if (c.AcceptableBelow <= c.NotRecommendedBelow)
                    errors.Add("ClassificationThresholds.AcceptableBelow must be > NotRecommendedBelow");
                if (c.GoodBelow <= c.AcceptableBelow)
                    errors.Add("ClassificationThresholds.GoodBelow must be > AcceptableBelow");
            }

            // ScenarioRules — must contain all 9 required IDs (including gaming_4k)
            if (dataset.ScenarioRules == null || dataset.ScenarioRules.Count == 0)
                errors.Add("ScenarioRules must be non-empty");
            else
            {
                foreach (var id in RequiredScenarioIds)
                {
                    if (!dataset.ScenarioRules.ContainsKey(id))
                        errors.Add($"ScenarioRules missing required scenario: {id}");
                }
                foreach (var kvp in dataset.ScenarioRules)
                {
                    if (kvp.Value == null)
                        errors.Add($"ScenarioRules[{kvp.Key}] is null");
                    else if (kvp.Value.Bonuses == null)
                        errors.Add($"ScenarioRules[{kvp.Key}].Bonuses is null");
                }
            }

            // Floors
            if (dataset.Floors == null)
                errors.Add("Floors is required");

            return errors;
        }

        private static void ValidatePatterns(List<PatternRule> patterns, string name, List<string> errors)
        {
            for (int i = 0; i < patterns.Count; i++)
            {
                var p = patterns[i];
                if (string.IsNullOrWhiteSpace(p.Pattern))
                    errors.Add($"{name}[{i}].Pattern is empty");
                if (p.TierOrder < 1 || p.TierOrder > 5)
                    errors.Add($"{name}[{i}].TierOrder must be 1-5, got {p.TierOrder}");
            }
        }
    }
}
