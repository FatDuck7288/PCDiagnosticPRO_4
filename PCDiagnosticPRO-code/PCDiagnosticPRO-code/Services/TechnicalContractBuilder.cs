using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Builds the unified technical contract node injected into scan_result_combined.json.
    /// </summary>
    public static class TechnicalContractBuilder
    {
        public static TechnicalContract Build(CombinedScanResult combined, HealthReport? healthReport = null)
        {
            var contract = new TechnicalContract();
            var gpuCompleteness = GpuCompletenessEvaluator.Evaluate(combined.DiagnosticSnapshot, combined.SensorsCsharp);
            contract.GpuCompleteness = new TechnicalContractGpuCompleteness
            {
                State = gpuCompleteness.State,
                Label = gpuCompleteness.Label,
                Reason = gpuCompleteness.Reason,
                InventoryAvailable = gpuCompleteness.InventoryAvailable,
                TelemetryAvailable = gpuCompleteness.TelemetryAvailable,
                TelemetryCoveragePercent = gpuCompleteness.TelemetryCoveragePercent
            };

            contract.Score = BuildScore(healthReport);
            contract.ServicesStartupTasks = BuildServicesStartupTasks(combined.ScanPowershell);
            contract.CriticalRows = BuildCriticalRows(combined);
            return contract;
        }

        private static TechnicalContractScore BuildScore(HealthReport? healthReport)
        {
            if (healthReport == null)
            {
                return new TechnicalContractScore
                {
                    SourceOfTruth = "UDIS",
                    IsAvailable = false,
                    UnavailableReason = "Score UDIS non calculé au moment de l'écriture."
                };
            }

            var udis = healthReport.UdisReport;
            var isFailClose = healthReport.InsufficientDataForDiagnostic || (udis?.InsufficientDataForDiagnostic == true);
            var score = new TechnicalContractScore
            {
                SourceOfTruth = "UDIS",
                IsAvailable = !isFailClose,
                Value = isFailClose ? null : healthReport.GlobalScore,
                Grade = isFailClose ? null : healthReport.Grade,
                UnavailableReason = isFailClose
                    ? (!string.IsNullOrWhiteSpace(udis?.FailCloseUserMessage) ? udis.FailCloseUserMessage : "Le score est bloqué car des données critiques sont manquantes.")
                    : string.Empty
            };

            if (isFailClose)
            {
                score.FailClose = new TechnicalContractFailClose
                {
                    IsFailClose = true,
                    ReasonCode = udis?.FailCloseReasonCode ?? "CRITICAL_DATA_MISSING",
                    UserMessage = udis?.FailCloseUserMessage ?? "Diagnostic incomplet: relancez le scan avec les permissions administrateur.",
                    Impact = udis?.FailCloseImpact ?? "Le score global est indisponible tant que les données critiques ne sont pas collectées.",
                    Action = udis?.FailCloseAction ?? "Fermez les outils qui bloquent la collecte puis relancez un scan complet."
                };
            }

            return score;
        }

        private static List<TechnicalContractCriticalRow> BuildCriticalRows(CombinedScanResult combined)
        {
            var rows = new List<TechnicalContractCriticalRow>();
            JsonElement root;
            try
            {
                var serialized = JsonSerializer.Serialize(combined, HardwareSensorsResult.JsonOptions);
                using var doc = JsonDocument.Parse(serialized);
                root = doc.RootElement.Clone();
            }
            catch
            {
                using var emptyDoc = JsonDocument.Parse("{}");
                root = emptyDoc.RootElement.Clone();
            }
            foreach (var item in IntegralFieldProvenanceCatalog.GetCriticalCatalog())
            {
                var hasValue = TryReadStringAtPath(root, item.JsonPath, out var rawValue);
                var status = StatusPresentationService.Present(hasValue ? rawValue : null, item.JsonPath);
                rows.Add(new TechnicalContractCriticalRow
                {
                    SectionId = item.SectionId,
                    FieldId = item.FieldId,
                    DisplayLabel = item.FieldId,
                    JsonPath = item.JsonPath,
                    ProvenanceType = IntegralFieldProvenanceCatalog.ToDisplayLabel(hasValue ? item.ProvenanceType : DataProvenanceType.Unavailable).ToLowerInvariant(),
                    Status = new TechnicalContractRowStatus
                    {
                        Label = status.Label,
                        Reason = status.Reason,
                        Confidence = status.Confidence
                    },
                    MissingDataExplained = status.IsMissing
                });
            }

            return rows;
        }

        private static TechnicalContractServicesStartupTasks BuildServicesStartupTasks(JsonElement scanPowershell)
        {
            var summary = new TechnicalContractServicesStartupTasks();
            if (scanPowershell.ValueKind != JsonValueKind.Object)
                return summary;

            var sections = TryGetSections(scanPowershell);
            if (!sections.HasValue || sections.Value.ValueKind != JsonValueKind.Object)
                return summary;

            var sectionRoot = sections.Value;
            if (TryGetDataObject(sectionRoot, "Services", out var servicesData))
            {
                summary.ServicesTotal = GetInt(servicesData, "totalServices");
                summary.ServicesRunning = GetInt(servicesData, "runningServices");
                if (servicesData.TryGetProperty("criticalServices", out var critical) && critical.ValueKind == JsonValueKind.Array)
                    summary.ServicesCritical = critical.GetArrayLength();
            }

            if (TryGetDataObject(sectionRoot, "StartupPrograms", out var startupData))
            {
                summary.StartupTotal = GetInt(startupData, "startupCount");
                if (summary.StartupTotal == null &&
                    startupData.TryGetProperty("startupItems", out var startupItems) &&
                    startupItems.ValueKind == JsonValueKind.Array)
                {
                    summary.StartupTotal = startupItems.GetArrayLength();
                }
            }

            if (TryGetDataObject(sectionRoot, "ScheduledTasks", out var tasksData))
            {
                summary.ScheduledTasksTotal = GetInt(tasksData, "totalTasks");
                summary.ScheduledTasksReady = GetInt(tasksData, "readyTasks");
            }

            return summary;
        }

        private static bool TryReadStringAtPath(JsonElement root, string jsonPath, out string value)
        {
            value = string.Empty;
            if (root.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(jsonPath))
                return false;

            var path = jsonPath.Replace("[", ".", StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
            var tokens = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            JsonElement current = root;
            foreach (var token in tokens)
            {
                if (token.All(char.IsDigit))
                {
                    if (current.ValueKind != JsonValueKind.Array)
                        return false;

                    var idx = int.Parse(token);
                    if (idx < 0 || idx >= current.GetArrayLength())
                        return false;
                    current = current[idx];
                    continue;
                }

                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(token, out var next))
                    return false;
                current = next;
            }

            if (current.ValueKind == JsonValueKind.Null || current.ValueKind == JsonValueKind.Undefined)
                return false;

            value = current.ValueKind == JsonValueKind.String ? current.GetString() ?? string.Empty : current.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static JsonElement? TryGetSections(JsonElement root)
        {
            if (root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Object)
                return sections;
            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("sections", out sections) &&
                sections.ValueKind == JsonValueKind.Object)
            {
                return sections;
            }

            return null;
        }

        private static bool TryGetDataObject(JsonElement sections, string sectionName, out JsonElement data)
        {
            data = default;
            if (!sections.TryGetProperty(sectionName, out var sectionRoot) || sectionRoot.ValueKind != JsonValueKind.Object)
                return false;

            if (sectionRoot.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
            {
                data = dataNode;
                return true;
            }

            if (sectionRoot.ValueKind == JsonValueKind.Object)
            {
                data = sectionRoot;
                return true;
            }

            return false;
        }

        private static int? GetInt(JsonElement data, string name)
        {
            if (!data.TryGetProperty(name, out var value))
                return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var asInt))
                return asInt;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out asInt))
                return asInt;

            return null;
        }
    }
}
