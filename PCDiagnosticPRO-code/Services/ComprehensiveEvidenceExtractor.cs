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
            var secData = GetSectionData(root, "Security");
            var secureBoot = GetBool(secData, "secureBootEnabled") ?? GetBool(secData, "SecureBootEnabled");
            AddYesNo(ev, "Secure Boot", secureBoot, "scan_powershell.sections.Security.data.secureBootEnabled");

            // 5. Antivirus actif + état
            var avName = GetString(secData, "antivirusName") ?? GetString(secData, "avName") ?? GetString(secData, "AntivirusName");
            var avStatus = GetString(secData, "antivirusStatus") ?? GetString(secData, "avStatus") ?? GetString(secData, "AntivirusStatus");
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
                    var letter = GetString(vol, "driveLetter")?.ToUpper();
                    if (letter == "C")
                    {
                        var freeGB = GetDouble(vol, "freeSpaceGB");
                        var sizeGB = GetDouble(vol, "sizeGB");
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
            
            if (cpuData.HasValue)
            {
                var cpuArray = GetArray(cpuData, "cpus") ?? GetArray(cpuData, "cpuList");
                if (cpuArray.HasValue)
                    firstCpu = cpuArray.Value.EnumerateArray().FirstOrDefault();
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
            if (signals.HasValue)
            {
                var throttle = GetSignalResult(signals.Value, "cpu_throttle");
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
                var gpuArray = GetArray(gpuData, "gpuList") ?? GetArray(gpuData, "gpus");
                if (gpuArray.HasValue)
                    firstGpu = gpuArray.Value.EnumerateArray().FirstOrDefault();
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
            if (storageData.HasValue && storageData.Value.TryGetProperty("disks", out var disks) && 
                disks.ValueKind == JsonValueKind.Array)
            {
                var diskList = disks.EnumerateArray().ToList();
                Add(ev, "Disques physiques", diskList.Count.ToString(), "scan_powershell.sections.Storage.data.disks.length");
                
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
                        _ => "HDD"
                    };
                    
                    var info = sizeGB.HasValue ? $"{typeStr} {sizeGB.Value:F0} GB" : typeStr;
                    Add(ev, $"Disque {i}", $"{model.Trim()} ({info})", $"scan_powershell.sections.Storage.data.disks[{i-1}]");
                    i++;
                }
            }
            else
            {
                AddUnknown(ev, "Disques physiques", "Storage.data.disks absent");
            }

            // 3. Températures disques (capteurs C#)
            if (sensors?.Disks?.Count > 0)
            {
                var temps = sensors.Disks
                    .Where(d => d.TempC?.Available == true)
                    .Select(d => {
                        var temp = d.TempC.Value;
                        var status = temp > 50 ? "⚠️" : "";
                        return $"{d.Name?.Value ?? "Disk"}: {temp:F0}°C{status}";
                    })
                    .Take(4);
                var tempsStr = string.Join(", ", temps);
                if (!string.IsNullOrEmpty(tempsStr))
                    Add(ev, "Températures disques", tempsStr, "sensors_csharp.disks[*].tempC");
            }
            // Optionnel

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
                    var letter = GetString(vol, "driveLetter") ?? "";
                    var freeGB = GetDouble(vol, "freeSpaceGB");
                    var sizeGB = GetDouble(vol, "sizeGB");
                    
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
            if (netData.HasValue && netData.Value.TryGetProperty("adapters", out var adapters) && 
                adapters.ValueKind == JsonValueKind.Array)
            {
                var excludePatterns = new[] { "vmware", "hyper-v", "virtual", "vmnet", "vethernet", "loopback" };
                
                foreach (var adapter in adapters.EnumerateArray())
                {
                    var name = GetString(adapter, "name")?.ToLower() ?? "";
                    var status = GetString(adapter, "status")?.ToLower() ?? "";
                    var ip = GetString(adapter, "ipv4") ?? "";
                    
                    // Ignorer les adaptateurs virtuels
                    if (excludePatterns.Any(p => name.Contains(p))) continue;
                    
                    // Préférer un adaptateur Up avec une IP
                    if ((status == "up" || status == "connected") && !string.IsNullOrEmpty(ip) && !ip.StartsWith("169.254"))
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
                var ip = GetString(activeAdapter, "ipv4");
                if (!string.IsNullOrEmpty(ip))
                    Add(ev, "Adresse IP", ip, "scan_powershell.sections.Network.data.adapters[active].ipv4");
                else
                    AddUnknown(ev, "Adresse IP", "ipv4 absent");

                // 4. Passerelle
                var gateway = GetString(activeAdapter, "gateway");
                if (!string.IsNullOrEmpty(gateway))
                    Add(ev, "Passerelle", gateway, "scan_powershell.sections.Network.data.adapters[active].gateway");

                // 5. DNS
                var dns = GetString(activeAdapter, "dns");
                if (activeAdapter.Value.TryGetProperty("dns", out var dnsEl) && dnsEl.ValueKind == JsonValueKind.Array)
                {
                    var dnsServers = string.Join(", ", dnsEl.EnumerateArray()
                        .Select(d => d.GetString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Take(2));
                    if (!string.IsNullOrEmpty(dnsServers))
                        Add(ev, "DNS", dnsServers, "scan_powershell.sections.Network.data.adapters[active].dns");
                }
                else if (!string.IsNullOrEmpty(dns))
                {
                    Add(ev, "DNS", dns, "scan_powershell.sections.Network.data.adapters[active].dns");
                }

                // 6. MAC
                var mac = GetString(activeAdapter, "macAddress");
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
            if (netDiag.HasValue)
            {
                // 8. Latence
                var latency = GetDouble(netDiag, "latencyMs") ?? GetDouble(netDiag, "pingMs");
                if (latency.HasValue)
                {
                    var status = latency > 100 ? " ⚠️ Élevée" : latency > 50 ? " ⚡" : " ✅";
                    Add(ev, "Latence (ping)", $"{latency.Value:F0} ms{status}", "network_diagnostics.latencyMs");
                }

                // 9. Jitter
                var jitter = GetDouble(netDiag, "jitterMs");
                if (jitter.HasValue)
                    Add(ev, "Gigue", $"{jitter.Value:F1} ms", "network_diagnostics.jitterMs");

                // 10. Perte paquets
                var loss = GetDouble(netDiag, "packetLossPercent");
                if (loss.HasValue)
                {
                    var status = loss > 1 ? " ⚠️" : " ✅";
                    Add(ev, "Perte paquets", $"{loss.Value:F1}%{status}", "network_diagnostics.packetLossPercent");
                }

                // 11. Débit FAI
                var download = GetDouble(netDiag, "downloadMbps");
                var upload = GetDouble(netDiag, "uploadMbps");
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
            
            // 1. Antivirus
            var avName = GetString(secData, "antivirusName") ?? GetString(secData, "avName");
            var avStatus = GetString(secData, "antivirusStatus") ?? GetString(secData, "avStatus");
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

            // 2. Pare-feu
            var fwEnabled = GetBool(secData, "firewallEnabled") ?? GetBool(secData, "firewall");
            var fwProfiles = GetString(secData, "firewallProfiles");
            if (fwEnabled.HasValue)
            {
                var status = fwEnabled.Value ? "✅ Activé" : "⚠️ Désactivé";
                if (!string.IsNullOrEmpty(fwProfiles)) status += $" ({fwProfiles})";
                Add(ev, "Pare-feu", status, "scan_powershell.sections.Security.data.firewallEnabled");
            }
            else
            {
                AddUnknown(ev, "Pare-feu", "firewallEnabled absent");
            }

            // 3. Secure Boot (Oui/Non)
            var secureBoot = GetBool(secData, "secureBootEnabled");
            AddYesNo(ev, "Secure Boot", secureBoot, "scan_powershell.sections.Security.data.secureBootEnabled");

            // 4. BitLocker (OUI/NON - OBLIGATOIRE, pas "—")
            var bitlocker = GetBool(secData, "bitlockerEnabled") ?? GetBool(secData, "bitLocker") ?? GetBool(secData, "BitLocker");
            AddYesNo(ev, "BitLocker", bitlocker, "scan_powershell.sections.Security.data.bitlockerEnabled");

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

        private static JsonElement? GetSignalResult(JsonElement signals, string signalName) =>
            signals.TryGetProperty(signalName, out var signal) ? signal : null;

        private static int? GetSignalInt(JsonElement? signals, string signalName, string valueName)
        {
            if (!signals.HasValue) return null;
            var signal = GetSignalResult(signals.Value, signalName);
            return signal.HasValue ? GetInt(signal, valueName) : null;
        }

        private static string? GetSignalString(JsonElement? signals, string signalName, string valueName)
        {
            if (!signals.HasValue) return null;
            var signal = GetSignalResult(signals.Value, signalName);
            return signal.HasValue ? GetString(signal, valueName) : null;
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

        private static List<string> GetTopProcesses(JsonElement root, string metric, int count)
        {
            var result = new List<string>();
            
            var telemetry = GetNestedElement(root, "process_telemetry");
            if (telemetry.HasValue)
            {
                var arrayName = metric switch
                {
                    "cpu" => "topCpu",
                    "memory" => "topMemory",
                    "io" => "topIo",
                    "network" => "topNetwork",
                    _ => $"top{char.ToUpper(metric[0])}{metric.Substring(1)}"
                };
                
                // Try multiple case variations
                var names = new[] { arrayName, arrayName.ToLower(), $"Top{metric}" };
                foreach (var name in names)
                {
                    if (telemetry.Value.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        result = arr.EnumerateArray()
                            .Select(p => GetString(p, "name") ?? GetString(p, "processName") ?? GetString(p, "Name") ?? "")
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Take(count)
                            .ToList();
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
