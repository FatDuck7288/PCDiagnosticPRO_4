using System;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Canonical policy for dedicated VRAM metrics. Additive and non-destructive.
    /// </summary>
    public static class GpuMetricCanonPolicy
    {
        public static void ApplyInPlace(HardwareSensorsResult? sensors)
        {
            if (sensors?.Gpu == null)
                return;

            var gpu = sensors.Gpu;
            var source = string.IsNullOrWhiteSpace(gpu.VramDedicatedSource)
                ? (string.IsNullOrWhiteSpace(gpu.VramUsedSource) ? "unknown" : gpu.VramUsedSource)
                : gpu.VramDedicatedSource;

            // Canonical total: dedicated total first, fallback to legacy total.
            var total = SelectMetric(gpu.VramDedicatedTotalMB, gpu.VramTotalMB);
            var used = SelectMetric(gpu.VramDedicatedUsedMB, gpu.VramUsedMB);

            gpu.VramDedicatedTotalMB = total;
            gpu.VramDedicatedUsedMB = used;
            gpu.VramDedicatedSource = source;
            gpu.VramDedicatedConfidence = NormalizeConfidence(source, gpu.VramDedicatedConfidence);

            if (!total.Available || total.Value <= 0)
            {
                gpu.VramDedicatedPercent = MetricUnavailable("NotPresent");
                gpu.VramDedicatedReasonIfMissing = string.IsNullOrWhiteSpace(total.Reason) ? "NotPresent" : total.Reason;
                return;
            }

            if (!used.Available || used.Value < 0)
            {
                gpu.VramDedicatedPercent = MetricUnavailable("NotPresent");
                gpu.VramDedicatedReasonIfMissing = string.IsNullOrWhiteSpace(used.Reason) ? "NotPresent" : used.Reason;
                return;
            }

            var rawPct = used.Value / total.Value * 100.0;
            var clampedPct = Math.Clamp(rawPct, 0.0, 100.0);
            gpu.VramDedicatedPercent = MetricAvailable(clampedPct, source, gpu.VramDedicatedConfidence);
            gpu.VramDedicatedReasonIfMissing = null;

            // Preserve non-bounded aggregate utilization with explicit label path.
            if (gpu.GpuLoadPercent.Available && gpu.GpuLoadPercent.Value > 100)
            {
                gpu.GpuEngineUtilizationAggregatePercent = MetricAvailable(
                    gpu.GpuLoadPercent.Value,
                    "PerfCounterAggregate",
                    "medium");
                gpu.GpuLoadPercent = MetricAvailable(100, gpu.GpuLoadPercent.Source, gpu.GpuLoadPercent.Confidence);
            }
            else if (gpu.GpuLoadPercent.Available)
            {
                gpu.GpuEngineUtilizationAggregatePercent = MetricAvailable(
                    gpu.GpuLoadPercent.Value,
                    "PerfCounterAggregate",
                    "medium");
            }
        }

        private static MetricValue<double> SelectMetric(MetricValue<double> preferred, MetricValue<double> fallback)
        {
            if (preferred != null && preferred.Available)
                return preferred;
            return fallback ?? MetricUnavailable("Unknown");
        }

        private static string NormalizeConfidence(string source, string? existing)
        {
            if (!string.IsNullOrWhiteSpace(existing))
                return existing!;

            if (source.Contains("NVML", StringComparison.OrdinalIgnoreCase))
                return "high";
            if (source.Contains("DXGI", StringComparison.OrdinalIgnoreCase))
                return "medium";
            if (source.Contains("Perf", StringComparison.OrdinalIgnoreCase))
                return "medium";
            if (source.Contains("WMI", StringComparison.OrdinalIgnoreCase))
                return "low";
            return "low";
        }

        private static MetricValue<double> MetricUnavailable(string reason)
        {
            return new MetricValue<double>
            {
                Available = false,
                Reason = reason,
                Source = "unknown",
                Confidence = "low",
                Timestamp = DateTime.UtcNow.ToString("o")
            };
        }

        private static MetricValue<double> MetricAvailable(double value, string source, string confidence)
        {
            return new MetricValue<double>
            {
                Available = true,
                Value = value,
                Reason = null,
                Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source,
                Confidence = string.IsNullOrWhiteSpace(confidence) ? "medium" : confidence,
                Timestamp = DateTime.UtcNow.ToString("o")
            };
        }
    }
}
