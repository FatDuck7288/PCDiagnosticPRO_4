using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    public class MetricValue<T>
    {
        [JsonPropertyName("value")]
        public T? Value { get; set; }

        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }

    public class GpuMetrics
    {
        [JsonPropertyName("name")]
        public MetricValue<string> Name { get; set; } = new MetricValue<string>();

        [JsonPropertyName("vramTotalMB")]
        public MetricValue<double> VramTotalMB { get; set; } = new MetricValue<double>();

        [JsonPropertyName("vramUsedMB")]
        public MetricValue<double> VramUsedMB { get; set; } = new MetricValue<double>();

        [JsonPropertyName("gpuLoadPercent")]
        public MetricValue<double> GpuLoadPercent { get; set; } = new MetricValue<double>();

        [JsonPropertyName("gpuTempC")]
        public MetricValue<double> GpuTempC { get; set; } = new MetricValue<double>();
        
        /// <summary>
        /// Source of GPU temperature sensor (GPU Core, Hot Spot, etc.)
        /// Useful for debugging Task Manager vs app discrepancies.
        /// </summary>
        [JsonPropertyName("gpuTempSource")]
        public string GpuTempSource { get; set; } = "N/A";
        
        /// <summary>
        /// Source of VRAM Used sensor (D3D Dedicated Memory Used = Task Manager, GPU Memory Used = allocated)
        /// Important: "D3D Dedicated Memory Used" matches Task Manager, others may show higher values (committed/allocated).
        /// </summary>
        [JsonPropertyName("vramUsedSource")]
        public string VramUsedSource { get; set; } = "N/A";
    }

    public class CpuMetrics
    {
        [JsonPropertyName("cpuTempC")]
        public MetricValue<double> CpuTempC { get; set; } = new MetricValue<double>();
        
        /// <summary>Source du capteur température (Package, Tctl, Tdie, etc.)</summary>
        [JsonPropertyName("cpuTempSource")]
        public string CpuTempSource { get; set; } = "N/A";
        
        /// <summary>Charge CPU % (provient du PS, pas de LHM)</summary>
        [JsonPropertyName("cpuLoadPercent")]
        public MetricValue<double> CpuLoadPercent { get; set; } = new MetricValue<double>();
    }

    public class DiskMetrics
    {
        [JsonPropertyName("name")]
        public MetricValue<string> Name { get; set; } = new MetricValue<string>();

        [JsonPropertyName("tempC")]
        public MetricValue<double> TempC { get; set; } = new MetricValue<double>();
    }

    public class HardwareSensorsResult
    {
        [JsonPropertyName("collectedAt")]
        public DateTimeOffset CollectedAt { get; set; }

        [JsonPropertyName("gpu")]
        public GpuMetrics Gpu { get; set; } = new GpuMetrics();

        [JsonPropertyName("cpu")]
        public CpuMetrics Cpu { get; set; } = new CpuMetrics();

        [JsonPropertyName("disks")]
        public List<DiskMetrics> Disks { get; set; } = new List<DiskMetrics>();
        
        /// <summary>
        /// Exceptions encountered during sensor collection (for Defender/WinRing0 detection)
        /// </summary>
        [JsonPropertyName("collectionExceptions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? CollectionExceptions { get; set; }
        
        /// <summary>
        /// Indicates if sensors were blocked by security software (Defender, etc.)
        /// </summary>
        [JsonPropertyName("blockedBySecurity")]
        public bool BlockedBySecurity { get; set; }
        
        /// <summary>
        /// User-friendly message explaining sensor collection issues
        /// </summary>
        [JsonPropertyName("blockingMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BlockingMessage { get; set; }
        
        /// <summary>
        /// Indicates if safe mode was used (no kernel drivers, WMI/PerfCounters only)
        /// Safe mode avoids Windows Defender WinRing0 alerts but has limited sensor access.
        /// </summary>
        [JsonPropertyName("safeModeUsed")]
        public bool SafeModeUsed { get; set; }

        public static JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = true
        };

        public (int available, int total) GetAvailabilitySummary()
        {
            var total = 0;
            var available = 0;

            void Count<T>(MetricValue<T> metric)
            {
                total++;
                if (metric.Available)
                {
                    available++;
                }
            }

            Count(Gpu.Name);
            Count(Gpu.VramTotalMB);
            Count(Gpu.VramUsedMB);
            Count(Gpu.GpuLoadPercent);
            Count(Gpu.GpuTempC);
            Count(Cpu.CpuTempC);

            foreach (var disk in Disks)
            {
                Count(disk.Name);
                Count(disk.TempC);
            }

            return (available, total);
        }
    }
}
