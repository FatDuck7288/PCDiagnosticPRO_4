using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Lightweight history index entry for atomic run listing.
    /// </summary>
    public class ScanIndexEntry
    {
        [JsonPropertyName("runId")]
        public string RunId { get; set; } = string.Empty;

        [JsonPropertyName("startTime")]
        public DateTime StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public DateTime? EndTime { get; set; }

        [JsonPropertyName("status")]
        public ScanStatus Status { get; set; } = ScanStatus.Running;

        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonPropertyName("grade")]
        public string Grade { get; set; } = "N/A";

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }

        [JsonPropertyName("errorSummary")]
        public string? ErrorSummary { get; set; }

        [JsonPropertyName("combinedJsonPath")]
        public string? CombinedJsonPath { get; set; }

        [JsonPropertyName("snapshotPath")]
        public string? SnapshotPath { get; set; }

        [JsonPropertyName("unifiedTxtPath")]
        public string? UnifiedTxtPath { get; set; }

        [JsonPropertyName("combinedSizeBytes")]
        public long CombinedSizeBytes { get; set; }

        [JsonPropertyName("statusReason")]
        public string? StatusReason { get; set; }

        [JsonPropertyName("machineName")]
        public string MachineName { get; set; } = string.Empty;

        [JsonPropertyName("appVersion")]
        public string AppVersion { get; set; } = string.Empty;
    }
}
