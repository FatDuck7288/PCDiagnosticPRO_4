using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// PHASE 1.1: DiagnosticSnapshot - Central normalized data structure.
    /// Schema version 2.0.0 contract.
    /// </summary>
    public class DiagnosticSnapshot
    {
        /// <summary>Fixed schema version for contract compliance</summary>
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; } = "2.0.0";

        /// <summary>Generation timestamp (ISO 8601 UTC)</summary>
        [JsonPropertyName("generatedAt")]
        public string GeneratedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>Machine identification</summary>
        [JsonPropertyName("machine")]
        public MachineInfo Machine { get; set; } = new();

        /// <summary>
        /// Metrics organized by group.
        /// Keys: cpu, gpu, storage, memory, network, boot, drivers, whea, power
        /// </summary>
        [JsonPropertyName("metrics")]
        public Dictionary<string, Dictionary<string, NormalizedMetric>> Metrics { get; set; } = new();

        /// <summary>Normalized findings (can be empty)</summary>
        [JsonPropertyName("findings")]
        public List<NormalizedFinding> Findings { get; set; } = new();

        /// <summary>Collection diagnostics and quality</summary>
        [JsonPropertyName("collectionQuality")]
        public CollectionQuality CollectionQuality { get; set; } = new();
    }

    /// <summary>
    /// Machine identification info
    /// </summary>
    public class MachineInfo
    {
        [JsonPropertyName("hostname")]
        public string Hostname { get; set; } = Environment.MachineName;

        [JsonPropertyName("os")]
        public string Os { get; set; } = Environment.OSVersion.ToString();

        [JsonPropertyName("isAdmin")]
        public bool IsAdmin { get; set; }
    }

    /// <summary>
    /// Normalized finding/issue
    /// </summary>
    public class NormalizedFinding
    {
        [JsonPropertyName("issueType")]
        public string IssueType { get; set; } = "";

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "info"; // critical, high, medium, low, info

        [JsonPropertyName("confidence")]
        public int Confidence { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("suggestedAction")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SuggestedAction { get; set; }

        [JsonPropertyName("autoFixPossible")]
        public bool AutoFixPossible { get; set; }

        [JsonPropertyName("riskLevel")]
        public string RiskLevel { get; set; } = "low"; // critical, high, medium, low

        [JsonPropertyName("evidencePaths")]
        public List<string> EvidencePaths { get; set; } = new();
    }

    /// <summary>
    /// Collection quality and diagnostics
    /// </summary>
    public class CollectionQuality
    {
        [JsonPropertyName("totalMetrics")]
        public int TotalMetrics { get; set; }

        [JsonPropertyName("availableMetrics")]
        public int AvailableMetrics { get; set; }

        [JsonPropertyName("unavailableMetrics")]
        public int UnavailableMetrics { get; set; }

        [JsonPropertyName("coveragePercent")]
        public double CoveragePercent { get; set; }

        [JsonPropertyName("errors")]
        public List<CollectionError> Errors { get; set; } = new();

        [JsonPropertyName("signalsCollected")]
        public int SignalsCollected { get; set; }

        [JsonPropertyName("signalsUnavailable")]
        public int SignalsUnavailable { get; set; }
    }

    /// <summary>
    /// Collection error info
    /// </summary>
    public class CollectionError
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "warning";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("isLimitation")]
        public bool IsLimitation { get; set; }
    }
}
