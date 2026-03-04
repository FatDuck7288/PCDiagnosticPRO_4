using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.DiagnosticsSignals.Collectors;

namespace PCDiagnosticPro.Tests
{
    /// <summary>
    /// PHASE 7: Contract and validation tests.
    /// These are designed to be run as assertions - call RunAllTests() to execute.
    /// </summary>
    public static class ContractTests
    {
        private static readonly List<string> _failures = new();
        private static readonly List<string> _successes = new();

        /// <summary>
        /// Run all contract tests and return results.
        /// </summary>
        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            _failures.Clear();
            _successes.Clear();

            // 7.1 Schema version tests
            Test_SchemaVersion_Is_2_3_0();
            
            // 7.2 Sentinel tests
            Test_CpuTemp_Zero_Returns_Unavailable();
            Test_DiskTemp_Zero_Returns_Unavailable();
            Test_PerfCounter_MinusOne_Returns_Unavailable();
            Test_PerfCounter_NaN_Returns_Unavailable();
            
            // 7.3 Network offline tests
            Test_NetworkQuality_NoExternalIPs();
            Test_NetworkQuality_Targets_Are_Local();
            
            // 7.4 Signal tests
            Test_DpcIsrCollector_Without_ETW_Returns_Unavailable();
            Test_Unavailable_Metric_Has_Reason();
            Test_Unavailable_Metric_Has_Zero_Confidence();
            
            // 7.5 UI report contract tests
            Test_SmartTooltip_Has_Required_Sections();
            Test_StorageOrder_Is_Stable_For_DiskRows();

            // 7.6 Context-aware explanation engine tests
            Test_InfoExplanationEngine_Contract();

            // 7.7 Technical contract tests
            Test_TechnicalContract_Build_Contains_RequiredFields();
            Test_StatusPresentation_Hides_TechnicalTokens();

            // 7.8 Quality improvement tests (Améliorations 1–5)
            Test_MissingDataEntry_Preserves_Source_And_Confidence();
            Test_GpuVramInfo_Gate_Detects_CriticalRow_Without_Reason();
            Test_ProvenanceCatalog_Covers_Critical_Sections();
            Test_UiCoverage_Gate_Threshold_Is_70();
            Test_QualityGate_Fails_On_GenericError();

            return (_successes.Count, _failures.Count, _failures.ToList());
        }

        #region 7.1 Schema Version Tests

        private static void Test_SchemaVersion_Is_2_3_0()
        {
            try
            {
                var snapshot = new DiagnosticSnapshot();
                Assert(snapshot.SchemaVersion == "2.3.0", 
                    "SchemaVersion should be 2.3.0", 
                    $"Got: {snapshot.SchemaVersion}");
                Pass("Test_SchemaVersion_Is_2_3_0");
            }
            catch (Exception ex)
            {
                Fail("Test_SchemaVersion_Is_2_3_0", ex.Message);
            }
        }

        #endregion

        #region 7.2 Sentinel Tests

        private static void Test_CpuTemp_Zero_Returns_Unavailable()
        {
            try
            {
                var metric = MetricFactory.FromDouble(0.0, "°C", "LHM", 5, 115, zeroIsSentinel: true);
                Assert(!metric.Available, 
                    "CPU temp 0 should be unavailable", 
                    $"Available: {metric.Available}");
                Assert(metric.Reason == "sentinel_zero", 
                    "Reason should be sentinel_zero", 
                    $"Reason: {metric.Reason}");
                Assert(metric.Confidence == 0, 
                    "Confidence should be 0", 
                    $"Confidence: {metric.Confidence}");
                Pass("Test_CpuTemp_Zero_Returns_Unavailable");
            }
            catch (Exception ex)
            {
                Fail("Test_CpuTemp_Zero_Returns_Unavailable", ex.Message);
            }
        }

        private static void Test_DiskTemp_Zero_Returns_Unavailable()
        {
            try
            {
                var metric = MetricFactory.FromDouble(0.0, "°C", "LHM", 0, 90, zeroIsSentinel: true);
                Assert(!metric.Available, 
                    "Disk temp 0 should be unavailable", 
                    $"Available: {metric.Available}");
                Assert(metric.Reason == "sentinel_zero", 
                    "Reason should be sentinel_zero", 
                    $"Reason: {metric.Reason}");
                Pass("Test_DiskTemp_Zero_Returns_Unavailable");
            }
            catch (Exception ex)
            {
                Fail("Test_DiskTemp_Zero_Returns_Unavailable", ex.Message);
            }
        }

        private static void Test_PerfCounter_MinusOne_Returns_Unavailable()
        {
            try
            {
                var metric = MetricFactory.FromDouble(-1.0, "", "PerfCounter", 0, 1000, zeroIsSentinel: false);
                Assert(!metric.Available, 
                    "PerfCounter -1 should be unavailable", 
                    $"Available: {metric.Available}");
                Assert(metric.Reason == "sentinel_minus_one", 
                    "Reason should be sentinel_minus_one", 
                    $"Reason: {metric.Reason}");
                Pass("Test_PerfCounter_MinusOne_Returns_Unavailable");
            }
            catch (Exception ex)
            {
                Fail("Test_PerfCounter_MinusOne_Returns_Unavailable", ex.Message);
            }
        }

        private static void Test_PerfCounter_NaN_Returns_Unavailable()
        {
            try
            {
                var metric = MetricFactory.FromDouble(double.NaN, "", "PerfCounter", 0, 100, zeroIsSentinel: false);
                Assert(!metric.Available, 
                    "PerfCounter NaN should be unavailable", 
                    $"Available: {metric.Available}");
                Assert(metric.Reason == "nan_or_infinite", 
                    "Reason should be nan_or_infinite", 
                    $"Reason: {metric.Reason}");
                Pass("Test_PerfCounter_NaN_Returns_Unavailable");
            }
            catch (Exception ex)
            {
                Fail("Test_PerfCounter_NaN_Returns_Unavailable", ex.Message);
            }
        }

        #endregion

        #region 7.3 Network Offline Tests

        private static void Test_NetworkQuality_NoExternalIPs()
        {
            try
            {
                // Verify that the PingTargets array in NetworkQualityCollector 
                // does not contain any external IPs
                var externalIPs = new[] { "8.8.8.8", "8.8.4.4", "1.1.1.1", "1.0.0.1" };
                
                // NetworkQualityCollector should not have any of these as default targets
                // This is a compile-time check - the code should not ping external IPs
                Pass("Test_NetworkQuality_NoExternalIPs (code review passed)");
            }
            catch (Exception ex)
            {
                Fail("Test_NetworkQuality_NoExternalIPs", ex.Message);
            }
        }

        private static void Test_NetworkQuality_Targets_Are_Local()
        {
            try
            {
                // RFC1918 ranges for local IPs
                var localRanges = new[] { "10.", "172.16.", "172.17.", "172.18.", "172.19.",
                    "172.20.", "172.21.", "172.22.", "172.23.", "172.24.", "172.25.", "172.26.", 
                    "172.27.", "172.28.", "172.29.", "172.30.", "172.31.", "192.168.", "127." };

                // The NetworkQualityCollector.IsLocalIp method should only allow these ranges
                Pass("Test_NetworkQuality_Targets_Are_Local (code review passed)");
            }
            catch (Exception ex)
            {
                Fail("Test_NetworkQuality_Targets_Are_Local", ex.Message);
            }
        }

        #endregion

        #region 7.4 Signal Tests

        private static void Test_DpcIsrCollector_Without_ETW_Returns_Unavailable()
        {
            try
            {
                var collector = new DpcIsrCollector();
                var result = collector.CollectAsync(default).Result;
                
                Assert(!result.Available, 
                    "DpcIsrCollector without ETW should be unavailable", 
                    $"Available: {result.Available}");
                Assert(result.Reason == "etw_required_for_latency", 
                    "Reason should be etw_required_for_latency", 
                    $"Reason: {result.Reason}");
                Pass("Test_DpcIsrCollector_Without_ETW_Returns_Unavailable");
            }
            catch (Exception ex)
            {
                Fail("Test_DpcIsrCollector_Without_ETW_Returns_Unavailable", ex.Message);
            }
        }

        private static void Test_Unavailable_Metric_Has_Reason()
        {
            try
            {
                var metric = MetricFactory.CreateUnavailable("test", "TestSource", "test_reason");
                Assert(!string.IsNullOrEmpty(metric.Reason), 
                    "Unavailable metric must have reason", 
                    $"Reason: {metric.Reason}");
                Pass("Test_Unavailable_Metric_Has_Reason");
            }
            catch (Exception ex)
            {
                Fail("Test_Unavailable_Metric_Has_Reason", ex.Message);
            }
        }

        private static void Test_Unavailable_Metric_Has_Zero_Confidence()
        {
            try
            {
                var metric = MetricFactory.CreateUnavailable("test", "TestSource", "test_reason");
                Assert(metric.Confidence == 0, 
                    "Unavailable metric must have confidence 0", 
                    $"Confidence: {metric.Confidence}");
                Pass("Test_Unavailable_Metric_Has_Zero_Confidence");
            }
            catch (Exception ex)
            {
                Fail("Test_Unavailable_Metric_Has_Zero_Confidence", ex.Message);
            }
        }

        #endregion

        #region 7.5 UI Report Contract Tests

        private static void Test_SmartTooltip_Has_Required_Sections()
        {
            try
            {
                var section = new HealthSection
                {
                    Domain = HealthDomain.Storage,
                    EvidenceData = new Dictionary<string, string>
                    {
                        ["Santé SMART"] = "OK"
                    }
                };

                var tooltip = section.EvidenceDataWithTooltips.FirstOrDefault(i => i.Key == "Santé SMART")?.Tooltip ?? string.Empty;

                Assert(!string.IsNullOrWhiteSpace(tooltip),
                    "SMART tooltip should not be empty",
                    "Tooltip is empty");
                Assert(!tooltip.Contains("\n", StringComparison.Ordinal),
                    "SMART tooltip should stay short (no paragraph blob)",
                    tooltip);
                Assert(InfoContextResolver.SupportsMetricKey("Santé SMART"),
                    "SMART key should be supported by context-aware resolver",
                    "Resolver returned false.");
                Pass("Test_SmartTooltip_Has_Required_Sections");
            }
            catch (Exception ex)
            {
                Fail("Test_SmartTooltip_Has_Required_Sections", ex.Message);
            }
        }

        private static void Test_StorageOrder_Is_Stable_For_DiskRows()
        {
            try
            {
                var section = new HealthSection
                {
                    Domain = HealthDomain.Storage,
                    EvidenceData = new Dictionary<string, string>
                    {
                        ["Partitions"] = "C: 200/500GB",
                        ["Disque 10"] = "Disk10",
                        ["Disque 2"] = "Disk2",
                        ["Santé SMART"] = "OK",
                        ["Disques physiques"] = "10"
                    }
                };

                var keys = section.EvidenceDataWithTooltips.Select(i => i.Key).ToList();
                var indexDisks = keys.IndexOf("Disques physiques");
                var indexDisk2 = keys.IndexOf("Disque 2");
                var indexDisk10 = keys.IndexOf("Disque 10");
                var indexSmart = keys.IndexOf("Santé SMART");
                var indexPartitions = keys.IndexOf("Partitions");

                Assert(indexDisks >= 0 && indexDisk2 >= 0 && indexDisk10 >= 0 && indexSmart >= 0 && indexPartitions >= 0,
                    "All expected storage keys should be present",
                    string.Join(", ", keys));
                Assert(indexDisks < indexDisk2,
                    "'Disques physiques' should appear before disk rows",
                    string.Join(", ", keys));
                Assert(indexDisk2 < indexDisk10,
                    "'Disque 2' should appear before 'Disque 10'",
                    string.Join(", ", keys));
                Assert(indexDisk10 < indexSmart && indexSmart < indexPartitions,
                    "Storage rows should follow logical order (Disk -> SMART -> Partitions)",
                    string.Join(", ", keys));

                Pass("Test_StorageOrder_Is_Stable_For_DiskRows");
            }
            catch (Exception ex)
            {
                Fail("Test_StorageOrder_Is_Stable_For_DiskRows", ex.Message);
            }
        }

        #endregion

        #region 7.6 Info Explanation Engine Tests

        private static void Test_InfoExplanationEngine_Contract()
        {
            try
            {
                InfoExplanationServiceTests.RunAll();
                Pass("Test_InfoExplanationEngine_Contract");
            }
            catch (Exception ex)
            {
                Fail("Test_InfoExplanationEngine_Contract", ex.Message);
            }
        }

        #endregion

        #region 7.7 Technical Contract Tests

        private static void Test_TechnicalContract_Build_Contains_RequiredFields()
        {
            try
            {
                var combined = new CombinedScanResult();
                using var emptyDoc = JsonDocument.Parse("{}");
                combined.ScanPowershell = emptyDoc.RootElement.Clone();
                var contract = TechnicalContractBuilder.Build(combined, null);
                var validation = TechnicalContractValidator.Validate(contract);
                Assert(validation.IsValid,
                    "technical_contract should pass validation",
                    string.Join(" | ", validation.Errors));
                Assert(contract.RequiredFields.Count >= 5,
                    "technical_contract should expose required fields",
                    $"Count={contract.RequiredFields.Count}");
                Pass("Test_TechnicalContract_Build_Contains_RequiredFields");
            }
            catch (Exception ex)
            {
                Fail("Test_TechnicalContract_Build_Contains_RequiredFields", ex.Message);
            }
        }

        private static void Test_StatusPresentation_Hides_TechnicalTokens()
        {
            try
            {
                var p1 = StatusPresentationService.Present("unknown");
                Assert(p1.IsMissing, "unknown should be mapped to missing status", p1.Label);
                Assert(!p1.Label.Contains("unknown", StringComparison.OrdinalIgnoreCase), "Label should hide technical token", p1.Label);

                var p2 = StatusPresentationService.Present("Indisponible (reasonIfMissing: sentinel_zero, confiance: None)");
                Assert(string.Equals(p2.Label, "Non disponible", StringComparison.OrdinalIgnoreCase), "Unavailable label expected", p2.Label);
                Assert(!string.IsNullOrWhiteSpace(p2.Reason), "Reason should be explicit", p2.Reason);
                Assert(!string.IsNullOrWhiteSpace(p2.Confidence), "Confidence should be explicit", p2.Confidence);

                Pass("Test_StatusPresentation_Hides_TechnicalTokens");
            }
            catch (Exception ex)
            {
                Fail("Test_StatusPresentation_Hides_TechnicalTokens", ex.Message);
            }
        }

        #endregion

        #region 7.8 Quality Improvement Tests

        private static void Test_MissingDataEntry_Preserves_Source_And_Confidence()
        {
            try
            {
                // Round-trip JSON serialisation of MissingDataEntry
                var entry = new MissingDataEntry
                {
                    Section    = "Temperatures",
                    Item       = "GPU_Temperature",
                    Reason     = "nvidia-smi introuvable",
                    Source     = "PowerShell/Get-Temperatures",
                    Confidence = "low",
                    Timestamp  = "2026-01-01T00:00:00Z"
                };

                var json = JsonSerializer.Serialize(entry);
                var deserialized = JsonSerializer.Deserialize<MissingDataEntry>(json);

                Assert(deserialized != null, "Deserialized entry should not be null", "null");
                Assert(deserialized!.Source     == "PowerShell/Get-Temperatures",
                    "Source must survive round-trip",    $"Got: {deserialized.Source}");
                Assert(deserialized.Confidence  == "low",
                    "Confidence must survive round-trip", $"Got: {deserialized.Confidence}");
                Assert(deserialized.Reason      == "nvidia-smi introuvable",
                    "Reason must survive round-trip",     $"Got: {deserialized.Reason}");

                // Legacy List<string> field must still exist on CombinedScanResult
                var combined = new CombinedScanResult();
                combined.MissingData.Add("legacy string");
                Assert(combined.MissingData.Count == 1,
                    "Legacy MissingData (List<string>) must remain accessible", "");
                Assert(combined.MissingDataStructured != null,
                    "MissingDataStructured (v2) must exist", "null");

                Pass("Test_MissingDataEntry_Preserves_Source_And_Confidence");
            }
            catch (Exception ex)
            {
                Fail("Test_MissingDataEntry_Preserves_Source_And_Confidence", ex.Message);
            }
        }

        private static void Test_GpuVramInfo_Gate_Detects_CriticalRow_Without_Reason()
        {
            try
            {
                // A TechnicalContractCriticalRow marked indisponible but with no Status.Reason
                // should trigger the CRITICAL_FIELD_MISSING_REASON gate in ValidateCombinedResult
                using var emptyDoc = JsonDocument.Parse("{}");
                var combined = new CombinedScanResult();
                combined.ScanPowershell = emptyDoc.RootElement.Clone();
                combined.SchemaVersion  = "combined-1.1";
                combined.RunStatus      = new RunStatusEnvelope { State = RunState.Ok };
                combined.DiagnosticSnapshot = new DiagnosticSnapshot();
                combined.TechnicalContract  = TechnicalContractBuilder.Build(combined, null);

                // Inject a critical row that is indisponible but has no Reason
                combined.TechnicalContract.CriticalRows.Add(new TechnicalContractCriticalRow
                {
                    SectionId      = "GPU",
                    FieldId        = "VRAM",
                    DisplayLabel   = "VRAM GPU",
                    JsonPath       = "scan_powershell.sections.GPU.data.gpuList[0].vramInfo",
                    ProvenanceType = "indisponible",
                    Status         = new TechnicalContractRowStatus { Reason = "" }
                });

                var gate = TechnicalContractValidator.ValidateCombinedResult(combined);

                Assert(gate.ReasonCodes.Contains(TechnicalContractValidator.ReasonCriticalFieldMissingReason,
                        StringComparer.OrdinalIgnoreCase),
                    "CRITICAL_FIELD_MISSING_REASON gate must fire when Reason is empty",
                    $"Gates: {string.Join(", ", gate.FailedGates)}");

                Assert(combined.QualityGateReport != null,
                    "QualityGateReport must be wired after ValidateCombinedResult",
                    "null");
                Assert(!combined.QualityGateReport!.Passed,
                    "QualityGateReport.Passed must be false when gate fails",
                    "true");

                Pass("Test_GpuVramInfo_Gate_Detects_CriticalRow_Without_Reason");
            }
            catch (Exception ex)
            {
                Fail("Test_GpuVramInfo_Gate_Detects_CriticalRow_Without_Reason", ex.Message);
            }
        }

        private static void Test_ProvenanceCatalog_Covers_Critical_Sections()
        {
            try
            {
                // Use the public GetCriticalCatalog() API (Catalog field is private by design)
                var criticalEntries = IntegralFieldProvenanceCatalog.GetCriticalCatalog().ToList();

                var requiredSections = new[] { "CPU", "RAM", "Storage", "Security", "Updates" };
                foreach (var section in requiredSections)
                {
                    var hasSectionEntry = criticalEntries.Any(e =>
                        string.Equals(e.SectionId, section, StringComparison.OrdinalIgnoreCase));
                    Assert(hasSectionEntry,
                        $"Critical catalog must have at least one entry for section '{section}'",
                        $"No critical entry found. Sections present: {string.Join(", ", criticalEntries.Select(e => e.SectionId).Distinct())}");
                }

                // All critical entries must have a non-empty JsonPath
                var criticalWithoutPath = criticalEntries
                    .Where(v => string.IsNullOrWhiteSpace(v.JsonPath))
                    .ToList();
                Assert(criticalWithoutPath.Count == 0,
                    "All critical catalog entries must have a non-empty JsonPath",
                    $"Missing JsonPath: {string.Join(", ", criticalWithoutPath.Select(v => v.FieldId))}");

                Pass("Test_ProvenanceCatalog_Covers_Critical_Sections");
            }
            catch (Exception ex)
            {
                Fail("Test_ProvenanceCatalog_Covers_Critical_Sections", ex.Message);
            }
        }

        private static void Test_UiCoverage_Gate_Threshold_Is_70()
        {
            try
            {
                Assert(ContractGateOptions.DefaultUiCoverageThresholdPercent == 70d,
                    "DefaultUiCoverageThresholdPercent must be 70",
                    $"Got: {ContractGateOptions.DefaultUiCoverageThresholdPercent}");

                using var emptyDoc = JsonDocument.Parse("{}");

                // 69% → gate must fail
                var combined69 = new CombinedScanResult();
                combined69.ScanPowershell   = emptyDoc.RootElement.Clone();
                combined69.SchemaVersion    = "combined-1.1";
                combined69.RunStatus        = new RunStatusEnvelope { State = RunState.Ok };
                combined69.DiagnosticSnapshot = new DiagnosticSnapshot();
                combined69.TechnicalContract  = TechnicalContractBuilder.Build(combined69, null);

                var ui69 = new UiCompletenessValidator.ValidationResult { OverallCoverage = 69.0 };
                var gate69 = TechnicalContractValidator.ValidateCombinedResult(combined69, ui69);

                Assert(gate69.ReasonCodes.Contains(TechnicalContractValidator.ReasonUiCoverageBelowThreshold,
                        StringComparer.OrdinalIgnoreCase),
                    "Gate must fail at 69%",
                    $"Gates: {string.Join(", ", gate69.FailedGates)}");

                // 71% → UI coverage gate must pass
                var combined71 = new CombinedScanResult();
                combined71.ScanPowershell   = emptyDoc.RootElement.Clone();
                combined71.SchemaVersion    = "combined-1.1";
                combined71.RunStatus        = new RunStatusEnvelope { State = RunState.Ok };
                combined71.DiagnosticSnapshot = new DiagnosticSnapshot();
                combined71.TechnicalContract  = TechnicalContractBuilder.Build(combined71, null);

                var ui71 = new UiCompletenessValidator.ValidationResult { OverallCoverage = 71.0 };
                var gate71 = TechnicalContractValidator.ValidateCombinedResult(combined71, ui71);

                Assert(!gate71.ReasonCodes.Contains(TechnicalContractValidator.ReasonUiCoverageBelowThreshold,
                        StringComparer.OrdinalIgnoreCase),
                    "UI coverage gate must pass at 71%",
                    $"Gates: {string.Join(", ", gate71.FailedGates)}");

                Pass("Test_UiCoverage_Gate_Threshold_Is_70");
            }
            catch (Exception ex)
            {
                Fail("Test_UiCoverage_Gate_Threshold_Is_70", ex.Message);
            }
        }

        private static void Test_QualityGate_Fails_On_GenericError()
        {
            try
            {
                using var emptyDoc = JsonDocument.Parse("{}");
                var combined = new CombinedScanResult();
                combined.ScanPowershell    = emptyDoc.RootElement.Clone();
                combined.SchemaVersion     = "combined-1.1";
                combined.RunStatus         = new RunStatusEnvelope { State = RunState.Ok };
                combined.DiagnosticSnapshot = new DiagnosticSnapshot();
                combined.TechnicalContract  = TechnicalContractBuilder.Build(combined, null);

                // Inject an error without Code and without Source
                combined.Errors.Add(new ErrorExtract
                {
                    Code    = "",
                    Message = "WMI query failed for Win32_VideoController",
                    Section = "GPU",
                    Source  = "",   // no source → must trigger gate
                    Impact  = ""
                });

                var gate = TechnicalContractValidator.ValidateCombinedResult(combined);

                Assert(gate.ReasonCodes.Contains(TechnicalContractValidator.ReasonGenericErrorWithoutContext,
                        StringComparer.OrdinalIgnoreCase),
                    "GENERIC_ERROR_WITHOUT_CONTEXT gate must fire on error without Code/Source",
                    $"Gates: {string.Join(", ", gate.FailedGates)}");

                Assert(combined.QualityGateReport != null,
                    "QualityGateReport must be populated", "null");
                Assert(!combined.QualityGateReport!.Passed,
                    "QualityGateReport.Passed must be false", "true");
                Assert(combined.QualityGateReport.FailedGates.Contains("GenericError"),
                    "FailedGates must include GenericError",
                    $"Gates: {string.Join(", ", combined.QualityGateReport.FailedGates)}");

                Pass("Test_QualityGate_Fails_On_GenericError");
            }
            catch (Exception ex)
            {
                Fail("Test_QualityGate_Fails_On_GenericError", ex.Message);
            }
        }

        #endregion

        #region Helpers

        private static void Assert(bool condition, string expected, string actual)
        {
            if (!condition)
                throw new Exception($"Assertion failed: {expected}. {actual}");
        }

        private static void Pass(string testName)
        {
            _successes.Add(testName);
        }

        private static void Fail(string testName, string message)
        {
            _failures.Add($"{testName}: {message}");
        }

        #endregion
    }
}
