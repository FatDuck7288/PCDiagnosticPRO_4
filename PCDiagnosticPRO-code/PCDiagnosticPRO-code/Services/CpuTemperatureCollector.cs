using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace PCDiagnosticPro.Services
{
    public sealed class CpuTemperatureCollectionResult
    {
        public double? TemperatureC { get; init; }
        public string Source { get; init; } = CpuTemperatureMetadataService.SourceNone;
        public string Confidence { get; init; } = CpuTemperatureMetadataService.ConfidenceNone;
        public string? SourceDetail { get; init; }
        public string? ReasonCode { get; init; }
        public string? ReasonDetail { get; init; }
        public bool Available => TemperatureC.HasValue;
    }

    /// <summary>
    /// Best-effort CPU temperature collection pipeline:
    /// 1) LHM sensors (CPU package/core)
    /// 2) ACPI ThermalZone fallback
    /// 3) Unavailable with normalized reason code
    /// </summary>
    public static class CpuTemperatureCollector
    {
        private const double MinValidTempC = 5.0;
        private const double MaxValidTempC = 115.0;

        public static CpuTemperatureCollectionResult CollectBestEffort(IEnumerable<ISensor>? sensors, bool blockedBySecurity = false)
        {
            var lhm = TryReadFromLhmSensors(sensors);
            if (lhm.TempC.HasValue)
            {
                return new CpuTemperatureCollectionResult
                {
                    TemperatureC = lhm.TempC.Value,
                    Source = CpuTemperatureMetadataService.SourceLhm,
                    Confidence = CpuTemperatureMetadataService.ConfidenceHigh,
                    SourceDetail = lhm.SourceDetail ?? CpuTemperatureMetadataService.SourceLhm
                };
            }

            var acpi = TryReadFromAcpi();
            if (acpi.TempC.HasValue)
            {
                return new CpuTemperatureCollectionResult
                {
                    TemperatureC = acpi.TempC.Value,
                    Source = CpuTemperatureMetadataService.SourceAcpi,
                    Confidence = CpuTemperatureMetadataService.ConfidenceLow,
                    SourceDetail = acpi.Source ?? CpuTemperatureMetadataService.SourceAcpi
                };
            }

            var reasonDetail = BuildReasonDetail(lhm.InvalidReason, acpi.Reason);
            var reasonCode = CpuTemperatureMetadataService.ClassifyReasonCode(reasonDetail, blockedBySecurity);
            return new CpuTemperatureCollectionResult
            {
                Source = CpuTemperatureMetadataService.SourceNone,
                Confidence = CpuTemperatureMetadataService.ConfidenceNone,
                SourceDetail = acpi.Source ?? CpuTemperatureMetadataService.SourceNone,
                ReasonCode = reasonCode,
                ReasonDetail = reasonDetail
            };
        }

        public static CpuTemperatureCollectionResult CollectAcpiOnly(bool blockedBySecurity = false)
        {
            var acpi = TryReadFromAcpi();
            if (acpi.TempC.HasValue)
            {
                return new CpuTemperatureCollectionResult
                {
                    TemperatureC = acpi.TempC.Value,
                    Source = CpuTemperatureMetadataService.SourceAcpi,
                    Confidence = CpuTemperatureMetadataService.ConfidenceLow,
                    SourceDetail = acpi.Source ?? CpuTemperatureMetadataService.SourceAcpi
                };
            }

            var reasonDetail = acpi.Reason ?? "no_acpi_sensor";
            var reasonCode = CpuTemperatureMetadataService.ClassifyReasonCode(reasonDetail, blockedBySecurity);
            return new CpuTemperatureCollectionResult
            {
                Source = CpuTemperatureMetadataService.SourceNone,
                Confidence = CpuTemperatureMetadataService.ConfidenceNone,
                SourceDetail = acpi.Source ?? CpuTemperatureMetadataService.SourceNone,
                ReasonCode = reasonCode,
                ReasonDetail = reasonDetail
            };
        }

        private static (double? TempC, string? SourceDetail, string? InvalidReason) TryReadFromLhmSensors(IEnumerable<ISensor>? sensors)
        {
            if (sensors == null)
                return (null, null, "lhm_no_sensor_collection");

            var tempSensors = sensors
                .Where(s => s != null &&
                            s.SensorType == SensorType.Temperature &&
                            s.Value.HasValue &&
                            s.Value.Value > 0)
                .ToList();

            if (tempSensors.Count == 0)
                return (null, null, "lhm_no_temperature_sensor");

            var (candidate, sourceDetail) = SelectBestLhmTemperature(tempSensors);
            if (!candidate.HasValue)
                return (null, sourceDetail, "lhm_no_temperature_candidate");

            var value = candidate.Value;
            if (value < MinValidTempC || value > MaxValidTempC)
                return (null, sourceDetail, $"lhm_invalid_temperature:{value:F1}");

            return (value, sourceDetail, null);
        }

        private static (double? TempC, string? Source) SelectBestLhmTemperature(List<ISensor> tempSensors)
        {
            var tdie = tempSensors.FirstOrDefault(s => s.Name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0);
            var tctl = tempSensors.FirstOrDefault(s => s.Name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0);
            var pkg = tempSensors.FirstOrDefault(s => s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0);
            if (tdie?.Value.HasValue == true) return (tdie.Value.Value, "Tdie (AMD)");
            if (tctl?.Value.HasValue == true) return (tctl.Value.Value, "Tctl (AMD)");
            if (pkg?.Value.HasValue == true) return (pkg.Value.Value, "CPU Package");

            var coreMax = tempSensors
                .Where(s => s.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(s => s.Value ?? 0)
                .FirstOrDefault();
            if (coreMax?.Value.HasValue == true) return (coreMax.Value.Value, $"Core ({coreMax.Name})");

            var ccd = tempSensors.FirstOrDefault(s => s.Name.IndexOf("CCD", StringComparison.OrdinalIgnoreCase) >= 0);
            if (ccd?.Value.HasValue == true) return (ccd.Value.Value, ccd.Name ?? "CCD");

            var first = tempSensors.FirstOrDefault();
            return first?.Value.HasValue == true
                ? (first.Value.Value, $"Fallback ({first.Name})")
                : (null, null);
        }

        private static (double? TempC, string Source, string? Reason) TryReadFromAcpi()
        {
            var fallback = WmiThermalZoneFallback.TryGetCpuTemp(minValidC: MinValidTempC, maxValidC: MaxValidTempC);
            return fallback;
        }

        private static string BuildReasonDetail(string? lhmReason, string? acpiReason)
        {
            if (string.IsNullOrWhiteSpace(lhmReason) && string.IsNullOrWhiteSpace(acpiReason))
                return "no_cpu_sensor_available";

            if (string.IsNullOrWhiteSpace(lhmReason))
                return $"lhm_unavailable; acpi={acpiReason}";

            if (string.IsNullOrWhiteSpace(acpiReason))
                return $"lhm={lhmReason}; acpi_unavailable";

            return $"lhm={lhmReason}; acpi={acpiReason}";
        }
    }
}
