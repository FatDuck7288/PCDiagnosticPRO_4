using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Tests
{
    /// <summary>
    /// Phase 1 tests for Platform/Firmware data-flow:
    /// unit extraction, UI contract validation, and end-to-end HealthReport integration.
    /// </summary>
    public static class PlatformFirmwareTests
    {
        private static readonly List<string> _failures = new();
        private static readonly List<string> _successes = new();

        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            _failures.Clear();
            _successes.Clear();

            RunTest(nameof(Test_Unit_Extraction_WithPresentData), Test_Unit_Extraction_WithPresentData);
            RunTest(nameof(Test_Unit_Extraction_WithMissingData_UsesUnavailableReason), Test_Unit_Extraction_WithMissingData_UsesUnavailableReason);
            RunTest(nameof(Test_Contract_UiCompleteness_PlatformFirmware), Test_Contract_UiCompleteness_PlatformFirmware);
            RunTest(nameof(Test_Integration_HealthReport_ContainsPlatformFirmwareSection), Test_Integration_HealthReport_ContainsPlatformFirmwareSection);

            return (_successes.Count, _failures.Count, _failures.ToList());
        }

        private static void Test_Unit_Extraction_WithPresentData()
        {
            var json = CreateCombinedJson(
                biosVersion: "1.2.3",
                biosDate: "2025-02-01",
                tpmPresent: true,
                tpmVersion: "2.0",
                secureBoot: true);

            using var doc = JsonDocument.Parse(json);
            var evidence = ComprehensiveEvidenceExtractor.Extract(HealthDomain.PlatformFirmware, doc.RootElement, null);

            Assert(evidence.TryGetValue("Version BIOS", out var bios) && bios != null && bios.Contains("1.2.3", StringComparison.Ordinal), "Version BIOS missing");
            Assert(evidence.TryGetValue("TPM", out var tpm) && tpm != null && tpm.Contains("Oui", StringComparison.Ordinal), "TPM should be Oui");
            Assert(evidence.TryGetValue("Secure Boot", out var sb) && sb != null && sb.Contains("Oui", StringComparison.Ordinal), "Secure Boot should be Oui");
            Assert(bios != null && bios.Contains("source:", StringComparison.OrdinalIgnoreCase), "Version BIOS must include source traceability");
        }

        private static void Test_Unit_Extraction_WithMissingData_UsesUnavailableReason()
        {
            var json = CreateCombinedJson(
                biosVersion: null,
                biosDate: null,
                tpmPresent: null,
                tpmVersion: null,
                secureBoot: null);

            using var doc = JsonDocument.Parse(json);
            var evidence = ComprehensiveEvidenceExtractor.Extract(HealthDomain.PlatformFirmware, doc.RootElement, null);

            Assert(evidence.TryGetValue("Version BIOS", out var bios), "Version BIOS key missing");
            Assert(evidence.TryGetValue("TPM", out var tpm), "TPM key missing");
            Assert(evidence.TryGetValue("Secure Boot", out var secureBoot), "Secure Boot key missing");

            Assert(bios != null && bios.StartsWith("Indisponible", StringComparison.OrdinalIgnoreCase), "Version BIOS should be unavailable with reason");
            Assert(tpm != null && tpm.StartsWith("Indisponible", StringComparison.OrdinalIgnoreCase), "TPM should be unavailable with reason");
            Assert(secureBoot != null && secureBoot.StartsWith("Indisponible", StringComparison.OrdinalIgnoreCase), "Secure Boot should be unavailable with reason");
        }

        private static void Test_Contract_UiCompleteness_PlatformFirmware()
        {
            var json = CreateCombinedJson(
                biosVersion: "3.4.5",
                biosDate: "2024-11-15",
                tpmPresent: true,
                tpmVersion: "2.0",
                secureBoot: false);

            using var doc = JsonDocument.Parse(json);
            var report = BuildHealthReportFromSectionsOnly(doc.RootElement);
            var validation = UiCompletenessValidator.Validate(doc.RootElement, report);
            var platformValidation = validation.Validations.FirstOrDefault(v => v.Domain == HealthDomain.PlatformFirmware);

            Assert(platformValidation != null, "PlatformFirmware validation missing");
            Assert(platformValidation!.DataExistsInJson, "PlatformFirmware data should exist in JSON");
            Assert(platformValidation.DataDisplayedInUi, "PlatformFirmware data should be displayed in UI");
            Assert(platformValidation.IsValid, $"PlatformFirmware contract invalid: {string.Join("; ", platformValidation.Warnings)}");
            Assert(platformValidation.MissingFields.Count == 0, "PlatformFirmware has missing required fields");
        }

        private static void Test_Integration_HealthReport_ContainsPlatformFirmwareSection()
        {
            var json = CreateCombinedJson(
                biosVersion: "9.9.9",
                biosDate: "2023-01-01",
                tpmPresent: false,
                tpmVersion: null,
                secureBoot: false);

            using var doc = JsonDocument.Parse(json);
            var report = BuildHealthReportFromSectionsOnly(doc.RootElement);
            var section = report.Sections.FirstOrDefault(s => s.Domain == HealthDomain.PlatformFirmware);

            Assert(section != null, "PlatformFirmware section missing from HealthReport");
            Assert(section!.HasData, "PlatformFirmware section should be flagged as having data");
            Assert(section.EvidenceData.ContainsKey("Version BIOS"), "PlatformFirmware section missing Version BIOS");
            Assert(section.EvidenceData.ContainsKey("TPM"), "PlatformFirmware section missing TPM");
            Assert(section.EvidenceData.ContainsKey("Secure Boot"), "PlatformFirmware section missing Secure Boot");
            Assert(section.EvidenceData["TPM"].Contains("Non", StringComparison.Ordinal), "TPM value should map to Non");
            Assert(section.EvidenceData["Secure Boot"].Contains("Non", StringComparison.Ordinal), "Secure Boot value should map to Non");
        }

        private static HealthReport BuildHealthReportFromSectionsOnly(JsonElement root)
        {
            var method = typeof(HealthReportBuilder).GetMethod(
                "BuildHealthSections",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
                throw new InvalidOperationException("HealthReportBuilder.BuildHealthSections not found via reflection.");

            var score = new ScoreV2Data { Score = 90, Grade = "A", TopPenalties = new List<PenaltyInfo>() };
            var sections = method.Invoke(null, new object?[] { root, score, null }) as List<HealthSection>;
            if (sections == null)
                throw new InvalidOperationException("BuildHealthSections returned null.");

            return new HealthReport { Sections = sections };
        }

        private static string CreateCombinedJson(
            string? biosVersion,
            string? biosDate,
            bool? tpmPresent,
            string? tpmVersion,
            bool? secureBoot)
        {
            var machineIdentityData = new Dictionary<string, object?>();
            var securityData = new Dictionary<string, object?>();

            if (!string.IsNullOrWhiteSpace(biosVersion)) machineIdentityData["biosVersion"] = biosVersion;
            if (!string.IsNullOrWhiteSpace(biosDate)) machineIdentityData["biosDate"] = biosDate;
            if (tpmPresent.HasValue) machineIdentityData["tpmPresent"] = tpmPresent.Value;
            if (!string.IsNullOrWhiteSpace(tpmVersion)) machineIdentityData["tpmVersion"] = tpmVersion;
            if (secureBoot.HasValue) machineIdentityData["secureBoot"] = secureBoot.Value;

            if (tpmPresent.HasValue) securityData["tpmPresent"] = tpmPresent.Value;
            if (secureBoot.HasValue) securityData["secureBootEnabled"] = secureBoot.Value;

            var sections = new Dictionary<string, object?>
            {
                ["MachineIdentity"] = new Dictionary<string, object?>
                {
                    ["status"] = "OK",
                    ["data"] = machineIdentityData
                },
                ["Security"] = new Dictionary<string, object?>
                {
                    ["status"] = "OK",
                    ["data"] = securityData
                }
            };

            var root = new Dictionary<string, object?>
            {
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["runId"] = "platform-firmware-tests",
                    ["timestamp"] = "2026-02-18T12:00:00Z",
                    ["version"] = "7.0",
                    ["partialFailure"] = false
                },
                ["scan_powershell"] = new Dictionary<string, object?>
                {
                    ["sections"] = sections,
                    ["scoreV2"] = new Dictionary<string, object?>
                    {
                        ["score"] = 91,
                        ["grade"] = "A",
                        ["topPenalties"] = Array.Empty<object>()
                    },
                    ["errors"] = Array.Empty<object>(),
                    ["missingData"] = Array.Empty<object>()
                },
                ["errors"] = Array.Empty<object>(),
                ["missingData"] = Array.Empty<object>()
            };

            return JsonSerializer.Serialize(root);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }

        private static void RunTest(string name, Action test)
        {
            try
            {
                test();
                _successes.Add(name);
            }
            catch (Exception ex)
            {
                _failures.Add($"{name}: {ex.Message}");
            }
        }
    }
}
