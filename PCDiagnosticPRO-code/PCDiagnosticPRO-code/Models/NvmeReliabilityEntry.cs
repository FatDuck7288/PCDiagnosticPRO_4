using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Per-disk reliability data collected via Get-StorageReliabilityCounter (Win8+).
    /// Works for NVMe, SATA, and SAS drives. Source is typed (never sentinel string).
    /// </summary>
    public class NvmeReliabilityEntry
    {
        [JsonPropertyName("friendlyName")]
        public string? FriendlyName { get; set; }

        [JsonPropertyName("serialNumber")]
        public string? SerialNumber { get; set; }

        [JsonPropertyName("busType")]
        public string? BusType { get; set; }

        [JsonPropertyName("mediaType")]
        public string? MediaType { get; set; }

        [JsonPropertyName("sizeBytes")]
        public long? SizeBytes { get; set; }

        [JsonPropertyName("healthStatus")]
        public string? HealthStatus { get; set; }

        [JsonPropertyName("operationalStatus")]
        public string? OperationalStatus { get; set; }

        /// <summary>
        /// Source of data: "StorageReliabilityCounter", "WMI_FailurePredictData", "unavailable_os_version".
        /// </summary>
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        /// <summary>Temperature in Celsius. Null when sensor not exposed.</summary>
        [JsonPropertyName("temperature")]
        public int? TemperatureC { get; set; }

        /// <summary>Wear/endurance indicator in percent (0–100). Lower = more worn. Null = not available.</summary>
        [JsonPropertyName("wear")]
        public int? WearPercent { get; set; }

        [JsonPropertyName("readErrorsTotal")]
        public long? ReadErrorsTotal { get; set; }

        [JsonPropertyName("writeErrorsTotal")]
        public long? WriteErrorsTotal { get; set; }

        [JsonPropertyName("powerOnHours")]
        public long? PowerOnHours { get; set; }

        [JsonPropertyName("readLatencyMaxMs")]
        public long? ReadLatencyMaxMs { get; set; }

        [JsonPropertyName("writeLatencyMaxMs")]
        public long? WriteLatencyMaxMs { get; set; }

        /// <summary>MediaWearoutIndicator — alias for WearPercent, set from Wear field.</summary>
        [JsonPropertyName("mediaWearoutIndicator")]
        public int? MediaWearoutIndicator { get; set; }
    }

    /// <summary>Container for all disk reliability results from StorageReliabilityCounter.</summary>
    public class StorageReliabilityResult
    {
        [JsonPropertyName("disks")]
        public System.Collections.Generic.List<NvmeReliabilityEntry> Disks { get; set; } = new();

        [JsonPropertyName("source")]
        public string Source { get; set; } = "StorageReliabilityCounter";

        [JsonPropertyName("diskCount")]
        public int DiskCount => Disks.Count;
    }
}
