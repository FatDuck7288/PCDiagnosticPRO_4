using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Extracteur complet de données pour l'UI des résultats diagnostiques.
    /// CONTRAT UI:
    /// - Pas de "—" : seulement Oui/Non/Inconnu (raison)
    /// - "Données non disponibles" uniquement si clé absente ou null sans alternative
    /// - Si scan_result_combined.json contient la donnée, elle DOIT s'afficher
    /// 
    /// Sources:
    /// 1. PowerShell (scan_powershell.sections.*) : inventaire large et stable
    /// 2. C# sensors (sensors_csharp) : temps réel (CPU load, RAM, IO, températures)
    /// 3. Diagnostics actifs (diagnostic_signals, network_diagnostics) : tests, scoring
    /// </summary>
    public static class ComprehensiveEvidenceExtractor
    {
        /// <summary>
        /// Mode debug: affiche le chemin JSON source pour chaque ligne
        /// Activer avec variable d'environnement PCDIAG_DEBUG_PATHS=1
        /// </summary>
        public static bool DebugPathsEnabled { get; set; } = 
            Environment.GetEnvironmentVariable("PCDIAG_DEBUG_PATHS") == "1";

        // #region agent log
        private static readonly string DebugLogPath = @"d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\.cursor\debug.log";
        private static void DebugLog(string hypothesisId, string location, string message, object? data = null)
        {
            try
            {
                var entry = System.Text.Json.JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    sessionId = "debug-session",
                    hypothesisId,
                    location,
                    message,
                    data
                });
                System.IO.File.AppendAllText(DebugLogPath, entry + "\n");
            }
            catch { /* ignore logging errors */ }
        }
        // #endregion

        /// <summary>
        /// Résultat d'extraction avec score de couverture
        /// </summary>
        public class ExtractionResult
        {
            public Dictionary<string, string> Evidence { get; set; } = new();
            public int ExpectedFields { get; set; }
            public int ActualFields { get; set; }
            public double CoverageScore => ExpectedFields > 0 ? (double)ActualFields / ExpectedFields * 100 : 0;
        }

        /// <summary>
        /// Extrait toutes les données pertinentes pour un domaine de santé.
        /// Retourne un dictionnaire avec les données.
        /// Le score de couverture est accessible via ExtractWithCoverage() si nécessaire.
        /// </summary>
        public static Dictionary<string, string> Extract(
            HealthDomain domain,
            JsonElement root,
            HardwareSensorsResult? sensors = null)
        {
            var result = ExtractWithCoverage(domain, root, sensors);
            return result.Evidence;
        }

        /// <summary>
        /// Extrait toutes les données pertinentes pour un domaine de santé AVEC le score de couverture.
        /// Utilisé pour les tests contractuels et le monitoring.
        /// </summary>
        public static ExtractionResult ExtractWithCoverage(
            HealthDomain domain,
            JsonElement root,
            HardwareSensorsResult? sensors = null)
        {
            var resolvedSensors = ResolveSensors(root, sensors);
            var result = domain switch
            {
                HealthDomain.OS => ExtractOS(root, resolvedSensors),
                HealthDomain.CPU => ExtractCPU(root, resolvedSensors),
                HealthDomain.GPU => ExtractGPU(root, resolvedSensors),
                HealthDomain.RAM => ExtractRAM(root, resolvedSensors),
                HealthDomain.Storage => ExtractStorage(root, resolvedSensors),
                HealthDomain.Network => ExtractNetwork(root),
                HealthDomain.SystemStability => ExtractSystemStability(root),
                HealthDomain.Drivers => ExtractDrivers(root),
                HealthDomain.Applications => ExtractApplications(root),
                HealthDomain.Performance => ExtractPerformance(root, resolvedSensors),
                HealthDomain.Security => ExtractSecurity(root),
                HealthDomain.Power => ExtractPower(root, resolvedSensors),
                _ => new ExtractionResult()
            };

            return result;
        }

        #region OS - Système d'exploitation
        // Champs attendus: Version, Build, Architecture, Uptime, SecureBoot, Antivirus, EspaceC, Updates, Reboot, Erreurs

        private static ExtractionResult ExtractOS(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            int expected = 10;
            
            // === PS: sections.OS ===
            var osData = GetSectionData(root, "OS");
            var machineId = GetSectionData(root, "MachineIdentity");
            
            // 1. Version Windows complète (édition + displayVersion + build)
            string? version = null;
            string? build = null;
            string? displayVer = null;
            
            if (osData.HasValue)
            {
                version = GetString(osData, "caption");
                build = GetString(osData, "buildNumber");
                displayVer = GetString(osData, "displayVersion") ?? GetString(osData, "version");
            }
            
            if (!string.IsNullOrEmpty(version))
            {
                var fullVersion = version;
                if (!string.IsNullOrEmpty(displayVer)) fullVersion += $" ({displayVer})";
                if (!string.IsNullOrEmpty(build)) fullVersion += $" Build {build}";
                Add(ev, "Version Windows", fullVersion, "scan_powershell.sections.OS.data.caption");
            }
            else
            {
                AddUnknown(ev, "Version Windows", "section OS absente");
            }

            // 2. Architecture
            var arch = GetString(osData, "architecture");
            Add(ev, "Architecture", arch ?? "Inconnu", "scan_powershell.sections.OS.data.architecture");

            // 3. Uptime
            var lastBoot = GetString(osData, "lastBootUpTime");
            // #region agent log
            DebugLog("C", "ExtractOS:uptime", "lastBootUpTime from OS.data", new { lastBoot });
            // Fallback to MachineIdentity.data.lastBoot
            if (string.IsNullOrEmpty(lastBoot))
            {
                lastBoot = GetString(machineId, "lastBoot") ?? GetString(machineId, "LastBoot");
                DebugLog("C", "ExtractOS:uptime", "fallback to MachineIdentity.lastBoot", new { lastBoot });
            }
            // #endregion
            if (!string.IsNullOrEmpty(lastBoot) && DateTime.TryParse(lastBoot, out var bootDt))
            {
                var uptime = DateTime.Now - bootDt;
                var uptimeStr = uptime.TotalDays >= 1 
                    ? $"{(int)uptime.TotalDays}j {uptime.Hours}h {uptime.Minutes}min"
                    : $"{uptime.Hours}h {uptime.Minutes}min";
                Add(ev, "Uptime", uptimeStr, "scan_powershell.sections.OS.data.lastBootUpTime (calculé)");
            }
            else
            {
                var uptimeDays = GetInt(machineId, "uptimeDays");
                var uptimeHours = GetInt(machineId, "uptimeHours");
                if (uptimeDays.HasValue || uptimeHours.HasValue)
                {
                    var days = uptimeDays ?? 0;
                    var hours = uptimeHours ?? 0;
                    Add(ev, "Uptime", days > 0 ? $"{days}j {hours}h" : $"{hours}h", "scan_powershell.sections.MachineIdentity.data.uptime*");
                }
                else
                {
                    AddUnknown(ev, "Uptime", "lastBootUpTime absent");
                }
            }

            // 4. Secure Boot (Oui/Non, PAS "—")
            var secData = GetSectionData(root, "Security");
            var secureBoot = GetBool(secData, "secureBootEnabled") 
                ?? GetBool(secData, "SecureBootEnabled")
                ?? GetBool(machineId, "secureBoot")
                ?? GetBool(machineId, "SecureBoot")
                ?? GetSnapshotBool(root, "security", "secureBootEnabled");
            AddYesNo(ev, "Secure Boot", secureBoot, "scan_powershell.sections.Security.data.secureBootEnabled");

            // 5. Antivirus actif + état
            string? avName = null;
            string? avStatus = null;
            
            if (secData.HasValue && TryGetPropertyIgnoreCase(secData.Value, "antivirusProducts", out var avProducts))
            {
                if (avProducts.ValueKind == JsonValueKind.Array)
                {
                    var firstAv = avProducts.EnumerateArray().FirstOrDefault();
                    if (firstAv.ValueKind == JsonValueKind.Object)
                    {
                        avName = GetString(firstAv, "displayName") ?? GetString(firstAv, "name");
                        avStatus = GetString(firstAv, "productState") ?? GetString(firstAv, "status");
                    }
                }
                else if (avProducts.ValueKind == JsonValueKind.Object)
                {
                    avName = GetString(avProducts, "displayName") ?? GetString(avProducts, "name");
                    avStatus = GetString(avProducts, "productState") ?? GetString(avProducts, "status");
                }
                else if (avProducts.ValueKind == JsonValueKind.String)
                {
                    avName = avProducts.GetString();
                }
            }
            
            if (string.IsNullOrEmpty(avName))
            {
                avName = GetString(secData, "antivirusName") ?? GetString(secData, "avName") ?? GetString(secData, "AntivirusName");
                avStatus = GetString(secData, "antivirusStatus") ?? GetString(secData, "avStatus") ?? GetString(secData, "AntivirusStatus");
            }
            
            if (string.IsNullOrEmpty(avName))
            {
                avName = GetSnapshotString(root, "security", "antivirusName");
                avStatus = GetSnapshotString(root, "security", "antivirusStatus");
            }
            
            if (string.IsNullOrEmpty(avName))
            {
                var defender = GetBool(secData, "defenderEnabled");
                if (defender.HasValue && defender.Value)
                {
                    avName = "Windows Defender";
                    avStatus = GetBool(secData, "defenderRTP") == true ? "Actif" : "Partiel";
                }
            }
            if (!string.IsNullOrEmpty(avName))
            {
                var avInfo = avName;
                if (!string.IsNullOrEmpty(avStatus))
                {
                    var icon = avStatus.ToLower() switch
                    {
                        "enabled" or "on" or "actif" or "à jour" => "✅",
                        "disabled" or "off" or "inactif" => "⚠️",
                        _ => ""
                    };
                    avInfo = $"{icon} {avName} ({avStatus})";
                }
                Add(ev, "Antivirus", avInfo, "scan_powershell.sections.Security.data.antivirusName");
            }
            else
            {
                AddUnknown(ev, "Antivirus", "données AV absentes");
            }

            // 6. Espace libre C: (total / libre / %)
            var storageData = GetSectionData(root, "Storage");
            bool foundC = false;
            if (storageData.HasValue && storageData.Value.TryGetProperty("volumes", out var volumes) && 
                volumes.ValueKind == JsonValueKind.Array)
            {
                foreach (var vol in volumes.EnumerateArray())
                {
                    var letterRaw = GetString(vol, "driveLetter") ?? GetString(vol, "letter");
                    var letter = letterRaw?.Replace(":", "").ToUpperInvariant();
                    if (letter == "C")
                    {
                        var freeGB = GetDouble(vol, "freeSpaceGB") ?? GetDouble(vol, "freeGB");
                        var sizeGB = GetDouble(vol, "sizeGB") ?? GetDouble(vol, "totalGB");
                        if (freeGB.HasValue && sizeGB.HasValue && sizeGB > 0)
                        {
                            var pct = (freeGB.Value / sizeGB.Value) * 100;
                            var status = pct < 10 ? " ⚠️ Critique" : pct < 20 ? " ⚡ Faible" : "";
                            Add(ev, "Espace C:", $"{freeGB.Value:F1} GB libre / {sizeGB.Value:F1} GB ({pct:F0}%){status}", 
                                "scan_powershell.sections.Storage.data.volumes[C]");
                            foundC = true;
                        }
                        break;
                    }
                }
            }
            if (!foundC) AddUnknown(ev, "Espace C:", "volume C non trouvé");

            // 7. Updates en attente
            var updateData = GetSectionData(root, "WindowsUpdate");
            var csharpUpdates = GetNestedElement(root, "updates_csharp");
            int? pendingCount = GetInt(updateData, "pendingCount") ?? GetInt(updateData, "PendingCount") ?? GetInt(csharpUpdates, "pendingCount");
            
            if (pendingCount.HasValue)
            {
                var status = pendingCount.Value > 0 ? $"⚠️ {pendingCount.Value} en attente" : "✅ Système à jour";
                Add(ev, "Updates Windows", status, "scan_powershell.sections.WindowsUpdate.data.pendingCount");
            }
            else
            {
                AddUnknown(ev, "Updates Windows", "WindowsUpdate absent");
            }

            // 8. Redémarrage requis
            var rebootRequired = GetBool(updateData, "rebootRequired") ?? GetBool(csharpUpdates, "rebootRequired");
            AddYesNo(ev, "Redémarrage requis", rebootRequired, "updates_csharp.rebootRequired");

            // 9. Erreurs critiques (WHEA, BSOD, Kernel-Power)
            var signals = GetDiagnosticSignals(root);
            var errorSummary = new List<string>();
            
            var wheaCount = GetSignalIntAny(signals, new[] { "whea_errors", "whea", "WHEA" }, "count", "Last30dCount", "Last7dCount", "FatalCount");
            if (wheaCount.HasValue && wheaCount > 0) errorSummary.Add($"WHEA: {wheaCount}");
            
            var bsodCount = GetSignalIntAny(signals, new[] { "bsod_minidump", "bsod", "driverStability" }, "count", "BugcheckCount30d", "BugcheckCount7d", "BugcheckCount");
            if (bsodCount.HasValue && bsodCount > 0) errorSummary.Add($"BSOD: {bsodCount}");
            
            var kpCount = GetSignalIntAny(signals, new[] { "kernel_power", "kernelPower", "driverStability" }, "count", "KernelPower41Count30d", "KernelPower41Count7d");
            if (kpCount.HasValue && kpCount > 0) errorSummary.Add($"Kernel-Power: {kpCount}");
            
            if (errorSummary.Count > 0)
            {
                Add(ev, "Erreurs critiques", $"⚠️ {string.Join(", ", errorSummary)}", "diagnostic_signals.*");
            }
            else if (signals.HasValue && (wheaCount.HasValue || bsodCount.HasValue || kpCount.HasValue))
            {
                Add(ev, "Erreurs critiques", "✅ Aucune détectée", "diagnostic_signals.*");
            }
            else
            {
                AddUnknown(ev, "Erreurs critiques", "diagnostic_signals absent");
            }

            // NOTE: BitLocker retiré de cette section -> va dans Sécurité

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region CPU - Processeur
        // Champs attendus: Modèle, Coeurs/Threads, FréqMax, FréqActuelle, Charge, Température, Throttling

        private static ExtractionResult ExtractCPU(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            int expected = 7;
            
            var cpuData = GetSectionData(root, "CPU");
            JsonElement? firstCpu = null;
            
            // #region agent log
            DebugLog("A", "ExtractCPU:entry", "cpuData exists", new { hasCpuData = cpuData.HasValue });
            if (cpuData.HasValue)
            {
                // Log the actual structure of cpus/cpuList
                var cpusKind = TryGetPropertyIgnoreCase(cpuData.Value, "cpus", out var cpusEl) ? cpusEl.ValueKind.ToString() : "NotFound";
                var cpuListKind = TryGetPropertyIgnoreCase(cpuData.Value, "cpuList", out var cpuListEl) ? cpuListEl.ValueKind.ToString() : "NotFound";
                DebugLog("A", "ExtractCPU:structure", "cpus/cpuList structure", new { cpusKind, cpuListKind });
            }
            // #endregion
            
            if (cpuData.HasValue)
            {
                var cpuArray = GetArray(cpuData, "cpus") ?? GetArray(cpuData, "cpuList");
                DebugLog("A", "ExtractCPU:array", "GetArray result", new { cpuArrayHasValue = cpuArray.HasValue });
                
                firstCpu = GetFirstObject(cpuData, "cpus", "cpuList");
                if (firstCpu.HasValue)
                {
                    DebugLog("A", "ExtractCPU:firstCpu", "resolved CPU object", new { firstCpuKind = firstCpu?.ValueKind.ToString() });
                }
            }

            // 1. Modèle CPU
            var model = GetString(firstCpu, "name")?.Trim();
            if (!string.IsNullOrEmpty(model))
                Add(ev, "Modèle", model, "scan_powershell.sections.CPU.data.cpus[0].name");
            else
                AddUnknown(ev, "Modèle", "CPU.data.cpus absent");

            // 2. Cœurs / Threads
            var cores = GetInt(firstCpu, "cores");
            var threads = GetInt(firstCpu, "threads");
            if (cores.HasValue && threads.HasValue)
                Add(ev, "Cœurs / Threads", $"{cores.Value} / {threads.Value}", "scan_powershell.sections.CPU.data.cpus[0].cores/threads");
            else if (cores.HasValue)
                Add(ev, "Cœurs", cores.Value.ToString(), "scan_powershell.sections.CPU.data.cpus[0].cores");
            else
                AddUnknown(ev, "Cœurs / Threads", "données absentes");

            // 3. Fréquence max (MHz → GHz)
            var maxClock = GetDouble(firstCpu, "maxClockSpeed");
            if (maxClock.HasValue && maxClock > 0)
            {
                var ghz = maxClock.Value / 1000.0;
                Add(ev, "Fréquence max", $"{ghz:F2} GHz ({maxClock.Value:F0} MHz)", "scan_powershell.sections.CPU.data.cpus[0].maxClockSpeed");
            }
            else
            {
                AddUnknown(ev, "Fréquence max", "maxClockSpeed absent");
            }

            // 4. Fréquence actuelle
            var currentClock = GetDouble(firstCpu, "currentClockSpeed");
            if (currentClock.HasValue && currentClock > 0)
            {
                var ghz = currentClock.Value / 1000.0;
                Add(ev, "Fréquence actuelle", $"{ghz:F2} GHz", "scan_powershell.sections.CPU.data.cpus[0].currentClockSpeed");
            }
            // Pas de "Inconnu" si absent - champ optionnel

            // 5. Charge actuelle (PS + calcul moyenne si possible)
            var loadPS = GetDouble(firstCpu, "currentLoad") ?? GetDouble(firstCpu, "load");
            if (loadPS.HasValue)
            {
                var status = loadPS > 90 ? " 🔥 Saturé" : loadPS > 70 ? " ⚠️ Élevé" : "";
                Add(ev, "Charge CPU", $"{loadPS.Value:F0}%{status}", "scan_powershell.sections.CPU.data.cpus[0].currentLoad");
            }
            else
            {
                AddUnknown(ev, "Charge CPU", "currentLoad absent");
            }

            // 6. Température CPU (capteurs C# - UNE SEULE LIGNE)
            if (sensors?.Cpu?.CpuTempC?.Available == true)
            {
                var temp = sensors.Cpu.CpuTempC.Value;
                var status = temp > 85 ? " 🔥 Critique" : temp > 70 ? " ⚠️ Élevée" : " ✅";
                Add(ev, "Température CPU", $"{temp:F0}°C{status}", "sensors_csharp.cpu.cpuTempC.value");
            }
            else
            {
                AddUnknown(ev, "Température CPU", sensors?.Cpu?.CpuTempC?.Reason ?? "capteur indisponible");
            }

            // 7. Throttling (Oui/Non + raison)
            var signals = GetDiagnosticSignals(root);
            // #region agent log
            if (signals.HasValue)
            {
                var signalNames = new List<string>();
                foreach (var prop in signals.Value.EnumerateObject()) signalNames.Add(prop.Name);
                DebugLog("D", "ExtractCPU:signals", "available signal names", new { signalNames });
            }
            else
            {
                DebugLog("D", "ExtractCPU:signals", "diagnostic_signals is NULL", null);
            }
            // #endregion
            if (signals.HasValue)
            {
                // Try multiple naming conventions for throttle signal
                var throttle = GetSignalResult(signals.Value, "cpu_throttle", "cpuThrottle", "CpuThrottle");
                // #region agent log
                DebugLog("D", "ExtractCPU:throttle", "throttle signal found", new { hasThrottle = throttle.HasValue });
                // #endregion
                if (throttle.HasValue)
                {
                    var throttleValue = GetSignalValue(throttle.Value);
                    bool? detected = null;
                    string reason = "";
                    
                    if (throttleValue.HasValue)
                    {
                        detected = GetBool(throttleValue, "detected") 
                            ?? GetBool(throttleValue, "ThrottleSuspected")
                            ?? GetBool(throttleValue, "throttlingSuspected");
                        reason = GetString(throttleValue, "reason") ?? GetString(throttleValue, "Reason") ?? "";
                        
                        var eventCount = GetInt(throttleValue, "ThrottlingEventCount30d") 
                            ?? GetInt(throttleValue, "ThrottlingEventCount7d")
                            ?? GetInt(throttleValue, "ThermalThrottleCount")
                            ?? GetInt(throttleValue, "PowerLimitCount");
                        if (!detected.HasValue && eventCount.HasValue)
                            detected = eventCount.Value > 0;
                    }
                    
                    if (detected == true)
                    {
                        var reasonStr = !string.IsNullOrEmpty(reason) ? $" ({reason})" : "";
                        Add(ev, "Throttling", $"⚠️ Oui{reasonStr}", "diagnostic_signals.cpu_throttle");
                    }
                    else if (detected == false)
                    {
                        Add(ev, "Throttling", "✅ Non détecté", "diagnostic_signals.cpu_throttle");
                    }
                    else
                    {
                        AddUnknown(ev, "Throttling", "signal cpuThrottle sans état exploitable");
                    }
                }
                else
                {
                    AddUnknown(ev, "Throttling", "signal cpu_throttle absent");
                }
            }
            else
            {
                AddUnknown(ev, "Throttling", "diagnostic_signals absent");
            }

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region GPU - Carte graphique
        // Champs attendus: Nom, Fabricant, Résolution, VersionPilote, DatePilote, VRAMTotal, VRAMUtilisée, ChargeGPU, TempGPU, TDR

        private static ExtractionResult ExtractGPU(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            int expected = 10;
            
            var gpuData = GetSectionData(root, "GPU");
            JsonElement? firstGpu = null;
            
            if (gpuData.HasValue)
            {
                var gpuArray = GetArray(gpuData, "gpuList") ?? GetArray(gpuData, "gpus");
                if (gpuArray.HasValue)
                    firstGpu = gpuArray.Value.EnumerateArray().FirstOrDefault();
                else
                    firstGpu = GetFirstObject(gpuData, "gpuList", "gpus");
            }

            // 1. Nom GPU
            var name = GetString(firstGpu, "name")?.Trim();
            if (!string.IsNullOrEmpty(name))
                Add(ev, "GPU", name, "scan_powershell.sections.GPU.data.gpuList[0].name");
            else
                AddUnknown(ev, "GPU", "GPU.data.gpuList absent");

            // 2. Fabricant
            var vendor = GetString(firstGpu, "vendor")?.Trim();
            if (!string.IsNullOrEmpty(vendor))
                Add(ev, "Fabricant", vendor, "scan_powershell.sections.GPU.data.gpuList[0].vendor");
            // Optionnel, pas de "Inconnu"

            // 3. Résolution (+ refresh si dispo)
            var resolution = GetString(firstGpu, "resolution");
            var refresh = GetInt(firstGpu, "refreshRate") ?? GetInt(firstGpu, "currentRefreshRate");
            if (!string.IsNullOrEmpty(resolution))
            {
                var resStr = refresh.HasValue ? $"{resolution} @ {refresh}Hz" : resolution;
                Add(ev, "Résolution", resStr, "scan_powershell.sections.GPU.data.gpuList[0].resolution");
            }
            else
            {
                AddUnknown(ev, "Résolution", "resolution absent");
            }

            // 4. Version pilote
            var driverVer = GetString(firstGpu, "driverVersion");
            if (!string.IsNullOrEmpty(driverVer))
                Add(ev, "Version pilote", driverVer, "scan_powershell.sections.GPU.data.gpuList[0].driverVersion");
            else
                AddUnknown(ev, "Version pilote", "driverVersion absent");

            // 5. Date pilote
            string? driverDate = null;
            if (firstGpu.HasValue && firstGpu.Value.TryGetProperty("driverDate", out var dd))
            {
                if (dd.ValueKind == JsonValueKind.Object && dd.TryGetProperty("DateTime", out var ddt))
                    driverDate = ddt.GetString();
                else if (dd.ValueKind == JsonValueKind.String)
                    driverDate = dd.GetString();
            }
            if (!string.IsNullOrEmpty(driverDate))
                Add(ev, "Date pilote", driverDate, "scan_powershell.sections.GPU.data.gpuList[0].driverDate");
            // Optionnel

            // 6 & 7. VRAM totale et utilisée (UNE SEULE SECTION, pas de doublons)
            // Priorité: capteurs C# > PS vramTotalMB > vramNote
            bool vramDisplayed = false;
            
            if (sensors?.Gpu?.VramTotalMB?.Available == true)
            {
                var totalMB = sensors.Gpu.VramTotalMB.Value;
                var totalStr = totalMB >= 1024 ? $"{totalMB / 1024:F1} GB" : $"{totalMB:F0} MB";
                
                if (sensors.Gpu.VramUsedMB?.Available == true)
                {
                    var usedMB = sensors.Gpu.VramUsedMB.Value;
                    var pct = totalMB > 0 ? (usedMB / totalMB * 100) : 0;
                    var status = pct > 90 ? " ⚠️" : "";
                    Add(ev, "VRAM", $"{usedMB:F0} MB / {totalStr} ({pct:F0}%){status}", "sensors_csharp.gpu.vramTotalMB/vramUsedMB");
                }
                else
                {
                    Add(ev, "VRAM totale", totalStr, "sensors_csharp.gpu.vramTotalMB");
                }
                vramDisplayed = true;
            }
            
            if (!vramDisplayed && firstGpu.HasValue)
            {
                var vramMB = GetDouble(firstGpu, "vramTotalMB");
                if (vramMB.HasValue && vramMB > 0)
                {
                    var str = vramMB >= 1024 ? $"{vramMB / 1024:F1} GB" : $"{vramMB:F0} MB";
                    Add(ev, "VRAM totale", str, "scan_powershell.sections.GPU.data.gpuList[0].vramTotalMB");
                    vramDisplayed = true;
                }
                else
                {
                    // Fallback: vramNote (si vramTotalMB est null)
                    var vramNote = GetString(firstGpu, "vramNote");
                    if (!string.IsNullOrEmpty(vramNote))
                    {
                        Add(ev, "VRAM", vramNote, "scan_powershell.sections.GPU.data.gpuList[0].vramNote");
                        vramDisplayed = true;
                    }
                }
            }
            
            if (!vramDisplayed)
                AddUnknown(ev, "VRAM", "limitation WMI - collecte externalisée");

            // 8. Charge GPU (capteurs C#)
            if (sensors?.Gpu?.GpuLoadPercent?.Available == true)
            {
                var load = sensors.Gpu.GpuLoadPercent.Value;
                var status = load > 90 ? " 🔥" : load > 70 ? " ⚠️" : "";
                Add(ev, "Charge GPU", $"{load:F0}%{status}", "sensors_csharp.gpu.gpuLoadPercent");
            }
            else
            {
                AddUnknown(ev, "Charge GPU", sensors?.Gpu?.GpuLoadPercent?.Reason ?? "capteur indisponible");
            }

            // 9. Température GPU (capteurs C# - UNE SEULE LIGNE)
            if (sensors?.Gpu?.GpuTempC?.Available == true)
            {
                var temp = sensors.Gpu.GpuTempC.Value;
                var status = temp > 85 ? " 🔥 Critique" : temp > 75 ? " ⚠️ Élevée" : " ✅";
                var label = "Température GPU";
                var source = sensors.Gpu.GpuTempSource ?? "";
                if (source.Contains("hot spot", StringComparison.OrdinalIgnoreCase) || 
                    source.Contains("hotspot", StringComparison.OrdinalIgnoreCase))
                {
                    label = "Température GPU (Hot Spot)";
                }
                Add(ev, label, $"{temp:F0}°C{status}", "sensors_csharp.gpu.gpuTempC");
            }
            else
            {
                AddUnknown(ev, "Température GPU", sensors?.Gpu?.GpuTempC?.Reason ?? "capteur indisponible");
            }

            // 10. TDR / crashes GPU
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var tdrCount = GetSignalIntAny(signals, new[] { "tdr_video", "gpuRootCause", "driverStability" }, "count", "TdrCount30d", "TdrCount7d");
                if (tdrCount.HasValue)
                {
                    Add(ev, "TDR (crashes GPU)", tdrCount > 0 ? $"⚠️ {tdrCount} détecté(s)" : "✅ Aucun", "diagnostic_signals.tdr_video.count");
                }
            }

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region RAM - Mémoire vive
        // Champs attendus: Total, Utilisée, Disponible, %, Virtual, Pagefile, Barrettes, Top5

        private static ExtractionResult ExtractRAM(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            int expected = 8;
            
            var memData = GetSectionData(root, "Memory");
            
            var totalGB = GetDouble(memData, "totalGB");
            var availGB = GetDouble(memData, "availableGB") ?? GetDouble(memData, "freeGB");
            
            // 1. RAM totale
            if (totalGB.HasValue && totalGB > 0)
                Add(ev, "RAM totale", $"{totalGB.Value:F1} GB", "scan_powershell.sections.Memory.data.totalGB");
            else
                AddUnknown(ev, "RAM totale", "Memory.data.totalGB absent");

            // 2-4. RAM utilisée / disponible / %
            if (totalGB.HasValue && totalGB > 0 && availGB.HasValue)
            {
                var usedGB = totalGB.Value - availGB.Value;
                var pct = (usedGB / totalGB.Value) * 100;
                var status = pct > 90 ? " ⚠️ Critique" : pct > 80 ? " ⚡ Élevé" : "";
                
                Add(ev, "RAM utilisée", $"{usedGB:F1} GB ({pct:F0}%){status}", "scan_powershell.sections.Memory.data (calculé)");
                Add(ev, "RAM disponible", $"{availGB.Value:F1} GB", "scan_powershell.sections.Memory.data.availableGB");
            }
            else
            {
                AddUnknown(ev, "RAM utilisée", "données manquantes");
            }

            // 5. Mémoire virtuelle
            var virtualTotal = GetDouble(memData, "virtualTotalGB") ?? GetDouble(memData, "commitLimitGB");
            var virtualUsed = GetDouble(memData, "virtualUsedGB") ?? GetDouble(memData, "commitUsedGB");
            if (virtualTotal.HasValue && virtualUsed.HasValue)
                Add(ev, "Mémoire virtuelle", $"{virtualUsed.Value:F1} / {virtualTotal.Value:F1} GB", "scan_powershell.sections.Memory.data.virtual*");
            // Optionnel

            // 6. Fichier de pagination
            var pageSize = GetDouble(memData, "pageFileSizeGB") ?? GetDouble(memData, "pagefileSize");
            var pageUsed = GetDouble(memData, "pageFileUsedGB") ?? GetDouble(memData, "pagefileUsed");
            if (pageSize.HasValue && pageUsed.HasValue)
                Add(ev, "Fichier d'échange", $"{pageUsed.Value:F1} / {pageSize.Value:F1} GB", "scan_powershell.sections.Memory.data.pagefile*");
            // Optionnel

            // 7. Barrettes
            var modCount = GetInt(memData, "moduleCount") ?? GetInt(memData, "slotCount");
            if (modCount.HasValue && modCount > 0)
                Add(ev, "Barrettes", modCount.Value.ToString(), "scan_powershell.sections.Memory.data.moduleCount");
            // Optionnel

            // 8. Top 5 processus RAM
            var topRam = GetTopProcesses(root, "memory", 5);
            if (topRam.Count > 0)
                Add(ev, "Top processus RAM", string.Join(", ", topRam), "process_telemetry.topMemory");
            else
                AddUnknown(ev, "Top processus RAM", "process_telemetry absent");

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region Storage - Stockage
        // Champs attendus: DisquesPhysiques, TypeDisque, TempDisques, SMART, ToutesPartitions, TopIO

        private static ExtractionResult ExtractStorage(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            int expected = 6;
            
            var storageData = GetSectionData(root, "Storage");
            
            // 1 & 2. Disques physiques avec type + température
            var diskList = new List<JsonElement>();
            var diskPathRoot = "physicalDisks";
            if (storageData.HasValue)
            {
                if (TryGetPropertyIgnoreCase(storageData.Value, "physicalDisks", out var physicalDisks) && 
                    physicalDisks.ValueKind == JsonValueKind.Array)
                {
                    diskList = physicalDisks.EnumerateArray().ToList();
                    diskPathRoot = "physicalDisks";
                }
                else if (TryGetPropertyIgnoreCase(storageData.Value, "disks", out var disks) && 
                    disks.ValueKind == JsonValueKind.Array)
                {
                    diskList = disks.EnumerateArray().ToList();
                    diskPathRoot = "disks";
                }
            }
            
            if (diskList.Count > 0)
            {
                Add(ev, "Disques physiques", diskList.Count.ToString(), $"scan_powershell.sections.Storage.data.{diskPathRoot}.length");
                
                var sensorTemps = sensors?.Disks?
                    .Select((d, idx) => (
                        Index: idx,
                        Name: d.Name?.Value ?? "",
                        Temp: d.TempC?.Available == true ? d.TempC.Value : (double?)null))
                    .ToList() ?? new List<(int Index, string Name, double? Temp)>();
                
                string TempEmoji(double t) => t < 45 ? "✅" : t <= 55 ? "⚡" : "⚠️";
                
                double? MatchDiskTemp(string model, int index)
                {
                    if (sensorTemps.Count == 0) return null;
                    
                    var match = sensorTemps.FirstOrDefault(s => s.Temp.HasValue &&
                        (!string.IsNullOrEmpty(s.Name) &&
                         (model.Contains(s.Name, StringComparison.OrdinalIgnoreCase) || 
                          s.Name.Contains(model, StringComparison.OrdinalIgnoreCase))));
                    if (!string.IsNullOrEmpty(match.Name) && match.Temp.HasValue) return match.Temp;
                    
                    if (index >= 0 && index < sensorTemps.Count && sensorTemps[index].Temp.HasValue)
                        return sensorTemps[index].Temp;
                    
                    return null;
                }
                
                double? maxTemp = null;
                int i = 1;
                foreach (var disk in diskList.Take(6))
                {
                    var model = GetString(disk, "model") ?? GetString(disk, "friendlyName") ?? GetString(disk, "name") ?? $"Disque {i}";
                    var mediaType = GetString(disk, "mediaType") ?? GetString(disk, "type") ?? "";
                    var sizeGB = GetDouble(disk, "sizeGB");
                    
                    var typeStr = mediaType.ToUpperInvariant() switch
                    {
                        "SSD" => "SSD",
                        "HDD" => "HDD",
                        "NVME" => "NVMe",
                        _ when model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) => "NVMe",
                        _ when model.Contains("SSD", StringComparison.OrdinalIgnoreCase) => "SSD",
                        _ => "HDD"
                    };
                    
                    var info = sizeGB.HasValue ? $"{typeStr} {sizeGB.Value:F0} GB" : typeStr;
                    var line = $"{model.Trim()} ({info})";
                    
                    var temp = MatchDiskTemp(model, i - 1);
                    if (temp.HasValue)
                    {
                        maxTemp = !maxTemp.HasValue ? temp.Value : Math.Max(maxTemp.Value, temp.Value);
                        line += $" — {temp.Value:F0}°C {TempEmoji(temp.Value)}";
                    }
                    else
                    {
                        line += " — Temp. inconnue";
                    }
                    
                    Add(ev, $"Disque {i}", line, $"scan_powershell.sections.Storage.data.{diskPathRoot}[{i-1}]");
                    i++;
                }
                
                if (maxTemp.HasValue)
                {
                    var emoji = TempEmoji(maxTemp.Value);
                    Add(ev, "TempMax Disques", $"{maxTemp.Value:F0}°C {emoji}", "sensors_csharp.disks[*].tempC");
                }
            }
            else
            {
                var diskCount = GetInt(storageData, "physicalDiskCount") ?? GetInt(storageData, "diskCount");
                if (diskCount.HasValue && diskCount.Value > 0)
                    Add(ev, "Disques physiques", diskCount.Value.ToString(), "scan_powershell.sections.Storage.data.physicalDiskCount");
                else
                    AddUnknown(ev, "Disques physiques", "Storage.data.physicalDisks absent");
            }

            // 4. Santé SMART
            var smartData = GetSectionData(root, "SmartDetails");
            if (smartData.HasValue)
            {
                var healthStatus = GetString(smartData, "overallHealth") ?? GetString(smartData, "status") ?? GetString(smartData, "health");
                if (!string.IsNullOrEmpty(healthStatus))
                {
                    var icon = healthStatus.ToLower() switch
                    {
                        "ok" or "healthy" or "good" or "passed" => "✅",
                        "caution" or "warning" => "⚠️",
                        "bad" or "failed" or "failing" => "❌",
                        _ => "❓"
                    };
                    Add(ev, "Santé SMART", $"{icon} {healthStatus}", "scan_powershell.sections.SmartDetails.data.overallHealth");
                }
            }
            else if (sensors?.Disks?.Count > 0)
            {
                Add(ev, "Santé SMART", "Capteurs C# détectés", "sensors_csharp.disks");
            }
            else
            {
                AddUnknown(ev, "Santé SMART", "SmartDetails absent");
            }

            // 5. TOUTES les partitions (obligatoire selon cahier des charges)
            if (storageData.HasValue && storageData.Value.TryGetProperty("volumes", out var volumes) && 
                volumes.ValueKind == JsonValueKind.Array)
            {
                var volList = new List<string>();
                foreach (var vol in volumes.EnumerateArray())
                {
                    var letter = GetString(vol, "driveLetter") ?? GetString(vol, "letter") ?? "";
                    var freeGB = GetDouble(vol, "freeSpaceGB") ?? GetDouble(vol, "freeGB");
                    var sizeGB = GetDouble(vol, "sizeGB") ?? GetDouble(vol, "totalGB");
                    
                    if (!string.IsNullOrEmpty(letter) && sizeGB.HasValue && sizeGB > 0)
                    {
                        var pct = freeGB.HasValue ? (freeGB.Value / sizeGB.Value * 100) : 0;
                        var alert = pct < 10 ? "⚠️" : pct < 20 ? "⚡" : "✅";
                        var freeStr = freeGB.HasValue ? $"{freeGB.Value:F0}" : "?";
                        volList.Add($"{letter}: {freeStr}/{sizeGB.Value:F0}GB {alert}");
                    }
                }
                if (volList.Count > 0)
                    Add(ev, "Partitions", string.Join(" | ", volList), "scan_powershell.sections.Storage.data.volumes[*]");
            }
            else
            {
                AddUnknown(ev, "Partitions", "Storage.data.volumes absent");
            }

            // 6. Top IO process
            var topIO = GetTopProcesses(root, "io", 3);
            if (topIO.Count > 0)
                Add(ev, "Top processus IO", string.Join(", ", topIO), "process_telemetry.topIo");
            // Optionnel

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region Network - Réseau
        // Champs: AdaptateurActif (pas VMware), Vitesse, IP, Passerelle, DNS, MAC, WiFiRSSI, Latence, Jitter, Perte, Débit, VPN

        private static ExtractionResult ExtractNetwork(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            int expected = 12;
            
            var netData = GetSectionData(root, "Network");
            JsonElement? activeAdapter = null;
            
            // CORRECTION: Sélectionner l'adaptateur actif RÉEL (ignorer VMware/Hyper-V)
            if (netData.HasValue && TryGetPropertyIgnoreCase(netData.Value, "adapters", out var adapters) && 
                adapters.ValueKind == JsonValueKind.Array)
            {
                var excludePatterns = new[] { "vmware", "hyper-v", "virtual", "vmnet", "vethernet", "loopback" };
                
                foreach (var adapter in adapters.EnumerateArray())
                {
                    var name = GetString(adapter, "name")?.ToLower() ?? "";
                    var status = GetString(adapter, "status")?.ToLower() ?? "";
                    
                    var ips = new List<string>();
                    if (TryGetPropertyIgnoreCase(adapter, "ipv4", out var ipv4El))
                    {
                        if (ipv4El.ValueKind == JsonValueKind.String) ips.Add(ipv4El.GetString() ?? "");
                        else if (ipv4El.ValueKind == JsonValueKind.Array)
                            ips.AddRange(ipv4El.EnumerateArray().Select(i => i.GetString() ?? "").Where(i => !string.IsNullOrEmpty(i)));
                    }
                    if (TryGetPropertyIgnoreCase(adapter, "ip", out var ipEl))
                    {
                        if (ipEl.ValueKind == JsonValueKind.String) ips.Add(ipEl.GetString() ?? "");
                        else if (ipEl.ValueKind == JsonValueKind.Array)
                            ips.AddRange(ipEl.EnumerateArray().Select(i => i.GetString() ?? "").Where(i => !string.IsNullOrEmpty(i)));
                    }
                    
                    var ipv4 = ips.FirstOrDefault(i => i.Contains('.') && !i.StartsWith("169.254", StringComparison.OrdinalIgnoreCase));
                    
                    // Ignorer les adaptateurs virtuels
                    if (excludePatterns.Any(p => name.Contains(p))) continue;
                    
                    // Préférer un adaptateur Up avec une IP
                    if ((status == "up" || status == "connected" || string.IsNullOrEmpty(status)) && 
                        !string.IsNullOrEmpty(ipv4))
                    {
                        activeAdapter = adapter;
                        break;
                    }
                    
                    // Fallback: premier adaptateur non-virtuel
                    if (!activeAdapter.HasValue)
                        activeAdapter = adapter;
                }
                
                // Si aucun trouvé, prendre le premier
                if (!activeAdapter.HasValue)
                    activeAdapter = adapters.EnumerateArray().FirstOrDefault();
            }

            if (activeAdapter.HasValue)
            {
                // 1. Adaptateur
                var name = GetString(activeAdapter, "name");
                Add(ev, "Adaptateur", name ?? "Inconnu", "scan_powershell.sections.Network.data.adapters[active].name");

                // 2. Vitesse lien
                var speed = GetString(activeAdapter, "speed") ?? 
                    (GetDouble(activeAdapter, "speedMbps").HasValue ? $"{GetDouble(activeAdapter, "speedMbps"):F0} Mbps" : null);
                if (!string.IsNullOrEmpty(speed))
                    Add(ev, "Vitesse lien", speed, "scan_powershell.sections.Network.data.adapters[active].speed");

                // 3. IP
                string? ip = null;
                if (TryGetPropertyIgnoreCase(activeAdapter.Value, "ipv4", out var ipv4El) && ipv4El.ValueKind == JsonValueKind.String)
                    ip = ipv4El.GetString();
                if (string.IsNullOrEmpty(ip) && TryGetPropertyIgnoreCase(activeAdapter.Value, "ip", out var ipEl))
                {
                    if (ipEl.ValueKind == JsonValueKind.Array)
                        ip = ipEl.EnumerateArray().Select(i => i.GetString()).FirstOrDefault(i => !string.IsNullOrEmpty(i) && i.Contains('.'));
                    else if (ipEl.ValueKind == JsonValueKind.String)
                        ip = ipEl.GetString();
                }
                if (!string.IsNullOrEmpty(ip))
                    Add(ev, "Adresse IP", ip, "scan_powershell.sections.Network.data.adapters[active].ipv4");
                else
                    AddUnknown(ev, "Adresse IP", "ipv4 absent");

                // 4. Passerelle
                string? gateway = null;
                if (TryGetPropertyIgnoreCase(activeAdapter.Value, "gateway", out var gwEl))
                {
                    if (gwEl.ValueKind == JsonValueKind.Array)
                        gateway = gwEl.EnumerateArray().Select(g => g.GetString()).FirstOrDefault(s => !string.IsNullOrEmpty(s));
                    else if (gwEl.ValueKind == JsonValueKind.String)
                        gateway = gwEl.GetString();
                }
                if (!string.IsNullOrEmpty(gateway))
                    Add(ev, "Passerelle", gateway, "scan_powershell.sections.Network.data.adapters[active].gateway");

                // 5. DNS
                string? dns = null;
                if (TryGetPropertyIgnoreCase(activeAdapter.Value, "dns", out var dnsEl) && dnsEl.ValueKind == JsonValueKind.Array)
                {
                    var dnsServers = string.Join(", ", dnsEl.EnumerateArray()
                        .Select(d => d.GetString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Take(2));
                    if (!string.IsNullOrEmpty(dnsServers))
                        Add(ev, "DNS", dnsServers, "scan_powershell.sections.Network.data.adapters[active].dns");
                }
                else if (dnsEl.ValueKind == JsonValueKind.String)
                {
                    dns = dnsEl.GetString();
                    if (!string.IsNullOrEmpty(dns))
                        Add(ev, "DNS", dns, "scan_powershell.sections.Network.data.adapters[active].dns");
                }

                // 6. MAC
                var mac = GetString(activeAdapter, "macAddress") ?? GetString(activeAdapter, "mac");
                if (!string.IsNullOrEmpty(mac))
                    Add(ev, "MAC", mac, "scan_powershell.sections.Network.data.adapters[active].macAddress");

                // 7. WiFi RSSI
                var rssi = GetInt(activeAdapter, "rssi") ?? GetInt(activeAdapter, "signalStrength");
                if (rssi.HasValue)
                {
                    var quality = rssi.Value > -50 ? "Excellent" : rssi.Value > -60 ? "Bon" : rssi.Value > -70 ? "Moyen" : "Faible";
                    Add(ev, "WiFi Signal", $"{rssi.Value} dBm ({quality})", "scan_powershell.sections.Network.data.adapters[active].rssi");
                }
            }
            else
            {
                AddUnknown(ev, "Adaptateur", "Network.data.adapters absent");
            }

            // === C#: network_diagnostics ===
            var netDiag = GetNestedElement(root, "network_diagnostics");
            // #region agent log
            if (netDiag.HasValue)
            {
                var netDiagProps = new List<string>();
                foreach (var prop in netDiag.Value.EnumerateObject()) netDiagProps.Add(prop.Name);
                DebugLog("B", "ExtractNetwork:netDiag", "network_diagnostics properties", new { netDiagProps });
            }
            else
            {
                DebugLog("B", "ExtractNetwork:netDiag", "network_diagnostics is NULL", null);
            }
            // #endregion
            if (netDiag.HasValue)
            {
                // 8. Latence - try multiple property name variations
                var latency = GetDouble(netDiag, "latencyMs") 
                    ?? GetDouble(netDiag, "pingMs")
                    ?? GetDouble(netDiag, "OverallLatencyMsP50")
                    ?? GetDouble(netDiag, "overallLatencyMsP50")
                    ?? GetDouble(netDiag, "LatencyMsP50");
                if (latency.HasValue)
                {
                    var status = latency > 100 ? " ⚠️ Élevée" : latency > 50 ? " ⚡" : " ✅";
                    Add(ev, "Latence (ping)", $"{latency.Value:F0} ms{status}", "network_diagnostics.latencyMs");
                }

                // 9. Jitter
                var jitter = GetDouble(netDiag, "jitterMs") 
                    ?? GetDouble(netDiag, "OverallJitterMsP95")
                    ?? GetDouble(netDiag, "overallJitterMsP95")
                    ?? GetDouble(netDiag, "JitterMsP95");
                if (jitter.HasValue)
                    Add(ev, "Gigue", $"{jitter.Value:F1} ms", "network_diagnostics.jitterMs");

                // 10. Perte paquets
                var loss = GetDouble(netDiag, "packetLossPercent") 
                    ?? GetDouble(netDiag, "OverallLossPercent")
                    ?? GetDouble(netDiag, "overallLossPercent");
                if (loss.HasValue)
                {
                    var status = loss > 1 ? " ⚠️" : " ✅";
                    Add(ev, "Perte paquets", $"{loss.Value:F1}%{status}", "network_diagnostics.packetLossPercent");
                }

                // 11. Débit FAI
                var download = GetDouble(netDiag, "downloadMbps");
                var upload = GetDouble(netDiag, "uploadMbps");
                var throughput = GetNestedElement(netDiag.Value, "Throughput");
                if (throughput.HasValue)
                {
                    download ??= GetDouble(throughput, "DownloadMbpsMedian") ?? GetDouble(throughput, "DownloadMbps");
                    upload ??= GetDouble(throughput, "UploadMbpsMedian") ?? GetDouble(throughput, "UploadMbps");
                }
                if (download.HasValue || upload.HasValue)
                {
                    var dlStr = download.HasValue ? $"↓{download.Value:F1}" : "?";
                    var ulStr = upload.HasValue ? $"↑{upload.Value:F1}" : "?";
                    Add(ev, "Débit FAI", $"{dlStr} / {ulStr} Mbps", "network_diagnostics.downloadMbps/uploadMbps");
                }

                // 12. VPN détecté
                var vpn = GetBool(netDiag, "vpnDetected");
                AddYesNo(ev, "VPN détecté", vpn, "network_diagnostics.vpnDetected");
            }
            else
            {
                AddUnknown(ev, "Diagnostics réseau", "network_diagnostics absent");
            }

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region SystemStability - Stabilité système
        // Champs: BSOD, WHEA, KernelPower, CrashesApps, ServicesFailed, SFC, DISM, RestorePoints

        private static ExtractionResult ExtractSystemStability(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            int expected = 8;
            
            var signals = GetDiagnosticSignals(root);
            
            // 1. BSOD (count + codes)
            if (signals.HasValue)
            {
                var bsodCount = GetSignalIntAny(signals, new[] { "bsod_minidump", "bsod", "driverStability" }, "count", "BugcheckCount30d", "BugcheckCount7d", "BugcheckCount");
                var bsodCodes = GetSignalString(signals.Value, "bsod_minidump", "codes");
                if (bsodCount.HasValue)
                {
                    var info = bsodCount == 0 ? "✅ Aucun" : $"⚠️ {bsodCount} crash(es)";
                    if (bsodCount > 0 && !string.IsNullOrEmpty(bsodCodes) && bsodCodes != "[]")
                        info += $" - Codes: {bsodCodes}";
                    Add(ev, "BSOD", info, "diagnostic_signals.bsod_minidump");
                }
                else
                {
                    AddUnknown(ev, "BSOD", "signal bsod_minidump absent");
                }

                // 2. WHEA
                var wheaCount = GetSignalIntAny(signals, new[] { "whea_errors", "whea", "WHEA" }, "count", "Last30dCount", "Last7dCount", "FatalCount");
                if (wheaCount.HasValue)
                    Add(ev, "Erreurs WHEA", wheaCount == 0 ? "✅ Aucune" : $"⚠️ {wheaCount} (30 jours)", "diagnostic_signals.whea_errors");
                else
                    AddUnknown(ev, "Erreurs WHEA", "signal absent");

                // 3. Kernel-Power
                var kpCount = GetSignalIntAny(signals, new[] { "kernel_power", "kernelPower", "driverStability" }, "count", "KernelPower41Count30d", "KernelPower41Count7d");
                if (kpCount.HasValue)
                    Add(ev, "Kernel-Power", kpCount == 0 ? "✅ Aucun" : $"⚠️ {kpCount} événement(s)", "diagnostic_signals.kernel_power");
                else
                    AddUnknown(ev, "Kernel-Power", "signal absent");
            }
            else
            {
                var minidump = GetSectionData(root, "MinidumpAnalysis");
                var eventLogs = GetSectionData(root, "EventLogs");
                
                var mdCount = GetInt(minidump, "minidumpCount") ?? GetInt(minidump, "count");
                var bsodLogCount = GetInt(eventLogs, "bsodCount");
                
                if (mdCount.HasValue)
                    Add(ev, "BSOD", mdCount == 0 ? "✅ Aucun" : $"⚠️ {mdCount} crash(es)", "scan_powershell.sections.MinidumpAnalysis.data.minidumpCount");
                else if (bsodLogCount.HasValue)
                    Add(ev, "BSOD", bsodLogCount == 0 ? "✅ Aucun" : $"⚠️ {bsodLogCount} crash(es)", "scan_powershell.sections.EventLogs.data.bsodCount");
                else
                    AddUnknown(ev, "BSOD", "diagnostic_signals absent");
                
                AddUnknown(ev, "Erreurs WHEA", "diagnostic_signals absent");
                AddUnknown(ev, "Kernel-Power", "diagnostic_signals absent");
            }

            // 4. Crashes applicatifs (top 5)
            var reliData = GetSectionData(root, "ReliabilityHistory");
            if (reliData.HasValue)
            {
                var appCrashes = GetInt(reliData, "appCrashCount") ?? GetInt(reliData, "applicationCrashes");
                if (appCrashes.HasValue)
                    Add(ev, "Crashes applicatifs", appCrashes == 0 ? "✅ Aucun" : $"⚠️ {appCrashes.Value}", 
                        "scan_powershell.sections.ReliabilityHistory.data.appCrashCount");
                
                // Top apps qui crashent
                if (reliData.Value.TryGetProperty("topCrashingApps", out var crashApps) && crashApps.ValueKind == JsonValueKind.Array)
                {
                    var apps = crashApps.EnumerateArray()
                        .Select(a => GetString(a, "name") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(5);
                    var appsStr = string.Join(", ", apps);
                    if (!string.IsNullOrEmpty(appsStr))
                        Add(ev, "Apps instables", appsStr, "scan_powershell.sections.ReliabilityHistory.data.topCrashingApps");
                }
            }

            // 5. Services en échec
            var svcData = GetSectionData(root, "Services");
            if (svcData.HasValue)
            {
                var failedCount = GetInt(svcData, "failedCount") ?? GetInt(svcData, "stoppedCritical");
                if (failedCount.HasValue)
                    Add(ev, "Services en échec", failedCount == 0 ? "✅ Aucun" : $"⚠️ {failedCount.Value}", 
                        "scan_powershell.sections.Services.data.failedCount");
                
                if (svcData.Value.TryGetProperty("failedServices", out var failed) && failed.ValueKind == JsonValueKind.Array)
                {
                    var names = failed.EnumerateArray()
                        .Select(f => GetString(f, "name") ?? GetString(f, "displayName") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(5);
                    var namesStr = string.Join(", ", names);
                    if (!string.IsNullOrEmpty(namesStr))
                        Add(ev, "Services problèmes", namesStr, "scan_powershell.sections.Services.data.failedServices");
                }
            }

            // 6-7. SFC / DISM
            var intData = GetSectionData(root, "SystemIntegrity");
            if (intData.HasValue)
            {
                var sfcStatus = GetString(intData, "sfcStatus") ?? GetString(intData, "sfc");
                if (!string.IsNullOrEmpty(sfcStatus))
                {
                    var icon = sfcStatus.ToLower().Contains("ok") || sfcStatus.ToLower().Contains("clean") || 
                               sfcStatus.ToLower().Contains("no integrity") ? "✅" : "⚠️";
                    Add(ev, "SFC", $"{icon} {sfcStatus}", "scan_powershell.sections.SystemIntegrity.data.sfcStatus");
                }
                
                var dismStatus = GetString(intData, "dismStatus") ?? GetString(intData, "dism");
                if (!string.IsNullOrEmpty(dismStatus))
                {
                    var icon = dismStatus.ToLower().Contains("ok") || dismStatus.ToLower().Contains("healthy") ? "✅" : "⚠️";
                    Add(ev, "DISM", $"{icon} {dismStatus}", "scan_powershell.sections.SystemIntegrity.data.dismStatus");
                }
            }

            // 8. Points de restauration
            var rpData = GetSectionData(root, "RestorePoints");
            if (rpData.HasValue)
            {
                var rpCount = GetInt(rpData, "count") ?? GetInt(rpData, "restorePointCount");
                if (rpCount.HasValue)
                    Add(ev, "Points de restauration", rpCount.Value.ToString(), "scan_powershell.sections.RestorePoints.data.count");
            }

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region Drivers - Pilotes
        // Champs: Total, NonSignés, Erreurs, Obsolètes, TableauPilotesCritiques

        private static ExtractionResult ExtractDrivers(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            int expected = 6;
            
            // === C#: driver_inventory ===
            var driverInv = GetNestedElement(root, "driver_inventory");
            
            if (driverInv.HasValue)
            {
                // 1. Total
                var total = GetInt(driverInv, "totalCount");
                if (total.HasValue)
                    Add(ev, "Pilotes détectés", total.Value.ToString(), "driver_inventory.totalCount");

                // 2. Non signés
                var unsigned = GetInt(driverInv, "unsignedCount");
                if (unsigned.HasValue)
                    Add(ev, "Non signés", unsigned == 0 ? "✅ Aucun" : $"⚠️ {unsigned.Value}", "driver_inventory.unsignedCount");

                // 3. Périphériques en erreur
                var problems = GetInt(driverInv, "problemCount");
                if (problems.HasValue)
                    Add(ev, "Périph. en erreur", problems == 0 ? "✅ Aucun" : $"⚠️ {problems.Value}", "driver_inventory.problemCount");

                // 4. Obsolètes
                var outdated = GetInt(driverInv, "outdatedCount");
                if (outdated.HasValue)
                    Add(ev, "Pilotes obsolètes", outdated == 0 ? "✅ Aucun" : $"⚠️ {outdated.Value}", "driver_inventory.outdatedCount");

                // 5. Tableau pilotes critiques (GPU, NET, AUDIO, STORAGE)
                if (driverInv.Value.TryGetProperty("drivers", out var drivers) && drivers.ValueKind == JsonValueKind.Array)
                {
                    var criticalClasses = new[] { "DISPLAY", "NET", "MEDIA", "HDC", "SCSIADAPTER", "BLUETOOTH", "AUDIO", "SOUND" };
                    var criticalList = new List<string>();
                    
                    foreach (var driver in drivers.EnumerateArray())
                    {
                        var cls = GetString(driver, "deviceClass")?.ToUpper() ?? "";
                        if (!criticalClasses.Any(c => cls.Contains(c))) continue;
                        
                        var name = GetString(driver, "deviceName") ?? "";
                        var version = GetString(driver, "driverVersion") ?? "?";
                        var date = GetString(driver, "driverDate") ?? "";
                        var provider = GetString(driver, "provider") ?? "";
                        var signed = GetBool(driver, "isSigned");
                        
                        var signedStr = signed.HasValue ? (signed.Value ? "✅" : "⚠️") : "";
                        var shortDate = !string.IsNullOrEmpty(date) && date.Length >= 10 ? date.Substring(0, 10) : date;
                        
                        criticalList.Add($"{cls}: {name.Trim()} v{version} {signedStr}");
                        
                        if (criticalList.Count >= 5) break;
                    }
                    
                    if (criticalList.Count > 0)
                    {
                        for (int i = 0; i < criticalList.Count; i++)
                        {
                            Add(ev, $"Pilote {i+1}", criticalList[i], "driver_inventory.drivers[*]");
                        }
                    }
                }
            }
            else
            {
                // Fallback: PS DevicesDrivers
                var devData = GetSectionData(root, "DevicesDrivers");
                if (devData.HasValue)
                {
                    var problemDevices = GetInt(devData, "problemDeviceCount") ?? GetInt(devData, "ProblemDeviceCount");
                    if (problemDevices.HasValue)
                        Add(ev, "Périph. en erreur", problemDevices == 0 ? "✅ Aucun" : $"⚠️ {problemDevices.Value}", 
                            "scan_powershell.sections.DevicesDrivers.data.problemDeviceCount");
                }
                else
                {
                    AddUnknown(ev, "Pilotes", "driver_inventory et DevicesDrivers absents");
                }
            }

            // Audio
            var audioData = GetSectionData(root, "Audio");
            if (audioData.HasValue)
            {
                var audioCount = GetInt(audioData, "deviceCount") ?? GetInt(audioData, "DeviceCount");
                if (audioCount.HasValue)
                    Add(ev, "Périph. audio", audioCount.Value.ToString(), "scan_powershell.sections.Audio.data.deviceCount");
            }

            // Printers
            var printData = GetSectionData(root, "Printers");
            if (printData.HasValue)
            {
                var printerCount = GetInt(printData, "printerCount") ?? GetInt(printData, "PrinterCount");
                if (printerCount.HasValue)
                    Add(ev, "Imprimantes", printerCount.Value.ToString(), "scan_powershell.sections.Printers.data.printerCount");
            }

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region Applications
        // Champs: TotalInstallées, Récentes, Démarrage, TopCPU, TopRAM

        private static ExtractionResult ExtractApplications(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            int expected = 5;
            
            // === PS: InstalledApplications ===
            var appData = GetSectionData(root, "InstalledApplications");
            if (appData.HasValue)
            {
                // 1. Total installées
                var appCount = GetInt(appData, "applicationCount") ?? GetInt(appData, "count") ?? GetInt(appData, "totalCount");
                if (appCount.HasValue)
                    Add(ev, "Apps installées", appCount.Value.ToString(), "scan_powershell.sections.InstalledApplications.data.count");
                else
                    AddUnknown(ev, "Apps installées", "count absent");
                
                // 2. Récentes
                if (appData.Value.TryGetProperty("recentInstalls", out var recent) && recent.ValueKind == JsonValueKind.Array)
                {
                    var recentList = recent.EnumerateArray()
                        .Select(a => GetString(a, "name") ?? GetString(a, "displayName") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(10);
                    var recentStr = string.Join(", ", recentList);
                    if (!string.IsNullOrEmpty(recentStr))
                        Add(ev, "Installées récemment", recentStr, "scan_powershell.sections.InstalledApplications.data.recentInstalls");
                }
                else if (appData.Value.TryGetProperty("applications", out var apps) && apps.ValueKind == JsonValueKind.Array)
                {
                    // Fallback: top 5 apps récentes par date
                    var appsList = apps.EnumerateArray()
                        .Select(a => new { 
                            Name = GetString(a, "name") ?? GetString(a, "displayName") ?? "",
                            Date = GetString(a, "installDate") ?? ""
                        })
                        .Where(a => !string.IsNullOrEmpty(a.Name))
                        .OrderByDescending(a => a.Date)
                        .Take(5)
                        .Select(a => a.Name);
                    var appsStr = string.Join(", ", appsList);
                    if (!string.IsNullOrEmpty(appsStr))
                        Add(ev, "Apps récentes", appsStr, "scan_powershell.sections.InstalledApplications.data.applications");
                }
            }
            else
            {
                AddUnknown(ev, "Apps installées", "InstalledApplications absent");
            }

            // === PS: StartupPrograms ===
            var startupData = GetSectionData(root, "StartupPrograms");
            if (startupData.HasValue)
            {
                // 3. Démarrage
                var startupCount = GetInt(startupData, "programCount") ?? GetInt(startupData, "count");
                if (startupCount.HasValue)
                    Add(ev, "Programmes démarrage", startupCount.Value.ToString(), "scan_powershell.sections.StartupPrograms.data.count");
                
                if (startupData.Value.TryGetProperty("programs", out var progs) && progs.ValueKind == JsonValueKind.Array)
                {
                    var heavyStartup = progs.EnumerateArray()
                        .Where(p => GetString(p, "impact")?.ToLower() == "high")
                        .Select(p => GetString(p, "name") ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(5);
                    var heavyStr = string.Join(", ", heavyStartup);
                    if (!string.IsNullOrEmpty(heavyStr))
                        Add(ev, "Démarrage lourd", $"⚠️ {heavyStr}", "scan_powershell.sections.StartupPrograms.data.programs[impact=high]");
                }
            }
            else
            {
                AddUnknown(ev, "Programmes démarrage", "StartupPrograms absent");
            }

            // 4-5. Top CPU/RAM
            var topCpu = GetTopProcesses(root, "cpu", 5);
            if (topCpu.Count > 0)
                Add(ev, "Top CPU", string.Join(", ", topCpu), "process_telemetry.topCpu");
            
            var topMem = GetTopProcesses(root, "memory", 5);
            if (topMem.Count > 0)
                Add(ev, "Top RAM", string.Join(", ", topMem), "process_telemetry.topMemory");

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region Performance
        // Champs: CPU%, RAM%, DiskIO, NetworkIO, Bottlenecks, Températures, TopProcesses

        private static ExtractionResult ExtractPerformance(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            int expected = 8;
            
            // 1. CPU % actuel
            var cpuData = GetSectionData(root, "CPU");
            if (cpuData.HasValue)
            {
                var first = GetFirstObject(cpuData, "cpus", "cpuList");
                if (first.HasValue)
                {
                    var load = GetDouble(first, "currentLoad") ?? GetDouble(first, "load");
                    if (load.HasValue)
                    {
                        var status = load > 90 ? "🔥 Saturé" : load > 70 ? "⚠️ Élevé" : "✅ Normal";
                        Add(ev, "CPU", $"{load.Value:F0}% {status}", "scan_powershell.sections.CPU.data.cpus[0].currentLoad");
                    }
                }
            }

            // 2. RAM % actuel
            var memData = GetSectionData(root, "Memory");
            if (memData.HasValue)
            {
                var totalGB = GetDouble(memData, "totalGB");
                var availGB = GetDouble(memData, "availableGB") ?? GetDouble(memData, "freeGB");
                if (totalGB.HasValue && totalGB > 0 && availGB.HasValue)
                {
                    var pct = ((totalGB.Value - availGB.Value) / totalGB.Value) * 100;
                    var status = pct > 90 ? "🔥 Saturée" : pct > 80 ? "⚠️ Élevée" : "✅ Normal";
                    Add(ev, "RAM", $"{pct:F0}% {status}", "scan_powershell.sections.Memory.data (calculé)");
                }
            }

            // 3. Disk IO
            var telemetry = GetNestedElement(root, "process_telemetry");
            if (telemetry.HasValue)
            {
                var readMBps = GetDouble(telemetry, "diskReadMBps");
                var writeMBps = GetDouble(telemetry, "diskWriteMBps");
                if (readMBps.HasValue || writeMBps.HasValue)
                {
                    var readStr = readMBps.HasValue ? $"R:{readMBps.Value:F1}" : "R:?";
                    var writeStr = writeMBps.HasValue ? $"W:{writeMBps.Value:F1}" : "W:?";
                    Add(ev, "Disk IO", $"{readStr} / {writeStr} MB/s", "process_telemetry.disk*MBps");
                }
            }

            // 4. Network IO
            var netDiag = GetNestedElement(root, "network_diagnostics");
            if (netDiag.HasValue)
            {
                var download = GetDouble(netDiag, "downloadMbps");
                var upload = GetDouble(netDiag, "uploadMbps");
                var throughput = GetNestedElement(netDiag.Value, "Throughput");
                if (throughput.HasValue)
                {
                    download ??= GetDouble(throughput, "DownloadMbpsMedian") ?? GetDouble(throughput, "DownloadMbps");
                    upload ??= GetDouble(throughput, "UploadMbpsMedian") ?? GetDouble(throughput, "UploadMbps");
                }
                if (download.HasValue || upload.HasValue)
                {
                    var dlStr = download.HasValue ? $"↓{download.Value:F1}" : "↓?";
                    var ulStr = upload.HasValue ? $"↑{upload.Value:F1}" : "↑?";
                    Add(ev, "Réseau", $"{dlStr} / {ulStr} Mbps", "network_diagnostics.*Mbps");
                }
            }

            // 5. Bottlenecks
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var bottlenecks = new List<string>();
                var cpuThrottle = GetSignalResult(signals.Value, "cpu_throttle", "cpuThrottle", "CpuThrottle");
                if (cpuThrottle.HasValue)
                {
                    var throttleVal = GetSignalValue(cpuThrottle.Value);
                    var detected = throttleVal.HasValue 
                        ? (GetBool(throttleVal, "detected") ?? GetBool(throttleVal, "ThrottleSuspected") ?? (bool?)null)
                        : null;
                    var events = throttleVal.HasValue 
                        ? (GetInt(throttleVal, "ThrottlingEventCount30d") ?? GetInt(throttleVal, "ThermalThrottleCount") ?? GetInt(throttleVal, "PowerLimitCount"))
                        : null;
                    if (detected == true || (events.HasValue && events.Value > 0))
                        bottlenecks.Add("CPU bound");
                }
                
                var memPressure = GetSignalResult(signals.Value, "ram_pressure", "memoryPressure");
                if (memPressure.HasValue)
                {
                    var memVal = GetSignalValue(memPressure.Value);
                    var sustained = memVal.HasValue ? GetInt(memVal, "SustainedSeconds") : null;
                    var hardFaults = memVal.HasValue ? GetDouble(memVal, "HardFaultsP95") : null;
                    if ((sustained.HasValue && sustained.Value > 0) || (hardFaults.HasValue && hardFaults.Value > 1000))
                        bottlenecks.Add("RAM pressure");
                }
                
                var diskSat = GetSignalResult(signals.Value, "disk_saturation", "storageLatency");
                if (diskSat.HasValue)
                {
                    var diskVal = GetSignalValue(diskSat.Value);
                    var readP95 = diskVal.HasValue ? GetDouble(diskVal, "ReadLatencyMsP95") : null;
                    var writeP95 = diskVal.HasValue ? GetDouble(diskVal, "WriteLatencyMsP95") : null;
                    var queueP95 = diskVal.HasValue ? GetDouble(diskVal, "QueueDepthP95") : null;
                    if ((readP95.HasValue && readP95.Value > 50) || (writeP95.HasValue && writeP95.Value > 50) || (queueP95.HasValue && queueP95.Value > 5))
                        bottlenecks.Add("Disk saturation");
                }
                
                var netSat = GetSignalResult(signals.Value, "network_saturation", "networkQuality");
                if (netSat.HasValue)
                {
                    var netVal = GetSignalValue(netSat.Value);
                    var loss = netVal.HasValue ? (GetDouble(netVal, "LossPercent") ?? GetDouble(netVal, "PacketLossPercent")) : null;
                    if (loss.HasValue && loss.Value > 1)
                        bottlenecks.Add("Network saturation");
                }
                
                Add(ev, "Bottlenecks", bottlenecks.Count > 0 ? $"⚠️ {string.Join(", ", bottlenecks)}" : "✅ Aucun détecté", 
                    "diagnostic_signals.*");
            }

            // 6. Températures (résumé)
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
                    Add(ev, "Températures", string.Join(" | ", temps), "sensors_csharp.*TempC");
            }

            // 7-8. Top processes
            var topCpu = GetTopProcesses(root, "cpu", 5);
            if (topCpu.Count > 0)
                Add(ev, "Top CPU", string.Join(", ", topCpu), "process_telemetry.topCpu");
            
            var topMem = GetTopProcesses(root, "memory", 5);
            if (topMem.Count > 0)
                Add(ev, "Top RAM", string.Join(", ", topMem), "process_telemetry.topMemory");
            
            var topIO = GetTopProcesses(root, "io", 3);
            if (topIO.Count > 0)
                Add(ev, "Top IO", string.Join(", ", topIO), "process_telemetry.topIo");

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region Security - Sécurité
        // Champs: Antivirus, Firewall, SecureBoot, BitLocker(OUI!), UAC, RDP, SMBv1, DernierPatch, Admins

        private static ExtractionResult ExtractSecurity(JsonElement root)
        {
            var ev = new Dictionary<string, string>();
            int expected = 9;
            
            var secData = GetSectionData(root, "Security");
            var machineId = GetSectionData(root, "MachineIdentity");
            // #region agent log
            if (secData.HasValue)
            {
                var secProps = new List<string>();
                foreach (var prop in secData.Value.EnumerateObject()) secProps.Add(prop.Name);
                DebugLog("C", "ExtractSecurity:entry", "Security section properties", new { secProps });
                // Check for antivirusProducts array
                if (secData.Value.TryGetProperty("antivirusProducts", out var avProducts))
                {
                    DebugLog("C", "ExtractSecurity:av", "antivirusProducts found", new { kind = avProducts.ValueKind.ToString() });
                }
                // Check for firewall structure
                if (secData.Value.TryGetProperty("firewall", out var fw))
                {
                    DebugLog("C", "ExtractSecurity:fw", "firewall found", new { kind = fw.ValueKind.ToString() });
                    if (fw.ValueKind == JsonValueKind.Object)
                    {
                        var fwProps = new List<string>();
                        foreach (var p in fw.EnumerateObject()) fwProps.Add(p.Name);
                        DebugLog("C", "ExtractSecurity:fw", "firewall properties", new { fwProps });
                    }
                }
            }
            else
            {
                DebugLog("C", "ExtractSecurity:entry", "Security section is NULL", null);
            }
            // #endregion
            
            // 1. Antivirus - try multiple sources
            string? avName = null;
            string? avStatus = null;
            if (secData.HasValue && TryGetPropertyIgnoreCase(secData.Value, "antivirusProducts", out var avProductsArr))
            {
                if (avProductsArr.ValueKind == JsonValueKind.Array)
                {
                    var firstAv = avProductsArr.EnumerateArray().FirstOrDefault();
                    if (firstAv.ValueKind == JsonValueKind.Object)
                    {
                        avName = GetString(firstAv, "displayName") ?? GetString(firstAv, "name");
                        avStatus = GetString(firstAv, "productState") ?? GetString(firstAv, "status");
                    }
                }
                else if (avProductsArr.ValueKind == JsonValueKind.Object)
                {
                    avName = GetString(avProductsArr, "displayName") ?? GetString(avProductsArr, "name");
                    avStatus = GetString(avProductsArr, "productState") ?? GetString(avProductsArr, "status");
                }
                else if (avProductsArr.ValueKind == JsonValueKind.String)
                {
                    avName = avProductsArr.GetString();
                }
            }
            // Fallback to direct properties
            if (string.IsNullOrEmpty(avName))
            {
                avName = GetString(secData, "antivirusName") ?? GetString(secData, "avName");
                avStatus = GetString(secData, "antivirusStatus") ?? GetString(secData, "avStatus");
            }
            if (string.IsNullOrEmpty(avName))
            {
                avName = GetSnapshotString(root, "security", "antivirusName");
                avStatus = GetSnapshotString(root, "security", "antivirusStatus");
            }
            if (!string.IsNullOrEmpty(avName))
            {
                var icon = avStatus?.ToLower() switch
                {
                    "enabled" or "on" or "actif" or "à jour" => "✅",
                    "disabled" or "off" => "⚠️",
                    _ => ""
                };
                Add(ev, "Antivirus", $"{icon} {avName}" + (!string.IsNullOrEmpty(avStatus) ? $" ({avStatus})" : ""), 
                    "scan_powershell.sections.Security.data.antivirusName");
            }
            else
            {
                AddUnknown(ev, "Antivirus", "données AV absentes");
            }

            // 2. Pare-feu - handle multiple structures
            bool? fwEnabled = GetBool(secData, "firewallEnabled") ?? GetBool(secData, "firewall");
            var fwProfiles = GetString(secData, "firewallProfiles");
            var enabledProfiles = new List<string>();
            var disabledProfiles = new List<string>();
            
            if (secData.HasValue && TryGetPropertyIgnoreCase(secData.Value, "firewall", out var fwObj))
            {
                if (!fwEnabled.HasValue)
                {
                    if (fwObj.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(fwObj, "value__", out var fwVal))
                    {
                        fwEnabled = fwVal.ValueKind == JsonValueKind.Number ? fwVal.GetInt32() == 1 : null;
                    }
                    else if (fwObj.ValueKind == JsonValueKind.Number)
                    {
                        fwEnabled = fwObj.GetInt32() == 1;
                    }
                }
                
                if (fwObj.ValueKind == JsonValueKind.Object)
                {
                    var profiles = new[] { ("Domain", "Domaine"), ("Private", "Privé"), ("Public", "Public") };
                    foreach (var (key, label) in profiles)
                    {
                        if (TryGetPropertyIgnoreCase(fwObj, key, out var profileVal))
                        {
                            bool? enabled = null;
                            if (profileVal.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(profileVal, "value__", out var val))
                                enabled = val.ValueKind == JsonValueKind.Number ? val.GetInt32() == 1 : (bool?)null;
                            else if (profileVal.ValueKind == JsonValueKind.Number)
                                enabled = profileVal.GetInt32() == 1;
                            else if (profileVal.ValueKind == JsonValueKind.True || profileVal.ValueKind == JsonValueKind.False)
                                enabled = profileVal.GetBoolean();
                            
                            if (enabled == true) enabledProfiles.Add(label);
                            else if (enabled == false) disabledProfiles.Add(label);
                        }
                    }
                }
            }
            
            if (fwEnabled.HasValue || enabledProfiles.Count > 0 || disabledProfiles.Count > 0)
            {
                string status;
                if (enabledProfiles.Count > 0 && disabledProfiles.Count > 0)
                {
                    status = $"⚠️ Partiel ({string.Join(", ", enabledProfiles)})";
                }
                else if (fwEnabled == true || enabledProfiles.Count > 0)
                {
                    status = "✅ Activé";
                    if (enabledProfiles.Count > 0) status += $" ({string.Join(", ", enabledProfiles)})";
                }
                else
                {
                    status = "⚠️ Désactivé";
                    if (disabledProfiles.Count > 0) status += $" ({string.Join(", ", disabledProfiles)})";
                }
                
                if (!string.IsNullOrEmpty(fwProfiles) && enabledProfiles.Count == 0 && disabledProfiles.Count == 0)
                    status += $" ({fwProfiles})";
                
                Add(ev, "Pare-feu", status, "scan_powershell.sections.Security.data.firewallEnabled");
            }
            else
            {
                AddUnknown(ev, "Pare-feu", "firewallEnabled absent");
            }

            // 3. Secure Boot (Oui/Non)
            var secureBoot = GetBool(secData, "secureBootEnabled") 
                ?? GetBool(machineId, "secureBoot") 
                ?? GetBool(machineId, "SecureBoot")
                ?? GetSnapshotBool(root, "security", "secureBootEnabled");
            AddYesNo(ev, "Secure Boot", secureBoot, "scan_powershell.sections.Security.data.secureBootEnabled");

            // 4. BitLocker (OUI/NON - OBLIGATOIRE, pas "—")
            bool? bitlocker = GetBool(secData, "bitlockerEnabled") 
                ?? GetBool(secData, "bitLocker") 
                ?? GetBool(secData, "BitLocker");
            if (!bitlocker.HasValue)
            {
                var statusStr = GetString(secData, "bitlockerStatus") ?? GetSnapshotString(root, "security", "bitlockerStatus");
                if (!string.IsNullOrEmpty(statusStr))
                {
                    var s = statusStr.ToLowerInvariant();
                    if (s.Contains("on") || s.Contains("enabled") || s.Contains("active") || s.Contains("oui"))
                        bitlocker = true;
                    else if (s.Contains("off") || s.Contains("disabled") || s.Contains("false") || s.Contains("non"))
                        bitlocker = false;
                }
            }
            if (bitlocker.HasValue)
                AddYesNo(ev, "BitLocker", bitlocker, "scan_powershell.sections.Security.data.bitlockerEnabled");
            else
                AddNotDetectable(ev, "BitLocker", "collecte non implémentée", "scan_powershell.sections.Security.data.bitlockerEnabled");

            // 5. UAC
            var uac = GetBool(secData, "uacEnabled") ?? GetBool(secData, "UAC");
            AddYesNo(ev, "UAC", uac, "scan_powershell.sections.Security.data.uacEnabled");

            // 6. RDP
            var rdp = GetBool(secData, "rdpEnabled") ?? GetBool(secData, "RDP");
            if (rdp.HasValue)
                Add(ev, "RDP", rdp.Value ? "⚠️ Activé" : "✅ Désactivé", "scan_powershell.sections.Security.data.rdpEnabled");
            else
                AddNotDetectable(ev, "RDP", "rdpEnabled absent", "scan_powershell.sections.Security.data.rdpEnabled");

            // 7. SMBv1
            var smb1 = GetBool(secData, "smbV1Enabled") ?? GetBool(secData, "SMBv1");
            if (smb1.HasValue)
                Add(ev, "SMBv1", smb1.Value ? "⚠️ Activé (risque)" : "✅ Désactivé", "scan_powershell.sections.Security.data.smbV1Enabled");
            else
                AddNotDetectable(ev, "SMBv1", "smbV1Enabled absent", "scan_powershell.sections.Security.data.smbV1Enabled");

            // 8. Dernier patch sécurité
            var updateData = GetSectionData(root, "WindowsUpdate");
            if (updateData.HasValue)
            {
                var lastInstall = GetString(updateData, "lastInstallDate") ?? GetString(updateData, "LastInstalled");
                if (!string.IsNullOrEmpty(lastInstall) && DateTime.TryParse(lastInstall, out var dt))
                {
                    var days = (DateTime.Now - dt).TotalDays;
                    var status = days > 30 ? " ⚠️ >30 jours" : "";
                    Add(ev, "Dernier patch", $"{dt:d MMM yyyy}{status}", "scan_powershell.sections.WindowsUpdate.data.lastInstallDate");
                }
            }

            // 9. Admins locaux
            var userData = GetSectionData(root, "UserProfiles");
            if (userData.HasValue)
            {
                var adminCount = GetInt(userData, "adminCount") ?? GetInt(userData, "localAdminCount");
                if (adminCount.HasValue)
                {
                    var status = adminCount > 2 ? " ⚠️" : "";
                    Add(ev, "Admins locaux", $"{adminCount.Value} compte(s){status}", "scan_powershell.sections.UserProfiles.data.adminCount");
                }
            }

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region Power - Alimentation
        // Champs: Batterie, PlanAlimentation, ModePerf, KernelPower, PowerThrottling

        private static ExtractionResult ExtractPower(JsonElement root, HardwareSensorsResult? sensors)
        {
            var ev = new Dictionary<string, string>();
            int expected = 5;
            
            // === PS: Battery ===
            var batteryData = GetSectionData(root, "Battery");
            if (batteryData.HasValue)
            {
                var hasBattery = GetBool(batteryData, "hasBattery") ?? GetBool(batteryData, "present");
                
                if (hasBattery == true)
                {
                    // Niveau
                    var charge = GetInt(batteryData, "chargePercent") ?? GetInt(batteryData, "remainingCapacityPercent");
                    if (charge.HasValue)
                    {
                        var status = charge < 20 ? " ⚠️" : "";
                        Add(ev, "Niveau batterie", $"{charge.Value}%{status}", "scan_powershell.sections.Battery.data.chargePercent");
                    }
                    
                    // Santé
                    var health = GetInt(batteryData, "healthPercent") ?? GetInt(batteryData, "designCapacityPercent");
                    if (health.HasValue)
                    {
                        var status = health < 50 ? " ⚠️ Usée" : health < 80 ? " ⚡" : " ✅";
                        Add(ev, "Santé batterie", $"{health.Value}%{status}", "scan_powershell.sections.Battery.data.healthPercent");
                    }
                    
                    // Cycles
                    var cycles = GetInt(batteryData, "cycleCount");
                    if (cycles.HasValue)
                        Add(ev, "Cycles", cycles.Value.ToString(), "scan_powershell.sections.Battery.data.cycleCount");
                    
                    // État
                    var battStatus = GetString(batteryData, "status") ?? GetString(batteryData, "chargingStatus");
                    if (!string.IsNullOrEmpty(battStatus))
                        Add(ev, "État batterie", battStatus, "scan_powershell.sections.Battery.data.status");
                }
                else
                {
                    Add(ev, "Batterie", "Non présente (Desktop)", "scan_powershell.sections.Battery.data.hasBattery=false");
                }
            }

            // === PS: PowerSettings ===
            var powerData = GetSectionData(root, "PowerSettings");
            if (powerData.HasValue)
            {
                var plan = GetString(powerData, "activePlan") ?? GetString(powerData, "powerPlan");
                if (!string.IsNullOrEmpty(plan))
                    Add(ev, "Plan alimentation", plan, "scan_powershell.sections.PowerSettings.data.activePlan");
                
                var mode = GetString(powerData, "performanceMode");
                if (!string.IsNullOrEmpty(mode))
                    Add(ev, "Mode performance", mode, "scan_powershell.sections.PowerSettings.data.performanceMode");
            }

            // === Diagnostic Signals ===
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                // Kernel-Power events (coupures de courant)
                var kpCount = GetSignalIntAny(signals, new[] { "kernel_power", "kernelPower", "driverStability" }, "count", "KernelPower41Count30d", "KernelPower41Count7d");
                if (kpCount.HasValue)
                {
                    Add(ev, "Kernel-Power", kpCount == 0 ? "✅ Aucun" : $"⚠️ {kpCount} coupure(s)", 
                        "diagnostic_signals.kernel_power.count");
                }
                
                // Power throttling
                var powerThrottle = GetSignalResult(signals.Value, "power_throttle", "powerThrottle");
                if (powerThrottle.HasValue)
                {
                    var throttleValue = GetSignalValue(powerThrottle.Value);
                    var detected = throttleValue.HasValue ? (GetBool(throttleValue, "detected") ?? GetBool(throttleValue, "ThrottleSuspected")) : null;
                    detected ??= false;
                    Add(ev, "Power throttling", detected ? "⚠️ Oui" : "✅ Non", "diagnostic_signals.power_throttle");
                }
            }

            return new ExtractionResult { Evidence = ev, ExpectedFields = expected, ActualFields = CountActualFields(ev) };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Ajoute une valeur avec chemin debug optionnel
        /// </summary>
        private static void Add(Dictionary<string, string> ev, string key, string value, string jsonPath)
        {
            if (string.IsNullOrEmpty(value)) return;
            
            if (DebugPathsEnabled)
                ev[key] = $"{value} 📍[{jsonPath}]";
            else
                ev[key] = value;
        }

        /// <summary>
        /// Ajoute "Oui/Non" pour un booléen (jamais "—")
        /// </summary>
        private static void AddYesNo(Dictionary<string, string> ev, string key, bool? value, string jsonPath)
        {
            string display;
            if (value.HasValue)
                display = value.Value ? "✅ Oui" : "❌ Non";
            else
                display = DebugPathsEnabled ? "Inconnu (données absentes)" : "Inconnu";
            
            Add(ev, key, display, jsonPath);
        }

        /// <summary>
        /// Ajoute "Inconnu (raison)" - jamais "—"
        /// </summary>
        private static void AddUnknown(Dictionary<string, string> ev, string key, string reason)
        {
            ev[key] = DebugPathsEnabled ? $"Inconnu ({reason}) 📍[n/a]" : "Inconnu";
        }

        /// <summary>
        /// Ajoute "Non détectable" (avec cause uniquement en debug)
        /// </summary>
        private static void AddNotDetectable(Dictionary<string, string> ev, string key, string reason, string jsonPath)
        {
            var display = DebugPathsEnabled ? $"Non détectable ({reason})" : "Non détectable";
            Add(ev, key, display, jsonPath);
        }

        /// <summary>
        /// Compte les champs réellement remplis (exclut Inconnu et __coverage__)
        /// </summary>
        private static int CountActualFields(Dictionary<string, string> ev)
        {
            return ev.Count(kvp => 
                !kvp.Key.StartsWith("__") && 
                !kvp.Value.StartsWith("Inconnu"));
        }

        private static HardwareSensorsResult? ResolveSensors(JsonElement root, HardwareSensorsResult? sensors)
        {
            if (sensors != null) return sensors;
            
            var sensorsElement = GetNestedElement(root, "sensors_csharp") ?? GetNestedElement(root, "sensorsCsharp");
            if (sensorsElement.HasValue)
            {
                try
                {
                    return JsonSerializer.Deserialize<HardwareSensorsResult>(sensorsElement.Value.GetRawText());
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[Evidence] Erreur désérialisation capteurs: {ex.Message}");
                }
            }
            
            return null;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propName, out JsonElement value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object) return false;
            
            if (element.TryGetProperty(propName, out value))
                return true;
            
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
            
            return false;
        }

        private static JsonElement? GetFirstObject(JsonElement? element, params string[] propNames)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            
            foreach (var name in propNames)
            {
                if (!TryGetPropertyIgnoreCase(element.Value, name, out var prop)) continue;
                
                if (prop.ValueKind == JsonValueKind.Array)
                    return prop.EnumerateArray().FirstOrDefault();
                
                if (prop.ValueKind == JsonValueKind.Object)
                    return prop;
            }
            
            return null;
        }

        private static JsonElement? GetSnapshotMetric(JsonElement root, string group, string key)
        {
            return GetNestedElement(root, "diagnostic_snapshot", "metrics", group, key);
        }

        private static bool? GetSnapshotBool(JsonElement root, string group, string key)
        {
            var metric = GetSnapshotMetric(root, group, key);
            if (!metric.HasValue) return null;
            
            if (TryGetPropertyIgnoreCase(metric.Value, "available", out var available) && 
                available.ValueKind == JsonValueKind.False)
                return null;
            
            if (TryGetPropertyIgnoreCase(metric.Value, "value", out var value))
            {
                if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                    return value.GetBoolean();
                if (value.ValueKind == JsonValueKind.Number)
                    return value.GetInt32() != 0;
                if (value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString()?.ToLowerInvariant();
                    if (s == "true" || s == "yes" || s == "1" || s == "oui") return true;
                    if (s == "false" || s == "no" || s == "0" || s == "non") return false;
                }
            }
            
            return null;
        }

        private static string? GetSnapshotString(JsonElement root, string group, string key)
        {
            var metric = GetSnapshotMetric(root, group, key);
            if (!metric.HasValue) return null;
            
            if (TryGetPropertyIgnoreCase(metric.Value, "available", out var available) && 
                available.ValueKind == JsonValueKind.False)
                return null;
            
            if (TryGetPropertyIgnoreCase(metric.Value, "value", out var value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
            
            return null;
        }

        private static JsonElement? GetSectionData(JsonElement root, string sectionName)
        {
            // Try scan_powershell.sections first
            if (root.TryGetProperty("scan_powershell", out var ps) &&
                ps.TryGetProperty("sections", out var sections) &&
                sections.TryGetProperty(sectionName, out var section))
            {
                return section.TryGetProperty("data", out var data) ? data : section;
            }
            
            // Direct sections access
            if (root.TryGetProperty("sections", out var directSections) &&
                directSections.TryGetProperty(sectionName, out var directSection))
            {
                return directSection.TryGetProperty("data", out var data) ? data : directSection;
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

        private static JsonElement? GetDiagnosticSignals(JsonElement root) =>
            root.TryGetProperty("diagnostic_signals", out var signals) ? signals : null;

        private static JsonElement? GetSignalResult(JsonElement signals, params string[] signalNames)
        {
            foreach (var name in signalNames)
            {
                if (signals.TryGetProperty(name, out var signal))
                    return signal;
            }
            
            foreach (var prop in signals.EnumerateObject())
            {
                foreach (var name in signalNames)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                        return prop.Value;
                }
            }
            
            return null;
        }

        private static JsonElement? GetSignalValue(JsonElement signal)
        {
            if (signal.ValueKind == JsonValueKind.Object && signal.TryGetProperty("value", out var value))
            {
                if (value.ValueKind == JsonValueKind.Null) return null;
                return value;
            }
            
            return signal;
        }

        private static int? GetSignalInt(JsonElement? signals, string signalName, params string[] valueNames)
        {
            if (!signals.HasValue) return null;
            var signal = GetSignalResult(signals.Value, signalName);
            return GetSignalIntFromSignal(signal, valueNames);
        }

        private static int? GetSignalIntAny(JsonElement? signals, string[] signalNames, params string[] valueNames)
        {
            if (!signals.HasValue) return null;
            var signal = GetSignalResult(signals.Value, signalNames);
            return GetSignalIntFromSignal(signal, valueNames);
        }

        private static int? GetSignalIntFromSignal(JsonElement? signal, params string[] valueNames)
        {
            if (!signal.HasValue) return null;
            var signalValue = GetSignalValue(signal.Value);
            if (!signalValue.HasValue) return null;
            
            foreach (var valueName in valueNames)
            {
                var val = GetInt(signalValue, valueName);
                if (val.HasValue) return val;
            }
            
            return null;
        }

        private static string? GetSignalString(JsonElement? signals, string signalName, params string[] valueNames)
        {
            if (!signals.HasValue) return null;
            var signal = GetSignalResult(signals.Value, signalName);
            return GetSignalStringFromSignal(signal, valueNames);
        }

        private static string? GetSignalStringAny(JsonElement? signals, string[] signalNames, params string[] valueNames)
        {
            if (!signals.HasValue) return null;
            var signal = GetSignalResult(signals.Value, signalNames);
            return GetSignalStringFromSignal(signal, valueNames);
        }

        private static string? GetSignalStringFromSignal(JsonElement? signal, params string[] valueNames)
        {
            if (!signal.HasValue) return null;
            var signalValue = GetSignalValue(signal.Value);
            if (!signalValue.HasValue) return null;
            
            foreach (var valueName in valueNames)
            {
                var val = GetString(signalValue, valueName);
                if (!string.IsNullOrEmpty(val)) return val;
            }
            
            return null;
        }

        private static string? GetString(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (TryGetPropertyIgnoreCase(element.Value, propName, out var prop))
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
            return null;
        }

        private static int? GetInt(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (TryGetPropertyIgnoreCase(element.Value, propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32();
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var i)) return i;
            }
            return null;
        }

        private static double? GetDouble(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (TryGetPropertyIgnoreCase(element.Value, propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number) return prop.GetDouble();
                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var d)) return d;
            }
            return null;
        }

        private static bool? GetBool(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (TryGetPropertyIgnoreCase(element.Value, propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True) return true;
                if (prop.ValueKind == JsonValueKind.False) return false;
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var s = prop.GetString()?.ToLower();
                    if (s == "true" || s == "yes" || s == "1" || s == "oui") return true;
                    if (s == "false" || s == "no" || s == "0" || s == "non") return false;
                }
            }
            return null;
        }

        private static JsonElement? GetArray(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (TryGetPropertyIgnoreCase(element.Value, propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
                return prop;
            return null;
        }

        private static List<string> GetTopProcesses(JsonElement root, string metric, int count)
        {
            var result = new List<string>();
            
            var telemetry = GetNestedElement(root, "process_telemetry");
            // #region agent log
            if (telemetry.HasValue)
            {
                var propNames = new List<string>();
                foreach (var prop in telemetry.Value.EnumerateObject()) propNames.Add(prop.Name);
                DebugLog("B", "GetTopProcesses:entry", $"process_telemetry props for metric={metric}", new { propNames });
            }
            else
            {
                DebugLog("B", "GetTopProcesses:entry", "process_telemetry is NULL", new { metric });
            }
            // #endregion
            if (telemetry.HasValue)
            {
                // Extended case variations including TopByXxx pattern
                var arrayName = metric switch
                {
                    "cpu" => "topCpu",
                    "memory" => "topMemory",
                    "io" => "topIo",
                    "network" => "topNetwork",
                    _ => $"top{char.ToUpper(metric[0])}{metric.Substring(1)}"
                };
                
                // Try multiple case variations including PascalCase "TopByXxx" pattern
                var names = new[] { 
                    arrayName, 
                    arrayName.ToLower(), 
                    $"Top{metric}",
                    $"TopBy{char.ToUpper(metric[0])}{metric.Substring(1)}",  // TopByCpu, TopByMemory
                    $"topBy{char.ToUpper(metric[0])}{metric.Substring(1)}"   // topByCpu, topByMemory
                };
                foreach (var name in names)
                {
                    if (TryGetPropertyIgnoreCase(telemetry.Value, name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        result = arr.EnumerateArray()
                            .Select(p => GetString(p, "name") ?? GetString(p, "processName") ?? GetString(p, "Name") ?? "")
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Take(count)
                            .ToList();
                        // #region agent log
                        DebugLog("B", "GetTopProcesses:found", $"Found data at {name}", new { resultCount = result.Count });
                        // #endregion
                        if (result.Count > 0) break;
                    }
                }
            }
            
            // Fallback PS Processes
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
