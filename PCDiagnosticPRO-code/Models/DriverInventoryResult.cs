using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Driver inventory collected via Windows WMI (Win32_PnPSignedDriver / Win32_PnPEntity).
    /// Data source is OS-provided, no third-party or proprietary database used.
    /// </summary>
    public class DriverInventoryResult
    {
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = "DriverInventoryCollector";

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }

        [JsonPropertyName("signedCount")]
        public int SignedCount { get; set; }

        [JsonPropertyName("unsignedCount")]
        public int UnsignedCount { get; set; }

        [JsonPropertyName("problemCount")]
        public int ProblemCount { get; set; }

        [JsonPropertyName("byClass")]
        public Dictionary<string, int> ByClass { get; set; } = new();

        [JsonPropertyName("drivers")]
        public List<DriverInventoryItem> Drivers { get; set; } = new();

        // Optional Windows Update lookup (best effort)
        [JsonPropertyName("updateCandidates")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<DriverUpdateCandidate>? UpdateCandidates { get; set; }

        [JsonPropertyName("updateSearchMode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UpdateSearchMode { get; set; }

        [JsonPropertyName("updateSearchError")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UpdateSearchError { get; set; }
    }

    public class DriverInventoryItem
    {
        [JsonPropertyName("deviceClass")]
        public string DeviceClass { get; set; } = "";

        [JsonPropertyName("deviceName")]
        public string DeviceName { get; set; } = "";

        [JsonPropertyName("provider")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Provider { get; set; }

        [JsonPropertyName("manufacturer")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Manufacturer { get; set; }

        [JsonPropertyName("driverVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverVersion { get; set; }

        [JsonPropertyName("driverDate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverDate { get; set; }

        [JsonPropertyName("infName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InfName { get; set; }

        [JsonPropertyName("hardwareIds")]
        public List<string> HardwareIds { get; set; } = new();

        [JsonPropertyName("pnpDeviceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PnpDeviceId { get; set; }

        [JsonPropertyName("isSigned")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsSigned { get; set; }

        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Status { get; set; }

        [JsonPropertyName("updateStatus")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UpdateStatus { get; set; }

        [JsonPropertyName("updateMatch")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DriverUpdateMatch? UpdateMatch { get; set; }
    }

    public class DriverUpdateCandidate
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("driverClass")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverClass { get; set; }

        [JsonPropertyName("driverManufacturer")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverManufacturer { get; set; }

        [JsonPropertyName("driverModel")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverModel { get; set; }

        [JsonPropertyName("driverVerDate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverVerDate { get; set; }

        [JsonPropertyName("driverVerVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverVerVersion { get; set; }

        [JsonPropertyName("driverHardwareId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverHardwareId { get; set; }
    }

    public class DriverUpdateMatch
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("version")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Version { get; set; }

        [JsonPropertyName("date")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Date { get; set; }

        [JsonPropertyName("matchReason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MatchReason { get; set; }
    }
}
