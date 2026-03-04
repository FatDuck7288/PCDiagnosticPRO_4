using System;
using System.Collections.Generic;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Evaluates GPU data completeness for explicit section header display.
    /// </summary>
    public static class GpuCompletenessEvaluator
    {
        public sealed class GpuCompletenessResult
        {
            public string State { get; init; } = "unavailable";
            public string Label { get; init; } = "Données GPU indisponibles";
            public string Reason { get; init; } = "Aucune donnée GPU exploitable.";
            public bool InventoryAvailable { get; init; }
            public bool TelemetryAvailable { get; init; }
            public double TelemetryCoveragePercent { get; init; }
        }

        public static GpuCompletenessResult Evaluate(DiagnosticSnapshot? snapshot, HardwareSensorsResult? sensors)
        {
            bool hasInventoryFromSnapshot = false;
            if (snapshot?.Metrics != null &&
                snapshot.Metrics.TryGetValue("gpu", out var gpuMetrics) &&
                gpuMetrics != null)
            {
                hasInventoryFromSnapshot = IsMetricAvailable(gpuMetrics, "name") ||
                                           IsMetricAvailable(gpuMetrics, "vramTotalMB") ||
                                           IsMetricAvailable(gpuMetrics, "vramUsedMB");
            }

            bool hasInventoryFromSensors = sensors?.Gpu?.Name?.Available == true ||
                                           sensors?.Gpu?.VramTotalMB?.Available == true;

            var inventoryAvailable = hasInventoryFromSnapshot || hasInventoryFromSensors;

            int telemetrySignals = 0;
            int telemetryAvailable = 0;

            RegisterTelemetry(sensors?.Gpu?.GpuTempC?.Available == true, ref telemetrySignals, ref telemetryAvailable);
            RegisterTelemetry(sensors?.Gpu?.GpuLoadPercent?.Available == true, ref telemetrySignals, ref telemetryAvailable);
            RegisterTelemetry(sensors?.Gpu?.VramUsedMB?.Available == true, ref telemetrySignals, ref telemetryAvailable);

            if (snapshot?.Metrics != null &&
                snapshot.Metrics.TryGetValue("gpu", out var metrics) &&
                metrics != null)
            {
                RegisterTelemetry(IsMetricAvailable(metrics, "temperature"), ref telemetrySignals, ref telemetryAvailable);
                RegisterTelemetry(IsMetricAvailable(metrics, "load"), ref telemetrySignals, ref telemetryAvailable);
            }

            var telemetryCoverage = telemetrySignals > 0
                ? Math.Round(100.0 * telemetryAvailable / telemetrySignals, 1)
                : 0.0;
            var hasTelemetry = telemetryAvailable > 0;

            if (!inventoryAvailable && !hasTelemetry)
            {
                return new GpuCompletenessResult
                {
                    State = "unavailable",
                    Label = "Données GPU indisponibles",
                    Reason = "Inventaire et télémétrie absents.",
                    InventoryAvailable = false,
                    TelemetryAvailable = false,
                    TelemetryCoveragePercent = telemetryCoverage
                };
            }

            if (inventoryAvailable && !hasTelemetry)
            {
                return new GpuCompletenessResult
                {
                    State = "inventory_only",
                    Label = "Inventaire OK, télémétrie indisponible",
                    Reason = "Le GPU est identifié, mais les mesures temps réel sont manquantes.",
                    InventoryAvailable = true,
                    TelemetryAvailable = false,
                    TelemetryCoveragePercent = telemetryCoverage
                };
            }

            if (inventoryAvailable && telemetryCoverage >= 75)
            {
                return new GpuCompletenessResult
                {
                    State = "complete",
                    Label = "Inventaire + télémétrie complets",
                    Reason = "Les mesures principales GPU sont disponibles.",
                    InventoryAvailable = true,
                    TelemetryAvailable = true,
                    TelemetryCoveragePercent = telemetryCoverage
                };
            }

            return new GpuCompletenessResult
            {
                State = "telemetry_partial",
                Label = "Télémétrie GPU partielle",
                Reason = "Certaines mesures GPU sont indisponibles ou partielles.",
                InventoryAvailable = inventoryAvailable,
                TelemetryAvailable = hasTelemetry,
                TelemetryCoveragePercent = telemetryCoverage
            };
        }

        private static void RegisterTelemetry(bool available, ref int total, ref int availableCount)
        {
            total++;
            if (available)
                availableCount++;
        }

        private static bool IsMetricAvailable(Dictionary<string, NormalizedMetric> metrics, string key)
        {
            return metrics.TryGetValue(key, out var metric) &&
                   metric != null &&
                   metric.Available;
        }
    }
}
