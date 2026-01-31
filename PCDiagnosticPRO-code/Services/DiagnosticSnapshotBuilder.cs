using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.DiagnosticsSignals;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// PHASE 1.3: Builds DiagnosticSnapshot from collected data.
    /// Applies all normalization rules and sentinel validation.
    /// </summary>
    public class DiagnosticSnapshotBuilder
    {
        private readonly DiagnosticSnapshot _snapshot;

        public DiagnosticSnapshotBuilder()
        {
            _snapshot = new DiagnosticSnapshot
            {
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                Machine = new MachineInfo
                {
                    Hostname = Environment.MachineName,
                    Os = Environment.OSVersion.ToString(),
                    IsAdmin = AdminHelper.IsRunningAsAdmin()
                }
            };
        }

        /// <summary>
        /// Add CPU metrics from HardwareSensorsResult
        /// </summary>
        public DiagnosticSnapshotBuilder AddCpuMetrics(HardwareSensorsResult? sensors)
        {
            var cpuMetrics = new Dictionary<string, NormalizedMetric>();

            if (sensors?.Cpu != null)
            {
                // CPU Temperature with sentinel validation
                cpuMetrics["temperature"] = NormalizeCpuTemp(sensors.Cpu.CpuTempC, sensors.Cpu.CpuTempSource);
            }
            else
            {
                cpuMetrics["temperature"] = MetricFactory.CreateUnavailable("°C", "HardwareSensorsCollector", "sensor_not_available");
            }

            _snapshot.Metrics["cpu"] = cpuMetrics;
            return this;
        }

        /// <summary>
        /// Add GPU metrics from HardwareSensorsResult
        /// </summary>
        public DiagnosticSnapshotBuilder AddGpuMetrics(HardwareSensorsResult? sensors)
        {
            var gpuMetrics = new Dictionary<string, NormalizedMetric>();

            if (sensors?.Gpu != null)
            {
                gpuMetrics["name"] = sensors.Gpu.Name.Available
                    ? MetricFactory.CreateAvailable(sensors.Gpu.Name.Value ?? "", "", "LHM", 100)
                    : MetricFactory.CreateUnavailable("", "LHM", sensors.Gpu.Name.Reason ?? "not_detected");

                gpuMetrics["temperature"] = NormalizeGpuTemp(sensors.Gpu.GpuTempC);
                gpuMetrics["load"] = NormalizePercent(sensors.Gpu.GpuLoadPercent, "LHM", "gpu_load");
                gpuMetrics["vramTotalMB"] = NormalizeVramTotal(sensors.Gpu.VramTotalMB);
                gpuMetrics["vramUsedMB"] = NormalizeVramUsed(sensors.Gpu.VramUsedMB, sensors.Gpu.VramTotalMB);
            }
            else
            {
                gpuMetrics["temperature"] = MetricFactory.CreateUnavailable("°C", "LHM", "gpu_not_detected");
                gpuMetrics["load"] = MetricFactory.CreateUnavailable("%", "LHM", "gpu_not_detected");
            }

            _snapshot.Metrics["gpu"] = gpuMetrics;
            return this;
        }

        /// <summary>
        /// Add storage metrics from HardwareSensorsResult
        /// </summary>
        public DiagnosticSnapshotBuilder AddStorageMetrics(HardwareSensorsResult? sensors)
        {
            var storageMetrics = new Dictionary<string, NormalizedMetric>();

            if (sensors?.Disks != null && sensors.Disks.Count > 0)
            {
                var validTemps = new List<double>();
                int diskIndex = 0;

                foreach (var disk in sensors.Disks)
                {
                    var diskName = disk.Name.Value ?? $"disk_{diskIndex}";
                    var tempMetric = NormalizeDiskTemp(disk.TempC, diskName);
                    storageMetrics[$"disk_{diskIndex}_temp"] = tempMetric;

                    if (tempMetric.Available && tempMetric.Value is double t)
                        validTemps.Add(t);

                    diskIndex++;
                }

                storageMetrics["diskCount"] = MetricFactory.FromCount(sensors.Disks.Count, "LHM");
                storageMetrics["maxDiskTemp"] = validTemps.Count > 0
                    ? MetricFactory.CreateAvailable(validTemps.Max(), "°C", "Derived", 100)
                    : MetricFactory.CreateUnavailable("°C", "Derived", "no_valid_disk_temps");
            }
            else
            {
                storageMetrics["diskCount"] = MetricFactory.FromCount(0, "LHM", "no_disks_detected");
            }

            _snapshot.Metrics["storage"] = storageMetrics;
            return this;
        }

        /// <summary>
        /// Add diagnostic signals (10 signals)
        /// </summary>
        public DiagnosticSnapshotBuilder AddDiagnosticSignals(Dictionary<string, SignalResult>? signals)
        {
            if (signals == null)
            {
                _snapshot.CollectionQuality.SignalsCollected = 0;
                _snapshot.CollectionQuality.SignalsUnavailable = 10;
                return this;
            }

            int collected = 0;
            int unavailable = 0;

            foreach (var kvp in signals)
            {
                var signalName = kvp.Key;
                var signal = kvp.Value;

                var metricsGroup = ConvertSignalToMetrics(signalName, signal);
                if (metricsGroup.Count > 0)
                {
                    _snapshot.Metrics[signalName] = metricsGroup;
                }

                if (signal.Available)
                    collected++;
                else
                    unavailable++;
            }

            _snapshot.CollectionQuality.SignalsCollected = collected;
            _snapshot.CollectionQuality.SignalsUnavailable = unavailable;

            return this;
        }

        /// <summary>
        /// Add findings
        /// </summary>
        public DiagnosticSnapshotBuilder AddFindings(List<NormalizedFinding>? findings)
        {
            if (findings != null)
                _snapshot.Findings.AddRange(findings);
            return this;
        }

        /// <summary>
        /// Build the final snapshot
        /// </summary>
        public DiagnosticSnapshot Build()
        {
            // Calculate collection quality
            int total = 0;
            int available = 0;

            foreach (var group in _snapshot.Metrics.Values)
            {
                foreach (var metric in group.Values)
                {
                    total++;
                    if (metric.Available)
                        available++;
                }
            }

            _snapshot.CollectionQuality.TotalMetrics = total;
            _snapshot.CollectionQuality.AvailableMetrics = available;
            _snapshot.CollectionQuality.UnavailableMetrics = total - available;
            _snapshot.CollectionQuality.CoveragePercent = total > 0 
                ? Math.Round((double)available / total * 100, 1) 
                : 0;

            return _snapshot;
        }

        #region Normalization Helpers

        private NormalizedMetric NormalizeCpuTemp(MetricValue<double>? metric, string source)
        {
            string actualSource = source ?? "LHM";
            
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("°C", actualSource, 
                    metric?.Reason ?? "sensor_not_available");

            var value = metric.Value;

            // Sentinel checks
            if (double.IsNaN(value) || double.IsInfinity(value))
                return MetricFactory.CreateUnavailable("°C", actualSource, "nan_or_infinite");

            if (Math.Abs(value) < 0.001)
                return MetricFactory.CreateUnavailable("°C", actualSource, "sentinel_zero");

            if (value == -1)
                return MetricFactory.CreateUnavailable("°C", actualSource, "sentinel_minus_one");

            // Range check (5-115°C for CPU)
            if (value < 5 || value > 115)
                return MetricFactory.CreateUnavailable("°C", actualSource, 
                    $"out_of_range ({value:F1}°C not in [5,115])");

            return MetricFactory.CreateAvailable(Math.Round(value, 1), "°C", actualSource, 100);
        }

        private NormalizedMetric NormalizeGpuTemp(MetricValue<double>? metric)
        {
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("°C", "LHM", 
                    metric?.Reason ?? "sensor_not_available");

            var value = metric.Value;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return MetricFactory.CreateUnavailable("°C", "LHM", "nan_or_infinite");

            if (value < 5 || value > 120)
                return MetricFactory.CreateUnavailable("°C", "LHM", 
                    $"out_of_range ({value:F1}°C not in [5,120])");

            return MetricFactory.CreateAvailable(Math.Round(value, 1), "°C", "LHM", 100);
        }

        private NormalizedMetric NormalizeDiskTemp(MetricValue<double>? metric, string diskName)
        {
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("°C", "LHM", 
                    metric?.Reason ?? "disk_temp_not_available", diskName);

            var value = metric.Value;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return MetricFactory.CreateUnavailable("°C", "LHM", "nan_or_infinite", diskName);

            if (Math.Abs(value) < 0.001)
                return MetricFactory.CreateUnavailable("°C", "LHM", "sentinel_zero", diskName);

            if (value < 0 || value > 90)
                return MetricFactory.CreateUnavailable("°C", "LHM", 
                    $"out_of_range ({value:F1}°C not in [0,90])", diskName);

            return MetricFactory.CreateAvailable(Math.Round(value, 1), "°C", "LHM", 100, diskName);
        }

        private NormalizedMetric NormalizePercent(MetricValue<double>? metric, string source, string name)
        {
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("%", source, metric?.Reason ?? "not_available");

            var value = metric.Value;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return MetricFactory.CreateUnavailable("%", source, "nan_or_infinite");

            if (value < 0 || value > 100)
                return MetricFactory.CreateUnavailable("%", source, $"out_of_range ({value:F1}% not in [0,100])");

            return MetricFactory.CreateAvailable(Math.Round(value, 1), "%", source, 100);
        }

        private NormalizedMetric NormalizeVramTotal(MetricValue<double>? metric)
        {
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("MB", "LHM", metric?.Reason ?? "not_available");

            var value = metric.Value;

            if (value <= 0)
                return MetricFactory.CreateUnavailable("MB", "LHM", "sentinel_zero_or_negative");

            return MetricFactory.CreateAvailable(Math.Round(value, 0), "MB", "LHM", 100);
        }

        private NormalizedMetric NormalizeVramUsed(MetricValue<double>? usedMetric, MetricValue<double>? totalMetric)
        {
            if (usedMetric == null || !usedMetric.Available)
                return MetricFactory.CreateUnavailable("MB", "LHM", usedMetric?.Reason ?? "not_available");

            var used = usedMetric.Value;
            var total = totalMetric?.Value ?? 0;

            if (used < 0)
                return MetricFactory.CreateUnavailable("MB", "LHM", "negative_value");

            if (total > 0 && used > total * 1.1)
                return MetricFactory.CreateUnavailable("MB", "LHM", $"vram_used_exceeds_total ({used:F0} > {total:F0})");

            return MetricFactory.CreateAvailable(Math.Round(used, 1), "MB", "LHM", 100);
        }

        private Dictionary<string, NormalizedMetric> ConvertSignalToMetrics(string signalName, SignalResult signal)
        {
            var metrics = new Dictionary<string, NormalizedMetric>();

            if (!signal.Available)
            {
                metrics["available"] = MetricFactory.CreateUnavailable("bool", signal.Source, 
                    signal.Reason ?? "signal_unavailable");
                return metrics;
            }

            // Add availability marker
            metrics["available"] = MetricFactory.CreateAvailable(true, "bool", signal.Source, 100);

            // Convert signal value to metrics based on signal type
            if (signal.Value is JsonElement jsonElement)
            {
                ExtractMetricsFromJson(metrics, jsonElement, signal.Source);
            }
            else if (signal.Value is IDictionary<string, object> dict)
            {
                foreach (var kvp in dict)
                {
                    metrics[kvp.Key] = ConvertValueToMetric(kvp.Value, signal.Source);
                }
            }
            else if (signal.Value != null)
            {
                // Try to extract properties using reflection for known types
                var valueType = signal.Value.GetType();
                foreach (var prop in valueType.GetProperties())
                {
                    try
                    {
                        var propValue = prop.GetValue(signal.Value);
                        if (propValue != null)
                        {
                            metrics[prop.Name] = ConvertValueToMetric(propValue, signal.Source);
                        }
                    }
                    catch { /* Ignore reflection errors */ }
                }
            }

            return metrics;
        }

        private void ExtractMetricsFromJson(Dictionary<string, NormalizedMetric> metrics, JsonElement element, string source)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        var value = prop.Value.GetDouble();
                        metrics[prop.Name] = MetricFactory.CreateAvailable(value, "", source, 100);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        metrics[prop.Name] = MetricFactory.CreateAvailable(prop.Value.GetString() ?? "", "", source, 100);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                    {
                        metrics[prop.Name] = MetricFactory.CreateAvailable(prop.Value.GetBoolean(), "bool", source, 100);
                    }
                }
            }
        }

        private NormalizedMetric ConvertValueToMetric(object value, string source)
        {
            return value switch
            {
                int i => MetricFactory.CreateAvailable(i, "count", source, 100),
                long l => MetricFactory.CreateAvailable(l, "count", source, 100),
                double d => MetricFactory.CreateAvailable(Math.Round(d, 2), "", source, 100),
                float f => MetricFactory.CreateAvailable(Math.Round(f, 2), "", source, 100),
                string s => MetricFactory.CreateAvailable(s, "", source, 100),
                bool b => MetricFactory.CreateAvailable(b, "bool", source, 100),
                DateTime dt => MetricFactory.CreateAvailable(dt.ToString("o"), "timestamp", source, 100),
                _ => MetricFactory.CreateAvailable(value.ToString() ?? "", "", source, 100)
            };
        }

        #endregion
    }
}
