using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.AI;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Tests
{
    public static class ContextPackBuilderTests
    {
        private static readonly List<string> Failures = new();
        private static readonly List<string> Successes = new();

        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            Failures.Clear();
            Successes.Clear();

            Run(nameof(Test_Build_ContextPack_BasicIntegrity), Test_Build_ContextPack_BasicIntegrity);
            Run(nameof(Test_BuildDeterministicPlan_ContainsItems), Test_BuildDeterministicPlan_ContainsItems);
            Run(nameof(Test_LoadFromFile_WithMetrics), Test_LoadFromFile_WithMetrics);

            return (Successes.Count, Failures.Count, Failures.ToList());
        }

        private static void Test_Build_ContextPack_BasicIntegrity()
        {
            var settings = AiSettings.CreateDefaultSafe();
            settings.ContextWindow = 2048;
            settings.MaxTokens = 256;

            var builder = new ContextPackBuilder(settings);
            var combined = BuildSampleCombined();

            var pack = builder.Build(combined);

            Assert(pack.RunId == "run-ctx-001", "RunId should propagate from metadata.");
            Assert(pack.KeyFindings.Count > 0, "Context must contain key findings.");
            Assert(pack.EstimatedTokens > 0, "Estimated tokens should be positive.");
            Assert(pack.EstimatedTokens <= settings.ContextWindow, "Context tokens should fit context window budget.");
        }

        private static void Test_BuildDeterministicPlan_ContainsItems()
        {
            var builder = new ContextPackBuilder(AiSettings.CreateDefaultSafe());
            var combined = BuildSampleCombined();

            var plan = builder.BuildDeterministicPlan(combined);
            Assert(plan.Count > 0, "Deterministic plan should not be empty.");
            Assert(plan.Any(x => !string.IsNullOrWhiteSpace(x.Title)), "Plan should include titled actions.");
        }

        private static void Test_LoadFromFile_WithMetrics()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "pcdx_contextpack_test.json");
            var json = "{\"metadata\":{\"runId\":\"run-ctx-001\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"isAdmin\":true},\"findings\":[{\"type\":\"Driver\",\"severity\":\"high\",\"message\":\"Outdated GPU driver\",\"source\":\"drivers\"}],\"errors\":[],\"missingData\":[],\"missingData_v2\":[],\"sections\":[],\"paths\":{},\"scan_powershell\":{}}";
            File.WriteAllText(tempPath, json);

            try
            {
                var loaded = ContextPackBuilder.LoadFromFile(tempPath, out var bytes, out var parseMs);
                Assert(loaded != null, "LoadFromFile should deserialize CombinedScanResult.");
                Assert(bytes > 0, "LoadFromFile metrics should report json size.");
                Assert(parseMs >= 0, "parseMs must be non-negative.");
                Assert(loaded!.Metadata.RunId == "run-ctx-001", "Loaded runId mismatch.");
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static CombinedScanResult BuildSampleCombined()
        {
            return new CombinedScanResult
            {
                ScanPowershell = JsonDocument.Parse("{}").RootElement.Clone(),
                Metadata = new ScanMetadataExtract
                {
                    RunId = "run-ctx-001",
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    IsAdmin = true,
                    DurationSeconds = 42
                },
                DiagnosticsQuality = new DiagnosticQualityResult
                {
                    CoverageScore = 96,
                    ReliabilityScore = 95
                },
                Findings = new List<FindingExtract>
                {
                    new() { Severity = "high", Type = "Driver", Message = "Outdated GPU driver", Source = "drivers" },
                    new() { Severity = "medium", Type = "Update", Message = "Pending reboot", Source = "updates" }
                },
                DiagnosticSnapshot = new DiagnosticSnapshot
                {
                    Findings = new List<NormalizedFinding>
                    {
                        new()
                        {
                            IssueType = "Kernel-Power instability",
                            Severity = "high",
                            Description = "Kernel-Power 41 observed",
                            SuggestedAction = "Inspect PSU and thermals",
                            AutoFixPossible = false,
                            RiskLevel = "high"
                        },
                        new()
                        {
                            IssueType = "Temp cleanup",
                            Severity = "low",
                            Description = "Temporary files can be cleaned",
                            SuggestedAction = "Remove stale temp files",
                            AutoFixPossible = true,
                            RiskLevel = "low"
                        }
                    }
                }
            };
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Successes.Add(name);
            }
            catch (Exception ex)
            {
                Failures.Add($"{name}: {ex.Message}");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
