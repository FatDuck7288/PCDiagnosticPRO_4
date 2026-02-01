using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Extracteur complet de données pour l'UI des résultats diagnostiques.
    /// Sources: PowerShell (inventaire), C# sensors (temps réel), Diagnostics actifs (tests/scoring).
    /// Objectif: "Rayon X" clair et lisible pour un junior.
    /// </summary>
    public static class ComprehensiveEvidenceExtractor
    {
        public static bool DebugPathsEnabled { get; set; } =
            Environment.GetEnvironmentVariable("PCDIAG_DEBUG_PATHS") == "1";

        /// <summary>
        /// Extrait toutes les données pertinentes pour un domaine de santé.
        /// Combine données PS, capteurs C#, et diagnostics actifs.
        /// </summary>
        public static Dictionary<string, string> Extract(
            HealthDomain domain,
            JsonElement root,
            HardwareSensorsResult? sensors = null)
        {
            return domain switch
            {
                HealthDomain.OS => ExtractOS(root, sensors),
                HealthDomain.CPU => ExtractCPU(root, sensors),
                HealthDomain.GPU => ExtractGPU(root, sensors),
                HealthDomain.RAM => ExtractRAM(root, sensors),
                HealthDomain.Storage => ExtractStorage(root, sensors),
                HealthDomain.Network => ExtractNetwork(root),
                HealthDomain.SystemStability => ExtractSystemStability(root),
                HealthDomain.Drivers => ExtractDrivers(root),
                HealthDomain.Applications => ExtractApplications(root),
                HealthDomain.Performance => ExtractPerformance(root, sensors),
                HealthDomain.Security => ExtractSecurity(root),
                HealthDomain.Power => ExtractPower(root, sensors),
                _ => new Dictionary<string, string>()
            };
        }

        #region OS - Système d'exploitation

        private static Dictionary<string, string> ExtractOS(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();

            AddIfNotEmpty(ev, "Version Windows", GetSnapshotMetricString(root, "os", "caption"),
                "diagnostic_snapshot.metrics.os.caption.value");
            AddIfNotEmpty(ev, "Build", GetSnapshotMetricString(root, "os", "buildNumber"),
                "diagnostic_snapshot.metrics.os.buildNumber.value");
            AddIfNotEmpty(ev, "Architecture", GetSnapshotMetricString(root, "os", "architecture"),
                "diagnostic_snapshot.metrics.os.architecture.value");
            AddIfNotEmpty(ev, "Uptime", GetSnapshotMetricString(root, "os", "uptime"),
                "diagnostic_snapshot.metrics.os.uptime.value");
            
            // === PS: sections.OS ===
            var osData = GetSectionData(root, "OS");
            if (osData.HasValue)
            {
                AddIfNotEmpty(ev, "Version Windows", GetString(osData, "caption"), "scan_powershell.sections.OS.data.caption");
                AddIfNotEmpty(ev, "Build", GetString(osData, "buildNumber"), "scan_powershell.sections.OS.data.buildNumber");
                AddIfNotEmpty(ev, "Architecture", GetString(osData, "architecture"), "scan_powershell.sections.OS.data.architecture");
                
                // Uptime
                var lastBoot = GetString(osData, "lastBootUpTime");
                if (!string.IsNullOrEmpty(lastBoot) && DateTime.TryParse(lastBoot, out var bootDt))
                {
                    var uptime = DateTime.Now - bootDt;
                    AddIfNotEmpty(ev, "Uptime", uptime.TotalDays >= 1 
                        ? $"{(int)uptime.TotalDays}j {uptime.Hours}h {uptime.Minutes}min"
                        : $"{uptime.Hours}h {uptime.Minutes}min", "scan_powershell.sections.OS.data.lastBootUpTime");
                }
            }

            // === PS: sections.MachineIdentity ===
            var machineId = GetSectionData(root, "MachineIdentity");
            if (machineId.HasValue)
            {
                AddIfNotEmpty(ev, "Version Windows", GetString(machineId, "osCaption"), "scan_powershell.sections.MachineIdentity.data.osCaption");
                AddIfNotEmpty(ev, "Build", GetString(machineId, "osBuild"), "scan_powershell.sections.MachineIdentity.data.osBuild");
                var uptimeDays = GetInt(machineId, "uptimeDays");
                var uptimeHours = GetInt(machineId, "uptimeHours");
                if (uptimeDays.HasValue || uptimeHours.HasValue)
                {
                    var uptime = $"{uptimeDays.GetValueOrDefault()}j {uptimeHours.GetValueOrDefault()}h";
                    AddIfNotEmpty(ev, "Uptime", uptime, "scan_powershell.sections.MachineIdentity.data.uptimeDays");
                }
            }

            // === PS: sections.Security - Secure Boot, BitLocker, Antivirus ===
            var secData = GetSectionData(root, "Security");
            if (secData.HasValue)
            {
                var secureBoot = GetBool(secData, "secureBootEnabled");
                if (secureBoot.HasValue)
                {
                    AddIfNotEmpty(ev, "Secure Boot", secureBoot.Value ? "✅ Activé" : "❌ Désactivé",
                        "scan_powershell.sections.Security.data.secureBootEnabled");
                }
                
                var bitlocker = GetBool(secData, "bitlockerEnabled");
                if (!bitlocker.HasValue) bitlocker = GetBool(secData, "bitLocker");
                if (bitlocker.HasValue)
                {
                    AddIfNotEmpty(ev, "BitLocker", bitlocker.Value ? "✅ Actif" : "❌ Inactif",
                        "scan_powershell.sections.Security.data.bitlockerEnabled");
                }
                
                // Antivirus
                var avName = GetString(secData, "antivirusName") ?? GetString(secData, "avName");
                var avStatus = GetString(secData, "antivirusStatus") ?? GetString(secData, "avStatus");
                if (!string.IsNullOrEmpty(avName))
                {
                    AddIfNotEmpty(ev, "Antivirus", string.IsNullOrEmpty(avStatus) ? avName : $"{avName} ({avStatus})",
                        "scan_powershell.sections.Security.data.antivirusName");
                }
                else if (secData.Value.TryGetProperty("antivirusProducts", out var avProducts))
                {
                    var avLabel = avProducts.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", avProducts.EnumerateArray().Select(p => p.GetString()).Where(p => !string.IsNullOrEmpty(p)))
                        : avProducts.ToString();
                    AddIfNotEmpty(ev, "Antivirus", avLabel, "scan_powershell.sections.Security.data.antivirusProducts");
                }
            }

            if (machineId.HasValue)
            {
                var secureBoot = GetBool(machineId, "secureBoot");
                if (secureBoot.HasValue)
                    AddIfNotEmpty(ev, "Secure Boot", secureBoot.Value ? "✅ Activé" : "❌ Désactivé", "scan_powershell.sections.MachineIdentity.data.secureBoot");
            }

            // === PS: sections.Storage - Espace C: ===
            var storageData = GetSectionData(root, "Storage");
            if (storageData.HasValue && storageData.Value.TryGetProperty("volumes", out var volumes) && 
                volumes.ValueKind == JsonValueKind.Array)
            {
                foreach (var vol in volumes.EnumerateArray())
                {
                    var letter = GetString(vol, "driveLetter")?.ToUpper();
                    if (letter == "C")
                    {
                        var freeGB = GetDouble(vol, "freeSpaceGB");
                        var sizeGB = GetDouble(vol, "sizeGB");
                        if (freeGB.HasValue && sizeGB.HasValue && sizeGB > 0)
                        {
                            var pct = (freeGB.Value / sizeGB.Value) * 100;
                            var status = pct < 10 ? " ⚠️" : pct < 20 ? " ⚡" : "";
                            AddIfNotEmpty(ev, "Espace C:", $"{freeGB.Value:F1} GB libre ({pct:F0}%){status}",
                                "scan_powershell.sections.Storage.data.volumes[*]");
                        }
                        break;
                    }
                }
            }

            // === Diagnostic Signals: WHEA, BSOD, Kernel-Power ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var whea7d = GetSignalValue(signals.Value, new[] { "whea", "whea_errors" }, "Last7dCount", "count");
                if (!string.IsNullOrEmpty(whea7d))
                    AddIfNotEmpty(ev, "Erreurs WHEA", whea7d == "0" ? "✅ Aucune" : $"⚠️ {whea7d}",
                        "diagnostic_signals.whea.value.Last7dCount");
            }

            // === PS: WindowsUpdate ===
            var updateData = GetSectionData(root, "WindowsUpdate");
            if (updateData.HasValue)
            {
                var pending = GetInt(updateData, "pendingCount") ?? GetInt(updateData, "PendingCount");
                if (pending.HasValue)
                    AddIfNotEmpty(ev, "Updates en attente", pending.Value > 0 ? $"⚠️ {pending.Value}" : "✅ À jour",
                        "scan_powershell.sections.WindowsUpdate.data.pendingCount");
            }

            // === C# Updates ===
            var csharpUpdates = GetNestedElement(root, "updates_csharp");
            if (csharpUpdates.HasValue)
            {
                var pending = GetInt(csharpUpdates, "pendingCount");
                if (pending.HasValue && !ev.ContainsKey("Updates en attente"))
                    AddIfNotEmpty(ev, "Updates en attente", pending.Value > 0 ? $"⚠️ {pending.Value}" : "✅ À jour",
                        "updates_csharp.pendingCount");
            }

            var snapshotPending = GetSnapshotMetricString(root, "updates", "pendingCount");
            if (!string.IsNullOrEmpty(snapshotPending))
                AddIfNotEmpty(ev, "Updates en attente", int.TryParse(snapshotPending, out var p) && p > 0 ? $"⚠️ {p}" : "✅ À jour",
                    "diagnostic_snapshot.metrics.updates.pendingCount.value");

            return ev;
        }

        #endregion

        #region CPU - Processeur

        private static Dictionary<string, string> ExtractCPU(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            
            // === PS: sections.CPU ===
            var cpuData = GetSectionData(root, "CPU");
            if (cpuData.HasValue)
            {
                var firstCpu = GetFirstElement(cpuData, "cpus", "cpuList");
                if (firstCpu.HasValue)
                {
                    AddIfNotEmpty(ev, "Modèle", GetString(firstCpu, "name")?.Trim(), "scan_powershell.sections.CPU.data.cpus.name");
                    
                    var cores = GetInt(firstCpu, "cores");
                    var threads = GetInt(firstCpu, "threads");
                    if (cores.HasValue && threads.HasValue)
                        ev["Cœurs / Threads"] = FormatValue($"{cores.Value} / {threads.Value}", "scan_powershell.sections.CPU.data.cpus.cores");
                    else if (cores.HasValue)
                        ev["Cœurs"] = FormatValue(cores.Value.ToString(), "scan_powershell.sections.CPU.data.cpus.cores");
                    
                    var maxClock = GetDouble(firstCpu, "maxClockSpeed");
                    if (maxClock.HasValue && maxClock > 0)
                        ev["Fréquence max"] = FormatValue($"{maxClock.Value:F0} MHz", "scan_powershell.sections.CPU.data.cpus.maxClockSpeed");
                    
                    var load = GetDouble(firstCpu, "currentLoad") ?? GetDouble(firstCpu, "load");
                    if (load.HasValue)
                        ev["Charge (PS)"] = FormatValue($"{load.Value:F0} %", "scan_powershell.sections.CPU.data.cpus.currentLoad");
                }
                
                var cpuCount = GetInt(cpuData, "cpuCount");
                if (cpuCount.HasValue && cpuCount > 1)
                    ev["Nombre de CPU"] = FormatValue(cpuCount.Value.ToString(), "scan_powershell.sections.CPU.data.cpuCount");
            }

            // === C# Sensors: Temperature ===
            if (sensors?.Cpu?.CpuTempC?.Available == true)
            {
                var temp = sensors.Cpu.CpuTempC.Value;
                var status = temp > 85 ? " 🔥" : temp > 70 ? " ⚠️" : "";
                ev["Température"] = FormatValue($"{temp:F0}°C{status}", "sensors_csharp.cpu.cpuTempC.value");
            }

            // === Diagnostic Signals: Throttling ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var throttle = GetSignalResult(signals.Value, "cpuThrottle", "cpu_throttle");
                if (throttle.HasValue)
                {
                    var detected = GetBool(GetNestedElement(throttle.Value, "value"), "ThrottleSuspected") ??
                        GetBool(throttle, "detected") ?? false;
                    ev["Throttling"] = FormatValue(detected ? "⚠️ Oui" : "✅ Non", "diagnostic_signals.cpuThrottle.value.ThrottleSuspected");
                    if (detected)
                    {
                        var reason = GetString(GetNestedElement(throttle.Value, "value"), "PerfPercentAvg");
                        if (!string.IsNullOrEmpty(reason))
                            ev["Raison throttle"] = FormatValue($"Perf={reason}%", "diagnostic_signals.cpuThrottle.value.PerfPercentAvg");
                    }
                }
            }

            return ev;
        }

        #endregion

        #region GPU - Carte graphique

        private static Dictionary<string, string> ExtractGPU(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            
            // === PS: sections.GPU ===
            var gpuData = GetSectionData(root, "GPU");
            if (gpuData.HasValue)
            {
                var firstGpu = GetFirstElement(gpuData, "gpuList", "gpus");
                if (firstGpu.HasValue)
                {
                    AddIfNotEmpty(ev, "GPU", GetString(firstGpu, "name")?.Trim(), "scan_powershell.sections.GPU.data.gpuList.name");
                    AddIfNotEmpty(ev, "Fabricant", GetString(firstGpu, "vendor"), "scan_powershell.sections.GPU.data.gpuList.vendor");
                    AddIfNotEmpty(ev, "Résolution", GetString(firstGpu, "resolution"), "scan_powershell.sections.GPU.data.gpuList.resolution");
                    
                    var driverVer = GetString(firstGpu, "driverVersion");
                    if (!string.IsNullOrEmpty(driverVer))
                        ev["Version pilote"] = FormatValue(driverVer, "scan_powershell.sections.GPU.data.gpuList.driverVersion");
                    
                    // Date pilote (nested or direct)
                    string? driverDate = null;
                    if (firstGpu.Value.TryGetProperty("driverDate", out var dd))
                    {
                        if (dd.ValueKind == JsonValueKind.Object && dd.TryGetProperty("DateTime", out var ddt))
                            driverDate = ddt.GetString();
                        else if (dd.ValueKind == JsonValueKind.String)
                            driverDate = dd.GetString();
                    }
                    AddIfNotEmpty(ev, "Date pilote", driverDate, "scan_powershell.sections.GPU.data.gpuList.driverDate.DateTime");
                    
                    // VRAM from PS (often null with note)
                    var vramMB = GetDouble(firstGpu, "vramTotalMB");
                    if (vramMB.HasValue && vramMB > 0)
                    {
                        ev["VRAM (PS)"] = FormatValue(vramMB >= 1024 ? $"{vramMB / 1024:F1} GB" : $"{vramMB:F0} MB",
                            "scan_powershell.sections.GPU.data.gpuList.vramTotalMB");
                    }
                    else
                    {
                        var note = GetString(firstGpu, "vramNote");
                        if (!string.IsNullOrEmpty(note))
                            ev["VRAM (PS)"] = FormatValue(note, "scan_powershell.sections.GPU.data.gpuList.vramNote");
                    }
                }
                
                var gpuCount = GetInt(gpuData, "gpuCount");
                if (gpuCount.HasValue && gpuCount > 1)
                    ev["Nombre de GPU"] = FormatValue(gpuCount.Value.ToString(), "scan_powershell.sections.GPU.data.gpuCount");
            }

            // === C# Sensors ===
            if (sensors?.Gpu != null)
            {
                if (sensors.Gpu.GpuTempC.Available)
                {
                    var temp = sensors.Gpu.GpuTempC.Value;
                    var status = temp > 85 ? " 🔥" : temp > 75 ? " ⚠️" : "";
                    ev["Température GPU"] = FormatValue($"{temp:F0}°C{status}", "sensors_csharp.gpu.gpuTempC.value");
                }
                
                if (sensors.Gpu.GpuLoadPercent.Available)
                    ev["Charge GPU"] = FormatValue($"{sensors.Gpu.GpuLoadPercent.Value:F0} %",
                        "sensors_csharp.gpu.gpuLoadPercent.value");
                
                if (sensors.Gpu.VramTotalMB.Available && sensors.Gpu.VramUsedMB.Available)
                {
                    var total = sensors.Gpu.VramTotalMB.Value;
                    var used = sensors.Gpu.VramUsedMB.Value;
                    var pct = total > 0 ? (used / total * 100) : 0;
                    ev["VRAM utilisée"] = FormatValue($"{used:F0} MB / {total:F0} MB ({pct:F0}%)",
                        "sensors_csharp.gpu.vramUsedMB.value");
                }
            }

            // === Diagnostic Signals: TDR ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var tdr7d = GetSignalValue(signals.Value, new[] { "gpuRootCause", "tdr_video" }, "TdrCount7d", "count");
                if (!string.IsNullOrEmpty(tdr7d))
                    ev["TDR (crashes GPU)"] = FormatValue(tdr7d == "0" ? "✅ Aucun" : $"⚠️ {tdr7d} détecté(s)",
                        "diagnostic_signals.gpuRootCause.value.TdrCount7d");
            }

            return ev;
        }

        #endregion

        #region RAM - Mémoire vive

        private static Dictionary<string, string> ExtractRAM(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();

            var snapTotal = GetSnapshotMetricDisplay(root, "memory", "totalGB");
            if (!string.IsNullOrEmpty(snapTotal))
                AddIfNotEmpty(ev, "RAM totale", snapTotal, "diagnostic_snapshot.metrics.memory.totalGB.value");
            var snapAvail = GetSnapshotMetricDisplay(root, "memory", "availableGB");
            if (!string.IsNullOrEmpty(snapAvail))
                AddIfNotEmpty(ev, "RAM disponible", snapAvail, "diagnostic_snapshot.metrics.memory.availableGB.value");
            var snapUsed = GetSnapshotMetricDisplay(root, "memory", "usedPercent");
            if (!string.IsNullOrEmpty(snapUsed))
                AddIfNotEmpty(ev, "RAM utilisée", snapUsed, "diagnostic_snapshot.metrics.memory.usedPercent.value");
            
            // === PS: sections.Memory ===
            var memData = GetSectionData(root, "Memory");
            if (memData.HasValue)
            {
                var totalGB = GetDouble(memData, "totalGB");
                var availGB = GetDouble(memData, "availableGB") ?? GetDouble(memData, "freeGB");
                
                if (totalGB.HasValue && totalGB > 0)
                {
                    ev["RAM totale"] = FormatValue($"{totalGB.Value:F1} GB", "scan_powershell.sections.Memory.data.totalGB");
                    
                    if (availGB.HasValue)
                    {
                        var usedGB = totalGB.Value - availGB.Value;
                        var pct = (usedGB / totalGB.Value) * 100;
                        var status = pct > 90 ? " ⚠️" : pct > 80 ? " ⚡" : "";
                        ev["RAM utilisée"] = FormatValue($"{usedGB:F1} GB ({pct:F0}%){status}",
                            "scan_powershell.sections.Memory.data.usedPercent");
                        ev["RAM disponible"] = FormatValue($"{availGB.Value:F1} GB", "scan_powershell.sections.Memory.data.freeGB");
                    }
                }
                
                // Virtual memory
                var virtualTotal = GetDouble(memData, "virtualTotalGB") ?? GetDouble(memData, "commitLimitGB");
                var virtualUsed = GetDouble(memData, "virtualUsedGB") ?? GetDouble(memData, "commitUsedGB");
                if (virtualTotal.HasValue && virtualUsed.HasValue)
                    ev["Mémoire virtuelle"] = FormatValue($"{virtualUsed.Value:F1} / {virtualTotal.Value:F1} GB",
                        "scan_powershell.sections.Memory.data.virtualTotalGB");
                
                // Page file
                var pageSize = GetDouble(memData, "pageFileSizeGB");
                var pageUsed = GetDouble(memData, "pageFileUsedGB");
                if (pageSize.HasValue && pageUsed.HasValue)
                    ev["Fichier d'échange"] = FormatValue($"{pageUsed.Value:F1} / {pageSize.Value:F1} GB",
                        "scan_powershell.sections.Memory.data.pageFileSizeGB");
                
                // Module count
                var modCount = GetInt(memData, "moduleCount");
                if (modCount.HasValue && modCount > 0)
                    ev["Barrettes"] = FormatValue(modCount.Value.ToString(), "scan_powershell.sections.Memory.data.moduleCount");
                
                if (!modCount.HasValue && memData.Value.TryGetProperty("modules", out var modules))
                {
                    var count = modules.ValueKind == JsonValueKind.Array ? modules.GetArrayLength() : 1;
                    if (count > 0)
                        ev["Barrettes"] = FormatValue(count.ToString(), "scan_powershell.sections.Memory.data.modules");
                }
            }

            // === Process Telemetry: Top RAM ===
            var topRam = GetTopProcesses(root, "memory", 5);
            if (topRam.Count > 0)
                ev["Top processus RAM"] = FormatValue(string.Join(", ", topRam), "process_telemetry.topMemory[*].name");

            return ev;
        }

        #endregion

        #region Storage - Stockage

        private static Dictionary<string, string> ExtractStorage(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            
            // === PS: sections.Storage ===
            var storageData = GetSectionData(root, "Storage");
            if (storageData.HasValue)
            {
                // Disks summary
                if (storageData.Value.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
                {
                    var diskList = disks.EnumerateArray().ToList();
                    ev["Disques physiques"] = FormatValue(diskList.Count.ToString(), "scan_powershell.sections.Storage.data.disks");
                    
                    // List each disk with type
                    int i = 1;
                    foreach (var disk in diskList.Take(4))
                    {
                        var model = GetString(disk, "model") ?? GetString(disk, "friendlyName") ?? $"Disque {i}";
                        var mediaType = GetString(disk, "mediaType") ?? "";
                        var sizeGB = GetDouble(disk, "sizeGB");
                        
                        var typeStr = mediaType.ToUpper() switch
                        {
                            "SSD" => "SSD",
                            "HDD" => "HDD",
                            "NVME" => "NVMe",
                            _ when model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) => "NVMe",
                            _ when model.Contains("SSD", StringComparison.OrdinalIgnoreCase) => "SSD",
                            _ => ""
                        };
                        
                        var info = !string.IsNullOrEmpty(typeStr) ? $"{typeStr}" : "";
                        if (sizeGB.HasValue) info += $" {sizeGB.Value:F0} GB";
                        
                        ev[$"Disque {i}"] = FormatValue($"{model.Trim()} ({info.Trim()})", "scan_powershell.sections.Storage.data.disks[*]");
                        i++;
                    }
                }
                else if (storageData.Value.TryGetProperty("physicalDisks", out var physicalDisks))
                {
                    var diskList = EnumerateElements(physicalDisks).ToList();
                    if (diskList.Count > 0)
                        ev["Disques physiques"] = FormatValue(diskList.Count.ToString(),
                            "scan_powershell.sections.Storage.data.physicalDisks");
                    
                    int i = 1;
                    foreach (var disk in diskList.Take(4))
                    {
                        var model = GetString(disk, "model") ?? GetString(disk, "friendlyName") ?? $"Disque {i}";
                        var mediaType = GetString(disk, "mediaType") ?? "";
                        var sizeGB = GetDouble(disk, "sizeGB");
                        
                        var typeStr = mediaType.ToUpper() switch
                        {
                            "SSD" => "SSD",
                            "HDD" => "HDD",
                            "NVME" => "NVMe",
                            _ when model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) => "NVMe",
                            _ when model.Contains("SSD", StringComparison.OrdinalIgnoreCase) => "SSD",
                            _ => ""
                        };
                        
                        var info = !string.IsNullOrEmpty(typeStr) ? $"{typeStr}" : "";
                        if (sizeGB.HasValue) info += $" {sizeGB.Value:F0} GB";
                        
                        ev[$"Disque {i}"] = FormatValue($"{model.Trim()} ({info.Trim()})",
                            "scan_powershell.sections.Storage.data.physicalDisks[*]");
                        i++;
                    }
                }
                
                // Volumes with alerts
                if (storageData.Value.TryGetProperty("volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Array)
                {
                    var volList = new List<string>();
                    foreach (var vol in volumes.EnumerateArray())
                    {
                        var letter = GetString(vol, "driveLetter") ?? "";
                        var freeGB = GetDouble(vol, "freeSpaceGB");
                        var sizeGB = GetDouble(vol, "sizeGB");
                        
                        if (!string.IsNullOrEmpty(letter) && freeGB.HasValue && sizeGB.HasValue && sizeGB > 0)
                        {
                            var pct = (freeGB.Value / sizeGB.Value) * 100;
                            var alert = pct < 10 ? "⚠️" : pct < 20 ? "⚡" : "✅";
                            volList.Add($"{letter}: {freeGB.Value:F0}GB libre ({pct:F0}%) {alert}");
                        }
                    }
                    if (volList.Count > 0)
                        ev["Partitions"] = FormatValue(string.Join(" | ", volList.Take(4)),
                            "scan_powershell.sections.Storage.data.volumes[*]");
                }
            }

            // === PS: SmartDetails ===
            var smartData = GetSectionData(root, "SmartDetails");
            if (smartData.HasValue)
            {
                var smartDisk = smartData.Value.TryGetProperty("disks", out var smartDisks)
                    ? EnumerateElements(smartDisks).FirstOrDefault()
                    : (JsonElement?)null;
                
                if (smartDisk.HasValue)
                {
                    var predictFailure = GetBool(smartDisk, "predictFailure");
                    var temp = GetDouble(smartDisk, "temperature");
                    var status = predictFailure == true ? "⚠️ Alerte" : "✅ OK";
                    ev["Santé SMART"] = FormatValue(status, "scan_powershell.sections.SmartDetails.data.disks.predictFailure");
                    if (temp.HasValue && temp > 0)
                        ev["Température disque (SMART)"] = FormatValue($"{temp.Value:F0}°C",
                            "scan_powershell.sections.SmartDetails.data.disks.temperature");
                }
            }

            // === C# Sensors: Disk temps ===
            if (sensors?.Disks?.Count > 0)
            {
                var temps = sensors.Disks
                    .Where(d => d.TempC.Available)
                    .Select(d => $"{d.Name.Value ?? "Disk"}: {d.TempC.Value:F0}°C")
                    .Take(3);
                var tempsStr = string.Join(", ", temps);
                if (!string.IsNullOrEmpty(tempsStr))
                    ev["Températures disques"] = FormatValue(tempsStr, "sensors_csharp.disks[*].tempC.value");
            }

            // === Process Telemetry: Top IO ===
            var topIO = GetTopProcesses(root, "io", 3);
            if (topIO.Count > 0)
                ev["Top processus IO"] = FormatValue(string.Join(", ", topIO), "process_telemetry.topIo[*].name");

            return ev;
        }

        #endregion

        #region Network - Réseau

        private static Dictionary<string, string> ExtractNetwork(JsonElement root)
        {
            var ev = new Dictionary<string, string>();

            AddIfNotEmpty(ev, "Passerelle", GetSnapshotMetricString(root, "network", "defaultGateway"),
                "diagnostic_snapshot.metrics.network.defaultGateway.value");
            AddIfNotEmpty(ev, "DNS", GetSnapshotMetricString(root, "network", "dnsServers"),
                "diagnostic_snapshot.metrics.network.dnsServers.value");
            
            // === PS: sections.Network ===
            var netData = GetSectionData(root, "Network");
            if (netData.HasValue && netData.Value.TryGetProperty("adapters", out var adapters) && 
                adapters.ValueKind == JsonValueKind.Array)
            {
                var first = adapters.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    AddIfNotEmpty(ev, "Adaptateur", GetString(first, "name"), "scan_powershell.sections.Network.data.adapters[0].name");
                    AddIfNotEmpty(ev, "Vitesse lien", GetString(first, "speed") ?? 
                        (GetDouble(first, "speedMbps").HasValue ? $"{GetDouble(first, "speedMbps"):F0} Mbps" : null),
                        "scan_powershell.sections.Network.data.adapters[0].speed");
                    AddIfNotEmpty(ev, "Adresse IP", GetString(first, "ipv4"), "scan_powershell.sections.Network.data.adapters[0].ipv4");
                    AddIfNotEmpty(ev, "Passerelle", ExtractFirstString(first, "gateway"),
                        "scan_powershell.sections.Network.data.adapters[0].gateway");
                    AddIfNotEmpty(ev, "DNS", ExtractFirstString(first, "dns"),
                        "scan_powershell.sections.Network.data.adapters[0].dns");
                    AddIfNotEmpty(ev, "MAC", GetString(first, "macAddress"), "scan_powershell.sections.Network.data.adapters[0].macAddress");
                    
                    // WiFi specific
                    var rssi = GetInt(first, "rssi") ?? GetInt(first, "signalStrength");
                    if (rssi.HasValue)
                    {
                        var quality = rssi.Value > -50 ? "Excellent" : rssi.Value > -60 ? "Bon" : rssi.Value > -70 ? "Moyen" : "Faible";
                        ev["WiFi Signal"] = FormatValue($"{rssi.Value} dBm ({quality})",
                            "scan_powershell.sections.Network.data.adapters[0].rssi");
                    }
                }
            }

            // === C#: network_diagnostics ===
            var netDiag = GetNestedElement(root, "network_diagnostics");
            if (netDiag.HasValue)
            {
                var latency = GetDouble(netDiag, "latencyMs") ?? GetDouble(netDiag, "pingMs");
                if (latency.HasValue)
                {
                    var status = latency > 100 ? " ⚠️" : latency > 50 ? " ⚡" : "";
                    ev["Latence (ping)"] = FormatValue($"{latency.Value:F0} ms{status}", "network_diagnostics.latencyMs");
                }
                
                var jitter = GetDouble(netDiag, "jitterMs");
                if (jitter.HasValue)
                    ev["Gigue"] = FormatValue($"{jitter.Value:F1} ms", "network_diagnostics.jitterMs");
                
                var loss = GetDouble(netDiag, "packetLossPercent");
                if (loss.HasValue)
                {
                    var status = loss > 1 ? " ⚠️" : "";
                    ev["Perte paquets"] = FormatValue($"{loss.Value:F1}%{status}", "network_diagnostics.packetLossPercent");
                }
                
                var download = GetDouble(netDiag, "downloadMbps");
                var upload = GetDouble(netDiag, "uploadMbps");
                if (download.HasValue && upload.HasValue)
                    ev["Débit FAI"] = FormatValue($"↓{download.Value:F1} / ↑{upload.Value:F1} Mbps",
                        "network_diagnostics.downloadMbps");
                else if (download.HasValue)
                    ev["Download"] = FormatValue($"{download.Value:F1} Mbps", "network_diagnostics.downloadMbps");
                
                var vpn = GetBool(netDiag, "vpnDetected");
                if (vpn.HasValue)
                    ev["VPN/Proxy"] = FormatValue(vpn.Value ? "⚠️ Détecté" : "Non", "network_diagnostics.vpnDetected");
            }

            return ev;
        }

        #endregion

        #region SystemStability - Stabilité système

        private static Dictionary<string, string> ExtractSystemStability(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            
            // === Diagnostic Signals ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var whea7d = GetSignalValue(signals.Value, new[] { "whea", "whea_errors" }, "Last7dCount", "count");
                if (!string.IsNullOrEmpty(whea7d))
                    ev["Erreurs WHEA"] = FormatValue(whea7d == "0" ? "✅ Aucune" : $"⚠️ {whea7d}",
                        "diagnostic_signals.whea.value.Last7dCount");
            }

            // === PS: ReliabilityHistory ===
            var reliData = GetSectionData(root, "ReliabilityHistory");
            if (reliData.HasValue)
            {
                var appCrashes = GetInt(reliData, "appCrashCount") ?? GetInt(reliData, "applicationCrashes") ?? GetInt(reliData, "appCrashes");
                if (appCrashes.HasValue)
                    ev["Crashes applicatifs"] = FormatValue(appCrashes == 0 ? "✅ Aucun" : $"⚠️ {appCrashes.Value}",
                        "scan_powershell.sections.ReliabilityHistory.data.appCrashes");
            }

            // === PS: Services ===
            var svcData = GetSectionData(root, "Services");
            if (svcData.HasValue)
            {
                var failedCount = GetInt(svcData, "failedCount") ?? GetInt(svcData, "stoppedCritical");
                if (failedCount.HasValue && failedCount > 0)
                    ev["Services en échec"] = FormatValue($"⚠️ {failedCount.Value}",
                        "scan_powershell.sections.Services.data.failedCount");
                
                if (svcData.Value.TryGetProperty("failedServices", out var failed) && failed.ValueKind == JsonValueKind.Array)
                {
                    var names = failed.EnumerateArray()
                        .Select(f => GetString(f, "name") ?? GetString(f, "displayName") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(3);
                    var namesStr = string.Join(", ", names);
                    if (!string.IsNullOrEmpty(namesStr))
                        ev["Services problèmes"] = FormatValue(namesStr, "scan_powershell.sections.Services.data.failedServices[*]");
                }
                
                var criticalServices = GetInt(svcData, "criticalServices");
                if (criticalServices.HasValue)
                    ev["Services critiques"] = FormatValue(criticalServices.Value.ToString(),
                        "scan_powershell.sections.Services.data.criticalServices");
            }

            // === PS: SystemIntegrity (SFC/DISM) ===
            var intData = GetSectionData(root, "SystemIntegrity");
            if (intData.HasValue)
            {
                var sfcStatus = GetString(intData, "sfcStatus") ?? GetString(intData, "sfc");
                if (!string.IsNullOrEmpty(sfcStatus))
                    ev["SFC"] = FormatValue(sfcStatus.ToLower().Contains("ok") || sfcStatus.ToLower().Contains("clean") 
                        ? "✅ OK" : $"⚠️ {sfcStatus}", "scan_powershell.sections.SystemIntegrity.data.sfcStatus");
                
                var dismStatus = GetString(intData, "dismStatus") ?? GetString(intData, "dism");
                if (!string.IsNullOrEmpty(dismStatus))
                    ev["DISM"] = FormatValue(dismStatus.ToLower().Contains("ok") || dismStatus.ToLower().Contains("healthy") 
                        ? "✅ OK" : $"⚠️ {dismStatus}", "scan_powershell.sections.SystemIntegrity.data.dismStatus");
            }

            // === PS: RestorePoints ===
            var rpData = GetSectionData(root, "RestorePoints");
            if (rpData.HasValue)
            {
                var rpCount = GetInt(rpData, "count") ?? GetInt(rpData, "restorePointCount");
                if (rpCount.HasValue)
                    ev["Points de restauration"] = FormatValue(rpCount.Value.ToString(),
                        "scan_powershell.sections.RestorePoints.data.count");
            }

            var eventData = GetSectionData(root, "EventLogs");
            if (eventData.HasValue)
            {
                var bsodCount = GetInt(eventData, "bsodCount");
                if (bsodCount.HasValue)
                    ev["BSOD récents"] = FormatValue(bsodCount == 0 ? "✅ Aucun" : $"⚠️ {bsodCount.Value}",
                        "scan_powershell.sections.EventLogs.data.bsodCount");
            }

            var minidump = GetSectionData(root, "MinidumpAnalysis");
            if (minidump.HasValue)
            {
                var mdCount = GetInt(minidump, "minidumpCount");
                if (mdCount.HasValue)
                    ev["Minidumps"] = FormatValue(mdCount.Value.ToString(),
                        "scan_powershell.sections.MinidumpAnalysis.data.minidumpCount");
            }

            return ev;
        }

        #endregion

        #region Drivers - Pilotes

        private static Dictionary<string, string> ExtractDrivers(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            
            // === C#: driver_inventory ===
            var driverInv = GetNestedElement(root, "driver_inventory");
            if (driverInv.HasValue)
            {
                var total = GetInt(driverInv, "totalCount");
                if (total.HasValue)
                    ev["Pilotes détectés"] = FormatValue(total.Value.ToString(), "driver_inventory.totalCount");
                
                var unsigned = GetInt(driverInv, "unsignedCount");
                if (unsigned.HasValue && unsigned > 0)
                    ev["Non signés"] = FormatValue($"⚠️ {unsigned.Value}", "driver_inventory.unsignedCount");
                
                var problems = GetInt(driverInv, "problemCount");
                if (problems.HasValue)
                    ev["Périph. en erreur"] = FormatValue(problems == 0 ? "✅ Aucun" : $"⚠️ {problems.Value}",
                        "driver_inventory.problemCount");
                
                var outdated = GetInt(driverInv, "outdatedCount");
                if (outdated.HasValue && outdated > 0)
                    ev["Pilotes obsolètes"] = FormatValue($"⚠️ {outdated.Value}", "driver_inventory.outdatedCount");
            }

            // === PS: DevicesDrivers ===
            var devData = GetSectionData(root, "DevicesDrivers");
            if (devData.HasValue)
            {
                var problemDevices = GetInt(devData, "problemDeviceCount") ?? GetInt(devData, "ProblemDeviceCount");
                if (problemDevices.HasValue && !ev.ContainsKey("Périph. en erreur"))
                    ev["Périph. en erreur"] = FormatValue(problemDevices == 0 ? "✅ Aucun" : $"⚠️ {problemDevices.Value}",
                        "scan_powershell.sections.DevicesDrivers.data.problemDeviceCount");
            }

            // === PS: Audio ===
            var audioData = GetSectionData(root, "Audio");
            if (audioData.HasValue)
            {
                var audioCount = GetInt(audioData, "deviceCount") ?? GetInt(audioData, "DeviceCount");
                if (audioCount.HasValue)
                    ev["Périph. audio"] = FormatValue(audioCount.Value.ToString(),
                        "scan_powershell.sections.Audio.data.deviceCount");
            }

            // === PS: Printers ===
            var printData = GetSectionData(root, "Printers");
            if (printData.HasValue)
            {
                var printerCount = GetInt(printData, "printerCount") ?? GetInt(printData, "PrinterCount");
                if (printerCount.HasValue)
                    ev["Imprimantes"] = FormatValue(printerCount.Value.ToString(),
                        "scan_powershell.sections.Printers.data.printerCount");
            }

            // === Key drivers list ===
            if (driverInv.HasValue && driverInv.Value.TryGetProperty("drivers", out var drivers) && 
                drivers.ValueKind == JsonValueKind.Array)
            {
                var critical = new[] { "DISPLAY", "NET", "MEDIA", "HDC", "SCSIADAPTER", "BLUETOOTH" };
                var criticalDrivers = drivers.EnumerateArray()
                    .Where(d => {
                        var cls = GetString(d, "deviceClass")?.ToUpper() ?? "";
                        return critical.Any(c => cls.Contains(c));
                    })
                    .Take(5)
                    .Select(d => $"{GetString(d, "deviceClass")}: {GetString(d, "deviceName")} v{GetString(d, "driverVersion")}")
                    .ToList();
                
                if (criticalDrivers.Count > 0)
                    ev["Pilotes critiques"] = FormatValue(string.Join(" | ", criticalDrivers.Take(3)),
                        "driver_inventory.drivers[*]");
            }

            return ev;
        }

        #endregion

        #region Applications

        private static Dictionary<string, string> ExtractApplications(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            
            // === PS: InstalledApplications ===
            var appData = GetSectionData(root, "InstalledApplications");
            if (appData.HasValue)
            {
                var appCount = GetInt(appData, "applicationCount") ?? GetInt(appData, "count") ?? GetInt(appData, "totalCount");
                if (appCount.HasValue)
                    ev["Apps installées"] = FormatValue(appCount.Value.ToString(),
                        "scan_powershell.sections.InstalledApplications.data.totalCount");
                
                // Recent installs
                if (appData.Value.TryGetProperty("recentInstalls", out var recent) && recent.ValueKind == JsonValueKind.Array)
                {
                    var recentList = recent.EnumerateArray()
                        .Select(a => GetString(a, "name") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(3);
                    var recentStr = string.Join(", ", recentList);
                    if (!string.IsNullOrEmpty(recentStr))
                        ev["Installations récentes"] = FormatValue(recentStr,
                            "scan_powershell.sections.InstalledApplications.data.recentInstalls[*]");
                }
            }

            // === PS: StartupPrograms ===
            var startupData = GetSectionData(root, "StartupPrograms");
            if (startupData.HasValue)
            {
                var startupCount = GetInt(startupData, "programCount") ?? GetInt(startupData, "count") ?? GetInt(startupData, "startupCount");
                if (startupCount.HasValue)
                    ev["Programmes démarrage"] = FormatValue(startupCount.Value.ToString(),
                        "scan_powershell.sections.StartupPrograms.data.startupCount");
                
                if (startupData.Value.TryGetProperty("startupItems", out var progs) && progs.ValueKind == JsonValueKind.Array)
                {
                    var heavyStartup = progs.EnumerateArray()
                        .Where(p => GetString(p, "impact")?.ToLower() == "high")
                        .Select(p => GetString(p, "name") ?? GetString(p, "command") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(3);
                    var heavyStr = string.Join(", ", heavyStartup);
                    if (!string.IsNullOrEmpty(heavyStr))
                        ev["Démarrage lourd"] = FormatValue($"⚠️ {heavyStr}",
                            "scan_powershell.sections.StartupPrograms.data.startupItems[*]");
                }
            }

            // === Process Telemetry: Top CPU apps ===
            var topCpu = GetTopProcesses(root, "cpu", 3);
            if (topCpu.Count > 0)
                ev["Top CPU"] = FormatValue(string.Join(", ", topCpu), "process_telemetry.topCpu[*].name");

            var topMem = GetTopProcesses(root, "memory", 3);
            if (topMem.Count > 0 && !ev.ContainsKey("Top RAM"))
                ev["Top RAM"] = FormatValue(string.Join(", ", topMem), "process_telemetry.topMemory[*].name");

            return ev;
        }

        #endregion

        #region Performance

        private static Dictionary<string, string> ExtractPerformance(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            
            // === Current state summary ===
            var cpuData = GetSectionData(root, "CPU");
            if (cpuData.HasValue)
            {
                var first = GetFirstElement(cpuData, "cpus", "cpuList");
                if (first.HasValue)
                {
                    var load = GetDouble(first, "currentLoad") ?? GetDouble(first, "load");
                    if (load.HasValue)
                    {
                        var status = load > 90 ? "🔥 Saturé" : load > 70 ? "⚠️ Élevé" : "✅ Normal";
                        ev["CPU"] = FormatValue($"{load.Value:F0}% {status}", "scan_powershell.sections.CPU.data.cpus.currentLoad");
                    }
                }
            }

            var memData = GetSectionData(root, "Memory");
            if (memData.HasValue)
            {
                var totalGB = GetDouble(memData, "totalGB");
                var availGB = GetDouble(memData, "availableGB") ?? GetDouble(memData, "freeGB");
                if (totalGB.HasValue && availGB.HasValue && totalGB > 0)
                {
                    var pct = ((totalGB.Value - availGB.Value) / totalGB.Value) * 100;
                    var status = pct > 90 ? "🔥 Saturée" : pct > 80 ? "⚠️ Élevée" : "✅ Normal";
                    ev["RAM"] = FormatValue($"{pct:F0}% {status}", "scan_powershell.sections.Memory.data.usedPercent");
                }
            }

            // === Bottlenecks from signals ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var bottlenecks = new List<string>();
                
                var cpuThrottle = GetSignalResult(signals.Value, "cpuThrottle", "cpu_throttle");
                if (cpuThrottle.HasValue &&
                    (GetBool(GetNestedElement(cpuThrottle.Value, "value"), "ThrottleSuspected") ?? GetBool(cpuThrottle, "detected") ?? false))
                    bottlenecks.Add("CPU throttle");
                
                var ramPressure = GetSignalResult(signals.Value, "memoryPressure", "ram_pressure");
                if (ramPressure.HasValue &&
                    (GetInt(GetNestedElement(ramPressure.Value, "value"), "HardFaultsAvg") ?? 0) > 0)
                    bottlenecks.Add("RAM pressure");
                
                var diskSat = GetSignalResult(signals.Value, "storageLatency", "disk_saturation");
                if (diskSat.HasValue &&
                    (GetBool(GetNestedElement(diskSat.Value, "value"), "SaturationDetected") ?? GetBool(diskSat, "detected") ?? false))
                    bottlenecks.Add("Disk saturation");
                
                if (bottlenecks.Count > 0)
                    ev["Bottlenecks"] = FormatValue($"⚠️ {string.Join(", ", bottlenecks)}",
                        "diagnostic_signals");
                else
                    ev["Bottlenecks"] = FormatValue("✅ Aucun détecté", "diagnostic_signals");
            }

            // === Temperatures summary ===
            if (sensors != null)
            {
                var temps = new List<string>();
                if (sensors.Cpu?.CpuTempC?.Available == true)
                {
                    var t = sensors.Cpu.CpuTempC.Value;
                    temps.Add($"CPU: {t:F0}°C" + (t > 80 ? "🔥" : ""));
                }
                if (sensors.Gpu?.GpuTempC?.Available == true)
                {
                    var t = sensors.Gpu.GpuTempC.Value;
                    temps.Add($"GPU: {t:F0}°C" + (t > 80 ? "🔥" : ""));
                }
                if (temps.Count > 0)
                    ev["Températures"] = FormatValue(string.Join(" | ", temps), "sensors_csharp");
            }

            // === Top processes ===
            var topCpu = GetTopProcesses(root, "cpu", 5);
            if (topCpu.Count > 0)
                ev["Top CPU"] = FormatValue(string.Join(", ", topCpu), "process_telemetry.topCpu[*].name");

            var topMem = GetTopProcesses(root, "memory", 5);
            if (topMem.Count > 0)
                ev["Top RAM"] = FormatValue(string.Join(", ", topMem), "process_telemetry.topMemory[*].name");

            var topIO = GetTopProcesses(root, "io", 3);
            if (topIO.Count > 0)
                ev["Top IO"] = FormatValue(string.Join(", ", topIO), "process_telemetry.topIo[*].name");

            return ev;
        }

        #endregion

        #region Security - Sécurité

        private static Dictionary<string, string> ExtractSecurity(JsonElement root)
        {
            var ev = new Dictionary<string, string>();

            var snapAv = GetSnapshotMetricString(root, "security", "antivirusName");
            if (!string.IsNullOrEmpty(snapAv))
                AddIfNotEmpty(ev, "Antivirus", snapAv, "diagnostic_snapshot.metrics.security.antivirusName.value");
            var snapSecureBoot = GetSnapshotMetricBool(root, "security", "secureBootEnabled");
            if (snapSecureBoot.HasValue)
                AddIfNotEmpty(ev, "Secure Boot", snapSecureBoot.Value ? "✅ Activé" : "⚠️ Désactivé",
                    "diagnostic_snapshot.metrics.security.secureBootEnabled.value");
            var snapUac = GetSnapshotMetricBool(root, "security", "uacEnabled");
            if (snapUac.HasValue)
                AddIfNotEmpty(ev, "UAC", snapUac.Value ? "✅ Activé" : "⚠️ Désactivé",
                    "diagnostic_snapshot.metrics.security.uacEnabled.value");
            var snapBitlocker = GetSnapshotMetricString(root, "security", "bitlockerStatus");
            if (!string.IsNullOrEmpty(snapBitlocker))
                AddIfNotEmpty(ev, "Chiffrement disque", snapBitlocker,
                    "diagnostic_snapshot.metrics.security.bitlockerStatus.value");
            
            // === PS: Security ===
            var secData = GetSectionData(root, "Security");
            if (secData.HasValue)
            {
                // Antivirus
                var avName = GetString(secData, "antivirusName") ?? GetString(secData, "avName");
                var avStatus = GetString(secData, "antivirusStatus") ?? GetString(secData, "avStatus");
                if (!string.IsNullOrEmpty(avName))
                {
                    var icon = avStatus?.ToLower() switch
                    {
                        "enabled" or "on" or "actif" => "✅",
                        "disabled" or "off" => "⚠️",
                        _ => ""
                    };
                    AddIfNotEmpty(ev, "Antivirus", $"{icon} {avName}", "scan_powershell.sections.Security.data.antivirusName");
                }
                else if (secData.Value.TryGetProperty("antivirusProducts", out var avProducts))
                {
                    var av = avProducts.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", avProducts.EnumerateArray().Select(p => p.GetString()).Where(p => !string.IsNullOrEmpty(p)))
                        : avProducts.ToString();
                    AddIfNotEmpty(ev, "Antivirus", av, "scan_powershell.sections.Security.data.antivirusProducts");
                }
                
                // Firewall
                var fwStatus = GetBool(secData, "firewallEnabled") ?? GetBool(secData, "firewall");
                if (fwStatus.HasValue)
                {
                    AddIfNotEmpty(ev, "Pare-feu", fwStatus.Value ? "✅ Activé" : "⚠️ Désactivé",
                        "scan_powershell.sections.Security.data.firewallEnabled");
                }
                else if (secData.Value.TryGetProperty("firewall", out var firewall))
                {
                    var enabled = firewall.ValueKind == JsonValueKind.Object &&
                                  firewall.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.Object &&
                                                                      p.Value.TryGetProperty("value__", out var v) &&
                                                                      v.GetInt32() == 1);
                    AddIfNotEmpty(ev, "Pare-feu", enabled ? "✅ Activé" : "⚠️ Désactivé",
                        "scan_powershell.sections.Security.data.firewall");
                }
                
                // Secure Boot
                var secureBoot = GetBool(secData, "secureBootEnabled");
                if (secureBoot.HasValue)
                    AddIfNotEmpty(ev, "Secure Boot", secureBoot.Value ? "✅ Activé" : "⚠️ Désactivé",
                        "scan_powershell.sections.Security.data.secureBootEnabled");
                
                // BitLocker
                var bitlocker = GetBool(secData, "bitlockerEnabled") ?? GetBool(secData, "bitLocker");
                if (bitlocker.HasValue)
                    AddIfNotEmpty(ev, "Chiffrement disque", bitlocker.Value ? "✅ BitLocker actif" : "❌ Non chiffré",
                        "scan_powershell.sections.Security.data.bitlockerEnabled");
                
                // UAC
                var uac = GetBool(secData, "uacEnabled");
                if (uac.HasValue)
                    AddIfNotEmpty(ev, "UAC", uac.Value ? "✅ Activé" : "⚠️ Désactivé",
                        "scan_powershell.sections.Security.data.uacEnabled");
                
                // RDP
                var rdp = GetBool(secData, "rdpEnabled");
                if (rdp.HasValue && rdp.Value)
                    AddIfNotEmpty(ev, "RDP", "⚠️ Activé", "scan_powershell.sections.Security.data.rdpEnabled");
                
                // SMBv1
                var smb1 = GetBool(secData, "smbV1Enabled");
                if (smb1.HasValue && smb1.Value)
                    AddIfNotEmpty(ev, "SMBv1", "⚠️ Activé (risque)", "scan_powershell.sections.Security.data.smbV1Enabled");
            }

            // === PS: WindowsUpdate - Last patch ===
            var updateData = GetSectionData(root, "WindowsUpdate");
            if (updateData.HasValue)
            {
                var lastInstall = GetString(updateData, "lastInstallDate") ?? GetString(updateData, "LastInstalled");
                if (!string.IsNullOrEmpty(lastInstall) && DateTime.TryParse(lastInstall, out var dt))
                {
                    var days = (DateTime.Now - dt).TotalDays;
                    var status = days > 30 ? " ⚠️" : "";
                    AddIfNotEmpty(ev, "Dernier patch", $"{dt:d MMM yyyy}{status}",
                        "scan_powershell.sections.WindowsUpdate.data.lastInstallDate");
                }
            }

            // === PS: UserProfiles - Admin accounts ===
            var userData = GetSectionData(root, "UserProfiles");
            if (userData.HasValue)
            {
                var adminCount = GetInt(userData, "adminCount") ?? GetInt(userData, "localAdminCount");
                if (adminCount.HasValue && adminCount > 1)
                    AddIfNotEmpty(ev, "Admins locaux", $"⚠️ {adminCount.Value} comptes",
                        "scan_powershell.sections.UserProfiles.data.adminCount");
            }

            var machineId = GetSectionData(root, "MachineIdentity");
            if (machineId.HasValue)
            {
                var secureBoot = GetBool(machineId, "secureBoot");
                if (secureBoot.HasValue)
                    AddIfNotEmpty(ev, "Secure Boot", secureBoot.Value ? "✅ Activé" : "⚠️ Désactivé",
                        "scan_powershell.sections.MachineIdentity.data.secureBoot");
            }

            return ev;
        }

        #endregion

        #region Power - Alimentation

        private static Dictionary<string, string> ExtractPower(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            
            // === PS: Battery ===
            var batteryData = GetSectionData(root, "Battery");
            if (batteryData.HasValue)
            {
                var hasBattery = GetBool(batteryData, "hasBattery") ?? GetBool(batteryData, "present");
                if (hasBattery == true)
                {
                    var charge = GetInt(batteryData, "chargePercent") ?? GetInt(batteryData, "remainingCapacityPercent");
                    if (charge.HasValue)
                        ev["Niveau batterie"] = FormatValue($"{charge.Value}%", "scan_powershell.sections.Battery.data.chargePercent");
                    
                    var health = GetInt(batteryData, "healthPercent") ?? GetInt(batteryData, "designCapacityPercent");
                    if (health.HasValue)
                    {
                        var status = health < 50 ? " ⚠️ Usée" : health < 80 ? " ⚡" : "";
                        ev["Santé batterie"] = FormatValue($"{health.Value}%{status}",
                            "scan_powershell.sections.Battery.data.healthPercent");
                    }
                    
                    var cycles = GetInt(batteryData, "cycleCount");
                    if (cycles.HasValue)
                        ev["Cycles"] = FormatValue(cycles.Value.ToString(), "scan_powershell.sections.Battery.data.cycleCount");
                    
                    var status2 = GetString(batteryData, "status") ?? GetString(batteryData, "chargingStatus");
                    if (!string.IsNullOrEmpty(status2))
                        ev["État"] = FormatValue(status2, "scan_powershell.sections.Battery.data.status");
                }
                else
                {
                    ev["Batterie"] = FormatValue("Non présente (Desktop)", "scan_powershell.sections.Battery.data.present");
                }
            }

            // === PS: PowerSettings ===
            var powerData = GetSectionData(root, "PowerSettings");
            if (powerData.HasValue)
            {
                var plan = GetString(powerData, "activePlan") ?? GetString(powerData, "powerPlan");
                if (!string.IsNullOrEmpty(plan))
                    ev["Plan alimentation"] = FormatValue(plan, "scan_powershell.sections.PowerSettings.data.activePlan");
                
                var mode = GetString(powerData, "performanceMode");
                if (!string.IsNullOrEmpty(mode))
                    ev["Mode performance"] = FormatValue(mode, "scan_powershell.sections.PowerSettings.data.performanceMode");
            }

            // === Diagnostic Signals: Power events ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var powerLimit = GetSignalResult(signals.Value, "powerLimits", "power_throttle");
                if (powerLimit.HasValue)
                {
                    var detected = GetBool(GetNestedElement(powerLimit.Value, "value"), "PowerLimitDetected") ??
                                   GetBool(powerLimit, "detected") ?? false;
                    if (detected)
                        ev["Power throttle"] = FormatValue("⚠️ Détecté", "diagnostic_signals.powerLimits.value.PowerLimitDetected");
                }
            }

            return ev;
        }

        #endregion

        #region Helper Methods

        private static JsonElement? GetSectionData(JsonElement root, string sectionName)
        {
            // Try scan_powershell.sections first
            if (root.TryGetProperty("scan_powershell", out var ps) &&
                ps.TryGetProperty("sections", out var sections) &&
                sections.TryGetProperty(sectionName, out var section))
            {
                if (section.TryGetProperty("data", out var data))
                    return data;
                return section;
            }
            
            // Direct sections access
            if (root.TryGetProperty("sections", out var directSections) &&
                directSections.TryGetProperty(sectionName, out var directSection))
            {
                if (directSection.TryGetProperty("data", out var data))
                    return data;
                return directSection;
            }
            
            return null;
        }

        private static JsonElement? GetNestedElement(JsonElement root, params string[] path)
        {
            JsonElement current = root;
            foreach (var key in path)
            {
                if (!current.TryGetProperty(key, out current))
                {
                    // Case-insensitive fallback
                    bool found = false;
                    if (current.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in current.EnumerateObject())
                        {
                            if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                            {
                                current = prop.Value;
                                found = true;
                                break;
                            }
                        }
                    }
                    if (!found) return null;
                }
            }
            return current;
        }

        private static JsonElement? GetDiagnosticSignals(JsonElement root)
        {
            if (root.TryGetProperty("diagnostic_signals", out var signals))
                return signals;
            if (root.TryGetProperty("diagnosticSignals", out var signalsAlt))
                return signalsAlt;
            return null;
        }

        private static JsonElement? GetSignalResult(JsonElement signals, params string[] signalNames)
        {
            foreach (var name in signalNames)
            {
                if (signals.TryGetProperty(name, out var signal))
                    return signal;
                foreach (var prop in signals.EnumerateObject())
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                        return prop.Value;
                }
            }
            return null;
        }

        private static string? GetSignalValue(JsonElement signals, string[] signalNames, string valueName, string? fallbackName = null)
        {
            var signal = GetSignalResult(signals, signalNames);
            if (signal.HasValue)
            {
                var signalValue = GetNestedElement(signal.Value, "value");
                var value = GetString(signalValue, valueName);
                if (!string.IsNullOrEmpty(value))
                    return value;
                if (!string.IsNullOrEmpty(fallbackName))
                    return GetString(signalValue, fallbackName) ?? GetString(signal, fallbackName);
            }
            return null;
        }

        private static string? GetString(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (element.Value.TryGetProperty(propName, out var prop))
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
            return null;
        }

        private static int? GetInt(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (element.Value.TryGetProperty(propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32();
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var i)) return i;
            }
            return null;
        }

        private static double? GetDouble(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (element.Value.TryGetProperty(propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number) return prop.GetDouble();
                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var d)) return d;
            }
            return null;
        }

        private static bool? GetBool(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (element.Value.TryGetProperty(propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True) return true;
                if (prop.ValueKind == JsonValueKind.False) return false;
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var s = prop.GetString()?.ToLower();
                    if (s == "true" || s == "yes" || s == "1") return true;
                    if (s == "false" || s == "no" || s == "0") return false;
                }
            }
            return null;
        }

        private static JsonElement? GetArray(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (element.Value.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
                return prop;
            return null;
        }

        private static void AddIfNotEmpty(Dictionary<string, string> dict, string key, string? value, string? jsonPath = null)
        {
            if (!string.IsNullOrEmpty(value) && !dict.ContainsKey(key))
                dict[key] = FormatValue(value, jsonPath);
        }

        private static bool TryGetSnapshotMetric(JsonElement root, string category, string key, out JsonElement metric)
        {
            metric = default;
            if (root.TryGetProperty("diagnostic_snapshot", out var snapshot) &&
                snapshot.TryGetProperty("metrics", out var metrics) &&
                metrics.TryGetProperty(category, out var cat) &&
                cat.TryGetProperty(key, out var metricEl))
            {
                metric = metricEl;
                return true;
            }
            return false;
        }

        private static string? GetSnapshotMetricString(JsonElement root, string category, string key)
        {
            if (TryGetSnapshotMetric(root, category, key, out var metric) &&
                metric.TryGetProperty("available", out var avail) &&
                avail.ValueKind == JsonValueKind.True &&
                metric.TryGetProperty("value", out var value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
            return null;
        }

        private static string? GetSnapshotMetricDisplay(JsonElement root, string category, string key)
        {
            if (TryGetSnapshotMetric(root, category, key, out var metric) &&
                metric.TryGetProperty("available", out var avail) &&
                avail.ValueKind == JsonValueKind.True &&
                metric.TryGetProperty("value", out var value))
            {
                var unit = metric.TryGetProperty("unit", out var unitEl) ? unitEl.GetString() : "";
                var valueStr = value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble().ToString("F2")
                    : value.ToString();
                return string.IsNullOrEmpty(unit) ? valueStr : $"{valueStr} {unit}";
            }
            return null;
        }

        private static bool? GetSnapshotMetricBool(JsonElement root, string category, string key)
        {
            if (TryGetSnapshotMetric(root, category, key, out var metric) &&
                metric.TryGetProperty("available", out var avail) &&
                avail.ValueKind == JsonValueKind.True &&
                metric.TryGetProperty("value", out var value))
            {
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b)) return b;
            }
            return null;
        }

        private static string FormatValue(string value, string? jsonPath)
        {
            if (DebugPathsEnabled && !string.IsNullOrEmpty(jsonPath))
                return $"{value} 📍[{jsonPath}]";
            return value;
        }

        private static JsonElement? GetFirstElement(JsonElement? element, params string[] propNames)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var propName in propNames)
            {
                if (element.Value.TryGetProperty(propName, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Array)
                        return prop.EnumerateArray().FirstOrDefault();
                    if (prop.ValueKind == JsonValueKind.Object)
                        return prop;
                }
            }

            return null;
        }

        private static IEnumerable<JsonElement> EnumerateElements(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
                return element.EnumerateArray();
            if (element.ValueKind == JsonValueKind.Object)
                return new[] { element };
            return Array.Empty<JsonElement>();
        }

        private static string? ExtractFirstString(JsonElement element, string propName)
        {
            if (element.TryGetProperty(propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                    return prop.GetString();
                if (prop.ValueKind == JsonValueKind.Array)
                {
                    var first = prop.EnumerateArray().FirstOrDefault();
                    return first.ValueKind == JsonValueKind.String ? first.GetString() : first.ToString();
                }
                if (prop.ValueKind == JsonValueKind.Object)
                {
                    if (prop.TryGetProperty("value", out var val))
                        return val.ValueKind == JsonValueKind.String ? val.GetString() : val.ToString();
                    return prop.EnumerateObject().Any() ? prop.ToString() : null;
                }
            }
            return null;
        }

        private static List<string> GetTopProcesses(JsonElement root, string metric, int count)
        {
            var result = new List<string>();
            
            // Try process_telemetry first
            var telemetry = GetNestedElement(root, "process_telemetry");
            if (telemetry.HasValue)
            {
                var arrayName = metric switch
                {
                    "cpu" => "topCpu",
                    "memory" => "topMemory",
                    "io" => "topIo",
                    _ => $"top{char.ToUpper(metric[0])}{metric.Substring(1)}"
                };
                
                if (telemetry.Value.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    result = arr.EnumerateArray()
                        .Select(p => GetString(p, "name") ?? GetString(p, "processName") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(count)
                        .ToList();
                }
            }
            
            // Fallback to PS Processes section
            if (result.Count == 0)
            {
                var processes = GetSectionData(root, "Processes");
                if (processes.HasValue && processes.Value.TryGetProperty("topProcesses", out var top) && 
                    top.ValueKind == JsonValueKind.Array)
                {
                    result = top.EnumerateArray()
                        .Select(p => GetString(p, "name") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(count)
                        .ToList();
                }
            }
            
            return result;
        }

        #endregion
    }
}
