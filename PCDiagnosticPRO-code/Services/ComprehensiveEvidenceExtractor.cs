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
            
            // === PS: sections.OS ===
            var osData = GetSectionData(root, "OS");
            if (osData.HasValue)
            {
                AddIfNotEmpty(ev, "Version Windows", GetString(osData, "caption"));
                AddIfNotEmpty(ev, "Build", GetString(osData, "buildNumber"));
                AddIfNotEmpty(ev, "Architecture", GetString(osData, "architecture"));
                
                // Uptime
                var lastBoot = GetString(osData, "lastBootUpTime");
                if (!string.IsNullOrEmpty(lastBoot) && DateTime.TryParse(lastBoot, out var bootDt))
                {
                    var uptime = DateTime.Now - bootDt;
                    ev["Uptime"] = uptime.TotalDays >= 1 
                        ? $"{(int)uptime.TotalDays}j {uptime.Hours}h {uptime.Minutes}min"
                        : $"{uptime.Hours}h {uptime.Minutes}min";
                }
            }

            // === PS: sections.Security - Secure Boot, BitLocker, Antivirus ===
            var secData = GetSectionData(root, "Security");
            if (secData.HasValue)
            {
                var secureBoot = GetBool(secData, "secureBootEnabled");
                ev["Secure Boot"] = secureBoot.HasValue ? (secureBoot.Value ? "✅ Activé" : "❌ Désactivé") : "—";
                
                var bitlocker = GetBool(secData, "bitlockerEnabled");
                if (!bitlocker.HasValue) bitlocker = GetBool(secData, "bitLocker");
                ev["BitLocker"] = bitlocker.HasValue ? (bitlocker.Value ? "✅ Actif" : "❌ Inactif") : "—";
                
                // Antivirus
                var avName = GetString(secData, "antivirusName") ?? GetString(secData, "avName");
                var avStatus = GetString(secData, "antivirusStatus") ?? GetString(secData, "avStatus");
                if (!string.IsNullOrEmpty(avName))
                {
                    ev["Antivirus"] = string.IsNullOrEmpty(avStatus) ? avName : $"{avName} ({avStatus})";
                }
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
                            ev["Espace C:"] = $"{freeGB.Value:F1} GB libre ({pct:F0}%){status}";
                        }
                        break;
                    }
                }
            }

            // === Diagnostic Signals: WHEA, BSOD, Kernel-Power ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var whea = GetSignalValue(signals.Value, "whea_errors", "count");
                if (!string.IsNullOrEmpty(whea) && whea != "0")
                    ev["Erreurs WHEA"] = $"⚠️ {whea} détectée(s)";
                
                var bsod = GetSignalValue(signals.Value, "bsod_minidump", "count");
                if (!string.IsNullOrEmpty(bsod) && bsod != "0")
                    ev["BSOD récents"] = $"⚠️ {bsod} crash(es)";
                
                var kernelPower = GetSignalValue(signals.Value, "kernel_power", "count");
                if (!string.IsNullOrEmpty(kernelPower) && kernelPower != "0")
                    ev["Kernel-Power"] = $"⚠️ {kernelPower} événement(s)";
            }

            // === PS: WindowsUpdate ===
            var updateData = GetSectionData(root, "WindowsUpdate");
            if (updateData.HasValue)
            {
                var pending = GetInt(updateData, "pendingCount") ?? GetInt(updateData, "PendingCount");
                if (pending.HasValue)
                    ev["Updates en attente"] = pending.Value > 0 ? $"⚠️ {pending.Value}" : "✅ À jour";
            }

            // === C# Updates ===
            var csharpUpdates = GetNestedElement(root, "updates_csharp");
            if (csharpUpdates.HasValue)
            {
                var pending = GetInt(csharpUpdates, "pendingCount");
                if (pending.HasValue && !ev.ContainsKey("Updates en attente"))
                    ev["Updates en attente"] = pending.Value > 0 ? $"⚠️ {pending.Value}" : "✅ À jour";
            }

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
                var cpuArray = GetArray(cpuData, "cpus") ?? GetArray(cpuData, "cpuList");
                if (cpuArray.HasValue)
                {
                    var first = cpuArray.Value.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        AddIfNotEmpty(ev, "Modèle", GetString(first, "name")?.Trim());
                        
                        var cores = GetInt(first, "cores");
                        var threads = GetInt(first, "threads");
                        if (cores.HasValue && threads.HasValue)
                            ev["Cœurs / Threads"] = $"{cores.Value} / {threads.Value}";
                        else if (cores.HasValue)
                            ev["Cœurs"] = cores.Value.ToString();
                        
                        var maxClock = GetDouble(first, "maxClockSpeed");
                        if (maxClock.HasValue && maxClock > 0)
                            ev["Fréquence max"] = $"{maxClock.Value:F0} MHz";
                        
                        var currentClock = GetDouble(first, "currentClockSpeed");
                        if (currentClock.HasValue && currentClock > 0)
                            ev["Fréquence actuelle"] = $"{currentClock.Value:F0} MHz";
                        
                        var load = GetDouble(first, "currentLoad") ?? GetDouble(first, "load");
                        if (load.HasValue)
                            ev["Charge (PS)"] = $"{load.Value:F0} %";
                    }
                }
                
                var cpuCount = GetInt(cpuData, "cpuCount");
                if (cpuCount.HasValue && cpuCount > 1)
                    ev["Nombre de CPU"] = cpuCount.Value.ToString();
            }

            // === C# Sensors: Temperature ===
            if (sensors?.Cpu?.CpuTempC?.Available == true)
            {
                var temp = sensors.Cpu.CpuTempC.Value;
                var status = temp > 85 ? " 🔥" : temp > 70 ? " ⚠️" : "";
                ev["Température"] = $"{temp:F0}°C{status}";
            }

            // === Diagnostic Signals: Throttling ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var throttle = GetSignalResult(signals.Value, "cpu_throttle");
                if (throttle.HasValue)
                {
                    var detected = GetBool(throttle, "detected") ?? false;
                    ev["Throttling"] = detected ? "⚠️ Oui" : "✅ Non";
                    if (detected)
                    {
                        var reason = GetString(throttle, "reason");
                        if (!string.IsNullOrEmpty(reason))
                            ev["Raison throttle"] = reason;
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
                var gpuArray = GetArray(gpuData, "gpuList") ?? GetArray(gpuData, "gpus");
                if (gpuArray.HasValue)
                {
                    var first = gpuArray.Value.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        AddIfNotEmpty(ev, "GPU", GetString(first, "name")?.Trim());
                        AddIfNotEmpty(ev, "Fabricant", GetString(first, "vendor"));
                        AddIfNotEmpty(ev, "Résolution", GetString(first, "resolution"));
                        
                        var driverVer = GetString(first, "driverVersion");
                        if (!string.IsNullOrEmpty(driverVer))
                            ev["Version pilote"] = driverVer;
                        
                        // Date pilote (nested or direct)
                        string? driverDate = null;
                        if (first.TryGetProperty("driverDate", out var dd))
                        {
                            if (dd.ValueKind == JsonValueKind.Object && dd.TryGetProperty("DateTime", out var ddt))
                                driverDate = ddt.GetString();
                            else if (dd.ValueKind == JsonValueKind.String)
                                driverDate = dd.GetString();
                        }
                        AddIfNotEmpty(ev, "Date pilote", driverDate);
                        
                        // VRAM from PS (often null with note)
                        var vramMB = GetDouble(first, "vramTotalMB");
                        if (vramMB.HasValue && vramMB > 0)
                        {
                            ev["VRAM (PS)"] = vramMB >= 1024 ? $"{vramMB / 1024:F1} GB" : $"{vramMB:F0} MB";
                        }
                        else
                        {
                            var note = GetString(first, "vramNote");
                            if (!string.IsNullOrEmpty(note))
                                ev["VRAM (PS)"] = note;
                        }
                    }
                }
                
                var gpuCount = GetInt(gpuData, "gpuCount");
                if (gpuCount.HasValue && gpuCount > 1)
                    ev["Nombre de GPU"] = gpuCount.Value.ToString();
            }

            // === C# Sensors ===
            if (sensors?.Gpu != null)
            {
                if (sensors.Gpu.GpuTempC.Available)
                {
                    var temp = sensors.Gpu.GpuTempC.Value;
                    var status = temp > 85 ? " 🔥" : temp > 75 ? " ⚠️" : "";
                    ev["Température GPU"] = $"{temp:F0}°C{status}";
                }
                
                if (sensors.Gpu.GpuLoadPercent.Available)
                    ev["Charge GPU"] = $"{sensors.Gpu.GpuLoadPercent.Value:F0} %";
                
                if (sensors.Gpu.VramTotalMB.Available && sensors.Gpu.VramUsedMB.Available)
                {
                    var total = sensors.Gpu.VramTotalMB.Value;
                    var used = sensors.Gpu.VramUsedMB.Value;
                    var pct = total > 0 ? (used / total * 100) : 0;
                    ev["VRAM utilisée"] = $"{used:F0} MB / {total:F0} MB ({pct:F0}%)";
                }
            }

            // === Diagnostic Signals: TDR ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var tdr = GetSignalValue(signals.Value, "tdr_video", "count");
                if (!string.IsNullOrEmpty(tdr) && tdr != "0")
                    ev["TDR (crashes GPU)"] = $"⚠️ {tdr} détecté(s)";
            }

            return ev;
        }

        #endregion

        #region RAM - Mémoire vive

        private static Dictionary<string, string> ExtractRAM(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            
            // === PS: sections.Memory ===
            var memData = GetSectionData(root, "Memory");
            if (memData.HasValue)
            {
                var totalGB = GetDouble(memData, "totalGB");
                var availGB = GetDouble(memData, "availableGB");
                
                if (totalGB.HasValue && totalGB > 0)
                {
                    ev["RAM totale"] = $"{totalGB.Value:F1} GB";
                    
                    if (availGB.HasValue)
                    {
                        var usedGB = totalGB.Value - availGB.Value;
                        var pct = (usedGB / totalGB.Value) * 100;
                        var status = pct > 90 ? " ⚠️" : pct > 80 ? " ⚡" : "";
                        ev["RAM utilisée"] = $"{usedGB:F1} GB ({pct:F0}%){status}";
                        ev["RAM disponible"] = $"{availGB.Value:F1} GB";
                    }
                }
                
                // Virtual memory
                var virtualTotal = GetDouble(memData, "virtualTotalGB") ?? GetDouble(memData, "commitLimitGB");
                var virtualUsed = GetDouble(memData, "virtualUsedGB") ?? GetDouble(memData, "commitUsedGB");
                if (virtualTotal.HasValue && virtualUsed.HasValue)
                    ev["Mémoire virtuelle"] = $"{virtualUsed.Value:F1} / {virtualTotal.Value:F1} GB";
                
                // Page file
                var pageSize = GetDouble(memData, "pageFileSizeGB");
                var pageUsed = GetDouble(memData, "pageFileUsedGB");
                if (pageSize.HasValue && pageUsed.HasValue)
                    ev["Fichier d'échange"] = $"{pageUsed.Value:F1} / {pageSize.Value:F1} GB";
                
                // Module count
                var modCount = GetInt(memData, "moduleCount");
                if (modCount.HasValue && modCount > 0)
                    ev["Barrettes"] = modCount.Value.ToString();
            }

            // === Process Telemetry: Top RAM ===
            var topRam = GetTopProcesses(root, "memory", 5);
            if (topRam.Count > 0)
                ev["Top processus RAM"] = string.Join(", ", topRam);

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
                    ev["Disques physiques"] = diskList.Count.ToString();
                    
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
                        
                        ev[$"Disque {i}"] = $"{model.Trim()} ({info.Trim()})";
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
                        ev["Partitions"] = string.Join(" | ", volList.Take(4));
                }
            }

            // === PS: SmartDetails ===
            var smartData = GetSectionData(root, "SmartDetails");
            if (smartData.HasValue)
            {
                var healthStatus = GetString(smartData, "overallHealth") ?? GetString(smartData, "status");
                if (!string.IsNullOrEmpty(healthStatus))
                {
                    var icon = healthStatus.ToLower() switch
                    {
                        "ok" or "healthy" or "good" => "✅",
                        "caution" or "warning" => "⚠️",
                        _ => "❓"
                    };
                    ev["Santé SMART"] = $"{icon} {healthStatus}";
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
                    ev["Températures disques"] = tempsStr;
            }

            // === Process Telemetry: Top IO ===
            var topIO = GetTopProcesses(root, "io", 3);
            if (topIO.Count > 0)
                ev["Top processus IO"] = string.Join(", ", topIO);

            return ev;
        }

        #endregion

        #region Network - Réseau

        private static Dictionary<string, string> ExtractNetwork(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            
            // === PS: sections.Network ===
            var netData = GetSectionData(root, "Network");
            if (netData.HasValue && netData.Value.TryGetProperty("adapters", out var adapters) && 
                adapters.ValueKind == JsonValueKind.Array)
            {
                var first = adapters.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    AddIfNotEmpty(ev, "Adaptateur", GetString(first, "name"));
                    AddIfNotEmpty(ev, "Vitesse lien", GetString(first, "speed") ?? 
                        (GetDouble(first, "speedMbps").HasValue ? $"{GetDouble(first, "speedMbps"):F0} Mbps" : null));
                    AddIfNotEmpty(ev, "Adresse IP", GetString(first, "ipv4"));
                    AddIfNotEmpty(ev, "Passerelle", GetString(first, "gateway"));
                    AddIfNotEmpty(ev, "DNS", GetString(first, "dns"));
                    AddIfNotEmpty(ev, "MAC", GetString(first, "macAddress"));
                    
                    // WiFi specific
                    var rssi = GetInt(first, "rssi") ?? GetInt(first, "signalStrength");
                    if (rssi.HasValue)
                    {
                        var quality = rssi.Value > -50 ? "Excellent" : rssi.Value > -60 ? "Bon" : rssi.Value > -70 ? "Moyen" : "Faible";
                        ev["WiFi Signal"] = $"{rssi.Value} dBm ({quality})";
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
                    ev["Latence (ping)"] = $"{latency.Value:F0} ms{status}";
                }
                
                var jitter = GetDouble(netDiag, "jitterMs");
                if (jitter.HasValue)
                    ev["Gigue"] = $"{jitter.Value:F1} ms";
                
                var loss = GetDouble(netDiag, "packetLossPercent");
                if (loss.HasValue)
                {
                    var status = loss > 1 ? " ⚠️" : "";
                    ev["Perte paquets"] = $"{loss.Value:F1}%{status}";
                }
                
                var download = GetDouble(netDiag, "downloadMbps");
                var upload = GetDouble(netDiag, "uploadMbps");
                if (download.HasValue && upload.HasValue)
                    ev["Débit FAI"] = $"↓{download.Value:F1} / ↑{upload.Value:F1} Mbps";
                else if (download.HasValue)
                    ev["Download"] = $"{download.Value:F1} Mbps";
                
                var vpn = GetBool(netDiag, "vpnDetected");
                if (vpn.HasValue)
                    ev["VPN/Proxy"] = vpn.Value ? "⚠️ Détecté" : "Non";
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
                // BSOD
                var bsodCount = GetSignalValue(signals.Value, "bsod_minidump", "count");
                if (!string.IsNullOrEmpty(bsodCount))
                {
                    ev["BSOD récents"] = bsodCount == "0" ? "✅ Aucun" : $"⚠️ {bsodCount} crash(es)";
                }
                
                var bsodCodes = GetSignalValue(signals.Value, "bsod_minidump", "codes");
                if (!string.IsNullOrEmpty(bsodCodes) && bsodCodes != "[]")
                    ev["Codes BSOD"] = bsodCodes;
                
                // WHEA
                var wheaCount = GetSignalValue(signals.Value, "whea_errors", "count");
                if (!string.IsNullOrEmpty(wheaCount))
                    ev["Erreurs WHEA"] = wheaCount == "0" ? "✅ Aucune" : $"⚠️ {wheaCount}";
                
                // Kernel-Power
                var kpCount = GetSignalValue(signals.Value, "kernel_power", "count");
                if (!string.IsNullOrEmpty(kpCount))
                    ev["Kernel-Power"] = kpCount == "0" ? "✅ Aucun" : $"⚠️ {kpCount} événement(s)";
            }

            // === PS: ReliabilityHistory ===
            var reliData = GetSectionData(root, "ReliabilityHistory");
            if (reliData.HasValue)
            {
                var appCrashes = GetInt(reliData, "appCrashCount") ?? GetInt(reliData, "applicationCrashes");
                if (appCrashes.HasValue)
                    ev["Crashes applicatifs"] = appCrashes == 0 ? "✅ Aucun" : $"⚠️ {appCrashes.Value}";
            }

            // === PS: Services ===
            var svcData = GetSectionData(root, "Services");
            if (svcData.HasValue)
            {
                var failedCount = GetInt(svcData, "failedCount") ?? GetInt(svcData, "stoppedCritical");
                if (failedCount.HasValue && failedCount > 0)
                    ev["Services en échec"] = $"⚠️ {failedCount.Value}";
                
                if (svcData.Value.TryGetProperty("failedServices", out var failed) && failed.ValueKind == JsonValueKind.Array)
                {
                    var names = failed.EnumerateArray()
                        .Select(f => GetString(f, "name") ?? GetString(f, "displayName") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(3);
                    var namesStr = string.Join(", ", names);
                    if (!string.IsNullOrEmpty(namesStr))
                        ev["Services problèmes"] = namesStr;
                }
            }

            // === PS: SystemIntegrity (SFC/DISM) ===
            var intData = GetSectionData(root, "SystemIntegrity");
            if (intData.HasValue)
            {
                var sfcStatus = GetString(intData, "sfcStatus") ?? GetString(intData, "sfc");
                if (!string.IsNullOrEmpty(sfcStatus))
                    ev["SFC"] = sfcStatus.ToLower().Contains("ok") || sfcStatus.ToLower().Contains("clean") 
                        ? "✅ OK" : $"⚠️ {sfcStatus}";
                
                var dismStatus = GetString(intData, "dismStatus") ?? GetString(intData, "dism");
                if (!string.IsNullOrEmpty(dismStatus))
                    ev["DISM"] = dismStatus.ToLower().Contains("ok") || dismStatus.ToLower().Contains("healthy") 
                        ? "✅ OK" : $"⚠️ {dismStatus}";
            }

            // === PS: RestorePoints ===
            var rpData = GetSectionData(root, "RestorePoints");
            if (rpData.HasValue)
            {
                var rpCount = GetInt(rpData, "count") ?? GetInt(rpData, "restorePointCount");
                if (rpCount.HasValue)
                    ev["Points de restauration"] = rpCount.Value.ToString();
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
                    ev["Pilotes détectés"] = total.Value.ToString();
                
                var unsigned = GetInt(driverInv, "unsignedCount");
                if (unsigned.HasValue && unsigned > 0)
                    ev["Non signés"] = $"⚠️ {unsigned.Value}";
                
                var problems = GetInt(driverInv, "problemCount");
                if (problems.HasValue)
                    ev["Périph. en erreur"] = problems == 0 ? "✅ Aucun" : $"⚠️ {problems.Value}";
                
                var outdated = GetInt(driverInv, "outdatedCount");
                if (outdated.HasValue && outdated > 0)
                    ev["Pilotes obsolètes"] = $"⚠️ {outdated.Value}";
            }

            // === PS: DevicesDrivers ===
            var devData = GetSectionData(root, "DevicesDrivers");
            if (devData.HasValue)
            {
                var problemDevices = GetInt(devData, "problemDeviceCount") ?? GetInt(devData, "ProblemDeviceCount");
                if (problemDevices.HasValue && !ev.ContainsKey("Périph. en erreur"))
                    ev["Périph. en erreur"] = problemDevices == 0 ? "✅ Aucun" : $"⚠️ {problemDevices.Value}";
            }

            // === PS: Audio ===
            var audioData = GetSectionData(root, "Audio");
            if (audioData.HasValue)
            {
                var audioCount = GetInt(audioData, "deviceCount") ?? GetInt(audioData, "DeviceCount");
                if (audioCount.HasValue)
                    ev["Périph. audio"] = audioCount.Value.ToString();
            }

            // === PS: Printers ===
            var printData = GetSectionData(root, "Printers");
            if (printData.HasValue)
            {
                var printerCount = GetInt(printData, "printerCount") ?? GetInt(printData, "PrinterCount");
                if (printerCount.HasValue)
                    ev["Imprimantes"] = printerCount.Value.ToString();
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
                    ev["Pilotes critiques"] = string.Join(" | ", criticalDrivers.Take(3));
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
                var appCount = GetInt(appData, "applicationCount") ?? GetInt(appData, "count");
                if (appCount.HasValue)
                    ev["Apps installées"] = appCount.Value.ToString();
                
                // Recent installs
                if (appData.Value.TryGetProperty("recentInstalls", out var recent) && recent.ValueKind == JsonValueKind.Array)
                {
                    var recentList = recent.EnumerateArray()
                        .Select(a => GetString(a, "name") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(3);
                    var recentStr = string.Join(", ", recentList);
                    if (!string.IsNullOrEmpty(recentStr))
                        ev["Installations récentes"] = recentStr;
                }
            }

            // === PS: StartupPrograms ===
            var startupData = GetSectionData(root, "StartupPrograms");
            if (startupData.HasValue)
            {
                var startupCount = GetInt(startupData, "programCount") ?? GetInt(startupData, "count");
                if (startupCount.HasValue)
                    ev["Programmes démarrage"] = startupCount.Value.ToString();
                
                if (startupData.Value.TryGetProperty("programs", out var progs) && progs.ValueKind == JsonValueKind.Array)
                {
                    var heavyStartup = progs.EnumerateArray()
                        .Where(p => GetString(p, "impact")?.ToLower() == "high")
                        .Select(p => GetString(p, "name") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(3);
                    var heavyStr = string.Join(", ", heavyStartup);
                    if (!string.IsNullOrEmpty(heavyStr))
                        ev["Démarrage lourd"] = $"⚠️ {heavyStr}";
                }
            }

            // === Process Telemetry: Top CPU apps ===
            var topCpu = GetTopProcesses(root, "cpu", 3);
            if (topCpu.Count > 0)
                ev["Top CPU"] = string.Join(", ", topCpu);

            var topMem = GetTopProcesses(root, "memory", 3);
            if (topMem.Count > 0 && !ev.ContainsKey("Top RAM"))
                ev["Top RAM"] = string.Join(", ", topMem);

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
                var cpuArray = GetArray(cpuData, "cpus") ?? GetArray(cpuData, "cpuList");
                if (cpuArray.HasValue)
                {
                    var first = cpuArray.Value.EnumerateArray().FirstOrDefault();
                    var load = GetDouble(first, "currentLoad") ?? GetDouble(first, "load");
                    if (load.HasValue)
                    {
                        var status = load > 90 ? "🔥 Saturé" : load > 70 ? "⚠️ Élevé" : "✅ Normal";
                        ev["CPU"] = $"{load.Value:F0}% {status}";
                    }
                }
            }

            var memData = GetSectionData(root, "Memory");
            if (memData.HasValue)
            {
                var totalGB = GetDouble(memData, "totalGB");
                var availGB = GetDouble(memData, "availableGB");
                if (totalGB.HasValue && availGB.HasValue && totalGB > 0)
                {
                    var pct = ((totalGB.Value - availGB.Value) / totalGB.Value) * 100;
                    var status = pct > 90 ? "🔥 Saturée" : pct > 80 ? "⚠️ Élevée" : "✅ Normal";
                    ev["RAM"] = $"{pct:F0}% {status}";
                }
            }

            // === Bottlenecks from signals ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var bottlenecks = new List<string>();
                
                var cpuThrottle = GetSignalResult(signals.Value, "cpu_throttle");
                if (cpuThrottle.HasValue && (GetBool(cpuThrottle, "detected") ?? false))
                    bottlenecks.Add("CPU throttle");
                
                var ramPressure = GetSignalResult(signals.Value, "ram_pressure");
                if (ramPressure.HasValue && (GetBool(ramPressure, "detected") ?? false))
                    bottlenecks.Add("RAM pressure");
                
                var diskSat = GetSignalResult(signals.Value, "disk_saturation");
                if (diskSat.HasValue && (GetBool(diskSat, "detected") ?? false))
                    bottlenecks.Add("Disk saturation");
                
                if (bottlenecks.Count > 0)
                    ev["Bottlenecks"] = $"⚠️ {string.Join(", ", bottlenecks)}";
                else
                    ev["Bottlenecks"] = "✅ Aucun détecté";
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
                    ev["Températures"] = string.Join(" | ", temps);
            }

            // === Top processes ===
            var topCpu = GetTopProcesses(root, "cpu", 5);
            if (topCpu.Count > 0)
                ev["Top CPU"] = string.Join(", ", topCpu);

            var topMem = GetTopProcesses(root, "memory", 5);
            if (topMem.Count > 0)
                ev["Top RAM"] = string.Join(", ", topMem);

            var topIO = GetTopProcesses(root, "io", 3);
            if (topIO.Count > 0)
                ev["Top IO"] = string.Join(", ", topIO);

            return ev;
        }

        #endregion

        #region Security - Sécurité

        private static Dictionary<string, string> ExtractSecurity(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            
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
                    ev["Antivirus"] = $"{icon} {avName}";
                }
                
                // Firewall
                var fwStatus = GetBool(secData, "firewallEnabled") ?? GetBool(secData, "firewall");
                if (fwStatus.HasValue)
                    ev["Pare-feu"] = fwStatus.Value ? "✅ Activé" : "⚠️ Désactivé";
                
                // Secure Boot
                var secureBoot = GetBool(secData, "secureBootEnabled");
                if (secureBoot.HasValue)
                    ev["Secure Boot"] = secureBoot.Value ? "✅ Activé" : "⚠️ Désactivé";
                
                // BitLocker
                var bitlocker = GetBool(secData, "bitlockerEnabled") ?? GetBool(secData, "bitLocker");
                if (bitlocker.HasValue)
                    ev["Chiffrement disque"] = bitlocker.Value ? "✅ BitLocker actif" : "❌ Non chiffré";
                
                // UAC
                var uac = GetBool(secData, "uacEnabled");
                if (uac.HasValue)
                    ev["UAC"] = uac.Value ? "✅ Activé" : "⚠️ Désactivé";
                
                // RDP
                var rdp = GetBool(secData, "rdpEnabled");
                if (rdp.HasValue && rdp.Value)
                    ev["RDP"] = "⚠️ Activé";
                
                // SMBv1
                var smb1 = GetBool(secData, "smbV1Enabled");
                if (smb1.HasValue && smb1.Value)
                    ev["SMBv1"] = "⚠️ Activé (risque)";
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
                    ev["Dernier patch"] = $"{dt:d MMM yyyy}{status}";
                }
            }

            // === PS: UserProfiles - Admin accounts ===
            var userData = GetSectionData(root, "UserProfiles");
            if (userData.HasValue)
            {
                var adminCount = GetInt(userData, "adminCount") ?? GetInt(userData, "localAdminCount");
                if (adminCount.HasValue && adminCount > 1)
                    ev["Admins locaux"] = $"⚠️ {adminCount.Value} comptes";
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
                        ev["Niveau batterie"] = $"{charge.Value}%";
                    
                    var health = GetInt(batteryData, "healthPercent") ?? GetInt(batteryData, "designCapacityPercent");
                    if (health.HasValue)
                    {
                        var status = health < 50 ? " ⚠️ Usée" : health < 80 ? " ⚡" : "";
                        ev["Santé batterie"] = $"{health.Value}%{status}";
                    }
                    
                    var cycles = GetInt(batteryData, "cycleCount");
                    if (cycles.HasValue)
                        ev["Cycles"] = cycles.Value.ToString();
                    
                    var status2 = GetString(batteryData, "status") ?? GetString(batteryData, "chargingStatus");
                    if (!string.IsNullOrEmpty(status2))
                        ev["État"] = status2;
                }
                else
                {
                    ev["Batterie"] = "Non présente (Desktop)";
                }
            }

            // === PS: PowerSettings ===
            var powerData = GetSectionData(root, "PowerSettings");
            if (powerData.HasValue)
            {
                var plan = GetString(powerData, "activePlan") ?? GetString(powerData, "powerPlan");
                if (!string.IsNullOrEmpty(plan))
                    ev["Plan alimentation"] = plan;
                
                var mode = GetString(powerData, "performanceMode");
                if (!string.IsNullOrEmpty(mode))
                    ev["Mode performance"] = mode;
            }

            // === Diagnostic Signals: Power events ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var kpCount = GetSignalValue(signals.Value, "kernel_power", "count");
                if (!string.IsNullOrEmpty(kpCount) && kpCount != "0")
                    ev["Kernel-Power events"] = $"⚠️ {kpCount} (coupures)";
                
                var powerThrottle = GetSignalResult(signals.Value, "power_throttle");
                if (powerThrottle.HasValue && (GetBool(powerThrottle, "detected") ?? false))
                    ev["Power throttle"] = "⚠️ Détecté";
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
            return null;
        }

        private static JsonElement? GetSignalResult(JsonElement signals, string signalName)
        {
            if (signals.TryGetProperty(signalName, out var signal))
                return signal;
            return null;
        }

        private static string? GetSignalValue(JsonElement signals, string signalName, string valueName)
        {
            var signal = GetSignalResult(signals, signalName);
            if (signal.HasValue)
                return GetString(signal, valueName);
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

        private static void AddIfNotEmpty(Dictionary<string, string> dict, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                dict[key] = value;
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
