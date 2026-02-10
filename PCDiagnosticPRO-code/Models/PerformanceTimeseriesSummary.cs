using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Min/Max/Avg over a sampling interval for a single metric.
    /// </summary>
    public class MinMaxAvg
    {
        [JsonPropertyName("min")]
        public double Min { get; set; }
        [JsonPropertyName("max")]
        public double Max { get; set; }
        [JsonPropertyName("avg")]
        public double Avg { get; set; }
    }

    /// <summary>
    /// Performance timeseries summary: aggregates (min/max/avg) over a 10-30s sampling window.
    /// </summary>
    public class PerformanceTimeseriesSummary
    {
        [JsonPropertyName("interval_seconds")]
        public int IntervalSeconds { get; set; }
        [JsonPropertyName("sample_count")]
        public int SampleCount { get; set; }
        [JsonPropertyName("cpu_percent")]
        public MinMaxAvg? CpuPercent { get; set; }
        [JsonPropertyName("memory_available_mb")]
        public MinMaxAvg? MemoryAvailableMB { get; set; }
        [JsonPropertyName("memory_committed_percent")]
        public MinMaxAvg? MemoryCommittedPercent { get; set; }
        [JsonPropertyName("disk_read_bytes_per_sec")]
        public MinMaxAvg? DiskReadBytesPerSec { get; set; }
        [JsonPropertyName("disk_write_bytes_per_sec")]
        public MinMaxAvg? DiskWriteBytesPerSec { get; set; }
        [JsonPropertyName("disk_queue_length")]
        public MinMaxAvg? DiskQueueLength { get; set; }
        [JsonPropertyName("network_bytes_per_sec")]
        public MinMaxAvg? NetworkBytesPerSec { get; set; }
        [JsonPropertyName("gpu_utilization_percent")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MinMaxAvg? GpuUtilizationPercent { get; set; }
    }
}
