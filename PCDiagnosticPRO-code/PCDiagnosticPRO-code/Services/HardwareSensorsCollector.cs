using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// CPU temperature collection methods (documentation):
    /// - LibreHardwareMonitor (LHM): software sensors via driver; may require admin, can trigger security;
    ///   when not in safe mode, provides real-time CPU package/core temps (can be "noisy" for some AV).
    /// - WMI (MSAcpi_ThermalZoneTemperature, Win32_TemperatureProbe): built-in Windows, no extra driver;
    ///   often limited or empty on desktops; "silent" (no special signals).
    /// - Performance Counters: Windows does not expose CPU temperature in PerfCounter; only frequency/load.
    /// - ACPI: not used in this codebase; would require kernel/BIOS support.
    /// Safe mode uses only WMI/PerfCounter (and for GPU: PerfCounter + NVML); no LHM.
    /// </summary>
    public class HardwareSensorsCollector
    {
        /// <summary>
        /// Use safe mode by default to avoid Windows Defender WinRing0 alerts.
        /// Safe mode uses WMI, Performance Counters, and NVML (for NVIDIA) instead of kernel drivers.
        /// Set to false only if user explicitly wants full sensor access and has whitelisted the app.
        /// </summary>
        public static bool UseSafeModeByDefault { get; set; } = true;
        
        /// <summary>
        /// Force unsafe mode (LibreHardwareMonitor with WinRing0) for this collection.
        /// Use only when user has explicitly configured exclusion in Windows Defender.
        /// </summary>
        public bool ForceUnsafeMode { get; set; } = false;
        
        /// <summary>
        /// When true, log all hardware and CPU sensors to %TEMP%\PCDiagnosticPro_CPU_Temp_Diagnostic.log
        /// and App.LogMessage, and print explanation if no temperature is found.
        /// </summary>
        public static bool CpuTempDiagnosticMode { get; set; } = false;

        public Task<HardwareSensorsResult> CollectAsync(CancellationToken ct)
        {
            if (ForceUnsafeMode)
            {
                if (RequiresAdminForFullSensors())
                    App.LogMessage("[HardwareSensors] LHM (LibreHardwareMonitor) : exécution sans droits administrateur. Si la température CPU n'apparaît pas, relancez l'application en tant qu'administrateur ou ajoutez une exclusion Defender.");
                App.LogMessage("[HardwareSensors] Using LHM mode (LibreHardwareMonitor) - ForceUnsafeMode=true");
                return Task.Run(() => CollectInternal(ct), ct);
            }
            App.LogMessage("[HardwareSensors] Using SAFE mode (WMI/NVML) - WinRing0 eliminated");
            var safeCollector = new SafeHardwareSensorsCollector();
            return safeCollector.CollectAsync(ct);
        }

        private HardwareSensorsResult CollectInternal(CancellationToken ct)
        {
            var result = CreateDefaultResult();
            result.CollectionExceptions = new List<string>();
            Computer? computer = null;

            try
            {
                computer = new Computer();
                computer.IsCpuEnabled = true;
                computer.IsGpuEnabled = true;
                computer.IsStorageEnabled = true;
                computer.Open();

                // Update récursif : tous les hardware et subhardware pour que les sensors aient des valeurs
                foreach (var hw in computer.Hardware)
                    UpdateHardwareRecursive(hw);

                TryCollectGpuMetrics(computer, result);
                TryCollectCpuMetrics(computer, result);
                TryCollectDiskMetrics(computer, result);

                result.CollectedAt = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                var exMsg = ex.Message;
                result.CollectionExceptions.Add($"Global: {exMsg}");
                MarkAllUnavailable(result, string.Format("Erreur globale: {0}", exMsg));
                
                // Detect Defender/WinRing0/security blocking
                DetectSecurityBlocking(result, ex);
            }
            finally
            {
                if (computer != null)
                {
                    try
                    {
                        computer.Close();
                    }
                    catch
                    {
                        // Ignorer les erreurs de fermeture
                    }
                }
            }
            
            // Post-process: check if blocking detected from individual collector errors
            if (!result.BlockedBySecurity && result.CollectionExceptions?.Count > 0)
            {
                foreach (var ex in result.CollectionExceptions)
                {
                    if (IsSecurityBlockingError(ex))
                    {
                        result.BlockedBySecurity = true;
                        result.BlockingMessage = "Capteurs bloqués par la sécurité. Exécuter en tant qu'administrateur ou ajouter une exclusion sur le dossier de l'application.";
                        if (result.Cpu != null && result.Cpu.CpuTempC?.Available != true)
                        {
                            CpuTemperatureMetadataService.SetUnavailable(
                                result.Cpu,
                                CpuTemperatureMetadataService.ReasonBlockedBySecurity,
                                "Security software blocking sensor access");
                        }
                        break;
                    }
                }
            }
            
            // Log to temp for debugging
            LogSensorCollectionStatus(result);

            return result;
        }
        
        /// <summary>
        /// Detect if exception indicates Defender/WinRing0/security blocking
        /// </summary>
        private static void DetectSecurityBlocking(HardwareSensorsResult result, Exception ex)
        {
            var exLower = ex.Message.ToLowerInvariant();
            var innerEx = ex.InnerException?.Message?.ToLowerInvariant() ?? "";
            
            // Detailed logging for Defender/CFA diagnostics
            App.LogMessage($"[HardwareSensors] Exception type: {ex.GetType().FullName}");
            App.LogMessage($"[HardwareSensors] Message: {ex.Message}");
            if (ex.InnerException != null)
                App.LogMessage($"[HardwareSensors] InnerException: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
            App.LogMessage($"[HardwareSensors] AppBaseDir: {AppContext.BaseDirectory}");
            
            if (IsSecurityBlockingError(exLower) || IsSecurityBlockingError(innerEx))
            {
                result.BlockedBySecurity = true;
                result.BlockingMessage = "Capteurs bloqués par la sécurité. Exécuter en tant qu'administrateur ou ajouter une exclusion sur le dossier de l'application.";
                if (result.Cpu != null && result.Cpu.CpuTempC?.Available != true)
                {
                    CpuTemperatureMetadataService.SetUnavailable(
                        result.Cpu,
                        CpuTemperatureMetadataService.ReasonBlockedBySecurity,
                        "Security exception path");
                }
                App.LogMessage($"[HardwareSensors] SECURITY BLOCKING DETECTED: {ex.Message}");
                App.LogMessage($"[HardwareSensors] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Check if hardware sensor collection would require admin privileges.
        /// Returns true if admin is NOT available and full sensors are requested.
        /// </summary>
        public static bool RequiresAdminForFullSensors()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return !principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return true; }
        }
        
        /// <summary>
        /// Check if error message indicates security blocking
        /// </summary>
        private static bool IsSecurityBlockingError(string errorMsg)
        {
            if (string.IsNullOrEmpty(errorMsg)) return false;
            
            var lower = errorMsg.ToLowerInvariant();
            return lower.Contains("access denied") || 
                   lower.Contains("access is denied") ||
                   lower.Contains("defender") || 
                   lower.Contains("antivirus") || 
                   lower.Contains("blocked") ||
                   lower.Contains("winring0") ||
                   lower.Contains("ring0") ||
                   lower.Contains("driver") && (lower.Contains("load") || lower.Contains("failed")) ||
                   lower.Contains("security") ||
                   lower.Contains("unauthorized") ||
                   lower.Contains("permission") ||
                   lower.Contains("privilege");
        }
        
        /// <summary>
        /// Log all GPU memory sensors for debugging VRAM accuracy
        /// </summary>
        private static void LogGpuMemorySensors(List<ISensor> sensors, double? selectedTotal, double? selectedUsed)
        {
            try
            {
                var memorySensors = sensors.Where(s => 
                    s.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.Name.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.Name.IndexOf("D3D", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                
                if (memorySensors.Count > 0)
                {
                    var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCDiagnosticPro_VRAM_Debug.log");
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"=== GPU Memory Sensors - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    sb.AppendLine($"Total sensors found: {memorySensors.Count}");
                    sb.AppendLine();
                    
                    foreach (var sensor in memorySensors)
                    {
                        sb.AppendLine($"  Sensor: {sensor.Name}");
                        sb.AppendLine($"    Type: {sensor.SensorType}");
                        sb.AppendLine($"    Value: {sensor.Value?.ToString() ?? "null"} (Hardware: {sensor.Hardware?.Name ?? "unknown"})");
                    }
                    
                    sb.AppendLine();
                    sb.AppendLine($"SELECTED Values:");
                    sb.AppendLine($"  VRAM Total: {selectedTotal?.ToString("F0") ?? "null"} MB");
                    sb.AppendLine($"  VRAM Used:  {selectedUsed?.ToString("F0") ?? "null"} MB");
                    sb.AppendLine();
                    
                    System.IO.File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
                    App.LogMessage($"[GPU Memory] Found {memorySensors.Count} memory sensors. Selected: Total={selectedTotal:F0}MB, Used={selectedUsed:F0}MB");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GPU Memory] Logging error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Log sensor collection status to %TEMP% for debugging
        /// </summary>
        private static void LogSensorCollectionStatus(HardwareSensorsResult result)
        {
            try
            {
                var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCDiagnosticPro_SensorCollection.log");
                var (available, total) = result.GetAvailabilitySummary();
                var logContent = $"=== Sensor Collection Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n" +
                                 $"Available: {available}/{total}\n" +
                                 $"BlockedBySecurity: {result.BlockedBySecurity}\n" +
                                 $"BlockingMessage: {result.BlockingMessage ?? "N/A"}\n" +
                                 $"Exceptions: {string.Join("; ", result.CollectionExceptions ?? new List<string>())}\n" +
                                 $"CPU Temp: {(result.Cpu.CpuTempC.Available ? result.Cpu.CpuTempC.Value.ToString("F1") + "°C" : result.Cpu.CpuTempC.Reason)}\n" +
                                 $"GPU Temp: {(result.Gpu.GpuTempC.Available ? result.Gpu.GpuTempC.Value.ToString("F1") + "°C" : result.Gpu.GpuTempC.Reason)}\n";
                System.IO.File.AppendAllText(logPath, logContent + "\n", Encoding.UTF8);
            }
            catch { /* Ignore logging errors */ }
        }

        private static HardwareSensorsResult CreateDefaultResult()
        {
            var res = new HardwareSensorsResult();
            res.CollectedAt = DateTimeOffset.Now;
            
            res.Gpu = new GpuMetrics();
            res.Gpu.Name = Unavailable("GPU non collecte");
            res.Gpu.VramTotalMB = UnavailableDouble("VRAM totale non collectee");
            res.Gpu.VramUsedMB = UnavailableDouble("VRAM utilisee non collectee");
            res.Gpu.VramDedicatedTotalMB = UnavailableDouble("VRAM dediee totale non collectee");
            res.Gpu.VramDedicatedUsedMB = UnavailableDouble("VRAM dediee utilisee non collectee");
            res.Gpu.VramDedicatedPercent = UnavailableDouble("VRAM dediee pourcentage non collecte");
            res.Gpu.VramDedicatedSource = "N/A";
            res.Gpu.VramDedicatedConfidence = "low";
            res.Gpu.VramDedicatedReasonIfMissing = "Collecte GPU non demarree";
            res.Gpu.GpuLoadPercent = UnavailableDouble("Charge GPU non collectee");
            res.Gpu.GpuTempC = UnavailableDouble("Temperature GPU non collectee");
            
            res.Cpu = new CpuMetrics();
            res.Cpu.CpuTempC = UnavailableDouble("Temperature CPU non collectee");
            res.Cpu.CpuTempSource = CpuTemperatureMetadataService.SourceNone;
            res.Cpu.CpuTempConfidence = CpuTemperatureMetadataService.ConfidenceNone;
            res.Cpu.CpuTempReasonIfMissing = CpuTemperatureMetadataService.ReasonNoSensors;
            res.Cpu.CpuTempSourceDetail = "Collector not started";
            res.Cpu.CpuLoadPercent = UnavailableDouble("Charge CPU: utiliser donnees PowerShell");
            
            res.Disks = new List<DiskMetrics>();
            
            return res;
        }

        private static void MarkAllUnavailable(HardwareSensorsResult result, string reason)
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
            result.Cpu.CpuTempC = UnavailableDouble(reason);
            CpuTemperatureMetadataService.SetUnavailable(
                result.Cpu,
                CpuTemperatureMetadataService.ClassifyReasonCode(reason, result.BlockedBySecurity),
                "Global collector failure");
            result.Cpu.CpuLoadPercent = UnavailableDouble(reason);
            result.Disks.Clear();
            
            var diskMetric = new DiskMetrics();
            diskMetric.Name = Unavailable(reason);
            diskMetric.TempC = UnavailableDouble(reason);
            result.Disks.Add(diskMetric);
        }

        private static void TryCollectGpuMetrics(Computer computer, HardwareSensorsResult result)
        {
            try
            {
                IHardware? gpu = null;
                foreach (var hw in computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.GpuAmd ||
                        hw.HardwareType == HardwareType.GpuNvidia ||
                        hw.HardwareType == HardwareType.GpuIntel)
                    {
                        gpu = hw;
                        break;
                    }
                }

                if (gpu == null)
                {
                    SetGpuUnavailable(result, "GPU introuvable");
                    return;
                }

                gpu.Update();
                UpdateSubHardware(gpu);

                var sensors = GetAllSensors(gpu).ToList();

                result.Gpu.Name = Available(gpu.Name);

                // VRAM Total: prioritize accurate sensors
                var vramTotal = FindSensorValue(sensors, "GPU Memory Total", "Memory Total", "VRAM Total");
                if (vramTotal.HasValue)
                    result.Gpu.VramTotalMB = Available(vramTotal.Value);
                else
                    result.Gpu.VramTotalMB = UnavailableDouble("VRAM totale indisponible");

                // FIX #6: VRAM Used - UNIQUEMENT "D3D Dedicated Memory Used" (correspond au Gestionnaire des tâches)
                // Les fallbacks "Memory Used", "GPU Memory Used" donnent des valeurs incorrectes (mémoire allouée/committed)
                // qui peuvent afficher 10-11GB au lieu de ~3GB réellement utilisés
                var vramUsedSensorNames = new[] {
                    "D3D Dedicated Memory Used",  // Seul sensor valide - correspond au Gestionnaire des tâches
                    "Dedicated Memory Used"       // Alternative naming
                    // SUPPRIMÉ: "Memory Used", "VRAM Used", "GPU Memory Used" - valeurs incorrectes
                };
                
                double? vramUsed = null;
                string vramUsedSource = "N/A";
                
                // Recherche exacte du sensor (Equals au lieu de IndexOf pour éviter les faux positifs)
                foreach (var sensorName in vramUsedSensorNames)
                {
                    var matchingSensor = sensors.FirstOrDefault(s => 
                        s.Name.Equals(sensorName, StringComparison.OrdinalIgnoreCase) && 
                        s.Value.HasValue);
                    
                    if (matchingSensor != null)
                    {
                        vramUsed = matchingSensor.Value;
                        vramUsedSource = matchingSensor.Name;
                        break;
                    }
                }
                
                if (vramUsed.HasValue)
                {
                    result.Gpu.VramUsedMB = Available(vramUsed.Value);
                    result.Gpu.VramUsedSource = vramUsedSource;
                    App.LogMessage($"[VRAM] D3D Dedicated Memory Used trouvé: {vramUsed:F0} MB");
                }
                else
                {
                    // FIX #6: Retourner indisponible plutôt qu'un fallback incorrect
                    result.Gpu.VramUsedMB = UnavailableDouble("D3D Dedicated Memory Used non disponible");
                    result.Gpu.VramUsedSource = "N/A";
                    App.LogMessage("[VRAM] D3D Dedicated Memory Used non trouvé - valeur marquée indisponible");
                }
                PopulateDedicatedVramMetadata(result.Gpu);
                
                // Debug: Log all GPU memory sensors found for verification
                LogGpuMemorySensors(sensors, vramTotal, vramUsed);
                App.LogMessage($"[VRAM] Selected sensor: '{vramUsedSource}' = {vramUsed:F0} MB");

                // GPU Load: use only "GPU Core" / 3D engine to align with Task Manager "GPU 3D".
                // LHM can expose multiple engines (3D, Copy, Video Decode); we take a single sensor only.
                // If multiple were summed, the % would differ from Task Manager which shows 3D separately.
                var gpuLoad = FindSensorValueByType(sensors, SensorType.Load, "GPU Core", "D3D 3D", "Core");
                if (gpuLoad.HasValue)
                    result.Gpu.GpuLoadPercent = Available(Math.Clamp(gpuLoad.Value, 0.0, 100.0));
                else
                    result.Gpu.GpuLoadPercent = UnavailableDouble("Charge GPU indisponible");

                // === FIX: GPU Temperature - PRIORITIZED SELECTION ===
                // Task Manager typically shows "GPU Core" or "GPU Temperature" (edge temperature)
                // Hot Spot is usually 10-15°C higher and is NOT what Task Manager shows
                // Priorities:
                // 1. "GPU Core" - main die temperature (matches most monitoring tools)
                // 2. "GPU Temperature" - generic/edge temperature  
                // 3. "Core" - fallback
                // 4. "Hot Spot" - only if nothing else (labeled differently)
                
                // Log ALL GPU temperature sensors for debugging
                LogAllGpuTempSensors(sensors);
                
                double? gpuTemp = null;
                string gpuTempSource = "N/A";
                
                // Priority 1: "GPU Core" - matches HWiNFO/GPU-Z/Task Manager
                gpuTemp = FindSensorValueByTypeExact(sensors, SensorType.Temperature, "GPU Core");
                if (gpuTemp.HasValue)
                {
                    gpuTempSource = "GPU Core";
                }
                
                // Priority 2: "GPU Temperature" - some drivers report this
                if (!gpuTemp.HasValue)
                {
                    gpuTemp = FindSensorValueByTypeExact(sensors, SensorType.Temperature, "GPU Temperature");
                    if (gpuTemp.HasValue) gpuTempSource = "GPU Temperature";
                }
                
                // Priority 3: Any sensor containing "Core" but not "Hot"
                if (!gpuTemp.HasValue)
                {
                    gpuTemp = FindSensorValueByType(sensors, SensorType.Temperature, "Core");
                    var maybeHotSpot = sensors.FirstOrDefault(s => 
                        s.SensorType == SensorType.Temperature && 
                        s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                        !s.Name.Contains("Hot", StringComparison.OrdinalIgnoreCase));
                    if (maybeHotSpot?.Value.HasValue == true)
                    {
                        gpuTemp = maybeHotSpot.Value;
                        gpuTempSource = maybeHotSpot.Name;
                    }
                }
                
                // Priority 4: "GPU" generic
                if (!gpuTemp.HasValue)
                {
                    gpuTemp = FindSensorValueByTypeExact(sensors, SensorType.Temperature, "GPU");
                    if (gpuTemp.HasValue) gpuTempSource = "GPU";
                }
                
                // Priority 5 (last resort): "Hot Spot" - warn user this is typically higher
                if (!gpuTemp.HasValue)
                {
                    gpuTemp = FindSensorValueByType(sensors, SensorType.Temperature, "Hot Spot", "Hotspot");
                    if (gpuTemp.HasValue) gpuTempSource = "Hot Spot (note: typically 10-15°C higher than core)";
                }
                
                // Validation
                if (gpuTemp is double gpuTempValue && gpuTempValue > 0 && gpuTempValue < 115)
                {
                    result.Gpu.GpuTempC = Available(gpuTempValue);
                    result.Gpu.GpuTempSource = gpuTempSource;
                    App.LogMessage($"[Sensors→GPU] Temperature: {gpuTempValue:F1}°C from sensor '{gpuTempSource}'");
                }
                else
                {
                    result.Gpu.GpuTempC = UnavailableDouble("Temperature GPU indisponible");
                    result.Gpu.GpuTempSource = "N/A";
                }
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"GPU: {ex.Message}");
                SetGpuUnavailable(result, string.Format("Erreur GPU: {0}", ex.Message));
            }
        }

        private static void TryCollectCpuMetrics(Computer computer, HardwareSensorsResult result)
        {
            try
            {
                if (CpuTempDiagnosticMode)
                    LogCpuTempDiagnostic(computer, step: "before_cpu_lookup");

                IHardware? cpu = null;
                foreach (var hw in computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        cpu = hw;
                        break;
                    }
                }

                if (cpu == null)
                {
                    result.Cpu.CpuTempC = UnavailableDouble("CPU introuvable");
                    CpuTemperatureMetadataService.SetUnavailable(
                        result.Cpu,
                        CpuTemperatureMetadataService.ReasonNoSensors,
                        "CPU hardware not found");
                    CpuTemperatureMetadataService.PublishUiSnapshot(
                        null,
                        CpuTemperatureMetadataService.SourceNone,
                        CpuTemperatureMetadataService.ConfidenceNone,
                        CpuTemperatureMetadataService.ReasonNoSensors,
                        "CPU hardware not found");
                    if (CpuTempDiagnosticMode)
                        LogCpuTempDiagnostic(computer, step: "cpu_not_found", cpuHardware: null);
                    return;
                }

                var sensors = GetAllSensors(cpu).ToList();
                if (CpuTempDiagnosticMode)
                    LogCpuTempDiagnostic(computer, step: "sensors_collected", cpuHardware: cpu, sensors: sensors);

                var tempResult = CpuTemperatureCollector.CollectBestEffort(sensors, result.BlockedBySecurity);
                if (tempResult.Available)
                {
                    result.Cpu.CpuTempC = Available(tempResult.TemperatureC!.Value);
                    if (tempResult.Source.Equals(CpuTemperatureMetadataService.SourceLhm, StringComparison.OrdinalIgnoreCase))
                        CpuTemperatureMetadataService.SetAvailableFromLhm(result.Cpu, tempResult.SourceDetail);
                    else
                        CpuTemperatureMetadataService.SetAvailableFromAcpi(result.Cpu, tempResult.SourceDetail);

                    CpuTemperatureMetadataService.PublishUiSnapshot(
                        tempResult.TemperatureC,
                        tempResult.Source,
                        tempResult.Confidence,
                        null,
                        null);
                    App.LogMessage($"[Sensors->CPU] Temperature valide: {tempResult.TemperatureC.Value:F1}C (source: {tempResult.SourceDetail ?? tempResult.Source})");
                }
                else
                {
                    if (CpuTempDiagnosticMode)
                        LogCpuTempDiagnosticNoTempFound(computer, cpu, sensors);

                    var reasonCode = CpuTemperatureMetadataService.NormalizeReasonCode(tempResult.ReasonCode);
                    var reasonDetail = string.IsNullOrWhiteSpace(tempResult.ReasonDetail)
                        ? "cpu_temperature_unavailable"
                        : tempResult.ReasonDetail!;
                    result.Cpu.CpuTempC = UnavailableDouble(reasonDetail);
                    CpuTemperatureMetadataService.SetUnavailable(result.Cpu, reasonCode, tempResult.SourceDetail ?? tempResult.Source);
                    CpuTemperatureMetadataService.PublishUiSnapshot(
                        null,
                        tempResult.Source,
                        tempResult.Confidence,
                        reasonCode,
                        reasonDetail);
                    App.LogMessage($"[Sensors->CPU] Temperature indisponible: {reasonCode} ({reasonDetail})");
                }
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"CPU: {ex.Message}");
                result.Cpu.CpuTempC = UnavailableDouble(string.Format("Erreur CPU: {0}", ex.Message));
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
                App.LogMessage($"[Sensors->CPU] ERREUR: {ex.Message}");
            }
        }

        /// <summary>
        /// Choisit la meilleure temperature CPU : AMD (Tctl/Tdie ou Package), Intel (Package puis Core Max puis Core #0), sinon premiere non nulle.
        /// </summary>
        private static (double? TempC, string? Source) GetBestCpuTemp(List<ISensor> sensors)
        {
            var tempSensors = sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0).ToList();
            if (tempSensors.Count == 0)
                return (null, null);

            // AMD : Tctl / Tdie en priorité, puis Package
            var tctl = tempSensors.FirstOrDefault(s => s.Name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0);
            var tdie = tempSensors.FirstOrDefault(s => s.Name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0);
            var pkg = tempSensors.FirstOrDefault(s => s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0);
            if (tdie?.Value.HasValue == true) return (tdie.Value.Value, $"Tdie (AMD)");
            if (tctl?.Value.HasValue == true) return (tctl.Value.Value, "Tctl (AMD)");
            if (pkg?.Value.HasValue == true) return (pkg.Value.Value, "CPU Package");

            // Intel : Core Max puis Core #0 (Package déjà traité ci-dessus)
            var coreMax = tempSensors.Where(s => s.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0).OrderByDescending(s => s.Value ?? 0).FirstOrDefault();
            if (coreMax?.Value.HasValue == true) return (coreMax.Value.Value, $"Core ({coreMax.Name})");
            var core0 = tempSensors.FirstOrDefault(s => s.Name.IndexOf("Core #0", StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf("Core 0", StringComparison.OrdinalIgnoreCase) >= 0);
            if (core0?.Value.HasValue == true) return (core0.Value.Value, core0.Name ?? "Core #0");

            // CCD, Core (Tctl/Tdie), etc.
            var ccd = tempSensors.FirstOrDefault(s => s.Name.IndexOf("CCD", StringComparison.OrdinalIgnoreCase) >= 0);
            if (ccd?.Value.HasValue == true) return (ccd.Value.Value, ccd.Name ?? "CCD");

            // Première température non nulle
            var first = tempSensors.First();
            if (!first.Value.HasValue)
                return (null, null);

            return (first.Value.Value, $"Fallback ({first.Name})");
        }

        private static void LogCpuTempDiagnostic(Computer computer, string step, IHardware? cpuHardware = default, List<ISensor>? sensors = default)
        {
            try
            {
                var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCDiagnosticPro_CPU_Temp_Diagnostic.log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== LHM CPU Temp Diagnostic - {DateTime.Now:yyyy-MM-dd HH:mm:ss} === step={step}");
                sb.AppendLine();

                sb.AppendLine("--- Tous les hardware détectés (Name, HardwareType) ---");
                foreach (var hw in computer.Hardware)
                {
                    sb.AppendLine($"  [{hw.HardwareType}] {hw.Name}");
                    foreach (var sub in hw.SubHardware)
                        sb.AppendLine($"    Sub: [{sub.HardwareType}] {sub.Name}");
                }
                sb.AppendLine();

                if (cpuHardware != null)
                {
                    sb.AppendLine($"--- Sensors du CPU: {cpuHardware.Name} ---");
                    if (sensors != null)
                    {
                        var byType = sensors.GroupBy(s => s.SensorType).OrderBy(g => g.Key.ToString());
                        foreach (var grp in byType)
                        {
                            sb.AppendLine($"  SensorType: {grp.Key}");
                            foreach (var s in grp.OrderBy(s => s.Name))
                                sb.AppendLine($"    Name=\"{s.Name}\" Value={s.Value?.ToString() ?? "null"} Identifier={s.Identifier}");
                        }
                    }
                    else
                    {
                        var list = GetAllSensors(cpuHardware);
                        foreach (var s in list)
                            sb.AppendLine($"  [{s.SensorType}] \"{s.Name}\" = {s.Value?.ToString() ?? "null"} Id={s.Identifier}");
                    }
                }
                sb.AppendLine();
                System.IO.File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);

                App.LogMessage($"[LHM Diagnostic] {step} -> {logPath}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[LHM Diagnostic] Log error: {ex.Message}");
            }
        }

        private static void LogCpuTempDiagnosticNoTempFound(Computer computer, IHardware cpu, List<ISensor> sensors)
        {
            try
            {
                var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCDiagnosticPro_CPU_Temp_Diagnostic.log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("--- Aucune température CPU trouvée ---");
                sb.AppendLine("Explications possibles:");
                sb.AppendLine("  - Capteur non exposé par le fabricant (laptop OEM, certains BIOS).");
                sb.AppendLine("  - Droits insuffisants: exécuter l'application en tant qu'administrateur.");
                sb.AppendLine("  - Windows Defender / antivirus bloque le pilote WinRing0: ajouter une exclusion pour le dossier de l'application.");
                sb.AppendLine("  - LibreHardwareMonitor nécessite parfois un redémarrage après première installation.");
                sb.AppendLine("Prochaines actions:");
                sb.AppendLine("  1) Relancer l'app en tant qu'administrateur (clic droit -> Exécuter en tant qu'administrateur).");
                sb.AppendLine("  2) Vérifier les exclusions Defender pour le dossier de l'application.");
                sb.AppendLine("  3) Si aucun sensor Temperature n'apparaît ci-dessus, le matériel/BIOS n'expose pas la température via LHM.");
                var tempTypes = sensors?.Where(s => s.SensorType == SensorType.Temperature).ToList() ?? new List<ISensor>();
                var allTypes = sensors?.Select(s => s.SensorType).Distinct().ToList() ?? new List<SensorType>();
                sb.AppendLine($"  SensorType Temperature count: {tempTypes.Count}. Tous les SensorType présents: {string.Join(", ", allTypes)}");
                sb.AppendLine();
                System.IO.File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
                App.LogMessage($"[LHM Diagnostic] Aucune température -> explication et actions écrites dans " + logPath);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[LHM Diagnostic] NoTemp log error: {ex.Message}");
            }
        }

        private static void TryCollectDiskMetrics(Computer computer, HardwareSensorsResult result)
        {
            try
            {
                result.Disks.Clear();

                var disks = new List<IHardware>();
                foreach (var hw in computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Storage)
                    {
                        disks.Add(hw);
                    }
                }

                if (disks.Count == 0)
                {
                    var diskMetric = new DiskMetrics();
                    diskMetric.Name = Unavailable("Aucun disque detecte");
                    diskMetric.TempC = UnavailableDouble("Temperature disque indisponible");
                    result.Disks.Add(diskMetric);
                    return;
                }

                foreach (var disk in disks)
                {
                    disk.Update();
                    UpdateSubHardware(disk);

                    var sensors = GetAllSensors(disk).ToList();
                    var temp = FindSensorValueByType(sensors, SensorType.Temperature, "Temperature", "Temp");

                    var diskMetric = new DiskMetrics();
                    diskMetric.Name = Available(disk.Name);
                    
                    if (temp.HasValue)
                        diskMetric.TempC = Available(temp.Value);
                    else
                        diskMetric.TempC = UnavailableDouble("Temperature disque indisponible");
                    
                    result.Disks.Add(diskMetric);
                }
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"Disks: {ex.Message}");
                result.Disks.Clear();
                var diskMetric = new DiskMetrics();
                diskMetric.Name = Unavailable(string.Format("Erreur disques: {0}", ex.Message));
                diskMetric.TempC = UnavailableDouble(string.Format("Erreur disques: {0}", ex.Message));
                result.Disks.Add(diskMetric);
            }
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
                gpu.VramDedicatedConfidence =
                    gpu.VramDedicatedSource.Contains("D3D", StringComparison.OrdinalIgnoreCase) ||
                    gpu.VramDedicatedSource.Contains("Dedicated", StringComparison.OrdinalIgnoreCase)
                        ? "high"
                        : "medium";
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

        /// <summary>
        /// Récupère tous les sensors du hardware et de tous les SubHardware (récursif).
        /// Ne pas se limiter à hw.Sensors : les températures CPU sont souvent dans SubHardware.
        /// </summary>
        private static List<ISensor> GetAllSensors(IHardware hardware)
        {
            var allSensors = new List<ISensor>();
            CollectSensorsRecursive(hardware, allSensors);
            return allSensors;
        }

        private static void CollectSensorsRecursive(IHardware hardware, List<ISensor> list)
        {
            if (hardware == null) return;
            foreach (var sensor in hardware.Sensors)
                list.Add(sensor);
            foreach (var sub in hardware.SubHardware)
                CollectSensorsRecursive(sub, list);
        }

        private static void UpdateSubHardware(IHardware hardware)
        {
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Update();
            }
        }

        /// <summary>
        /// Update récursif : hardware + tous les SubHardware (nécessaire pour que LHM expose les sensors CPU).
        /// </summary>
        private static void UpdateHardwareRecursive(IHardware hardware)
        {
            if (hardware == null) return;
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                UpdateHardwareRecursive(sub);
        }

        private static double? FindSensorValue(List<ISensor> sensors, params string[] nameContains)
        {
            foreach (var sensor in sensors)
            {
                if (sensor.Value.HasValue)
                {
                    foreach (var token in nameContains)
                    {
                        if (sensor.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return sensor.Value.Value;
                        }
                    }
                }
            }
            return null;
        }

        private static double? FindSensorValueByType(List<ISensor> sensors, SensorType sensorType, params string[] nameContains)
        {
            foreach (var sensor in sensors)
            {
                if (sensor.SensorType == sensorType && sensor.Value.HasValue)
                {
                    foreach (var token in nameContains)
                    {
                        if (sensor.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return sensor.Value.Value;
                        }
                    }
                }
            }
            return null;
        }
        
        /// <summary>
        /// Find sensor by EXACT name match (not substring).
        /// Used for precise GPU temperature selection.
        /// </summary>
        private static double? FindSensorValueByTypeExact(List<ISensor> sensors, SensorType sensorType, string exactName)
        {
            foreach (var sensor in sensors)
            {
                if (sensor.SensorType == sensorType && sensor.Value.HasValue)
                {
                    if (string.Equals(sensor.Name, exactName, StringComparison.OrdinalIgnoreCase))
                    {
                        return sensor.Value.Value;
                    }
                }
            }
            return null;
        }
        
        /// <summary>
        /// Log ALL GPU temperature sensors for debugging discrepancies.
        /// Helps diagnose Task Manager vs app temperature differences.
        /// </summary>
        private static void LogAllGpuTempSensors(List<ISensor> sensors)
        {
            try
            {
                var tempSensors = sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
                
                if (tempSensors.Count == 0)
                {
                    App.LogMessage("[GPU Temp Debug] No temperature sensors found");
                    return;
                }
                
                var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCDiagnosticPro_GPU_Temp_Debug.log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== GPU Temperature Sensors - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                sb.AppendLine($"Total temperature sensors: {tempSensors.Count}");
                sb.AppendLine();
                
                foreach (var sensor in tempSensors.OrderBy(s => s.Name))
                {
                    var valueStr = sensor.Value.HasValue ? $"{sensor.Value.Value:F1}°C" : "null";
                    sb.AppendLine($"  [{sensor.Name}] = {valueStr}");
                }
                
                sb.AppendLine();
                sb.AppendLine("Priority order for selection:");
                sb.AppendLine("  1. 'GPU Core' (matches Task Manager)");
                sb.AppendLine("  2. 'GPU Temperature'");
                sb.AppendLine("  3. Any 'Core' (not Hot Spot)");
                sb.AppendLine("  4. 'GPU' generic");
                sb.AppendLine("  5. 'Hot Spot' (typically 10-15°C higher)");
                sb.AppendLine();
                
                System.IO.File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
                App.LogMessage($"[GPU Temp Debug] Found {tempSensors.Count} sensors. See {logPath} for details.");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GPU Temp Debug] Logging error: {ex.Message}");
            }
        }

        private static MetricValue<string> Available(string value)
        {
            var m = new MetricValue<string>();
            m.Available = true;
            m.Value = value;
            return m;
        }

        private static MetricValue<double> Available(double value)
        {
            var m = new MetricValue<double>();
            m.Available = true;
            m.Value = value;
            return m;
        }

        private static MetricValue<string> Unavailable(string reason)
        {
            var m = new MetricValue<string>();
            m.Available = false;
            m.Reason = reason;
            return m;
        }

        private static MetricValue<double> UnavailableDouble(string reason)
        {
            var m = new MetricValue<double>();
            m.Available = false;
            m.Reason = reason;
            return m;
        }
    }
}



