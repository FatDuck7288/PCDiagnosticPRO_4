using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Safe hardware sensors collector that does NOT use WinRing0 or any kernel drivers.
    /// Uses only Windows-native APIs: WMI, Performance Counters, and vendor-specific usermode APIs.
    /// This avoids triggering Windows Defender alerts for vulnerable drivers.
    /// CPU temp: WMI only (MSAcpi_ThermalZoneTemperature, TemperatureProbe, ThermalZoneInformation)
    /// - these methods do NOT trigger any security signal; often "Non disponible" on gaming desktops
    /// where ACPI Thermal Zone is empty. No PerfCounter for CPU temp (Windows does not expose it); no LHM in safe mode.
    /// See docs/CPU_TEMPERATURE_AND_THROTTLING.md for all CPU temperature methods and "no signal" options.
    /// GPU load: Performance Counter "GPU Engine" with engtype_3D only (aligned with Task Manager "GPU 3D").
    /// </summary>
    public class SafeHardwareSensorsCollector
    {
        public Task<HardwareSensorsResult> CollectAsync(CancellationToken ct)
        {
            return Task.Run(() => CollectInternal(ct), ct);
        }

        private HardwareSensorsResult CollectInternal(CancellationToken ct)
        {
            var result = CreateDefaultResult();
            result.CollectionExceptions = new List<string>();

            try
            {
                // Collect CPU temperature via WMI (safe, no driver needed)
                TryCollectCpuMetricsWmi(result);
                
                // Collect GPU metrics via WMI and Performance Counters
                TryCollectGpuMetricsSafe(result);
                
                // Collect disk temperatures via WMI S.M.A.R.T. (if available)
                TryCollectDiskMetricsWmi(result);

                result.CollectedAt = DateTimeOffset.Now;
                result.SafeModeUsed = true;
                
                App.LogMessage("[SafeSensors] Collection completed using safe mode (no kernel drivers)");
            }
            catch (Exception ex)
            {
                result.CollectionExceptions.Add($"SafeCollector: {ex.Message}");
                App.LogMessage($"[SafeSensors] Error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Collect CPU temperature via WMI ThermalZone (Windows built-in, no driver needed)
        /// </summary>
        private void TryCollectCpuMetricsWmi(HardwareSensorsResult result)
        {
            try
            {
                var tempResult = CpuTemperatureCollector.CollectAcpiOnly(result.BlockedBySecurity);
                if (tempResult.Available)
                {
                    result.Cpu.CpuTempC = Available(tempResult.TemperatureC!.Value);
                    CpuTemperatureMetadataService.SetAvailableFromAcpi(result.Cpu, tempResult.SourceDetail ?? tempResult.Source);
                    CpuTemperatureMetadataService.PublishUiSnapshot(
                        tempResult.TemperatureC,
                        tempResult.Source,
                        tempResult.Confidence,
                        null,
                        null);
                    App.LogMessage($"[SafeSensors->CPU] Temperature: {tempResult.TemperatureC.Value:F1}C via {tempResult.SourceDetail ?? tempResult.Source}");
                }
                else
                {
                    var reasonCode = CpuTemperatureMetadataService.NormalizeReasonCode(tempResult.ReasonCode);
                    var reason = string.IsNullOrWhiteSpace(tempResult.ReasonDetail)
                        ? "acpi_temperature_unavailable"
                        : tempResult.ReasonDetail!;
                    result.Cpu.CpuTempC = UnavailableDouble(reason);
                    CpuTemperatureMetadataService.SetUnavailable(
                        result.Cpu,
                        reasonCode,
                        tempResult.SourceDetail ?? tempResult.Source);
                    CpuTemperatureMetadataService.PublishUiSnapshot(
                        null,
                        tempResult.Source,
                        tempResult.Confidence,
                        reasonCode,
                        reason);
                }
                
                // CPU Load via Performance Counter (always available)
                try
                {
                    using var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    cpuCounter.NextValue(); // First call returns 0
                    Thread.Sleep(100);
                    var cpuLoad = cpuCounter.NextValue();
                    result.Cpu.CpuLoadPercent = Available(cpuLoad);
                }
                catch
                {
                    result.Cpu.CpuLoadPercent = UnavailableDouble("Charge CPU indisponible");
                }
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"CPU WMI: {ex.Message}");
                result.Cpu.CpuTempC = UnavailableDouble($"Erreur WMI: {ex.Message}");
                CpuTemperatureMetadataService.SetUnavailable(
                    result.Cpu,
                    CpuTemperatureMetadataService.ReasonError,
                    ex.Message);
                CpuTemperatureMetadataService.PublishUiSnapshot(
                    null,
                    CpuTemperatureMetadataService.SourceNone,
                    CpuTemperatureMetadataService.ConfidenceNone,
                    CpuTemperatureMetadataService.ReasonError,
                    ex.Message);
            }
        }

        /// <summary>
        /// Try to get CPU temperature from Win32_TemperatureProbe
        /// </summary>
        private double? TryGetWin32TemperatureProbe()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_TemperatureProbe");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var currentReading = obj["CurrentReading"];
                    if (currentReading != null)
                    {
                        var tempKelvin = Convert.ToDouble(currentReading) / 10.0;
                        var tempC = tempKelvin - 273.15;
                        if (tempC > 5 && tempC < 115)
                            return tempC;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Collect GPU metrics using safe methods (WMI, Performance Counters, NVML usermode)
        /// </summary>
        private void TryCollectGpuMetricsSafe(HardwareSensorsResult result)
        {
            try
            {
                // Get GPU name from WMI first
                string gpuName = "GPU inconnu";
                long vramTotalBytesWmi = 0;
                
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        gpuName = obj["Name"]?.ToString() ?? "GPU inconnu";
                        var adapterRam = obj["AdapterRAM"];
                        if (adapterRam != null)
                        {
                            vramTotalBytesWmi = Convert.ToInt64(adapterRam);
                        }
                        break; // Take first GPU
                    }
                }
                
                result.Gpu.Name = Available(gpuName);
                
                // VRAM Total: NVML first (avoids WMI UInt32 overflow), then DXGI, then WMI fallback.
                var nvmlMem = NvmlTemperatureReader.TryGetMemoryInfo();
                if (nvmlMem.HasValue && nvmlMem.Value.Total > 0)
                {
                    var totalMB = nvmlMem.Value.Total / (1024.0 * 1024.0);
                    result.Gpu.VramTotalMB = Available(totalMB);
                    App.LogMessage($"[SafeSensorsâ†’GPU] VRAM Total via NVML: {totalMB:F0} Mo");
                }
                else
                {
                    // Try DXGI fallback for AMD/Intel GPUs (or when NVML fails)
                    var dxgiVram = DxgiVramReader.TryGetDedicatedVideoMemoryMB();
                    if (dxgiVram.HasValue && dxgiVram.Value > 0)
                    {
                        result.Gpu.VramTotalMB = Available(dxgiVram.Value);
                        App.LogMessage($"[SafeSensorsâ†’GPU] VRAM Total via DXGI: {dxgiVram.Value:F0} Mo");
                    }
                    else if (vramTotalBytesWmi > 0)
                    {
                        var vramTotalMBWmi = vramTotalBytesWmi / (1024.0 * 1024.0);
                        var gpuNameUpper = gpuName.ToUpperInvariant();
                        bool isHighEndGpu = gpuNameUpper.Contains("3090") || gpuNameUpper.Contains("4090") ||
                                           gpuNameUpper.Contains("3080") || gpuNameUpper.Contains("4080") ||
                                           gpuNameUpper.Contains("4070") || gpuNameUpper.Contains("7900") ||
                                           gpuNameUpper.Contains("6900") || gpuNameUpper.Contains("6800");
                        if (isHighEndGpu && vramTotalMBWmi < 8192)
                        {
                            App.LogMessage($"[SafeSensors→GPU] VRAM WMI overflow détecté: {vramTotalMBWmi:F0} Mo pour {gpuName}");
                            result.Gpu.VramTotalMB = UnavailableDouble("VRAM overflow WMI (UInt32) - valeur incorrecte pour GPU haute gamme");
                        }
                        else if (vramTotalMBWmi > 0 && vramTotalMBWmi < 100000)
                        {
                            result.Gpu.VramTotalMB = Available(vramTotalMBWmi);
                            App.LogMessage($"[SafeSensorsâ†’GPU] VRAM Total via WMI: {vramTotalMBWmi:F0} Mo");
                        }
                        else
                            result.Gpu.VramTotalMB = UnavailableDouble("VRAM totale non fiable via WMI");
                    }
                    else
                        result.Gpu.VramTotalMB = UnavailableDouble("VRAM totale non disponible");
                }
                
                // VRAM Used: Perf Counter "Dedicated Usage" + NVML Used as candidates; take minimum to avoid committed-style ~11 GB when Task Manager shows ~3 GB.
                var vramTotalMB = result.Gpu.VramTotalMB?.Available == true ? result.Gpu.VramTotalMB.Value : (double?)null;
                double? nvmlUsedMB = null;
                if (nvmlMem.HasValue && nvmlMem.Value.Used > 0)
                    nvmlUsedMB = nvmlMem.Value.Used / (1024.0 * 1024.0);
                var vramUsed = TryGetGpuVramUsed(vramTotalMB, nvmlUsedMB);
                if (vramUsed.HasValue)
                {
                    result.Gpu.VramUsedMB = Available(vramUsed.Value);
                    result.Gpu.VramUsedSource = "Performance Counters (Dedicated Usage - matches Task Manager)";
                    App.LogMessage($"[SafeSensorsâ†’GPU] VRAM Used: {vramUsed.Value:F0} Mo (Task Manager equivalent)");
                }
                else
                {
                    result.Gpu.VramUsedMB = UnavailableDouble("VRAM utilisée: voir Gestionnaire des tâches");
                    result.Gpu.VramUsedSource = "Non disponible (mode sécurisé)";
                }
                PopulateDedicatedVramMetadata(result.Gpu);
                
                // GPU Load via Performance Counters
                var gpuLoad = TryGetGpuLoadFromPerfCounters();
                if (gpuLoad.HasValue)
                {
                    result.Gpu.GpuLoadPercent = Available(Math.Clamp(gpuLoad.Value, 0.0, 100.0));
                }
                else
                {
                    result.Gpu.GpuLoadPercent = UnavailableDouble("Charge GPU: voir Gestionnaire des tâches");
                }
                
                // GPU Temperature: Try NVIDIA NVML (usermode DLL, no driver needed)
                if (gpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var nvmlTemp = TryGetNvidiaTemperature();
                    if (nvmlTemp.HasValue)
                    {
                        result.Gpu.GpuTempC = Available(nvmlTemp.Value);
                        result.Gpu.GpuTempSource = "NVIDIA NVML (usermode)";
                        App.LogMessage($"[SafeSensors→GPU] NVIDIA temp: {nvmlTemp.Value:F1}°C via NVML");
                    }
                    else
                    {
                        result.Gpu.GpuTempC = UnavailableDouble("Température GPU: voir Gestionnaire des tâches");
                        result.Gpu.GpuTempSource = "Non disponible (mode sécurisé)";
                    }
                }
                else
                {
                    // AMD/Intel: No safe usermode API available
                    result.Gpu.GpuTempC = UnavailableDouble("Température GPU: voir Gestionnaire des tâches ou GPU-Z");
                    result.Gpu.GpuTempSource = "Non disponible (mode sécurisé)";
                }
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"GPU Safe: {ex.Message}");
                SetGpuUnavailable(result, $"Erreur: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets dedicated GPU memory usage: Perf Counter "Dedicated Usage" + optional NVML Used. Returns minimum of all candidates so we never display committed-style ~11 GB when Task Manager shows ~3 GB. Rejects single value &gt; 8 GB as suspicious.
        /// </summary>
        private double? TryGetGpuVramUsed(double? vramTotalMB, double? nvmlUsedMB)
        {
            var candidates = new List<double>();
            try
            {
                // 1) Add NVML Used if available and sensible (often matches Task Manager dedicated)
                if (nvmlUsedMB.HasValue && nvmlUsedMB.Value > 0)
                {
                    if (!vramTotalMB.HasValue || nvmlUsedMB.Value <= vramTotalMB.Value)
                    {
                        candidates.Add(nvmlUsedMB.Value);
                        App.LogMessage($"[SafeSensorsâ†’VRAM] Candidate NVML Used: {nvmlUsedMB.Value:F0} Mo");
                    }
                }

                // 2) Perf counter instances (some report committed ~11 GB)
                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                var instances = category.GetInstanceNames();
                if (instances != null && instances.Length > 0)
                {
                    App.LogMessage($"[SafeSensorsâ†’VRAM] Perf instances: {instances.Length} ({string.Join(", ", instances)})");
                    foreach (var instance in instances)
                    {
                        try
                        {
                            using var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instance, true);
                            var value = counter.NextValue();
                            var dedicatedMB = value / (1024.0 * 1024.0);
                            if (dedicatedMB <= 0) continue;
                            if (vramTotalMB.HasValue && dedicatedMB > vramTotalMB.Value)
                            {
                                App.LogMessage($"[SafeSensorsâ†’VRAM] Skip instance '{instance}': {dedicatedMB:F0} Mo > total {vramTotalMB.Value:F0} Mo");
                                continue;
                            }
                            candidates.Add(dedicatedMB);
                            App.LogMessage($"[SafeSensorsâ†’VRAM] Candidate Perf '{instance}': {dedicatedMB:F0} Mo");
                        }
                        catch (Exception ex) { App.LogMessage($"[SafeSensorsâ†’VRAM] Perf instance '{instance}': {ex.Message}"); }
                    }
                }

                if (candidates.Count == 0) return null;
                var chosen = candidates.Min();
                if (vramTotalMB.HasValue && chosen > (vramTotalMB.Value * 1.05))
                {
                    App.LogMessage($"[SafeSensorsâ†’VRAM] Reject {chosen:F0} Mo (> total {vramTotalMB.Value:F0} Mo)");
                    return null;
                }
                if (!vramTotalMB.HasValue && chosen > 32768)
                {
                    App.LogMessage($"[SafeSensorsâ†’VRAM] Reject {chosen:F0} Mo (out-of-range without total VRAM)");
                    return null;
                }
                App.LogMessage($"[SafeSensorsâ†’VRAM] Chosen: {chosen:F0} Mo (min of {candidates.Count} candidates)");
                return chosen;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SafeSensors] GPU VRAM error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Try to get GPU load from Windows Performance Counters.
        /// Uses only "engtype_3D" instances so the value matches Task Manager "GPU 3D" (not Copy/Video Decode).
        /// </summary>
        private double? TryGetGpuLoadFromPerfCounters()
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();
                
                double maxUtilization = 0;
                
                foreach (var instance in instances)
                {
                    if (instance.Contains("engtype_3D"))
                    {
                        using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        counter.NextValue();
                        Thread.Sleep(50);
                        var value = counter.NextValue();
                        if (value > maxUtilization)
                            maxUtilization = value;
                    }
                }
                
                if (maxUtilization > 0)
                    return maxUtilization;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SafeSensors] GPU Engine PerfCounter error: {ex.Message}");
            }
            
            return null;
        }

        /// <summary>
        /// Try to get NVIDIA GPU temperature using NVML (usermode DLL)
        /// NVML is installed with NVIDIA drivers and doesn't require kernel access
        /// </summary>
        private double? TryGetNvidiaTemperature()
        {
            try
            {
                // Check if nvml.dll exists
                var nvmlPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var nvmlDll = System.IO.Path.Combine(nvmlPath, "nvml.dll");
                
                if (!System.IO.File.Exists(nvmlDll))
                {
                    // Try in NVIDIA driver folder
                    nvmlDll = @"C:\Windows\System32\nvml.dll";
                    if (!System.IO.File.Exists(nvmlDll))
                    {
                        App.LogMessage("[SafeSensors] nvml.dll not found");
                        return null;
                    }
                }
                
                // Use NvmlWrapper if available
                return NvmlTemperatureReader.TryGetTemperature();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SafeSensors] NVML error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Collect disk metrics using WMI (no driver needed)
        /// </summary>
        private void TryCollectDiskMetricsWmi(HardwareSensorsResult result)
        {
            try
            {
                result.Disks.Clear();
                
                // Get disk names from WMI
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                var diskList = searcher.Get().Cast<ManagementObject>().ToList();
                
                if (diskList.Count == 0)
                {
                    var diskMetric = new DiskMetrics
                    {
                        Name = Unavailable("Aucun disque détecté"),
                        TempC = UnavailableDouble("Température non disponible")
                    };
                    result.Disks.Add(diskMetric);
                    return;
                }
                
                foreach (var disk in diskList)
                {
                    var diskName = disk["Model"]?.ToString() ?? disk["Caption"]?.ToString() ?? "Disque inconnu";
                    
                    var diskMetric = new DiskMetrics
                    {
                        Name = Available(diskName),
                        // S.M.A.R.T. temperature requires admin rights and special access
                        // In safe mode, we can't reliably get disk temperature
                        TempC = UnavailableDouble("Température disque indisponible (capteur non accessible)")
                    };
                    
                    result.Disks.Add(diskMetric);
                }
                
                // Try to get disk temperature via SMART WMI (may require admin)
                TryEnrichDiskTemperatureFromSmart(result);
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"Disks WMI: {ex.Message}");
                result.Disks.Clear();
                var diskMetric = new DiskMetrics
                {
                    Name = Unavailable($"Erreur: {ex.Message}"),
                    TempC = UnavailableDouble($"Erreur: {ex.Message}")
                };
                result.Disks.Add(diskMetric);
            }
        }

        /// <summary>
        /// Try to get disk temperature from WMI SMART data
        /// </summary>
        private void TryEnrichDiskTemperatureFromSmart(HardwareSensorsResult result)
        {
            try
            {
                // MSStorageDriver_ATAPISmartData requires admin rights
                using var searcher = new ManagementObjectSearcher(@"root\WMI", 
                    "SELECT * FROM MSStorageDriver_ATAPISmartData");
                
                var smartData = searcher.Get().Cast<ManagementObject>().ToList();

                foreach (var data in smartData)
                {
                    if (result.Disks.Count == 0)
                        break;

                    var vendorSpecific = data["VendorSpecific"] as byte[];
                    if (!TryExtractSmartTemperature(vendorSpecific, out var temp))
                        continue;

                    var instanceName = data["InstanceName"]?.ToString() ?? string.Empty;
                    var normalizedInstance = NormalizeDiskLookupName(instanceName);

                    DiskMetrics? targetDisk = null;
                    foreach (var disk in result.Disks)
                    {
                        if (disk?.Name?.Available != true || string.IsNullOrWhiteSpace(disk.Name.Value))
                            continue;

                        var normalizedDiskName = NormalizeDiskLookupName(disk.Name.Value);
                        if (string.IsNullOrWhiteSpace(normalizedDiskName))
                            continue;

                        if (normalizedInstance.Contains(normalizedDiskName, StringComparison.OrdinalIgnoreCase) ||
                            normalizedDiskName.Contains(normalizedInstance, StringComparison.OrdinalIgnoreCase))
                        {
                            targetDisk = disk;
                            break;
                        }
                    }

                    if (targetDisk == null)
                    {
                        targetDisk = result.Disks.FirstOrDefault(d => d?.TempC?.Available != true);
                    }

                    if (targetDisk != null)
                        targetDisk.TempC = Available(temp);
                }
            }
            catch (Exception ex)
            {
                // SMART access often requires admin - silently ignore
                App.LogMessage($"[SafeSensors] SMART access failed (may need admin): {ex.Message}");
            }
        }

        private static bool TryExtractSmartTemperature(byte[]? vendorSpecific, out double temperature)
        {
            temperature = 0;
            if (vendorSpecific == null || vendorSpecific.Length < 12)
                return false;

            // SMART attributes are 12-byte entries. Temperature is usually attribute 194 (0xC2),
            // but some drives expose it as attribute 190 (0xBE).
            for (int i = 2; i <= vendorSpecific.Length - 12; i += 12)
            {
                var attrId = vendorSpecific[i];
                if (attrId != 0xC2 && attrId != 0xBE)
                    continue;

                var lowByte = vendorSpecific[i + 5];
                if (lowByte > 0 && lowByte <= 120)
                {
                    temperature = lowByte;
                    return true;
                }

                try
                {
                    if (i + 9 >= vendorSpecific.Length)
                        continue;

                    var raw = BitConverter.ToUInt32(vendorSpecific, i + 5);
                    var parsed = raw & 0xFF;
                    if (parsed > 0 && parsed <= 120)
                    {
                        temperature = parsed;
                        return true;
                    }
                }
                catch
                {
                    // Ignore malformed SMART payloads.
                }
            }

            return false;
        }

        private static string NormalizeDiskLookupName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim();
            var pipeIdx = normalized.IndexOf('|');
            if (pipeIdx > 0)
                normalized = normalized.Substring(0, pipeIdx);

            var parenIdx = normalized.IndexOf('(');
            if (parenIdx > 0)
                normalized = normalized.Substring(0, parenIdx);

            normalized = normalized.Replace("USB Device", string.Empty, StringComparison.OrdinalIgnoreCase);
            normalized = normalized.Replace("_", " ", StringComparison.Ordinal);
            normalized = normalized.Replace("\\", " ", StringComparison.Ordinal);
            normalized = normalized.Replace("/", " ", StringComparison.Ordinal);

            while (normalized.Contains("  ", StringComparison.Ordinal))
                normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);

            return normalized.Trim();
        }

        private static HardwareSensorsResult CreateDefaultResult()
        {
            var res = new HardwareSensorsResult
            {
                CollectedAt = DateTimeOffset.Now,
                Gpu = new GpuMetrics
                {
                    Name = Unavailable("GPU non collecté"),
                    VramTotalMB = UnavailableDouble("VRAM totale non collectée"),
                    VramUsedMB = UnavailableDouble("VRAM utilisée non collectée"),
                    VramDedicatedTotalMB = UnavailableDouble("VRAM dediee totale non collectee"),
                    VramDedicatedUsedMB = UnavailableDouble("VRAM dediee utilisee non collectee"),
                    VramDedicatedPercent = UnavailableDouble("VRAM dediee pourcentage non collecte"),
                    VramDedicatedSource = "N/A",
                    VramDedicatedConfidence = "low",
                    VramDedicatedReasonIfMissing = "Collecte GPU non demarree",
                    GpuLoadPercent = UnavailableDouble("Charge GPU non collectée"),
                    GpuTempC = UnavailableDouble("Température GPU non collectée")
                },
                Cpu = new CpuMetrics
                {
                    CpuTempC = UnavailableDouble("Température CPU non collectée"),
                    CpuTempSource = CpuTemperatureMetadataService.SourceNone,
                    CpuTempConfidence = CpuTemperatureMetadataService.ConfidenceNone,
                    CpuTempReasonIfMissing = CpuTemperatureMetadataService.ReasonNoSensors,
                    CpuTempSourceDetail = "Collector not started",
                    CpuLoadPercent = UnavailableDouble("Charge CPU non collectée")
                },
                Disks = new List<DiskMetrics>()
            };
            return res;
        }

        private static void SetGpuUnavailable(HardwareSensorsResult result, string reason)
        {
            result.Gpu.Name = Unavailable(reason);
            result.Gpu.VramTotalMB = UnavailableDouble(reason);
            result.Gpu.VramUsedMB = UnavailableDouble(reason);
            result.Gpu.VramDedicatedTotalMB = UnavailableDouble(reason);
            result.Gpu.VramDedicatedUsedMB = UnavailableDouble(reason);
            result.Gpu.VramDedicatedPercent = UnavailableDouble(reason);
            result.Gpu.VramDedicatedSource = "N/A";
            result.Gpu.VramDedicatedConfidence = "low";
            result.Gpu.VramDedicatedReasonIfMissing = reason;
            result.Gpu.GpuLoadPercent = UnavailableDouble(reason);
            result.Gpu.GpuTempC = UnavailableDouble(reason);
        }

        private static void PopulateDedicatedVramMetadata(GpuMetrics gpu)
        {
            if (gpu.VramTotalMB.Available && gpu.VramUsedMB.Available && gpu.VramTotalMB.Value > 0)
            {
                var percent = Math.Clamp((gpu.VramUsedMB.Value / gpu.VramTotalMB.Value) * 100.0, 0.0, 100.0);
                gpu.VramDedicatedTotalMB = Available(gpu.VramTotalMB.Value);
                gpu.VramDedicatedUsedMB = Available(gpu.VramUsedMB.Value);
                gpu.VramDedicatedPercent = Available(percent);
                gpu.VramDedicatedSource = string.IsNullOrWhiteSpace(gpu.VramUsedSource) ? "N/A" : gpu.VramUsedSource;
                gpu.VramDedicatedConfidence = gpu.VramDedicatedSource.Contains("Dedicated", StringComparison.OrdinalIgnoreCase) ? "high" : "medium";
                gpu.VramDedicatedReasonIfMissing = null;
                return;
            }

            var reason = gpu.VramUsedMB.Reason ?? gpu.VramTotalMB.Reason ?? "VRAM dediee indisponible";
            gpu.VramDedicatedTotalMB = UnavailableDouble(reason);
            gpu.VramDedicatedUsedMB = UnavailableDouble(reason);
            gpu.VramDedicatedPercent = UnavailableDouble(reason);
            gpu.VramDedicatedSource = string.IsNullOrWhiteSpace(gpu.VramUsedSource) ? "N/A" : gpu.VramUsedSource;
            gpu.VramDedicatedConfidence = "low";
            gpu.VramDedicatedReasonIfMissing = reason;
        }

        private static MetricValue<string> Available(string value) => new()
        {
            Available = true,
            Value = value,
            Source = "SafeHardwareSensorsCollector",
            Confidence = "medium",
            Timestamp = DateTime.UtcNow.ToString("o")
        };

        private static MetricValue<double> Available(double value) => new()
        {
            Available = true,
            Value = value,
            Source = "SafeHardwareSensorsCollector",
            Confidence = "medium",
            Timestamp = DateTime.UtcNow.ToString("o")
        };

        private static MetricValue<string> Unavailable(string reason) => new()
        {
            Available = false,
            Reason = string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason,
            Source = "SafeHardwareSensorsCollector",
            Confidence = "low",
            Timestamp = DateTime.UtcNow.ToString("o")
        };

        private static MetricValue<double> UnavailableDouble(string reason) => new()
        {
            Available = false,
            Reason = string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason,
            Source = "SafeHardwareSensorsCollector",
            Confidence = "low",
            Timestamp = DateTime.UtcNow.ToString("o")
        };
    }

    /// <summary>
    /// NVIDIA NVML temperature reader using P/Invoke
    /// NVML is a usermode library that doesn't require kernel drivers
    /// </summary>
    internal static class NvmlTemperatureReader
    {
        private const string NVML_DLL = "nvml.dll";

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlInit_v2();

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlShutdown();

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetCount_v2(out uint deviceCount);

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);

        [StructLayout(LayoutKind.Sequential)]
        private struct nvmlMemory_t
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out nvmlMemory_t mem);

        private const int NVML_SUCCESS = 0;
        private const int NVML_TEMPERATURE_GPU = 0; // Core temperature

        public static double? TryGetTemperature()
        {
            try
            {
                int result = nvmlInit_v2();
                if (result != NVML_SUCCESS)
                {
                    App.LogMessage($"[NVML] Init failed with code {result}");
                    return null;
                }

                try
                {
                    result = nvmlDeviceGetCount_v2(out uint deviceCount);
                    if (result != NVML_SUCCESS || deviceCount == 0)
                    {
                        App.LogMessage($"[NVML] No devices found (code {result}, count {deviceCount})");
                        return null;
                    }

                    // Get first GPU
                    result = nvmlDeviceGetHandleByIndex_v2(0, out IntPtr device);
                    if (result != NVML_SUCCESS)
                    {
                        App.LogMessage($"[NVML] GetHandle failed with code {result}");
                        return null;
                    }

                    result = nvmlDeviceGetTemperature(device, NVML_TEMPERATURE_GPU, out uint temperature);
                    if (result == NVML_SUCCESS && temperature > 0 && temperature < 150)
                    {
                        App.LogMessage($"[NVML] GPU temperature: {temperature}°C");
                        return temperature;
                    }

                    App.LogMessage($"[NVML] GetTemperature failed with code {result}");
                    return null;
                }
                finally
                {
                    nvmlShutdown();
                }
            }
            catch (DllNotFoundException)
            {
                App.LogMessage("[NVML] nvml.dll not found - NVIDIA drivers may not be installed");
                return null;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[NVML] Exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Try to get GPU memory info (Total/Used) via NVML.
        /// Returns null if NVML is not available or fails.
        /// This avoids the UInt32 overflow from WMI AdapterRAM for GPUs > 4GB.
        /// </summary>
        public static (ulong Total, ulong Used)? TryGetMemoryInfo()
        {
            try
            {
                int result = nvmlInit_v2();
                if (result != NVML_SUCCESS)
                {
                    App.LogMessage($"[NVML] Init failed with code {result} (memory info)");
                    return null;
                }

                try
                {
                    result = nvmlDeviceGetCount_v2(out uint deviceCount);
                    if (result != NVML_SUCCESS || deviceCount == 0)
                    {
                        App.LogMessage($"[NVML] No devices found for memory info (code {result}, count {deviceCount})");
                        return null;
                    }

                    // Get first GPU
                    result = nvmlDeviceGetHandleByIndex_v2(0, out IntPtr device);
                    if (result != NVML_SUCCESS)
                    {
                        App.LogMessage($"[NVML] GetHandle failed with code {result} (memory info)");
                        return null;
                    }

                    result = nvmlDeviceGetMemoryInfo(device, out nvmlMemory_t memInfo);
                    if (result == NVML_SUCCESS && memInfo.Total > 0)
                    {
                        App.LogMessage($"[NVML] GPU memory: Total={memInfo.Total / (1024 * 1024)} MB, Used={memInfo.Used / (1024 * 1024)} MB, Free={memInfo.Free / (1024 * 1024)} MB");
                        return (memInfo.Total, memInfo.Used);
                    }

                    App.LogMessage($"[NVML] GetMemoryInfo failed with code {result}");
                    return null;
                }
                finally
                {
                    nvmlShutdown();
                }
            }
            catch (DllNotFoundException)
            {
                App.LogMessage("[NVML] nvml.dll not found (memory info) - NVIDIA drivers may not be installed");
                return null;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[NVML] Exception (memory info): {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// DXGI-based VRAM reader that works for all GPUs (NVIDIA, AMD, Intel)
    /// Uses DirectX Graphics Infrastructure (DXGI) which is part of Windows
    /// No special drivers needed - works as a universal fallback
    /// </summary>
    internal static class DxgiVramReader
    {
        [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int CreateDXGIFactory1([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppFactory);

        // DXGI_ADAPTER_DESC structure
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public ulong DedicatedVideoMemory;    // This is VRAM
            public ulong DedicatedSystemMemory;
            public ulong SharedSystemMemory;
            public long AdapterLuid_LowPart;
            public int AdapterLuid_HighPart;
        }

        private static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IID_IDXGIAdapter1 = new Guid("29038f61-3839-4626-91fd-086879011a05");

        // IDXGIFactory1 interface methods (via vtable)
        private delegate int EnumAdapters1Delegate(IntPtr pFactory, uint index, out IntPtr ppAdapter);
        private delegate int GetDescDelegate(IntPtr pAdapter, out DXGI_ADAPTER_DESC pDesc);

        /// <summary>
        /// Try to get dedicated video memory (VRAM) in MB using DXGI
        /// This works for all GPU vendors (NVIDIA, AMD, Intel)
        /// </summary>
        public static double? TryGetDedicatedVideoMemoryMB()
        {
            IntPtr pFactory = IntPtr.Zero;
            IntPtr pAdapter = IntPtr.Zero;

            try
            {
                // Create DXGI Factory
                int hr = CreateDXGIFactory1(IID_IDXGIFactory1, out pFactory);
                if (hr != 0 || pFactory == IntPtr.Zero)
                {
                    App.LogMessage($"[DXGI] CreateDXGIFactory1 failed with HRESULT 0x{hr:X8}");
                    return null;
                }

                // Get vtable pointer for IDXGIFactory1
                IntPtr vtable = Marshal.ReadIntPtr(pFactory);
                
                // EnumAdapters1 is at vtable index 12 (after IUnknown methods + IDXGIObject + IDXGIFactory methods)
                IntPtr enumAdaptersPtr = Marshal.ReadIntPtr(vtable, 12 * IntPtr.Size);
                var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdaptersPtr);

                // Enumerate first adapter (primary GPU)
                hr = enumAdapters(pFactory, 0, out pAdapter);
                if (hr != 0 || pAdapter == IntPtr.Zero)
                {
                    App.LogMessage($"[DXGI] EnumAdapters1 failed with HRESULT 0x{hr:X8}");
                    return null;
                }

                // Get adapter vtable
                IntPtr adapterVtable = Marshal.ReadIntPtr(pAdapter);
                
                // GetDesc is at vtable index 8 (after IUnknown + IDXGIObject methods)
                IntPtr getDescPtr = Marshal.ReadIntPtr(adapterVtable, 8 * IntPtr.Size);
                var getDesc = Marshal.GetDelegateForFunctionPointer<GetDescDelegate>(getDescPtr);

                // Get adapter description
                hr = getDesc(pAdapter, out DXGI_ADAPTER_DESC desc);
                if (hr != 0)
                {
                    App.LogMessage($"[DXGI] GetDesc failed with HRESULT 0x{hr:X8}");
                    return null;
                }

                var dedicatedVideoMemoryMB = desc.DedicatedVideoMemory / (1024.0 * 1024.0);
                App.LogMessage($"[DXGI] GPU: {desc.Description}, Dedicated VRAM: {dedicatedVideoMemoryMB:F0} MB");

                // Sanity check - VRAM should be at least 128MB for a discrete GPU
                if (dedicatedVideoMemoryMB < 128)
                {
                    App.LogMessage($"[DXGI] VRAM too low ({dedicatedVideoMemoryMB:F0} MB), might be integrated GPU without dedicated memory");
                    return null;
                }

                return dedicatedVideoMemoryMB;
            }
            catch (DllNotFoundException)
            {
                App.LogMessage("[DXGI] dxgi.dll not found");
                return null;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DXGI] Exception: {ex.Message}");
                return null;
            }
            finally
            {
                // Release COM objects
                if (pAdapter != IntPtr.Zero)
                    Marshal.Release(pAdapter);
                if (pFactory != IntPtr.Zero)
                    Marshal.Release(pFactory);
            }
        }
    }
}

