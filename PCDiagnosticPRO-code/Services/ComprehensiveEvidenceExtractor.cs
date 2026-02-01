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
            var result = domain switch
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
                var machineId = GetSectionData(root, "MachineIdentity");
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
                AddUnknown(ev, "Uptime", "lastBootUpTime absent");
            }

            // 4. Secure Boot (Oui/Non, PAS "—")
            // FIX: Check MachineIdentity.secureBoot as primary source, Security as fallback
            var secData = GetSectionData(root, "Security");
            var machineIdData = GetSectionData(root, "MachineIdentity");
            
            // FIX: Priority order: MachineIdentity.secureBoot (most reliable) > Security.secureBootEnabled
            var secureBoot = GetBool(machineIdData, "secureBoot") ?? 
                             GetBool(secData, "secureBootEnabled") ?? 
                             GetBool(secData, "SecureBootEnabled");
            
            string secureBootSource = machineIdData.HasValue && GetBool(machineIdData, "secureBoot").HasValue 
                ? "MachineIdentity.secureBoot" 
                : "Security.secureBootEnabled";
            AddYesNo(ev, "Secure Boot", secureBoot, secureBootSource);

            // 5. Antivirus actif + état
            // FIX: Handle antivirusProducts as string OR array
            string? avName = null;
            string? avStatus = null;
            
            if (secData.HasValue && secData.Value.TryGetProperty("antivirusProducts", out var avProductsProp))
            {
                // Case 1: String (actual PS output for single AV)
                if (avProductsProp.ValueKind == JsonValueKind.String)
                {
                    avName = avProductsProp.GetString();
                }
                // Case 2: Array (multiple AV products)
                else if (avProductsProp.ValueKind == JsonValueKind.Array)
                {
                    var firstAv = avProductsProp.EnumerateArray().FirstOrDefault();
                    if (firstAv.ValueKind == JsonValueKind.Object)
                    {
                        avName = GetString(firstAv, "displayName") ?? GetString(firstAv, "name");
                        avStatus = GetString(firstAv, "productState") ?? GetString(firstAv, "status");
                    }
                    else if (firstAv.ValueKind == JsonValueKind.String)
                    {
                        avName = firstAv.GetString();
                    }
                }
            }
            
            // Fallback to direct properties
            if (string.IsNullOrEmpty(avName))
            {
                avName = GetString(secData, "antivirusName") ?? GetString(secData, "avName") ?? GetString(secData, "AntivirusName");
            }
            if (string.IsNullOrEmpty(avStatus))
            {
                avStatus = GetString(secData, "antivirusStatus") ?? GetString(secData, "avStatus") ?? GetString(secData, "AntivirusStatus");
            }
            
            // FIX: Check defenderEnabled/defenderRTP as status indicators
            if (string.IsNullOrEmpty(avStatus) && secData.HasValue)
            {
                var defenderEnabled = GetBool(secData, "defenderEnabled");
                var defenderRTP = GetBool(secData, "defenderRTP");
                if (defenderEnabled == true || defenderRTP == true)
                {
                    avStatus = "Actif";
                }
                else if (defenderEnabled == false)
                {
                    avStatus = "Désactivé";
                }
            }
            
            if (!string.IsNullOrEmpty(avName))
            {
                var icon = avStatus?.ToLower() switch
                {
                    "enabled" or "on" or "actif" or "à jour" => "✅",
                    "disabled" or "off" or "inactif" or "désactivé" => "⚠️",
                    _ => "✅" // Default to OK if AV is present
                };
                var avInfo = !string.IsNullOrEmpty(avStatus) ? $"{icon} {avName} ({avStatus})" : $"{icon} {avName}";
                Add(ev, "Antivirus", avInfo, "scan_powershell.sections.Security.data.antivirusProducts");
            }
            else
            {
                AddUnknown(ev, "Antivirus", "données AV absentes");
            }

            // 6. Espace libre C: (total / libre / %)
            // FIX: Support both "letter" (PS actual) and "driveLetter" (legacy)
            // FIX: Support both "freeGB"/"totalGB" (PS actual) and "freeSpaceGB"/"sizeGB" (legacy)
            var storageData = GetSectionData(root, "Storage");
            bool foundC = false;
            if (storageData.HasValue && storageData.Value.TryGetProperty("volumes", out var volumes) && 
                volumes.ValueKind == JsonValueKind.Array)
            {
                foreach (var vol in volumes.EnumerateArray())
                {
                    // FIX: Support both "letter" (actual PS output) and "driveLetter" (expected)
                    var letter = GetString(vol, "letter") ?? GetString(vol, "driveLetter");
                    var letterUpper = letter?.ToUpper().TrimEnd(':');
                    
                    if (letterUpper == "C")
                    {
                        // FIX: Support both freeGB/totalGB (actual) and freeSpaceGB/sizeGB (legacy)
                        var freeGB = GetDouble(vol, "freeGB") ?? GetDouble(vol, "freeSpaceGB");
                        var sizeGB = GetDouble(vol, "totalGB") ?? GetDouble(vol, "sizeGB");
                        
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
            
            var wheaCount = GetSignalInt(signals, "whea_errors", "count");
            if (wheaCount.HasValue && wheaCount > 0) errorSummary.Add($"WHEA: {wheaCount}");
            
            var bsodCount = GetSignalInt(signals, "bsod_minidump", "count");
            if (bsodCount.HasValue && bsodCount > 0) errorSummary.Add($"BSOD: {bsodCount}");
            
            var kpCount = GetSignalInt(signals, "kernel_power", "count");
            if (kpCount.HasValue && kpCount > 0) errorSummary.Add($"Kernel-Power: {kpCount}");
            
            if (errorSummary.Count > 0)
            {
                Add(ev, "Erreurs critiques", $"⚠️ {string.Join(", ", errorSummary)}", "diagnostic_signals.*");
            }
            else if (signals.HasValue)
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
                var cpusKind = cpuData.Value.TryGetProperty("cpus", out var cpusEl) ? cpusEl.ValueKind.ToString() : "NotFound";
                var cpuListKind = cpuData.Value.TryGetProperty("cpuList", out var cpuListEl) ? cpuListEl.ValueKind.ToString() : "NotFound";
                DebugLog("A", "ExtractCPU:structure", "cpus/cpuList structure", new { cpusKind, cpuListKind });
            }
            // #endregion
            
            if (cpuData.HasValue)
            {
                // FIX: Support both array AND object for cpus/cpuList (PS script may return object for single CPU)
                firstCpu = GetFirstItemFromArrayOrObject(cpuData, "cpus") ?? GetFirstItemFromArrayOrObject(cpuData, "cpuList");
                
                // #region agent log
                DebugLog("A", "ExtractCPU:result", "firstCpu resolved", new { hasCpu = firstCpu.HasValue, kind = firstCpu?.ValueKind.ToString() });
                // #endregion
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
                var throttle = GetSignalResult(signals.Value, "cpu_throttle") 
                    ?? GetSignalResult(signals.Value, "cpuThrottle")
                    ?? GetSignalResult(signals.Value, "CpuThrottle");
                // #region agent log
                DebugLog("D", "ExtractCPU:throttle", "throttle signal found", new { hasThrottle = throttle.HasValue });
                // #endregion
                if (throttle.HasValue)
                {
                    var detected = GetBool(throttle, "detected") ?? false;
                    var reason = GetString(throttle, "reason") ?? "";
                    if (detected)
                    {
                        var reasonStr = !string.IsNullOrEmpty(reason) ? $" ({reason})" : "";
                        Add(ev, "Throttling", $"⚠️ Oui{reasonStr}", "diagnostic_signals.cpu_throttle");
                    }
                    else
                    {
                        Add(ev, "Throttling", "✅ Non détecté", "diagnostic_signals.cpu_throttle");
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
                // FIX: Support both array AND object for gpuList/gpus (PS script may return object for single GPU)
                firstGpu = GetFirstItemFromArrayOrObject(gpuData, "gpuList") ?? GetFirstItemFromArrayOrObject(gpuData, "gpus");
                
                // #region agent log
                DebugLog("A", "ExtractGPU:result", "firstGpu resolved", new { hasGpu = firstGpu.HasValue, kind = firstGpu?.ValueKind.ToString() });
                // #endregion
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
                Add(ev, "Température GPU", $"{temp:F0}°C{status}", "sensors_csharp.gpu.gpuTempC");
            }
            else
            {
                AddUnknown(ev, "Température GPU", sensors?.Gpu?.GpuTempC?.Reason ?? "capteur indisponible");
            }

            // 10. TDR / crashes GPU
            var signals = GetDiagnosticSignals(root);
            if (signals.HasValue)
            {
                var tdrCount = GetSignalInt(signals.Value, "tdr_video", "count");
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
            
            // 1 & 2. Disques physiques avec type
            // FIX: Support both "physicalDisks" (actual PS output) and "disks" (legacy)
            JsonElement? disksElement = null;
            string diskSource = "physicalDisks";
            
            if (storageData.HasValue)
            {
                if (storageData.Value.TryGetProperty("physicalDisks", out var pDisks) && pDisks.ValueKind == JsonValueKind.Array)
                {
                    disksElement = pDisks;
                    diskSource = "physicalDisks";
                }
                else if (storageData.Value.TryGetProperty("disks", out var legacyDisks) && legacyDisks.ValueKind == JsonValueKind.Array)
                {
                    disksElement = legacyDisks;
                    diskSource = "disks";
                }
            }
            
            if (disksElement.HasValue)
            {
                var diskList = disksElement.Value.EnumerateArray().ToList();
                Add(ev, "Disques physiques", diskList.Count.ToString(), $"scan_powershell.sections.Storage.data.{diskSource}.length");
                
                int i = 1;
                foreach (var disk in diskList)
                {
                    var model = GetString(disk, "model") ?? GetString(disk, "friendlyName") ?? $"Disque {i}";
                    var mediaType = GetString(disk, "type") ?? GetString(disk, "mediaType") ?? "";
                    var sizeGB = GetDouble(disk, "sizeGB");
                    var interfaceType = GetString(disk, "interface") ?? "";
                    
                    // Determine type from multiple sources
                    var typeStr = mediaType.ToUpper() switch
                    {
                        "SSD" => "SSD",
                        "HDD" => "HDD",
                        "NVME" => "NVMe",
                        _ when model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) => "NVMe",
                        _ when model.Contains("SSD", StringComparison.OrdinalIgnoreCase) => "SSD",
                        _ when interfaceType.Contains("SCSI", StringComparison.OrdinalIgnoreCase) && 
                               (model.Contains("Micron", StringComparison.OrdinalIgnoreCase) || 
                                model.Contains("Samsung", StringComparison.OrdinalIgnoreCase)) => "SSD",
                        _ => mediaType.ToUpper() == "HDD" ? "HDD" : "Disque"
                    };
                    
                    // FIX: Display temperature with emoji if available from sensors
                    string? tempInfo = null;
                    if (sensors?.Disks != null)
                    {
                        var matchingDisk = sensors.Disks.FirstOrDefault(d => 
                            d.Name?.Available == true && 
                            model.Contains(d.Name.Value?.Split('_')[0] ?? "###", StringComparison.OrdinalIgnoreCase));
                        
                        if (matchingDisk?.TempC?.Available == true)
                        {
                            var temp = matchingDisk.TempC.Value;
                            var emoji = temp < 45 ? "✅" : temp < 55 ? "⚡" : "⚠️";
                            tempInfo = $" {emoji}{temp:F0}°C";
                        }
                    }
                    
                    var info = sizeGB.HasValue ? $"{typeStr} {sizeGB.Value:F0} GB" : typeStr;
                    Add(ev, $"Disque {i}", $"{model.Trim()} ({info}){tempInfo ?? ""}", 
                        $"scan_powershell.sections.Storage.data.{diskSource}[{i-1}]");
                    i++;
                }
            }
            else
            {
                AddUnknown(ev, "Disques physiques", "Storage.data.physicalDisks/disks absent");
            }

            // 3. Températures disques (capteurs C#) - affichage individuel avec emoji
            if (sensors?.Disks?.Count > 0)
            {
                var tempsWithEmoji = sensors.Disks
                    .Where(d => d.TempC?.Available == true)
                    .Select(d => {
                        var temp = d.TempC.Value;
                        var emoji = temp < 45 ? "✅" : temp < 55 ? "⚡" : "⚠️";
                        var shortName = d.Name?.Value?.Split('_')[0] ?? "Disk";
                        if (shortName.Length > 15) shortName = shortName.Substring(0, 15) + "..";
                        return $"{shortName}: {emoji}{temp:F0}°C";
                    })
                    .Take(5);
                var tempsStr = string.Join(" | ", tempsWithEmoji);
                if (!string.IsNullOrEmpty(tempsStr))
                    Add(ev, "Températures disques", tempsStr, "sensors_csharp.disks[*].tempC");
                
                // Also show max temperature
                var maxTemp = sensors.Disks
                    .Where(d => d.TempC?.Available == true)
                    .Select(d => d.TempC.Value)
                    .DefaultIfEmpty(0)
                    .Max();
                if (maxTemp > 0)
                {
                    var emoji = maxTemp < 45 ? "✅" : maxTemp < 55 ? "⚡" : "⚠️";
                    Add(ev, "TempMax Disques", $"{emoji} {maxTemp:F0}°C", "sensors_csharp.disks (max)");
                }
            }

            // 4. Santé SMART - check both SmartDetails and Storage.smart
            var smartData = GetSectionData(root, "SmartDetails");
            bool smartFound = false;
            
            if (smartData.HasValue)
            {
                // Check for disks property that may contain health info
                if (smartData.Value.TryGetProperty("disks", out var smartDisks))
                {
                    var predictFailure = GetBool(smartDisks, "predictFailure");
                    if (predictFailure.HasValue)
                    {
                        var icon = predictFailure.Value ? "⚠️ Défaillance prédite" : "✅ OK";
                        Add(ev, "Santé SMART", icon, "scan_powershell.sections.SmartDetails.data.disks.predictFailure");
                        smartFound = true;
                    }
                }
                
                if (!smartFound)
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
                        smartFound = true;
                    }
                }
            }
            
            // Fallback: check Storage.smart
            if (!smartFound && storageData.HasValue && storageData.Value.TryGetProperty("smart", out var storageSmart))
            {
                var predictFailure = GetBool(storageSmart, "predictFailure");
                if (predictFailure.HasValue)
                {
                    var icon = predictFailure.Value ? "⚠️ Défaillance prédite" : "✅ OK";
                    Add(ev, "Santé SMART", icon, "scan_powershell.sections.Storage.data.smart.predictFailure");
                    smartFound = true;
                }
            }
            
            if (!smartFound)
            {
                if (sensors?.Disks?.Count > 0)
                    Add(ev, "Santé SMART", "✅ Capteurs C# détectés", "sensors_csharp.disks");
                else
                    AddUnknown(ev, "Santé SMART", "SmartDetails absent");
            }

            // 5. TOUTES les partitions (obligatoire selon cahier des charges)
            // FIX: Support both "letter" (actual) and "driveLetter" (legacy)
            // FIX: Support both "freeGB"/"totalGB" (actual) and "freeSpaceGB"/"sizeGB" (legacy)
            if (storageData.HasValue && storageData.Value.TryGetProperty("volumes", out var volumes) && 
                volumes.ValueKind == JsonValueKind.Array)
            {
                var volList = new List<string>();
                foreach (var vol in volumes.EnumerateArray())
                {
                    var letter = GetString(vol, "letter") ?? GetString(vol, "driveLetter") ?? "";
                    letter = letter.TrimEnd(':'); // Normalize: "C:" -> "C"
                    
                    var freeGB = GetDouble(vol, "freeGB") ?? GetDouble(vol, "freeSpaceGB");
                    var sizeGB = GetDouble(vol, "totalGB") ?? GetDouble(vol, "sizeGB");
                    
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
            
            // CORRECTION: Sélectionner l'adaptateur actif RÉEL (ignorer VMware/Hyper-V/VirtualBox)
            if (netData.HasValue && netData.Value.TryGetProperty("adapters", out var adapters) && 
                adapters.ValueKind == JsonValueKind.Array)
            {
                var excludePatterns = new[] { "vmware", "hyper-v", "virtual", "vmnet", "vethernet", "loopback", "virtualbox" };
                
                foreach (var adapter in adapters.EnumerateArray())
                {
                    var name = GetString(adapter, "name")?.ToLower() ?? "";
                    var status = GetString(adapter, "status")?.ToLower() ?? "";
                    
                    // FIX: Extract IPv4 from ip[] array OR ipv4 string
                    var ipv4 = ExtractIPv4FromAdapter(adapter);
                    
                    // Ignorer les adaptateurs virtuels
                    if (excludePatterns.Any(p => name.Contains(p))) continue;
                    
                    // Préférer un adaptateur avec une IP non-APIPA et gateway
                    var gateway = GetGatewayFromAdapter(adapter);
                    if (!string.IsNullOrEmpty(ipv4) && !ipv4.StartsWith("169.254") && !string.IsNullOrEmpty(gateway))
                    {
                        activeAdapter = adapter;
                        break;
                    }
                    
                    // Second priority: adapter with valid IP
                    if (!activeAdapter.HasValue && !string.IsNullOrEmpty(ipv4) && !ipv4.StartsWith("169.254"))
                        activeAdapter = adapter;
                }
                
                // Fallback: first non-virtual adapter
                if (!activeAdapter.HasValue)
                {
                    foreach (var adapter in adapters.EnumerateArray())
                    {
                        var name = GetString(adapter, "name")?.ToLower() ?? "";
                        if (!excludePatterns.Any(p => name.Contains(p)))
                        {
                            activeAdapter = adapter;
                            break;
                        }
                    }
                }
                
                // Final fallback: first adapter
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

                // 3. IP - FIX: Extract from ip[] array OR ipv4 string
                var ipv4 = ExtractIPv4FromAdapter(activeAdapter.Value);
                if (!string.IsNullOrEmpty(ipv4))
                    Add(ev, "Adresse IP", ipv4, "scan_powershell.sections.Network.data.adapters[active].ip[]");
                else
                    AddUnknown(ev, "Adresse IP", "ip/ipv4 absent");

                // 4. Passerelle - FIX: Handle gateway as string or object
                var gateway = GetGatewayFromAdapter(activeAdapter.Value);
                if (!string.IsNullOrEmpty(gateway))
                    Add(ev, "Passerelle", gateway, "scan_powershell.sections.Network.data.adapters[active].gateway");

                // 5. DNS - Handle as array or object
                string? dnsServers = null;
                if (activeAdapter.Value.TryGetProperty("dns", out var dnsEl))
                {
                    if (dnsEl.ValueKind == JsonValueKind.Array)
                    {
                        dnsServers = string.Join(", ", dnsEl.EnumerateArray()
                            .Select(d => d.ValueKind == JsonValueKind.String ? d.GetString() : null)
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Take(2));
                    }
                    else if (dnsEl.ValueKind == JsonValueKind.String)
                    {
                        dnsServers = dnsEl.GetString();
                    }
                }
                if (!string.IsNullOrEmpty(dnsServers))
                    Add(ev, "DNS", dnsServers, "scan_powershell.sections.Network.data.adapters[active].dns");

                // 6. MAC
                var mac = GetString(activeAdapter, "mac") ?? GetString(activeAdapter, "macAddress");
                if (!string.IsNullOrEmpty(mac))
                    Add(ev, "MAC", mac, "scan_powershell.sections.Network.data.adapters[active].mac");

                // 7. WiFi RSSI
                var rssi = GetInt(activeAdapter, "rssi") ?? GetInt(activeAdapter, "signalStrength");
                if (rssi.HasValue)
                {
                    var quality = rssi.Value > -50 ? "Excellent" : rssi.Value > -60 ? "Bon" : rssi.Value > -70 ? "Moyen" : "Faible";
                    Add(ev, "WiFi Signal", $"{rssi.Value} dBm ({quality})", "scan_powershell.sections.Network.data.adapters[active].rssi");
                }
                
                // 7b. DHCP
                var dhcp = GetBool(activeAdapter, "dhcp");
                if (dhcp.HasValue)
                    Add(ev, "DHCP", dhcp.Value ? "Oui" : "Non", "scan_powershell.sections.Network.data.adapters[active].dhcp");
            }
            else
            {
                AddUnknown(ev, "Adaptateur", "Network.data.adapters absent");
            }

            // === C#: network_diagnostics ===
            var netDiag = GetNestedElement(root, "network_diagnostics");
            // Also try diagnostic_signals.networkQuality as fallback
            var netQuality = GetSignalResult(GetDiagnosticSignals(root) ?? default, "networkQuality");
            JsonElement? netQualityValue = null;
            if (netQuality.HasValue && netQuality.Value.TryGetProperty("value", out var nqv))
                netQualityValue = nqv;
            
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
            
            bool hasNetDiagData = false;
            
            if (netDiag.HasValue || netQualityValue.HasValue)
            {
                // 8. Latence - FIX: try multiple property name variations (PascalCase from C#)
                var latency = GetDouble(netDiag, "latencyMs") 
                    ?? GetDouble(netDiag, "pingMs")
                    ?? GetDouble(netDiag, "LatencyMsP50")
                    ?? GetDouble(netDiag, "OverallLatencyMsP50")
                    ?? GetDouble(netQualityValue, "LatencyMsP50")
                    ?? GetDouble(netQualityValue, "LatencyMsP95");
                if (latency.HasValue)
                {
                    var status = latency > 100 ? " ⚠️ Élevée" : latency > 50 ? " ⚡" : " ✅";
                    Add(ev, "Latence (ping)", $"{latency.Value:F0} ms{status}", "network_diagnostics.LatencyMsP50");
                    hasNetDiagData = true;
                }

                // 9. Jitter - FIX: Support PascalCase
                var jitter = GetDouble(netDiag, "jitterMs") 
                    ?? GetDouble(netDiag, "JitterMsP95")
                    ?? GetDouble(netQualityValue, "JitterMsP95");
                if (jitter.HasValue)
                {
                    Add(ev, "Gigue", $"{jitter.Value:F1} ms", "network_diagnostics.JitterMsP95");
                    hasNetDiagData = true;
                }

                // 10. Perte paquets - FIX: Support PascalCase
                var loss = GetDouble(netDiag, "packetLossPercent") 
                    ?? GetDouble(netDiag, "PacketLossPercent")
                    ?? GetDouble(netQualityValue, "PacketLossPercent");
                if (loss.HasValue)
                {
                    var status = loss > 1 ? " ⚠️" : " ✅";
                    Add(ev, "Perte paquets", $"{loss.Value:F1}%{status}", "network_diagnostics.PacketLossPercent");
                    hasNetDiagData = true;
                }

                // 11. Débit FAI - FIX: Support PascalCase
                var download = GetDouble(netDiag, "downloadMbps") ?? GetDouble(netDiag, "DownloadMbps");
                var upload = GetDouble(netDiag, "uploadMbps") ?? GetDouble(netDiag, "UploadMbps");
                if (download.HasValue || upload.HasValue)
                {
                    var dlStr = download.HasValue ? $"↓{download.Value:F1}" : "?";
                    var ulStr = upload.HasValue ? $"↑{upload.Value:F1}" : "?";
                    Add(ev, "Débit FAI", $"{dlStr} / {ulStr} Mbps", "network_diagnostics.downloadMbps/uploadMbps");
                    hasNetDiagData = true;
                }

                // 12. VPN détecté
                var vpn = GetBool(netDiag, "vpnDetected") ?? GetBool(netDiag, "VpnDetected");
                if (vpn.HasValue)
                {
                    AddYesNo(ev, "VPN détecté", vpn, "network_diagnostics.vpnDetected");
                    hasNetDiagData = true;
                }
                
                // 13. Connection verdict from networkQuality signal
                var verdict = GetString(netQualityValue, "ConnectionVerdict");
                if (!string.IsNullOrEmpty(verdict))
                {
                    var icon = verdict.ToLower() switch
                    {
                        "excellent" => "✅",
                        "good" or "bon" => "👍",
                        "fair" or "moyen" => "⚡",
                        _ => "⚠️"
                    };
                    Add(ev, "Qualité connexion", $"{icon} {verdict}", "diagnostic_signals.networkQuality.ConnectionVerdict");
                    hasNetDiagData = true;
                }
            }
            
            if (!hasNetDiagData)
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
                var bsodCount = GetSignalInt(signals.Value, "bsod_minidump", "count");
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
                var wheaCount = GetSignalInt(signals.Value, "whea_errors", "count");
                if (wheaCount.HasValue)
                    Add(ev, "Erreurs WHEA", wheaCount == 0 ? "✅ Aucune" : $"⚠️ {wheaCount} (30 jours)", "diagnostic_signals.whea_errors");
                else
                    AddUnknown(ev, "Erreurs WHEA", "signal absent");

                // 3. Kernel-Power
                var kpCount = GetSignalInt(signals.Value, "kernel_power", "count");
                if (kpCount.HasValue)
                    Add(ev, "Kernel-Power", kpCount == 0 ? "✅ Aucun" : $"⚠️ {kpCount} événement(s)", "diagnostic_signals.kernel_power");
                else
                    AddUnknown(ev, "Kernel-Power", "signal absent");
            }
            else
            {
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
                var cpuArray = GetArray(cpuData, "cpus") ?? GetArray(cpuData, "cpuList");
                if (cpuArray.HasValue)
                {
                    var first = cpuArray.Value.EnumerateArray().FirstOrDefault();
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
                var availGB = GetDouble(memData, "availableGB");
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
                
                if (GetBool(GetSignalResult(signals.Value, "cpu_throttle"), "detected") == true)
                    bottlenecks.Add("CPU bound");
                if (GetBool(GetSignalResult(signals.Value, "ram_pressure"), "detected") == true)
                    bottlenecks.Add("RAM pressure");
                if (GetBool(GetSignalResult(signals.Value, "disk_saturation"), "detected") == true)
                    bottlenecks.Add("Disk saturation");
                if (GetBool(GetSignalResult(signals.Value, "network_saturation"), "detected") == true)
                    bottlenecks.Add("Network saturation");
                
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
            var machineIdData = GetSectionData(root, "MachineIdentity");
            
            // #region agent log
            if (secData.HasValue)
            {
                var secProps = new List<string>();
                foreach (var prop in secData.Value.EnumerateObject()) secProps.Add(prop.Name);
                DebugLog("C", "ExtractSecurity:entry", "Security section properties", new { secProps });
                // Check for antivirusProducts
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
            // FIX: Handle antivirusProducts as string OR array
            string? avName = null;
            string? avStatus = null;
            
            if (secData.HasValue && secData.Value.TryGetProperty("antivirusProducts", out var avProductsProp))
            {
                // Case 1: String (actual PS output for single AV like "Windows Defender")
                if (avProductsProp.ValueKind == JsonValueKind.String)
                {
                    avName = avProductsProp.GetString();
                }
                // Case 2: Array (multiple AV products)
                else if (avProductsProp.ValueKind == JsonValueKind.Array)
                {
                    var firstAv = avProductsProp.EnumerateArray().FirstOrDefault();
                    if (firstAv.ValueKind == JsonValueKind.Object)
                    {
                        avName = GetString(firstAv, "displayName") ?? GetString(firstAv, "name");
                        avStatus = GetString(firstAv, "productState") ?? GetString(firstAv, "status");
                    }
                    else if (firstAv.ValueKind == JsonValueKind.String)
                    {
                        avName = firstAv.GetString();
                    }
                }
            }
            
            // Fallback to direct properties
            if (string.IsNullOrEmpty(avName))
            {
                avName = GetString(secData, "antivirusName") ?? GetString(secData, "avName");
            }
            if (string.IsNullOrEmpty(avStatus))
            {
                avStatus = GetString(secData, "antivirusStatus") ?? GetString(secData, "avStatus");
            }
            
            // FIX: Check defenderEnabled/defenderRTP as status indicators
            if (string.IsNullOrEmpty(avStatus) && secData.HasValue)
            {
                var defenderEnabled = GetBool(secData, "defenderEnabled");
                var defenderRTP = GetBool(secData, "defenderRTP");
                if (defenderEnabled == true || defenderRTP == true)
                {
                    avStatus = "Actif";
                }
                else if (defenderEnabled == false)
                {
                    avStatus = "Désactivé";
                }
            }
            
            if (!string.IsNullOrEmpty(avName))
            {
                var icon = avStatus?.ToLower() switch
                {
                    "enabled" or "on" or "actif" or "à jour" => "✅",
                    "disabled" or "off" or "désactivé" => "⚠️",
                    _ => "✅" // Default to OK if AV is present
                };
                var avInfo = !string.IsNullOrEmpty(avStatus) ? $"{icon} {avName} ({avStatus})" : $"{icon} {avName}";
                Add(ev, "Antivirus", avInfo, "scan_powershell.sections.Security.data.antivirusProducts");
            }
            else
            {
                AddUnknown(ev, "Antivirus", "données AV absentes");
            }

            // 2. Pare-feu - handle multiple structures
            // FIX: Handle firewall as object with profiles (Private/Domain/Public) with value__ property
            bool? fwEnabled = GetBool(secData, "firewallEnabled");
            string fwProfiles = "";
            
            if (!fwEnabled.HasValue && secData.HasValue && secData.Value.TryGetProperty("firewall", out var fwObj))
            {
                if (fwObj.ValueKind == JsonValueKind.Object)
                {
                    // FIX: Parse firewall object with profile sub-objects containing value__
                    // Structure: { "Private": { "value__": 1 }, "Domain": { "value__": 1 }, "Public": { "value__": 1 } }
                    var enabledProfiles = new List<string>();
                    var disabledProfiles = new List<string>();
                    
                    foreach (var profile in fwObj.EnumerateObject())
                    {
                        bool? profileEnabled = null;
                        
                        if (profile.Value.ValueKind == JsonValueKind.Object && 
                            profile.Value.TryGetProperty("value__", out var valProp))
                        {
                            profileEnabled = valProp.ValueKind == JsonValueKind.Number ? valProp.GetInt32() == 1 : null;
                        }
                        else if (profile.Value.ValueKind == JsonValueKind.Number)
                        {
                            profileEnabled = profile.Value.GetInt32() == 1;
                        }
                        else if (profile.Value.ValueKind == JsonValueKind.True)
                        {
                            profileEnabled = true;
                        }
                        else if (profile.Value.ValueKind == JsonValueKind.False)
                        {
                            profileEnabled = false;
                        }
                        
                        if (profileEnabled == true)
                            enabledProfiles.Add(profile.Name);
                        else if (profileEnabled == false)
                            disabledProfiles.Add(profile.Name);
                    }
                    
                    // Firewall is enabled if at least one profile is enabled
                    fwEnabled = enabledProfiles.Count > 0;
                    
                    if (enabledProfiles.Count > 0 && enabledProfiles.Count < 3)
                        fwProfiles = string.Join("+", enabledProfiles);
                    else if (enabledProfiles.Count == 3)
                        fwProfiles = "Tous profils";
                    
                    if (disabledProfiles.Count > 0 && disabledProfiles.Count < 3)
                        fwProfiles += (fwProfiles.Length > 0 ? " | " : "") + $"Désactivé: {string.Join(",", disabledProfiles)}";
                }
                else if (fwObj.ValueKind == JsonValueKind.Number)
                {
                    fwEnabled = fwObj.GetInt32() == 1;
                }
                else if (fwObj.ValueKind == JsonValueKind.True || fwObj.ValueKind == JsonValueKind.False)
                {
                    fwEnabled = fwObj.GetBoolean();
                }
            }
            
            if (fwEnabled.HasValue)
            {
                var status = fwEnabled.Value ? "✅ Activé" : "⚠️ Désactivé";
                if (!string.IsNullOrEmpty(fwProfiles)) status += $" ({fwProfiles})";
                Add(ev, "Pare-feu", status, "scan_powershell.sections.Security.data.firewall");
            }
            else
            {
                AddUnknown(ev, "Pare-feu", "firewall absent");
            }

            // 3. Secure Boot (Oui/Non) - FIX: Check MachineIdentity first
            var secureBoot = GetBool(machineIdData, "secureBoot") ?? 
                             GetBool(secData, "secureBootEnabled") ?? 
                             GetBool(secData, "SecureBootEnabled");
            
            string secureBootSource = machineIdData.HasValue && GetBool(machineIdData, "secureBoot").HasValue 
                ? "MachineIdentity.secureBoot" 
                : "Security.secureBootEnabled";
            AddYesNo(ev, "Secure Boot", secureBoot, secureBootSource);

            // 4. BitLocker (OUI/NON - OBLIGATOIRE, pas "—")
            // FIX: If no data found, show "Non détectable" with debug reason
            var bitlocker = GetBool(secData, "bitlockerEnabled") ?? GetBool(secData, "bitLocker") ?? GetBool(secData, "BitLocker");
            if (bitlocker.HasValue)
            {
                AddYesNo(ev, "BitLocker", bitlocker, "scan_powershell.sections.Security.data.bitlockerEnabled");
            }
            else
            {
                // Not "Inconnu" but "Non détectable" with reason
                Add(ev, "BitLocker", DebugPathsEnabled ? "Non détectable (collecte non implémentée) 📍[n/a]" : "Non détectable", 
                    "n/a");
            }

            // 5. UAC
            var uac = GetBool(secData, "uacEnabled") ?? GetBool(secData, "UAC");
            AddYesNo(ev, "UAC", uac, "scan_powershell.sections.Security.data.uacEnabled");

            // 6. RDP
            var rdp = GetBool(secData, "rdpEnabled") ?? GetBool(secData, "RDP");
            if (rdp.HasValue)
            {
                Add(ev, "RDP", rdp.Value ? "⚠️ Activé" : "✅ Désactivé", "scan_powershell.sections.Security.data.rdpEnabled");
            }
            else
            {
                AddUnknown(ev, "RDP", "rdpEnabled absent");
            }

            // 7. SMBv1
            var smb1 = GetBool(secData, "smbV1Enabled") ?? GetBool(secData, "SMBv1");
            if (smb1.HasValue)
            {
                Add(ev, "SMBv1", smb1.Value ? "⚠️ Activé (risque)" : "✅ Désactivé", "scan_powershell.sections.Security.data.smbV1Enabled");
            }
            else
            {
                AddUnknown(ev, "SMBv1", "smbV1Enabled absent");
            }

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
                var kpCount = GetSignalInt(signals.Value, "kernel_power", "count");
                if (kpCount.HasValue)
                {
                    Add(ev, "Kernel-Power", kpCount == 0 ? "✅ Aucun" : $"⚠️ {kpCount} coupure(s)", 
                        "diagnostic_signals.kernel_power.count");
                }
                
                // Power throttling
                var powerThrottle = GetSignalResult(signals.Value, "power_throttle");
                if (powerThrottle.HasValue)
                {
                    var detected = GetBool(powerThrottle, "detected") ?? false;
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
                display = "Inconnu (données absentes)";
            
            Add(ev, key, display, jsonPath);
        }

        /// <summary>
        /// Ajoute "Inconnu (raison)" - jamais "—"
        /// </summary>
        private static void AddUnknown(Dictionary<string, string> ev, string key, string reason)
        {
            ev[key] = DebugPathsEnabled ? $"Inconnu ({reason}) 📍[n/a]" : $"Inconnu ({reason})";
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

        /// <summary>
        /// FIX: Map snake_case signal names to actual camelCase names in diagnostic_signals.
        /// UI code uses: whea_errors, cpu_throttle, bsod_minidump, kernel_power, tdr_video
        /// JSON has:    whea, cpuThrottle, driverStability (contains BSOD + kernel power), gpuRootCause
        /// </summary>
        private static readonly Dictionary<string, string[]> SignalNameAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "whea_errors", new[] { "whea", "whea_errors" } },
            { "cpu_throttle", new[] { "cpuThrottle", "cpu_throttle", "CpuThrottle" } },
            { "bsod_minidump", new[] { "driverStability", "bsod_minidump" } },
            { "kernel_power", new[] { "driverStability", "kernel_power" } },
            { "tdr_video", new[] { "gpuRootCause", "tdr_video", "gpuRootCause" } },
            { "ram_pressure", new[] { "memoryPressure", "ram_pressure" } },
            { "disk_saturation", new[] { "storageLatency", "disk_saturation" } },
            { "network_saturation", new[] { "networkQuality", "network_saturation" } },
            { "power_throttle", new[] { "powerLimits", "power_throttle" } }
        };

        private static JsonElement? GetSignalResult(JsonElement signals, string signalName)
        {
            // Try exact name first
            if (signals.TryGetProperty(signalName, out var signal))
                return signal;
            
            // FIX: Try aliases
            if (SignalNameAliases.TryGetValue(signalName, out var aliases))
            {
                foreach (var alias in aliases)
                {
                    if (signals.TryGetProperty(alias, out var aliasedSignal))
                        return aliasedSignal;
                }
            }
            
            // Case-insensitive fallback
            foreach (var prop in signals.EnumerateObject())
            {
                if (string.Equals(prop.Name, signalName, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            }
            
            return null;
        }

        /// <summary>
        /// FIX: Get int value from signal, supporting nested value object.
        /// JSON structure: { "signalName": { "value": { "Count30d": 0 }, "available": true } }
        /// </summary>
        private static int? GetSignalInt(JsonElement? signals, string signalName, string valueName)
        {
            if (!signals.HasValue) return null;
            var signal = GetSignalResult(signals.Value, signalName);
            if (!signal.HasValue) return null;
            
            // FIX: Handle nested value object structure
            JsonElement valueContainer = signal.Value;
            if (signal.Value.TryGetProperty("value", out var valueObj) && valueObj.ValueKind == JsonValueKind.Object)
            {
                valueContainer = valueObj;
            }
            
            // FIX: Map value names for different signal structures
            var mappedValueNames = MapSignalValueName(signalName, valueName);
            
            foreach (var name in mappedValueNames)
            {
                var result = GetInt(valueContainer, name);
                if (result.HasValue) return result;
            }
            
            // Fallback to direct property
            return GetInt(signal, valueName);
        }

        private static string? GetSignalString(JsonElement? signals, string signalName, string valueName)
        {
            if (!signals.HasValue) return null;
            var signal = GetSignalResult(signals.Value, signalName);
            if (!signal.HasValue) return null;
            
            // FIX: Handle nested value object structure
            JsonElement valueContainer = signal.Value;
            if (signal.Value.TryGetProperty("value", out var valueObj) && valueObj.ValueKind == JsonValueKind.Object)
            {
                valueContainer = valueObj;
            }
            
            var mappedValueNames = MapSignalValueName(signalName, valueName);
            
            foreach (var name in mappedValueNames)
            {
                var result = GetString(valueContainer, name);
                if (!string.IsNullOrEmpty(result)) return result;
            }
            
            return GetString(signal, valueName);
        }
        
        /// <summary>
        /// FIX: Map expected value names to actual JSON property names.
        /// </summary>
        private static string[] MapSignalValueName(string signalName, string valueName)
        {
            // Map "count" to actual property names based on signal type
            if (valueName.Equals("count", StringComparison.OrdinalIgnoreCase))
            {
                return signalName.ToLower() switch
                {
                    "whea_errors" or "whea" => new[] { "Last30dCount", "Last7dCount", "FatalCount", "count" },
                    "bsod_minidump" or "driverstability" => new[] { "BugcheckCount30d", "count" },
                    "kernel_power" or "driverstability" => new[] { "KernelPower41Count30d", "count" },
                    "tdr_video" or "gpurootcause" => new[] { "TdrCount30d", "TdrCount7d", "count" },
                    _ => new[] { valueName, "count", "Count" }
                };
            }
            
            if (valueName.Equals("detected", StringComparison.OrdinalIgnoreCase))
            {
                return signalName.ToLower() switch
                {
                    "cpu_throttle" or "cputhrottle" => new[] { "ThrottleSuspected", "detected", "Detected" },
                    "ram_pressure" or "memorypressure" => new[] { "PressureDetected", "detected" },
                    _ => new[] { valueName, "detected", "Detected" }
                };
            }
            
            return new[] { valueName };
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
                    if (s == "true" || s == "yes" || s == "1" || s == "oui") return true;
                    if (s == "false" || s == "no" || s == "0" || s == "non") return false;
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

        /// <summary>
        /// FIX: Get first item from a property that can be either Array or Object.
        /// PowerShell may return an object for single-item collections (cpus, gpuList).
        /// </summary>
        private static JsonElement? GetFirstItemFromArrayOrObject(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            
            if (!element.Value.TryGetProperty(propName, out var prop)) return null;
            
            // Case 1: Array - return first element
            if (prop.ValueKind == JsonValueKind.Array)
            {
                var first = prop.EnumerateArray().FirstOrDefault();
                return first.ValueKind != JsonValueKind.Undefined ? first : (JsonElement?)null;
            }
            
            // Case 2: Object - return the object itself (single-item case)
            if (prop.ValueKind == JsonValueKind.Object)
            {
                return prop;
            }
            
            return null;
        }

        /// <summary>
        /// FIX: Extract IPv4 address from adapter's ip[] array OR ipv4 string.
        /// PS script returns ip as array: ["192.168.x.x", "fe80::xxxx"]
        /// </summary>
        private static string? ExtractIPv4FromAdapter(JsonElement adapter)
        {
            // Try ipv4 direct property first (legacy)
            var ipv4Direct = GetString(adapter, "ipv4");
            if (!string.IsNullOrEmpty(ipv4Direct))
                return ipv4Direct;
            
            // FIX: Try ip[] array (actual PS output)
            if (adapter.TryGetProperty("ip", out var ipEl))
            {
                if (ipEl.ValueKind == JsonValueKind.Array)
                {
                    // Find IPv4 (not IPv6) - IPv4 doesn't contain ':'
                    foreach (var ip in ipEl.EnumerateArray())
                    {
                        var ipStr = ip.ValueKind == JsonValueKind.String ? ip.GetString() : null;
                        if (!string.IsNullOrEmpty(ipStr) && !ipStr.Contains(":"))
                            return ipStr;
                    }
                    // Fallback: return first IP if no pure IPv4 found
                    var first = ipEl.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.String)
                        return first.GetString();
                }
                else if (ipEl.ValueKind == JsonValueKind.String)
                {
                    return ipEl.GetString();
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// FIX: Extract gateway from adapter's gateway property (can be string or empty object {}).
        /// </summary>
        private static string? GetGatewayFromAdapter(JsonElement adapter)
        {
            if (!adapter.TryGetProperty("gateway", out var gwEl))
                return null;
            
            if (gwEl.ValueKind == JsonValueKind.String)
            {
                var gw = gwEl.GetString();
                return string.IsNullOrEmpty(gw) ? null : gw;
            }
            
            // Empty object {} means no gateway
            if (gwEl.ValueKind == JsonValueKind.Object)
            {
                // Check if it has any properties (some PS versions put gateway in object)
                foreach (var prop in gwEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();
                }
                return null;
            }
            
            return null;
        }

        private static List<string> GetTopProcesses(JsonElement root, string metric, int count)
        {
            var result = new List<string>();
            
            // Source 1: process_telemetry (C# collector) - HIGHEST PRIORITY
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
                // FIX: Support multiple naming conventions including PascalCase "TopByXxx" (actual C# output)
                var metricCapitalized = char.ToUpper(metric[0]) + metric.Substring(1).ToLower();
                
                var names = new[] { 
                    // PascalCase variants (actual C# output): TopByMemory, TopByCpu
                    $"TopBy{metricCapitalized}",
                    $"TopBy{metric.ToUpper()}",
                    // camelCase variants: topByMemory, topByCpu  
                    $"topBy{metricCapitalized}",
                    // Simple variants: TopMemory, topMemory, TopCpu, topCpu
                    $"Top{metricCapitalized}",
                    $"top{metricCapitalized}",
                    // Lowercase: topmemory, topcpu
                    $"top{metric.ToLower()}",
                    // io special cases
                    metric.ToLower() == "io" ? "TopByIo" : null,
                    metric.ToLower() == "io" ? "topIo" : null
                }.Where(n => n != null).ToArray();
                
                foreach (var name in names!)
                {
                    if (telemetry.Value.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        result = arr.EnumerateArray()
                            .Select(p => GetString(p, "Name") ?? GetString(p, "name") ?? GetString(p, "processName") ?? "")
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Take(count)
                            .ToList();
                        // #region agent log
                        DebugLog("B", "GetTopProcesses:found", $"Found data at process_telemetry.{name}", new { resultCount = result.Count });
                        // #endregion
                        if (result.Count > 0) break;
                    }
                }
            }
            
            // Source 2: DynamicSignals from PS (fallback)
            if (result.Count == 0)
            {
                var dynamicSignals = GetSectionData(root, "DynamicSignals");
                if (dynamicSignals.HasValue)
                {
                    // PS script uses topCpu, topMemory arrays
                    var psNames = metric.ToLower() switch
                    {
                        "memory" => new[] { "topMemory", "TopMemory" },
                        "cpu" => new[] { "topCpu", "TopCpu" },
                        _ => new[] { $"top{char.ToUpper(metric[0])}{metric.Substring(1)}" }
                    };
                    
                    foreach (var name in psNames)
                    {
                        if (dynamicSignals.Value.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            result = arr.EnumerateArray()
                                .Select(p => GetString(p, "name") ?? GetString(p, "Name") ?? "")
                                .Where(n => !string.IsNullOrEmpty(n))
                                .Take(count)
                                .ToList();
                            
                            if (result.Count > 0)
                            {
                                DebugLog("B", "GetTopProcesses:fallback", $"Found data at DynamicSignals.{name}", new { resultCount = result.Count });
                                break;
                            }
                        }
                    }
                }
            }
            
            // Source 3: Processes section from PS (last fallback)
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
