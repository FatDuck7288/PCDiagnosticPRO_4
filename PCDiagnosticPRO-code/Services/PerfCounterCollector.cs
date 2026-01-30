using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Collecteur de compteurs de performance robustes (CPU, Memory, Disk).
    /// Fallback propre si counter non disponible.
    /// </summary>
    public static class PerfCounterCollector
    {
        public class PerfCounterResult
        {
            public double? CpuPercent { get; set; }
            public double? MemoryAvailableMB { get; set; }
            public double? DiskTimePercent { get; set; }
            public double? DiskQueueLength { get; set; }
            public double? NetworkBytesPerSec { get; set; }
            
            public bool CpuAvailable { get; set; }
            public bool MemoryAvailable { get; set; }
            public bool DiskTimeAvailable { get; set; }
            public bool DiskQueueAvailable { get; set; }
            public bool NetworkAvailable { get; set; }
            
            public string? CpuError { get; set; }
            public string? MemoryError { get; set; }
            public string? DiskTimeError { get; set; }
            public string? DiskQueueError { get; set; }
            public string? NetworkError { get; set; }
            
            public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.Now;
        }

        /// <summary>
        /// Collecte les compteurs de performance de manière robuste.
        /// </summary>
        public static async Task<PerfCounterResult> CollectAsync(CancellationToken ct = default)
        {
            var result = new PerfCounterResult();

            // Exécuter en parallèle avec timeout
            var tasks = new List<Task>
            {
                Task.Run(() => CollectCpu(result), ct),
                Task.Run(() => CollectMemory(result), ct),
                Task.Run(() => CollectDiskTime(result), ct),
                Task.Run(() => CollectDiskQueue(result), ct),
                Task.Run(() => CollectNetwork(result), ct)
            };

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                App.LogMessage("[PerfCounters] Collection annulée");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerfCounters] Erreur globale: {ex.Message}");
            }

            result.CollectedAt = DateTimeOffset.Now;
            return result;
        }

        private static void CollectCpu(PerfCounterResult result)
        {
            try
            {
                using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                counter.NextValue(); // First call returns 0
                Thread.Sleep(100);
                var value = counter.NextValue();
                
                if (value >= 0 && value <= 100)
                {
                    result.CpuPercent = Math.Round(value, 1);
                    result.CpuAvailable = true;
                    App.LogMessage($"[PerfCounters] CPU: {value:F1}%");
                }
                else
                {
                    result.CpuError = $"Valeur hors plage: {value}";
                }
            }
            catch (Exception ex)
            {
                result.CpuError = ex.Message;
                App.LogMessage($"[PerfCounters] CPU error: {ex.Message}");
            }
        }

        private static void CollectMemory(PerfCounterResult result)
        {
            try
            {
                using var counter = new PerformanceCounter("Memory", "Available MBytes", true);
                var value = counter.NextValue();
                
                if (value >= 0)
                {
                    result.MemoryAvailableMB = Math.Round(value, 0);
                    result.MemoryAvailable = true;
                    App.LogMessage($"[PerfCounters] Memory Available: {value:F0} MB");
                }
                else
                {
                    result.MemoryError = $"Valeur négative: {value}";
                }
            }
            catch (Exception ex)
            {
                result.MemoryError = ex.Message;
                App.LogMessage($"[PerfCounters] Memory error: {ex.Message}");
            }
        }

        private static void CollectDiskTime(PerfCounterResult result)
        {
            try
            {
                using var counter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
                counter.NextValue(); // First call
                Thread.Sleep(100);
                var value = counter.NextValue();
                
                // Disk time peut dépasser 100% sur certains systèmes, on cap à 100
                if (value >= 0)
                {
                    result.DiskTimePercent = Math.Min(Math.Round(value, 1), 100);
                    result.DiskTimeAvailable = true;
                    App.LogMessage($"[PerfCounters] Disk Time: {value:F1}%");
                }
                else
                {
                    result.DiskTimeError = $"Valeur négative: {value}";
                }
            }
            catch (Exception ex)
            {
                result.DiskTimeError = ex.Message;
                App.LogMessage($"[PerfCounters] DiskTime error: {ex.Message}");
            }
        }

        private static void CollectDiskQueue(PerfCounterResult result)
        {
            try
            {
                var wmiValue = TryGetDiskQueueFromWmi();
                if (wmiValue.HasValue && wmiValue.Value >= 0 && wmiValue.Value < 1000)
                {
                    result.DiskQueueLength = Math.Round(wmiValue.Value, 1);
                    result.DiskQueueAvailable = true;
                    App.LogMessage($"[PerfCounters] Disk Queue (WMI): {wmiValue.Value:F1}");
                    return;
                }

                using var counter = new PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", "_Total", true);
                var value = counter.NextValue();

                if (value >= 0 && value < 1000) // Queue > 1000 = aberrant
                {
                    result.DiskQueueLength = Math.Round(value, 1);
                    result.DiskQueueAvailable = true;
                    App.LogMessage($"[PerfCounters] Disk Queue: {value:F1}");
                }
                else if (value == -1)
                {
                    result.DiskQueueError = "Counter non supporté (sentinelle -1)";
                }
                else
                {
                    result.DiskQueueError = $"Valeur aberrante: {value}";
                }
            }
            catch (Exception ex)
            {
                result.DiskQueueError = ex.Message;
                App.LogMessage($"[PerfCounters] DiskQueue error: {ex.Message}");
            }
        }

        private static double? TryGetDiskQueueFromWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AvgDiskQueueLength FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    if (!string.Equals(name, "_Total", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (double.TryParse(obj["AvgDiskQueueLength"]?.ToString(), out var value))
                    {
                        return value;
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerfCounters] WMI DiskQueue error: {ex.Message}");
            }

            return null;
        }

        private static void CollectNetwork(PerfCounterResult result)
        {
            try
            {
                // Network Interface peut avoir plusieurs instances
                var category = new PerformanceCounterCategory("Network Interface");
                var instances = category.GetInstanceNames();
                
                if (instances.Length == 0)
                {
                    result.NetworkError = "Aucune interface réseau";
                    return;
                }

                double totalBytes = 0;
                int validInstances = 0;

                foreach (var instance in instances)
                {
                    try
                    {
                        using var counter = new PerformanceCounter("Network Interface", "Bytes Total/sec", instance, true);
                        var value = counter.NextValue();
                        if (value >= 0)
                        {
                            totalBytes += value;
                            validInstances++;
                        }
                    }
                    catch { /* Skip invalid instance */ }
                }

                if (validInstances > 0)
                {
                    result.NetworkBytesPerSec = Math.Round(totalBytes, 0);
                    result.NetworkAvailable = true;
                    App.LogMessage($"[PerfCounters] Network: {totalBytes:F0} bytes/sec ({validInstances} interfaces)");
                }
                else
                {
                    result.NetworkError = "Aucune interface valide";
                }
            }
            catch (Exception ex)
            {
                result.NetworkError = ex.Message;
                App.LogMessage($"[PerfCounters] Network error: {ex.Message}");
            }
        }
    }
}
