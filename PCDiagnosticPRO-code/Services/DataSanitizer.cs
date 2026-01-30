using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// PHASE 2: Couche centrale de validation/sanitization des données.
    /// Élimine TOUTES les valeurs sentinelles/absurdes AVANT scoring, UI, et TXT.
    /// </summary>
    public static class DataSanitizer
    {
        #region Physical Ranges (Based on Audit Requirements)

        /// <summary>CPU Temperature: >5°C and <115°C (<=5 or >=115 = invalid)</summary>
        public static readonly (double Min, double Max) CpuTempRange = (5.0, 115.0);

        /// <summary>GPU Temperature: 5°C - 120°C</summary>
        public static readonly (double Min, double Max) GpuTempRange = (5.0, 120.0);

        /// <summary>Disk Temperature: 0°C - 90°C</summary>
        public static readonly (double Min, double Max) DiskTempRange = (0.0, 90.0);

        /// <summary>SMART Temperature: max 200°C (917541 = corrupt)</summary>
        public static readonly double SmartTempMaxReasonable = 200.0;

        /// <summary>Sentinel values to reject</summary>
        public static readonly double[] SentinelValues = { -1, -999, 0 };

        #endregion

        #region Sanitization Results

        public class SanitizedValue<T>
        {
            public T? Value { get; set; }
            public bool IsValid { get; set; }
            public string DisplayValue { get; set; } = "";
            public string? InvalidReason { get; set; }
            
            public static SanitizedValue<T> Valid(T value, string display)
            {
                return new SanitizedValue<T>
                {
                    Value = value,
                    IsValid = true,
                    DisplayValue = display
                };
            }
            
            public static SanitizedValue<T> Invalid(string reason)
            {
                return new SanitizedValue<T>
                {
                    IsValid = false,
                    DisplayValue = $"Non disponible ({reason})",
                    InvalidReason = reason
                };
            }
        }

        #endregion

        #region CPU Temperature Sanitization

        /// <summary>
        /// PHASE 2.1: Sanitize CPU Temperature
        /// - 0°C or value < 5°C => Invalid
        /// - value > 110°C => Invalid
        /// - Sets available=false + display "Non disponible (capteur invalide: X)"
        /// </summary>
        public static SanitizedValue<double> SanitizeCpuTemp(MetricValue<double>? metric)
        {
            if (metric == null || !metric.Available)
            {
                var reason = metric?.Reason ?? "capteur non disponible";
                LogSanitize("CPU Temp", "missing", reason);
                return SanitizedValue<double>.Invalid(reason);
            }

            var value = metric.Value;

            // Check sentinel values
            if (IsSentinel(value) || value <= CpuTempRange.Min || value >= CpuTempRange.Max)
            {
                var reason = "sentinel_out_of_range";
                LogSanitize("CPU Temp", value.ToString(), reason);
                return SanitizedValue<double>.Invalid(reason);
            }

            return SanitizedValue<double>.Valid(value, $"{value:F1}°C");
        }

        #endregion

        #region GPU Temperature Sanitization

        /// <summary>
        /// PHASE 2.1: Sanitize GPU Temperature
        /// </summary>
        public static SanitizedValue<double> SanitizeGpuTemp(MetricValue<double>? metric)
        {
            if (metric == null || !metric.Available)
            {
                return SanitizedValue<double>.Invalid(metric?.Reason ?? "capteur non disponible");
            }

            var value = metric.Value;

            if (IsSentinel(value) || value < GpuTempRange.Min || value > GpuTempRange.Max)
            {
                var reason = $"capteur invalide: {value:F1}°C hors plage {GpuTempRange.Min}-{GpuTempRange.Max}°C";
                LogSanitize("GPU Temp", value.ToString(), reason);
                return SanitizedValue<double>.Invalid(reason);
            }

            return SanitizedValue<double>.Valid(value, $"{value:F1}°C");
        }

        #endregion

        #region Disk Temperature Sanitization

        /// <summary>
        /// PHASE 2.1: Sanitize Disk Temperature
        /// </summary>
        public static SanitizedValue<double> SanitizeDiskTemp(MetricValue<double>? metric, string diskName = "")
        {
            if (metric == null || !metric.Available)
            {
                return SanitizedValue<double>.Invalid(metric?.Reason ?? "capteur non disponible");
            }

            var value = metric.Value;

            if (IsSentinel(value) || value < DiskTempRange.Min || value > DiskTempRange.Max)
            {
                var reason = $"capteur invalide: {value:F1}°C hors plage";
                LogSanitize($"Disk Temp ({diskName})", value.ToString(), reason);
                return SanitizedValue<double>.Invalid(reason);
            }

            return SanitizedValue<double>.Valid(value, $"{value:F1}°C");
        }

        #endregion

        #region SMART Temperature Sanitization

        /// <summary>
        /// PHASE 2.2: Sanitize SMART Temperature
        /// - Detects aberrant values like 917541
        /// </summary>
        public static SanitizedValue<double> SanitizeSmartTemp(double value, string diskName = "")
        {
            if (IsSentinel(value))
            {
                return SanitizedValue<double>.Invalid("SMART valeur sentinelle");
            }

            if (value > SmartTempMaxReasonable)
            {
                var reason = $"SMART corrupt ({value:F0}°C > {SmartTempMaxReasonable}°C)";
                LogSanitize($"SMART Temp ({diskName})", value.ToString(), reason);
                return SanitizedValue<double>.Invalid(reason);
            }

            if (value < 0)
            {
                return SanitizedValue<double>.Invalid($"SMART valeur négative: {value}");
            }

            return SanitizedValue<double>.Valid(value, $"{value:F0}°C");
        }

        #endregion

        #region VRAM Sanitization

        /// <summary>
        /// PHASE 2.1: Sanitize VRAM values
        /// - vramTotalMB <= 0 => Invalid
        /// - vramUsedMB < 0 OR vramUsedMB > vramTotalMB => Invalid
        /// </summary>
        public static SanitizedValue<(double total, double used)> SanitizeVram(
            MetricValue<double>? totalMB, MetricValue<double>? usedMB)
        {
            if (totalMB == null || !totalMB.Available)
            {
                return SanitizedValue<(double, double)>.Invalid("VRAM total non disponible");
            }

            if (usedMB == null || !usedMB.Available)
            {
                return SanitizedValue<(double, double)>.Invalid("VRAM utilisée non disponible");
            }

            var total = totalMB.Value;
            var used = usedMB.Value;

            if (total <= 0)
            {
                LogSanitize("VRAM Total", total.ToString(), "valeur <= 0");
                return SanitizedValue<(double, double)>.Invalid($"VRAM total invalide: {total} MB");
            }

            if (used < 0)
            {
                LogSanitize("VRAM Used", used.ToString(), "valeur négative");
                return SanitizedValue<(double, double)>.Invalid($"VRAM utilisée négative: {used} MB");
            }

            if (used > total * 1.05) // 5% tolerance for timing differences
            {
                LogSanitize("VRAM", $"{used}/{total}", "used > total");
                return SanitizedValue<(double, double)>.Invalid($"VRAM incohérente: {used:F0} MB > {total:F0} MB");
            }

            return SanitizedValue<(double, double)>.Valid((total, used), $"{used:F0}/{total:F0} MB");
        }

        #endregion

        #region PerfCounter Sanitization

        /// <summary>
        /// PHASE 2.3: Sanitize PerfCounter values
        /// - diskQueueLength == -1 => "Non disponible (counter non supporté / sentinelle -1)"
        /// </summary>
        public static SanitizedValue<double> SanitizePerfCounter(double? value, string counterName)
        {
            if (!value.HasValue)
            {
                return SanitizedValue<double>.Invalid($"counter '{counterName}' non collecté");
            }

            var v = value.Value;

            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                LogSanitize($"PerfCounter.{counterName}", "NaN/Infinity", "valeur non numérique");
                return SanitizedValue<double>.Invalid("valeur non numérique");
            }

            if (v == -1)
            {
                LogSanitize($"PerfCounter.{counterName}", "-1", "counter non supporté / sentinelle");
                return SanitizedValue<double>.Invalid("counter non supporté / sentinelle -1");
            }

            if (IsSentinel(v))
            {
                LogSanitize($"PerfCounter.{counterName}", v.ToString(), "valeur sentinelle");
                return SanitizedValue<double>.Invalid($"valeur sentinelle ({v})");
            }

            // Context-specific validation
            if (counterName.Contains("Queue", StringComparison.OrdinalIgnoreCase))
            {
                if (v < 0 || v > 1000)
                {
                    return SanitizedValue<double>.Invalid($"queue length absurde: {v}");
                }
            }

            if (counterName.Contains("Percent", StringComparison.OrdinalIgnoreCase))
            {
                if (v < 0 || v > 100)
                {
                    return SanitizedValue<double>.Invalid($"pourcentage hors 0-100: {v}%");
                }
            }

            return SanitizedValue<double>.Valid(v, v.ToString("F2"));
        }

        #endregion

        #region Apply Sanitization to HardwareSensorsResult

        /// <summary>
        /// Applies sanitization to all sensors in HardwareSensorsResult.
        /// Modifies the object in-place, setting available=false for invalid values.
        /// Returns a list of sanitization actions taken.
        /// </summary>
        public static List<string> SanitizeSensors(HardwareSensorsResult? sensors)
        {
            var actions = new List<string>();
            
            if (sensors == null) return actions;

            // CPU Temperature
            var cpuTemp = SanitizeCpuTemp(sensors.Cpu.CpuTempC);
            if (!cpuTemp.IsValid && sensors.Cpu.CpuTempC.Available)
            {
                sensors.Cpu.CpuTempC.Available = false;
                sensors.Cpu.CpuTempC.Reason = cpuTemp.InvalidReason;
                actions.Add($"CPU Temp: {cpuTemp.InvalidReason}");
            }

            // GPU Temperature
            var gpuTemp = SanitizeGpuTemp(sensors.Gpu.GpuTempC);
            if (!gpuTemp.IsValid && sensors.Gpu.GpuTempC.Available)
            {
                sensors.Gpu.GpuTempC.Available = false;
                sensors.Gpu.GpuTempC.Reason = gpuTemp.InvalidReason;
                actions.Add($"GPU Temp: {gpuTemp.InvalidReason}");
            }

            // VRAM
            var vram = SanitizeVram(sensors.Gpu.VramTotalMB, sensors.Gpu.VramUsedMB);
            if (!vram.IsValid)
            {
                if (sensors.Gpu.VramTotalMB.Available && sensors.Gpu.VramTotalMB.Value <= 0)
                {
                    sensors.Gpu.VramTotalMB.Available = false;
                    sensors.Gpu.VramTotalMB.Reason = vram.InvalidReason;
                }
                if (sensors.Gpu.VramUsedMB.Available)
                {
                    var used = sensors.Gpu.VramUsedMB.Value;
                    var total = sensors.Gpu.VramTotalMB.Available ? sensors.Gpu.VramTotalMB.Value : 0;
                    if (used < 0 || (total > 0 && used > total * 1.05))
                    {
                        sensors.Gpu.VramUsedMB.Available = false;
                        sensors.Gpu.VramUsedMB.Reason = vram.InvalidReason;
                    }
                }
                actions.Add($"VRAM: {vram.InvalidReason}");
            }

            // Disk Temperatures
            foreach (var disk in sensors.Disks)
            {
                var diskTemp = SanitizeDiskTemp(disk.TempC, disk.Name.Value ?? "");
                if (!diskTemp.IsValid && disk.TempC.Available)
                {
                    disk.TempC.Available = false;
                    disk.TempC.Reason = diskTemp.InvalidReason;
                    actions.Add($"Disk Temp ({disk.Name.Value}): {diskTemp.InvalidReason}");
                }
            }

            return actions;
        }

        #endregion

        #region Helpers

        private static bool IsSentinel(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return true;

            return SentinelValues.Any(s => Math.Abs(value - s) < 0.001);
        }

        private static void LogSanitize(string metric, string value, string reason)
        {
            App.LogMessage($"[SANITIZE] {metric} invalid ({value}) -> hidden: {reason}");
        }

        #endregion
    }
}
