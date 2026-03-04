using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Structural validator for technical_contract node.
    /// </summary>
    public static class TechnicalContractValidator
    {
        public const string ReasonPsSchemaInvalid = "PS_SCHEMA_INVALID";
        public const string ReasonCombinedSchemaInvalid = "COMBINED_SCHEMA_INVALID";
        public const string ReasonTechContractMissingFields = "TECH_CONTRACT_MISSING_FIELDS";
        public const string ReasonUiCoverageBelowThreshold = "UI_COVERAGE_BELOW_THRESHOLD";
        public const string ReasonCriticalFieldMissingReason = "CRITICAL_FIELD_MISSING_REASON";
        public const string ReasonGenericErrorWithoutContext = "GENERIC_ERROR_WITHOUT_CONTEXT";
        // These must match the SectionId values produced by IntegralFieldProvenanceCatalog.GetCriticalCatalog().
        // Previously required CPU/Security/SystemStability/Updates which the catalog never emits,
        // causing TECH_CONTRACT_MISSING_FIELDS on every normal scan.
        private static readonly string[] RequiredContractSections =
        {
            "GPU",
            "PlatformFirmware",
            "Performance",
            "Devices"
        };

        public sealed class ValidationResult
        {
            public bool IsValid => Errors.Count == 0;
            public List<string> Errors { get; } = new();
        }

        public sealed class GateValidationResult
        {
            public bool IsValid => ReasonCodes.Count == 0;
            public List<string> ReasonCodes { get; } = new();
            public List<string> FailedGates { get; } = new();
            public List<string> Messages { get; } = new();
            public double? UiCoveragePercent { get; set; }
            public double? UiCoverageThreshold { get; set; }

            public void Add(string reasonCode, string gateName, string message)
            {
                if (!ReasonCodes.Contains(reasonCode, StringComparer.OrdinalIgnoreCase))
                    ReasonCodes.Add(reasonCode);
                if (!FailedGates.Contains(gateName, StringComparer.OrdinalIgnoreCase))
                    FailedGates.Add(gateName);
                Messages.Add(message);
            }

            public void Merge(GateValidationResult? other)
            {
                if (other == null)
                    return;

                foreach (var r in other.ReasonCodes)
                {
                    if (!ReasonCodes.Contains(r, StringComparer.OrdinalIgnoreCase))
                        ReasonCodes.Add(r);
                }

                foreach (var g in other.FailedGates)
                {
                    if (!FailedGates.Contains(g, StringComparer.OrdinalIgnoreCase))
                        FailedGates.Add(g);
                }

                foreach (var m in other.Messages)
                    Messages.Add(m);

                UiCoveragePercent ??= other.UiCoveragePercent;
                UiCoverageThreshold ??= other.UiCoverageThreshold;
            }

            public RunStatusEnvelope ToRunStatus(string baselineState = RunState.Ok)
            {
                return new RunStatusEnvelope
                {
                    State = IsValid ? baselineState : RunState.Incomplete,
                    ReasonCodes = ReasonCodes.ToList(),
                    FailedGates = FailedGates.ToList(),
                    UiCoveragePercent = UiCoveragePercent,
                    Threshold = UiCoverageThreshold,
                    Messages = Messages.Count == 0 ? null : Messages.ToList()
                };
            }
        }

        public static ValidationResult Validate(TechnicalContract? contract)
        {
            var result = new ValidationResult();
            if (contract == null)
            {
                result.Errors.Add("technical_contract absent.");
                return result;
            }

            if (string.IsNullOrWhiteSpace(contract.Version))
                result.Errors.Add("technical_contract.version absent.");

            if (contract.Score == null)
            {
                result.Errors.Add("technical_contract.score absent.");
            }
            else
            {
                if (!string.Equals(contract.Score.SourceOfTruth, "UDIS", StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add("technical_contract.score.sourceOfTruth must be UDIS.");

                if (!contract.Score.IsAvailable && string.IsNullOrWhiteSpace(contract.Score.UnavailableReason))
                    result.Errors.Add("technical_contract.score.unavailableReason required when score is unavailable.");
            }

            if (contract.GpuCompleteness == null || string.IsNullOrWhiteSpace(contract.GpuCompleteness.State))
                result.Errors.Add("technical_contract.gpuCompleteness.state absent.");

            if (contract.CriticalRows == null || contract.CriticalRows.Count == 0)
            {
                result.Errors.Add("technical_contract.criticalRows must contain at least one critical field.");
            }
            else
            {
                foreach (var row in contract.CriticalRows.Where(r =>
                             string.IsNullOrWhiteSpace(r.SectionId) ||
                             string.IsNullOrWhiteSpace(r.FieldId) ||
                             string.IsNullOrWhiteSpace(r.JsonPath) ||
                             string.IsNullOrWhiteSpace(r.ProvenanceType)))
                {
                    result.Errors.Add($"technical_contract.criticalRows invalid row: {row?.SectionId}/{row?.FieldId}");
                }

                var coveredSections = contract.CriticalRows
                    .Where(r => !string.IsNullOrWhiteSpace(r.SectionId))
                    .Select(r => r.SectionId!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var requiredSection in RequiredContractSections)
                {
                    if (!coveredSections.Contains(requiredSection))
                    {
                        result.Errors.Add($"technical_contract.criticalRows missing minimum coverage for section '{requiredSection}'.");
                    }
                }
            }

            if (contract.LegacyCompatibility == null || !contract.LegacyCompatibility.LegacyFieldsRetained)
                result.Errors.Add("technical_contract.legacyCompatibility.LegacyFieldsRetained must be true.");

            return result;
        }

        public static GateValidationResult ValidatePowerShellSnapshot(JsonElement psRoot)
        {
            var result = new GateValidationResult();

            if (psRoot.ValueKind != JsonValueKind.Object)
            {
                result.Add(ReasonPsSchemaInvalid, "ps_shape", "PowerShell snapshot root must be a JSON object.");
                return result;
            }

            if (!TryGetPropertyCaseInsensitive(psRoot, out var sections, "sections") || sections.ValueKind != JsonValueKind.Object)
            {
                result.Add(ReasonPsSchemaInvalid, "ps_shape", "PowerShell snapshot missing sections object.");
            }

            if (!TryGetPropertyCaseInsensitive(psRoot, out var metadata, "metadata") || metadata.ValueKind != JsonValueKind.Object)
            {
                result.Add(ReasonPsSchemaInvalid, "ps_metadata", "PowerShell snapshot missing metadata object.");
            }
            else
            {
                var version = GetString(metadata, "version");
                if (string.IsNullOrWhiteSpace(version))
                    result.Add(ReasonPsSchemaInvalid, "ps_metadata", "PowerShell snapshot metadata.version missing.");
            }

            return result;
        }

        public static GateValidationResult ValidateCombinedResult(
            CombinedScanResult? combined,
            UiCompletenessValidator.ValidationResult? uiValidation = null,
            ContractGateOptions? options = null)
        {
            var gate = new GateValidationResult();
            var cfg = options ?? new ContractGateOptions();

            if (combined == null)
            {
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "Combined result is null.");
                return gate;
            }

            if (string.IsNullOrWhiteSpace(combined.SchemaVersion) ||
                !combined.SchemaVersion.StartsWith("combined-", StringComparison.OrdinalIgnoreCase))
            {
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "Combined schemaVersion invalid.");
            }

            if (combined.RunStatus == null || string.IsNullOrWhiteSpace(combined.RunStatus.State))
            {
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "run_status.state missing in combined result.");
            }

            if (combined.DiagnosticSnapshot == null)
            {
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "diagnostic_snapshot missing.");
            }
            else if (string.IsNullOrWhiteSpace(combined.DiagnosticSnapshot.SchemaVersion))
            {
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "diagnostic_snapshot.schemaVersion missing.");
            }

            var contractValidation = Validate(combined.TechnicalContract);
            if (!contractValidation.IsValid)
            {
                foreach (var err in contractValidation.Errors)
                    gate.Messages.Add(err);
                gate.Add(ReasonTechContractMissingFields, "technical_contract", "technical_contract missing required fields.");
            }

            if (uiValidation != null)
            {
                gate.UiCoveragePercent = uiValidation.OverallCoverage;
                gate.UiCoverageThreshold = cfg.UiCoverageThresholdPercent;
                if (uiValidation.OverallCoverage < cfg.UiCoverageThresholdPercent)
                {
                    gate.Add(
                        ReasonUiCoverageBelowThreshold,
                        "ui_coverage",
                        $"UI coverage {uiValidation.OverallCoverage:F1}% is below threshold {cfg.UiCoverageThresholdPercent:F1}%.");
                }
            }

            // GATE: champ critique marqué indisponible sans raison structurée
            foreach (var row in combined.TechnicalContract?.CriticalRows ?? new List<TechnicalContractCriticalRow>())
            {
                if (string.Equals(row.ProvenanceType, "indisponible", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(row.Status?.Reason))
                {
                    gate.Add(
                        ReasonCriticalFieldMissingReason,
                        "CriticalFieldNoReason",
                        $"Champ critique '{row.DisplayLabel}' ({row.SectionId}/{row.FieldId}) indisponible sans raison structurée.");
                }
            }

            // GATE: erreur de collecte sans code exploitable ni source
            foreach (var err in combined.Errors)
            {
                if (string.IsNullOrWhiteSpace(err.Code)
                    || err.Code.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(err.Source))
                {
                    var snippet = err.Message != null
                        ? err.Message.Substring(0, Math.Min(60, err.Message.Length))
                        : "";
                    gate.Add(
                        ReasonGenericErrorWithoutContext,
                        "GenericError",
                        $"Erreur sans code/source : '{snippet}'");
                }
            }

            // Wire QualityGateReport (nœud toujours présent dans le JSON combiné)
            combined.QualityGateReport = new QualityGateReport
            {
                Passed = gate.IsValid,
                FailedGates = gate.FailedGates.ToList(),
                Messages = gate.Messages.ToList(),
                UiCoveragePercent = gate.UiCoveragePercent,
                GeneratedAt = DateTime.UtcNow.ToString("O")
            };

            return gate;
        }

        public static GateValidationResult ValidateCombinedJsonRoot(
            JsonElement combinedRoot,
            UiCompletenessValidator.ValidationResult? uiValidation = null,
            ContractGateOptions? options = null)
        {
            var gate = new GateValidationResult();
            var cfg = options ?? new ContractGateOptions();

            if (combinedRoot.ValueKind != JsonValueKind.Object)
            {
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "Combined JSON root must be an object.");
                return gate;
            }

            if (!TryGetPropertyCaseInsensitive(combinedRoot, out var schemaVersionEl, "schemaVersion") ||
                schemaVersionEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(schemaVersionEl.GetString()))
            {
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "schemaVersion missing in combined JSON.");
            }

            if (!TryGetPropertyCaseInsensitive(combinedRoot, out var runStatusEl, "run_status", "runStatus") ||
                runStatusEl.ValueKind != JsonValueKind.Object ||
                !TryGetPropertyCaseInsensitive(runStatusEl, out var runStateEl, "state") ||
                runStateEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(runStateEl.GetString()))
            {
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "run_status.state missing in combined JSON.");
            }

            if (!TryGetPropertyCaseInsensitive(combinedRoot, out _, "scan_powershell", "scanPowershell"))
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "scan_powershell missing in combined JSON.");

            if (!TryGetPropertyCaseInsensitive(combinedRoot, out _, "diagnostic_snapshot", "diagnosticSnapshot"))
                gate.Add(ReasonCombinedSchemaInvalid, "combined_shape", "diagnostic_snapshot missing in combined JSON.");

            if (!TryGetPropertyCaseInsensitive(combinedRoot, out var contractEl, "technical_contract", "technicalContract"))
            {
                gate.Add(ReasonTechContractMissingFields, "technical_contract", "technical_contract missing in combined JSON.");
            }
            else
            {
                try
                {
                    var contract = JsonSerializer.Deserialize<TechnicalContract>(contractEl.GetRawText(), HardwareSensorsResult.JsonOptions);
                    var validation = Validate(contract);
                    if (!validation.IsValid)
                    {
                        foreach (var err in validation.Errors)
                            gate.Messages.Add(err);
                        gate.Add(ReasonTechContractMissingFields, "technical_contract", "technical_contract structure invalid.");
                    }
                }
                catch (Exception ex)
                {
                    gate.Add(ReasonTechContractMissingFields, "technical_contract", $"technical_contract parse error: {ex.Message}");
                }
            }

            if (uiValidation != null)
            {
                gate.UiCoveragePercent = uiValidation.OverallCoverage;
                gate.UiCoverageThreshold = cfg.UiCoverageThresholdPercent;
                if (uiValidation.OverallCoverage < cfg.UiCoverageThresholdPercent)
                {
                    gate.Add(
                        ReasonUiCoverageBelowThreshold,
                        "ui_coverage",
                        $"UI coverage {uiValidation.OverallCoverage:F1}% is below threshold {cfg.UiCoverageThresholdPercent:F1}%.");
                }
            }

            return gate;
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement element, out JsonElement value, params string[] names)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out value))
                    return true;
            }

            foreach (var prop in element.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string? GetString(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
                return direct.GetString();

            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    return prop.Value.GetString();
                }
            }

            return null;
        }
    }
}
