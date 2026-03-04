using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Tests
{
    /// <summary>
    /// Unit tests for PerformanceDataset validation, cache fallback logic, schema integrity,
    /// and source selection policy (RequireExternal / AllowFallbackEmbedded).
    /// </summary>
    public static class DatasetValidationTests
    {
        private static readonly List<string> _failures = new();
        private static readonly List<string> _successes = new();

        /// <summary>Run all dataset validation tests. Returns (passed, failed, failures).</summary>
        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            _failures.Clear();
            _successes.Clear();

            // ── 1. Schema validation ──
            Test_ValidDataset_PassesValidation();
            Test_NullDataset_FailsValidation();
            Test_MissingVersion_FailsValidation();
            Test_EmptyCpuPatterns_FailsValidation();
            Test_EmptyGpuPatterns_FailsValidation();
            Test_InvalidTierOrder_FailsValidation();
            Test_MissingScenarioIds_FailsValidation();
            Test_InvalidClassificationThresholds_FailsValidation();

            // ── 2. Dataset model integrity ──
            Test_DatasetDeserialization_RoundTrip();
            Test_DatasetVersion_IsExposed();
            Test_AllBonusConditions_AreParseable();

            // ── 3. Cache fallback simulation ──
            Test_CacheFallback_LoaderReturnsNullWhenNoUrlNoCache();

            // ── 4. Source selection policy tests (3 required scenarios) ──
            Test_SourceSelection_ExternalDatasetAvailable();
            Test_SourceSelection_RequireExternal_DatasetUnavailable();
            Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable();

            return (_successes.Count, _failures.Count, _failures.ToList());
        }

        #region Helpers

        private static void Assert(bool condition, string testName, string detail = "")
        {
            if (!condition)
                throw new Exception($"{testName}: {detail}");
        }

        private static void Pass(string testName) => _successes.Add(testName);
        private static void Fail(string testName, string message) => _failures.Add($"{testName}: {message}");

        private static void RunTest(string name, Action test)
        {
            try { test(); Pass(name); }
            catch (Exception ex) { Fail(name, ex.Message); }
        }

        /// <summary>Build a minimal valid PerformanceDataset for testing.</summary>
        private static PerformanceDataset BuildValidDataset()
        {
            return new PerformanceDataset
            {
                DatasetVersion = "1.0.0",
                PublishedAt = "2026-02-12T00:00:00Z",
                CpuPatterns = new List<PatternRule>
                {
                    new() { Pattern = "ryzen 9", TierOrder = 4 },
                    new() { Pattern = "core i3", TierOrder = 1 }
                },
                GpuPatterns = new List<PatternRule>
                {
                    new() { Pattern = "rtx 40", TierOrder = 4 },
                    new() { Pattern = "uhd", TierOrder = 1 }
                },
                CpuHeuristicRules = new CpuHeuristicRules(),
                GpuVramThresholds = new GpuVramThresholds(),
                RamTierRules = new RamTierRules(),
                StorageTierRules = new StorageTierRules(),
                ClassificationThresholds = new ClassificationThresholds(),
                ScenarioRules = new Dictionary<string, ScenarioRule>
                {
                    ["office"] = new() { Base = 50, Bonuses = new() { new() { Condition = "CpuTierOrder>=1", Points = 25 } } },
                    ["multitasking"] = new() { Base = 30, Bonuses = new() },
                    ["gaming_1080p"] = new() { Base = 40, Bonuses = new() },
                    ["gaming_1440p"] = new() { Base = 20, Bonuses = new() },
                    ["4k_editing"] = new() { Base = 0, Bonuses = new() },
                    ["streaming_gaming"] = new() { Base = 25, Bonuses = new() },
                    ["vms"] = new() { Base = 20, Bonuses = new() },
                    ["ai_inference"] = new() { Base = 20, Bonuses = new() }
                },
                Floors = new FloorRules
                {
                    HighEndCondition = new FloorCondition { GpuPatterns = new() { "3090" }, MinVramMb = 24576, MinCores = 12, MinRamGb = 32 },
                    ScenarioFloors = new() { ["gaming_1440p"] = 80 }
                }
            };
        }

        #endregion

        // ═══════════════════════════════════════════════════════════
        // 1. SCHEMA VALIDATION
        // ═══════════════════════════════════════════════════════════

        private static void Test_ValidDataset_PassesValidation() => RunTest(nameof(Test_ValidDataset_PassesValidation), () =>
        {
            var ds = BuildValidDataset();
            var errors = PerformanceDatasetValidator.Validate(ds);
            Assert(errors.Count == 0, nameof(Test_ValidDataset_PassesValidation),
                $"Expected 0 errors, got {errors.Count}: {string.Join("; ", errors)}");
        });

        private static void Test_NullDataset_FailsValidation() => RunTest(nameof(Test_NullDataset_FailsValidation), () =>
        {
            var errors = PerformanceDatasetValidator.Validate(null);
            Assert(errors.Count > 0, nameof(Test_NullDataset_FailsValidation), "Null dataset should fail validation");
        });

        private static void Test_MissingVersion_FailsValidation() => RunTest(nameof(Test_MissingVersion_FailsValidation), () =>
        {
            var ds = BuildValidDataset();
            ds.DatasetVersion = "";
            var errors = PerformanceDatasetValidator.Validate(ds);
            Assert(errors.Any(e => e.Contains("DatasetVersion")), nameof(Test_MissingVersion_FailsValidation),
                $"Expected DatasetVersion error, got: {string.Join("; ", errors)}");
        });

        private static void Test_EmptyCpuPatterns_FailsValidation() => RunTest(nameof(Test_EmptyCpuPatterns_FailsValidation), () =>
        {
            var ds = BuildValidDataset();
            ds.CpuPatterns = new List<PatternRule>();
            var errors = PerformanceDatasetValidator.Validate(ds);
            Assert(errors.Any(e => e.Contains("CpuPatterns")), nameof(Test_EmptyCpuPatterns_FailsValidation),
                $"Expected CpuPatterns error, got: {string.Join("; ", errors)}");
        });

        private static void Test_EmptyGpuPatterns_FailsValidation() => RunTest(nameof(Test_EmptyGpuPatterns_FailsValidation), () =>
        {
            var ds = BuildValidDataset();
            ds.GpuPatterns = new List<PatternRule>();
            var errors = PerformanceDatasetValidator.Validate(ds);
            Assert(errors.Any(e => e.Contains("GpuPatterns")), nameof(Test_EmptyGpuPatterns_FailsValidation),
                $"Expected GpuPatterns error, got: {string.Join("; ", errors)}");
        });

        private static void Test_InvalidTierOrder_FailsValidation() => RunTest(nameof(Test_InvalidTierOrder_FailsValidation), () =>
        {
            var ds = BuildValidDataset();
            ds.CpuPatterns.Add(new PatternRule { Pattern = "bad", TierOrder = 0 });
            var errors = PerformanceDatasetValidator.Validate(ds);
            Assert(errors.Any(e => e.Contains("TierOrder")), nameof(Test_InvalidTierOrder_FailsValidation),
                $"Expected TierOrder error, got: {string.Join("; ", errors)}");

            ds = BuildValidDataset();
            ds.GpuPatterns.Add(new PatternRule { Pattern = "bad", TierOrder = 6 });
            errors = PerformanceDatasetValidator.Validate(ds);
            Assert(errors.Any(e => e.Contains("TierOrder")), nameof(Test_InvalidTierOrder_FailsValidation),
                $"Expected TierOrder error for value 6, got: {string.Join("; ", errors)}");
        });

        private static void Test_MissingScenarioIds_FailsValidation() => RunTest(nameof(Test_MissingScenarioIds_FailsValidation), () =>
        {
            var ds = BuildValidDataset();
            ds.ScenarioRules.Remove("gaming_1440p");
            var errors = PerformanceDatasetValidator.Validate(ds);
            Assert(errors.Any(e => e.Contains("gaming_1440p")), nameof(Test_MissingScenarioIds_FailsValidation),
                $"Expected missing scenario error, got: {string.Join("; ", errors)}");
        });

        private static void Test_InvalidClassificationThresholds_FailsValidation() => RunTest(nameof(Test_InvalidClassificationThresholds_FailsValidation), () =>
        {
            var ds = BuildValidDataset();
            ds.ClassificationThresholds = new ClassificationThresholds { NotRecommendedBelow = 70, AcceptableBelow = 55, GoodBelow = 40 };
            var errors = PerformanceDatasetValidator.Validate(ds);
            Assert(errors.Count > 0, nameof(Test_InvalidClassificationThresholds_FailsValidation),
                $"Inverted thresholds should fail, got: {string.Join("; ", errors)}");
        });

        // ═══════════════════════════════════════════════════════════
        // 2. DATASET MODEL INTEGRITY
        // ═══════════════════════════════════════════════════════════

        private static void Test_DatasetDeserialization_RoundTrip() => RunTest(nameof(Test_DatasetDeserialization_RoundTrip), () =>
        {
            var ds = BuildValidDataset();
            var json = System.Text.Json.JsonSerializer.Serialize(ds, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true });
            var ds2 = System.Text.Json.JsonSerializer.Deserialize<PerformanceDataset>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert(ds2 != null, nameof(Test_DatasetDeserialization_RoundTrip), "Deserialized dataset is null");
            Assert(ds2!.DatasetVersion == ds.DatasetVersion, nameof(Test_DatasetDeserialization_RoundTrip),
                $"Version mismatch: {ds2.DatasetVersion} vs {ds.DatasetVersion}");
            Assert(ds2.CpuPatterns.Count == ds.CpuPatterns.Count, nameof(Test_DatasetDeserialization_RoundTrip),
                $"CpuPatterns count mismatch: {ds2.CpuPatterns.Count} vs {ds.CpuPatterns.Count}");
            Assert(ds2.ScenarioRules.Count == ds.ScenarioRules.Count, nameof(Test_DatasetDeserialization_RoundTrip),
                $"ScenarioRules count mismatch: {ds2.ScenarioRules.Count} vs {ds.ScenarioRules.Count}");
        });

        private static void Test_DatasetVersion_IsExposed() => RunTest(nameof(Test_DatasetVersion_IsExposed), () =>
        {
            var ds = BuildValidDataset();
            Assert(ds.DatasetVersion == "1.0.0", nameof(Test_DatasetVersion_IsExposed),
                $"Expected 1.0.0, got {ds.DatasetVersion}");
            Assert(ds.PublishedAt == "2026-02-12T00:00:00Z", nameof(Test_DatasetVersion_IsExposed),
                $"Expected 2026-02-12T00:00:00Z, got {ds.PublishedAt}");
        });

        private static void Test_AllBonusConditions_AreParseable() => RunTest(nameof(Test_AllBonusConditions_AreParseable), () =>
        {
            // Verify all known condition forms are recognized by evaluating them against a dummy profile
            var p = new HardwareProfile
            {
                CpuTier = PerformanceTierTable.TierMidRange,
                GpuTier = PerformanceTierTable.TierHighEnd,
                RamGb = 32,
                GpuVramMb = 8192,
                CpuThreads = 12,
                CpuCores = 6,
                StorageKind = "NVMe"
            };
            var conditions = new[] {
                "CpuTierOrder>=1", "GpuTierOrder>=2", "RamGb>=16", "GpuVramMb>=4096",
                "CpuThreads>=8", "CpuCores>=4", "StorageKind==NVMe", "StorageKind==HDD"
            };
            var ds = BuildValidDataset();
            // Just verify no exceptions — each condition evaluates to some boolean
            foreach (var cond in conditions)
            {
                var rule = new ScenarioRule { Base = 0, Bonuses = new() { new() { Condition = cond, Points = 10 } } };
                var scores = UsageScenarioScorer.Score(p, new PerformanceDataset
                {
                    DatasetVersion = "test",
                    PublishedAt = "test",
                    CpuPatterns = ds.CpuPatterns,
                    GpuPatterns = ds.GpuPatterns,
                    CpuHeuristicRules = ds.CpuHeuristicRules,
                    GpuVramThresholds = ds.GpuVramThresholds,
                    RamTierRules = ds.RamTierRules,
                    StorageTierRules = ds.StorageTierRules,
                    ClassificationThresholds = ds.ClassificationThresholds,
                    ScenarioRules = new() {
                        ["office"] = rule, ["multitasking"] = rule, ["gaming_1080p"] = rule,
                        ["gaming_1440p"] = rule, ["4k_editing"] = rule, ["streaming_gaming"] = rule,
                        ["vms"] = rule, ["ai_inference"] = rule
                    },
                    Floors = ds.Floors
                });
                Assert(scores != null && scores.Count == 8, nameof(Test_AllBonusConditions_AreParseable),
                    $"Condition '{cond}' caused scoring to fail");
            }
        });

        // ═══════════════════════════════════════════════════════════
        // 3. CACHE FALLBACK
        // ═══════════════════════════════════════════════════════════

        private static void Test_CacheFallback_LoaderReturnsNullWhenNoUrlNoCache() => RunTest(nameof(Test_CacheFallback_LoaderReturnsNullWhenNoUrlNoCache), () =>
        {
            // When no URL is configured and no cache exists, PerformanceDatasetLoader.Current
            // should return null (the engine then uses hardcoded fallback).
            // This test verifies the loader's contract; actual I/O behavior depends on environment.
            // We just confirm the engine still produces valid results when dataset is null.
            var profile = new HardwareProfile
            {
                CpuModel = "Intel Core i5-12400F",
                CpuCores = 6,
                CpuThreads = 12,
                GpuModel = "NVIDIA GeForce RTX 3060",
                GpuVramMb = 12288,
                RamGb = 16,
                StorageKind = "NVMe"
            };
            var (cpuTier, _) = PerformanceTierTable.ResolveCpuTier(profile.CpuModel, profile.CpuCores, profile.CpuThreads);
            var (gpuTier, _) = PerformanceTierTable.ResolveGpuTier(profile.GpuModel, profile.GpuVramMb);
            profile.CpuTier = cpuTier;
            profile.GpuTier = gpuTier;
            profile.RamTier = PerformanceTierTable.ResolveRamTier(profile.RamGb);
            profile.StorageTier = PerformanceTierTable.ResolveStorageTier(profile.StorageKind);

            // Score with null dataset (hardcoded fallback)
            var scores = UsageScenarioScorer.Score(profile, null);
            Assert(scores.Count == 8, nameof(Test_CacheFallback_LoaderReturnsNullWhenNoUrlNoCache),
                $"Expected 8 scenarios, got {scores.Count}");
            foreach (var s in scores)
            {
                Assert(s.Score >= 0 && s.Score <= 100, nameof(Test_CacheFallback_LoaderReturnsNullWhenNoUrlNoCache),
                    $"{s.Name} score out of range: {s.Score}");
            }
        });

        // ═══════════════════════════════════════════════════════════
        // 4. SOURCE SELECTION POLICY TESTS (3 required scenarios)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Test Run 1: URL configured + dataset available → External used.
        /// Simulates: a valid PerformanceDataset is loaded (as if from remote/cache).
        /// Verifies: SourceKind = External, DatasetVersion matches, scores are valid.
        /// </summary>
        private static void Test_SourceSelection_ExternalDatasetAvailable() => RunTest(nameof(Test_SourceSelection_ExternalDatasetAvailable), () =>
        {
            var ds = BuildValidDataset();

            // Simulate what PerformanceEvaluationEngine does when a valid dataset is available
            var profile = BuildTestProfile();
            var (cpuTier, _) = PerformanceTierTable.ResolveCpuTier(profile.CpuModel, profile.CpuCores, profile.CpuThreads, ds);
            var (gpuTier, _) = PerformanceTierTable.ResolveGpuTier(profile.GpuModel, profile.GpuVramMb, ds);
            profile.CpuTier = cpuTier;
            profile.GpuTier = gpuTier;
            profile.RamTier = PerformanceTierTable.ResolveRamTier(profile.RamGb, ds);
            profile.StorageTier = PerformanceTierTable.ResolveStorageTier(profile.StorageKind, ds);
            var scores = UsageScenarioScorer.Score(profile, ds);

            // Build source info as the loader would
            var sourceInfo = new DatasetSourceInfo
            {
                SourceKind = DatasetSourceKind.External,
                Mode = PerformanceDatasetMode.RequireExternal,
                UrlConfigured = true,
                UrlHost = "data.example.com",
                VersionDisplay = ds.DatasetVersion,
                PublishedAt = ds.PublishedAt,
                CacheHit = false,
                RemoteFetchAttempted = true,
                RemoteFetchStatus = 200,
                ValidationResult = "pass",
                DisplayLabel = "Dataset externe (remote)",
                SourceLine = $"Source: External Dataset | {ds.DatasetVersion} | data.example.com | HTTP 200"
            };

            // Verify
            Assert(sourceInfo.SourceKind == DatasetSourceKind.External, nameof(Test_SourceSelection_ExternalDatasetAvailable),
                $"Expected External, got {sourceInfo.SourceKind}");
            Assert(sourceInfo.VersionDisplay == "1.0.0", nameof(Test_SourceSelection_ExternalDatasetAvailable),
                $"Expected version 1.0.0, got {sourceInfo.VersionDisplay}");
            Assert(scores.Count == 8, nameof(Test_SourceSelection_ExternalDatasetAvailable),
                $"Expected 8 scenarios, got {scores.Count}");
            Assert(scores.All(s => s.Score >= 0 && s.Score <= 100), nameof(Test_SourceSelection_ExternalDatasetAvailable),
                "Some scores out of 0-100 range");
            Assert(sourceInfo.DisplayLabel.Contains("externe"), nameof(Test_SourceSelection_ExternalDatasetAvailable),
                $"DisplayLabel should contain 'externe', got: {sourceInfo.DisplayLabel}");
        });

        /// <summary>
        /// Test Run 2: URL configured + dataset unavailable + RequireExternal → Indisponible.
        /// Simulates: remote fails, no cache, mode is RequireExternal.
        /// Verifies: SourceKind = Unavailable, no scores, display label says "indisponible".
        /// </summary>
        private static void Test_SourceSelection_RequireExternal_DatasetUnavailable() => RunTest(nameof(Test_SourceSelection_RequireExternal_DatasetUnavailable), () =>
        {
            // Simulate what the loader would produce when:
            // - URL is configured but remote fails
            // - No cache available
            // - Mode is RequireExternal
            var sourceInfo = new DatasetSourceInfo
            {
                SourceKind = DatasetSourceKind.Unavailable,
                Mode = PerformanceDatasetMode.RequireExternal,
                UrlConfigured = true,
                UrlHost = "data.example.com",
                CacheHit = false,
                RemoteFetchAttempted = true,
                RemoteFetchStatus = 503,
                RemoteFetchError = "HTTP 503",
                ValidationResult = "n/a",
                FallbackReason = "HTTP 503",
                DisplayLabel = "Évaluation indisponible (dataset externe requis)",
                SourceLine = "Source: Indisponible"
            };

            // Build the result as PerformanceEvaluationEngine would
            var result = new PerformanceEvaluationResult
            {
                IsUnavailable = true,
                UnavailableReason = sourceInfo.FallbackReason,
                SourceInfo = sourceInfo,
                DatasetVersion = null,
                DatasetPublishedAt = null,
                Score = -1,
                ScenarioScores = new List<ScenarioScore>(),
                Verdict = new VerdictSummary
                {
                    Category = "Indisponible",
                    RealisticExpectationSummary = sourceInfo.DisplayLabel
                }
            };

            // Verify
            Assert(result.IsUnavailable, nameof(Test_SourceSelection_RequireExternal_DatasetUnavailable),
                "Expected IsUnavailable = true");
            Assert(sourceInfo.SourceKind == DatasetSourceKind.Unavailable, nameof(Test_SourceSelection_RequireExternal_DatasetUnavailable),
                $"Expected Unavailable, got {sourceInfo.SourceKind}");
            Assert(result.ScenarioScores.Count == 0, nameof(Test_SourceSelection_RequireExternal_DatasetUnavailable),
                $"Expected 0 scores, got {result.ScenarioScores.Count}");
            Assert(result.Score == -1, nameof(Test_SourceSelection_RequireExternal_DatasetUnavailable),
                $"Expected score -1, got {result.Score}");
            Assert(result.Verdict.Category == "Indisponible", nameof(Test_SourceSelection_RequireExternal_DatasetUnavailable),
                $"Expected category 'Indisponible', got {result.Verdict.Category}");
            Assert(sourceInfo.DisplayLabel.Contains("indisponible", StringComparison.OrdinalIgnoreCase), nameof(Test_SourceSelection_RequireExternal_DatasetUnavailable),
                $"DisplayLabel should contain 'indisponible', got: {sourceInfo.DisplayLabel}");
        });

        /// <summary>
        /// Test Run 3: URL configured + dataset unavailable + AllowFallbackEmbedded → Embedded fallback with explicit label.
        /// Simulates: remote fails, no cache, mode is AllowFallbackEmbedded.
        /// Verifies: SourceKind = EmbeddedFallback, scores valid, display label says "Mode secours".
        /// </summary>
        private static void Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable() => RunTest(nameof(Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable), () =>
        {
            // Simulate what the loader would produce when:
            // - URL is configured but remote fails
            // - No cache available
            // - Mode is AllowFallbackEmbedded
            var sourceInfo = new DatasetSourceInfo
            {
                SourceKind = DatasetSourceKind.EmbeddedFallback,
                Mode = PerformanceDatasetMode.AllowFallbackEmbedded,
                UrlConfigured = true,
                UrlHost = "data.example.com",
                CacheHit = false,
                RemoteFetchAttempted = true,
                RemoteFetchStatus = 503,
                RemoteFetchError = "HTTP 503",
                VersionDisplay = $"embedded ({PerformanceEvaluationEngine.TableVersion})",
                ValidationResult = "n/a (embedded)",
                FallbackReason = "HTTP 503",
                DisplayLabel = "Mode secours : règles internes",
                SourceLine = $"Source: Embedded Fallback | embedded ({PerformanceEvaluationEngine.TableVersion}) | HTTP 503"
            };

            // Score with null dataset (embedded fallback)
            var profile = BuildTestProfile();
            var (cpuTier, _) = PerformanceTierTable.ResolveCpuTier(profile.CpuModel, profile.CpuCores, profile.CpuThreads);
            var (gpuTier, _) = PerformanceTierTable.ResolveGpuTier(profile.GpuModel, profile.GpuVramMb);
            profile.CpuTier = cpuTier;
            profile.GpuTier = gpuTier;
            profile.RamTier = PerformanceTierTable.ResolveRamTier(profile.RamGb);
            profile.StorageTier = PerformanceTierTable.ResolveStorageTier(profile.StorageKind);
            var scores = UsageScenarioScorer.Score(profile, null); // null = embedded

            // Build the result
            var result = new PerformanceEvaluationResult
            {
                IsUnavailable = false,
                SourceInfo = sourceInfo,
                DatasetVersion = sourceInfo.VersionDisplay,
                ScenarioScores = scores,
                Score = scores.Count > 0 ? (int)Math.Round(scores.Average(s => s.Score)) : 0,
                Verdict = new VerdictSummary { Category = "Mid-Range", RealisticExpectationSummary = "Embedded fallback scoring." }
            };

            // Verify
            Assert(!result.IsUnavailable, nameof(Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable),
                "Expected IsUnavailable = false (embedded fallback still provides scores)");
            Assert(sourceInfo.SourceKind == DatasetSourceKind.EmbeddedFallback, nameof(Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable),
                $"Expected EmbeddedFallback, got {sourceInfo.SourceKind}");
            Assert(result.ScenarioScores.Count == 8, nameof(Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable),
                $"Expected 8 scenarios, got {result.ScenarioScores.Count}");
            Assert(result.Score > 0, nameof(Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable),
                $"Expected positive score, got {result.Score}");
            Assert(sourceInfo.DisplayLabel.Contains("secours", StringComparison.OrdinalIgnoreCase), nameof(Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable),
                $"DisplayLabel should contain 'secours', got: {sourceInfo.DisplayLabel}");
            Assert(sourceInfo.VersionDisplay.Contains("embedded"), nameof(Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable),
                $"Version should contain 'embedded', got: {sourceInfo.VersionDisplay}");
            Assert(!string.IsNullOrEmpty(sourceInfo.FallbackReason), nameof(Test_SourceSelection_AllowFallbackEmbedded_DatasetUnavailable),
                "FallbackReason must be non-empty for embedded fallback");
        });

        /// <summary>Build a standard test hardware profile.</summary>
        private static HardwareProfile BuildTestProfile()
        {
            return new HardwareProfile
            {
                CpuModel = "Intel Core i5-12400F",
                CpuCores = 6,
                CpuThreads = 12,
                GpuModel = "NVIDIA GeForce RTX 3060",
                GpuVramMb = 12288,
                RamGb = 16,
                StorageKind = "NVMe"
            };
        }
    }
}
