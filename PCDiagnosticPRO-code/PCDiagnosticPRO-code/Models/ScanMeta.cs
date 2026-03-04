using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Run status enum shared between ScanMeta (disk storage) and ScanHistoryItem (UI).
    /// </summary>
    public enum ScanStatus
    {
        Running   = 0,
        Success   = 1,
        Partial   = 2,
        Failed    = 3,
        Cancelled = 4
    }

    /// <summary>
    /// Lightweight metadata persisted to meta.json in each run folder.
    /// Designed for FAST history loading — no need to parse the full 50MB combined JSON.
    /// Full data lives in scan_result_combined.json in the same folder.
    /// </summary>
    public class ScanMeta
    {
        public string RunId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string MachineName { get; set; } = string.Empty;
        public int Score { get; set; }
        public string Grade { get; set; } = "N/A";

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ScanStatus Status { get; set; } = ScanStatus.Running;

        public double DurationSeconds { get; set; }
        public string AppVersion { get; set; } = string.Empty;
        public string? ErrorSummary { get; set; }
        public int TotalItems { get; set; }
        public int OkCount { get; set; }
        public int WarnCount { get; set; }
        public int ErrorCount { get; set; }
        public int CriticalCount { get; set; }
        public string? SnapshotPath { get; set; }
        public string? UnifiedTxtPath { get; set; }
        public long CombinedSizeBytes { get; set; }
        public string? StatusReason { get; set; }
        public Dictionary<string, long>? TimingsDigest { get; set; }

        /// <summary>
        /// Resolved at runtime (not persisted) — path to scan_result_combined.json in the run folder.
        /// Set by ScanStorageService.EnumerateScans() when the file exists.
        /// </summary>
        [JsonIgnore]
        public string? CombinedJsonPath { get; set; }
    }
}
