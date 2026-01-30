using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    public static class NormalizedMetricsBuilder
    {
        public static List<NormalizedMetric> Build(JsonElement psRoot, HardwareSensorsResult? sensors, HealthReport? report)
        {
            var metrics = new List<NormalizedMetric>();
            var now = DateTimeOffset.Now;

            if (sensors != null)
            {
                metrics.Add(CreateMetric("CPU Temperature (C#)", sensors.Cpu.CpuTempC, sensors.Cpu.CpuTempSource, sensors.CollectedAt));
                metrics.Add(CreateMetric("GPU Temperature (C#)", sensors.Gpu.GpuTempC, sensors.Gpu.GpuTempC.Source ?? "LibreHardwareMonitor", sensors.CollectedAt));
                metrics.Add(CreateMetric("GPU Load (C#)", sensors.Gpu.GpuLoadPercent, sensors.Gpu.GpuLoadPercent.Source ?? "LibreHardwareMonitor", sensors.CollectedAt));
                metrics.Add(CreateMetric("VRAM Total (C#)", sensors.Gpu.VramTotalMB, sensors.Gpu.VramTotalMB.Source ?? "LibreHardwareMonitor", sensors.CollectedAt));
                metrics.Add(CreateMetric("VRAM Used (C#)", sensors.Gpu.VramUsedMB, sensors.Gpu.VramUsedMB.Source ?? "LibreHardwareMonitor", sensors.CollectedAt));

                foreach (var disk in sensors.Disks)
                {
                    var diskName = disk.Name.Value ?? "Disk";
                    metrics.Add(CreateMetric($"Disk Temp (C#) - {diskName}", disk.TempC, disk.TempC.Source ?? "LibreHardwareMonitor", sensors.CollectedAt));
                }
            }

            // Performance counters (PS)
            if (TryGetPerfCounterValue(psRoot, "diskQueueLength", out var queueLength, out var queueSource, out var queueReason))
            {
                metrics.Add(new NormalizedMetric
                {
                    Name = "Disk Queue Length (PS)",
                    Value = queueLength?.ToString("F2"),
                    Available = queueLength.HasValue,
                    Source = queueSource ?? "PowerShell",
                    Reason = queueReason,
                    Timestamp = now
                });
            }

            // Process list availability
            var processMissingReason = TryGetMissingDataReason(psRoot, "ProcessList");
            if (!string.IsNullOrEmpty(processMissingReason))
            {
                metrics.Add(new NormalizedMetric
                {
                    Name = "Process List (PS)",
                    Available = false,
                    Source = "PowerShell",
                    Reason = processMissingReason,
                    Timestamp = now
                });
            }
            else if (TryGetProcessesSource(psRoot, out var processSource))
            {
                metrics.Add(new NormalizedMetric
                {
                    Name = "Process List (PS)",
                    Value = "Collected",
                    Available = true,
                    Source = processSource,
                    Timestamp = now
                });
            }

            // Network speed test (UDIS)
            if (report?.UdisReport != null)
            {
                var udis = report.UdisReport;
                metrics.Add(new NormalizedMetric
                {
                    Name = "Network Download (UDIS)",
                    Value = udis.DownloadMbps?.ToString("F1"),
                    Available = udis.DownloadMbps.HasValue,
                    Source = "NetworkRealSpeedAnalyzer",
                    Reason = udis.DownloadMbps.HasValue ? null : udis.NetworkRecommendation,
                    Timestamp = now
                });
                metrics.Add(new NormalizedMetric
                {
                    Name = "Network Latency (UDIS)",
                    Value = udis.LatencyMs?.ToString("F0"),
                    Available = udis.LatencyMs.HasValue,
                    Source = "NetworkRealSpeedAnalyzer",
                    Reason = udis.LatencyMs.HasValue ? null : udis.NetworkRecommendation,
                    Timestamp = now
                });
            }

            return metrics;
        }

        private static NormalizedMetric CreateMetric(string name, MetricValue<double> metric, string source, DateTimeOffset fallbackTimestamp)
        {
            return new NormalizedMetric
            {
                Name = name,
                Value = metric.Available ? metric.Value?.ToString("F1") : null,
                Available = metric.Available,
                Source = metric.Source ?? source,
                Reason = metric.Available ? null : metric.Reason,
                Timestamp = metric.Timestamp ?? fallbackTimestamp
            };
        }

        private static bool TryGetPerfCounterValue(JsonElement root, string key, out double? value, out string? source, out string? reason)
        {
            value = null;
            source = null;
            reason = null;

            if (root.TryGetProperty("sections", out var sections) &&
                sections.TryGetProperty("PerformanceCounters", out var pc) &&
                pc.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty($"{key}Source", out var src) && src.ValueKind == JsonValueKind.String)
                    source = src.GetString();
                if (data.TryGetProperty($"{key}Reason", out var rsn) && rsn.ValueKind == JsonValueKind.String)
                    reason = rsn.GetString();

                if (data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                {
                    value = v.GetDouble();
                    return true;
                }

                if (data.TryGetProperty($"{key}Available", out var available) && available.ValueKind == JsonValueKind.False)
                {
                    return true;
                }
            }

            return false;
        }

        private static string? TryGetMissingDataReason(JsonElement root, string key)
        {
            if (!root.TryGetProperty("missingData", out var missing))
                return null;

            if (missing.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in missing.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in item.EnumerateObject())
                        {
                            if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                                return prop.Value.ToString();
                        }
                    }
                    if (item.ValueKind == JsonValueKind.String && item.GetString()?.Contains(key, StringComparison.OrdinalIgnoreCase) == true)
                        return "missing";
                }
            }
            else if (missing.ValueKind == JsonValueKind.Object)
            {
                if (missing.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.String &&
                    item.GetString()?.Equals(key, StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (missing.TryGetProperty("reason", out var reason))
                        return reason.ToString();
                    return "missing";
                }

                foreach (var prop in missing.EnumerateObject())
                {
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return prop.Value.ToString();
                }
            }

            return null;
        }

        private static bool TryGetProcessesSource(JsonElement root, out string source)
        {
            source = "PowerShell";
            if (root.TryGetProperty("sections", out var sections) &&
                sections.TryGetProperty("Processes", out var proc) &&
                proc.TryGetProperty("data", out var data) &&
                data.TryGetProperty("source", out var src) &&
                src.ValueKind == JsonValueKind.String)
            {
                source = src.GetString() ?? "PowerShell";
                return true;
            }

            return false;
        }
    }
}
