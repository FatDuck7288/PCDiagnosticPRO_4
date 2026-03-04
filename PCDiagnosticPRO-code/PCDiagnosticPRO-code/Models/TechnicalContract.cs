using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Versioned technical contract for end-to-end coherence (collection -> JSON -> scoring -> UI).
    /// </summary>
    public class TechnicalContract
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.1.0";

        [JsonPropertyName("generatedAtUtc")]
        public string GeneratedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");

        [JsonPropertyName("requiredFields")]
        public List<string> RequiredFields { get; set; } = new()
        {
            "technical_contract.version",
            "technical_contract.score.sourceOfTruth",
            "technical_contract.score.isAvailable",
            "technical_contract.gpuCompleteness.state",
            "technical_contract.criticalRows",
            "technical_contract.legacyCompatibility",
            "run_status.state"
        };

        [JsonPropertyName("score")]
        public TechnicalContractScore Score { get; set; } = new();

        [JsonPropertyName("gpuCompleteness")]
        public TechnicalContractGpuCompleteness GpuCompleteness { get; set; } = new();

        [JsonPropertyName("criticalRows")]
        public List<TechnicalContractCriticalRow> CriticalRows { get; set; } = new();

        [JsonPropertyName("servicesStartupTasks")]
        public TechnicalContractServicesStartupTasks ServicesStartupTasks { get; set; } = new();

        [JsonPropertyName("legacyCompatibility")]
        public TechnicalContractLegacyCompatibility LegacyCompatibility { get; set; } = new();
    }

    public class TechnicalContractScore
    {
        [JsonPropertyName("sourceOfTruth")]
        public string SourceOfTruth { get; set; } = "UDIS";

        [JsonPropertyName("isAvailable")]
        public bool IsAvailable { get; set; } = false;

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Value { get; set; }

        [JsonPropertyName("grade")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Grade { get; set; }

        [JsonPropertyName("unavailableReason")]
        public string UnavailableReason { get; set; } = string.Empty;

        [JsonPropertyName("failClose")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TechnicalContractFailClose? FailClose { get; set; }
    }

    public class TechnicalContractFailClose
    {
        [JsonPropertyName("isFailClose")]
        public bool IsFailClose { get; set; }

        [JsonPropertyName("reasonCode")]
        public string ReasonCode { get; set; } = string.Empty;

        [JsonPropertyName("userMessage")]
        public string UserMessage { get; set; } = string.Empty;

        [JsonPropertyName("impact")]
        public string Impact { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }

    public class TechnicalContractGpuCompleteness
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = "unavailable";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "Données GPU indisponibles";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonPropertyName("inventoryAvailable")]
        public bool InventoryAvailable { get; set; }

        [JsonPropertyName("telemetryAvailable")]
        public bool TelemetryAvailable { get; set; }

        [JsonPropertyName("telemetryCoveragePercent")]
        public double TelemetryCoveragePercent { get; set; }
    }

    public class TechnicalContractCriticalRow
    {
        [JsonPropertyName("sectionId")]
        public string SectionId { get; set; } = string.Empty;

        [JsonPropertyName("fieldId")]
        public string FieldId { get; set; } = string.Empty;

        [JsonPropertyName("displayLabel")]
        public string DisplayLabel { get; set; } = string.Empty;

        [JsonPropertyName("jsonPath")]
        public string JsonPath { get; set; } = string.Empty;

        [JsonPropertyName("provenanceType")]
        public string ProvenanceType { get; set; } = "collecte";

        [JsonPropertyName("status")]
        public TechnicalContractRowStatus Status { get; set; } = new();

        [JsonPropertyName("missingDataExplained")]
        public bool MissingDataExplained { get; set; }
    }

    public class TechnicalContractRowStatus
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public string Confidence { get; set; } = string.Empty;
    }

    public class TechnicalContractServicesStartupTasks
    {
        [JsonPropertyName("servicesTotal")]
        public int? ServicesTotal { get; set; }

        [JsonPropertyName("servicesRunning")]
        public int? ServicesRunning { get; set; }

        [JsonPropertyName("servicesCritical")]
        public int? ServicesCritical { get; set; }

        [JsonPropertyName("startupTotal")]
        public int? StartupTotal { get; set; }

        [JsonPropertyName("scheduledTasksTotal")]
        public int? ScheduledTasksTotal { get; set; }

        [JsonPropertyName("scheduledTasksReady")]
        public int? ScheduledTasksReady { get; set; }
    }

    public class TechnicalContractLegacyCompatibility
    {
        [JsonPropertyName("compatibilityMode")]
        public string CompatibilityMode { get; set; } = "additive";

        [JsonPropertyName("legacyFieldsRetained")]
        public bool LegacyFieldsRetained { get; set; } = true;

        [JsonPropertyName("aliases")]
        public List<string> Aliases { get; set; } = new()
        {
            "summary.score -> technical_contract.score.value",
            "health_report.globalScore -> technical_contract.score.value",
            "health_report.grade -> technical_contract.score.grade",
            "scoreV2 -> legacy_only"
        };
    }
}
