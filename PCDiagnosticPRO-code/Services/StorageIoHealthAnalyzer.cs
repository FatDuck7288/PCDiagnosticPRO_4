using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Storage IO Health — évalue la santé des IO disque (queue, latence, débit).
    /// </summary>
    public static class StorageIoHealthAnalyzer
    {
        public class StorageIoResult
        {
            public int IoHealthScore { get; set; } = 100;
            public string IoHealthStatus { get; set; } = "OK";
            public double? AvgDiskQueueLength { get; set; }
            public double? DiskReadBytesPerSec { get; set; }
            public double? DiskWriteBytesPerSec { get; set; }
            public double? DiskIdlePercent { get; set; }
            public List<string> Warnings { get; set; } = new();
            public string Recommendation { get; set; } = "";
        }

        /// <summary>
        /// Analyse les IO disque depuis les PerfCounters du JSON PowerShell.
        /// </summary>
        public static StorageIoResult Analyze(JsonElement? psRoot)
        {
            var result = new StorageIoResult();
            try
            {
                if (!psRoot.HasValue)
                {
                    result.IoHealthStatus = "Non mesuré";
                    result.IoHealthScore = 70;
                    return result;
                }

                var root = psRoot.Value;
                if (!root.TryGetProperty("sections", out var sections))
                    return result;

                // Chercher PerformanceCounters
                if (sections.TryGetProperty("PerformanceCounters", out var perf) && perf.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("diskQueueLength", out var dql) && dql.ValueKind == JsonValueKind.Number)
                    {
                        var val = dql.GetDouble();
                        if (val >= 0 && val < 1000) // Exclure sentinelles
                        {
                            result.AvgDiskQueueLength = val;
                        }
                    }
                    if (data.TryGetProperty("diskReadBytesPerSec", out var dr) && dr.ValueKind == JsonValueKind.Number)
                    {
                        var val = dr.GetDouble();
                        if (val >= 0) result.DiskReadBytesPerSec = val;
                    }
                    if (data.TryGetProperty("diskWriteBytesPerSec", out var dw) && dw.ValueKind == JsonValueKind.Number)
                    {
                        var val = dw.GetDouble();
                        if (val >= 0) result.DiskWriteBytesPerSec = val;
                    }
                    if (data.TryGetProperty("diskIdlePercent", out var di) && di.ValueKind == JsonValueKind.Number)
                    {
                        var val = di.GetDouble();
                        if (val >= 0 && val <= 100) result.DiskIdlePercent = val;
                    }
                }

                ComputeScore(result);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[StorageIoHealth] Erreur: {ex.Message}");
            }
            return result;
        }

        private static void ComputeScore(StorageIoResult result)
        {
            int penalties = 0;

            if (result.AvgDiskQueueLength.HasValue)
            {
                var q = result.AvgDiskQueueLength.Value;
                if (q > 10)
                {
                    penalties += 30;
                    result.Warnings.Add($"File d'attente disque élevée: {q:F1}");
                }
                else if (q > 5)
                {
                    penalties += 15;
                    result.Warnings.Add($"File d'attente disque modérée: {q:F1}");
                }
                else if (q > 2)
                {
                    penalties += 5;
                }
            }

            if (result.DiskIdlePercent.HasValue && result.DiskIdlePercent.Value < 20)
            {
                penalties += 10;
                result.Warnings.Add("Disque très sollicité (idle < 20%)");
            }

            result.IoHealthScore = Math.Max(0, 100 - penalties);

            if (penalties >= 30)
            {
                result.IoHealthStatus = "Saturé";
                result.Recommendation = "Le disque est surchargé. Fermez des applications ou envisagez un SSD plus rapide.";
            }
            else if (penalties >= 15)
            {
                result.IoHealthStatus = "Chargé";
                result.Recommendation = "IO disque élevé mais acceptable.";
            }
            else
            {
                result.IoHealthStatus = "OK";
                result.Recommendation = "IO disque sain.";
            }
        }
    }
}
