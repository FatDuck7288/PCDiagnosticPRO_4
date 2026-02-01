using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Centralized mapper for UI panel rows.
    /// Extracts data from scan_result_combined.json with proper fallback logic:
    /// 1. diagnostic_snapshot (if available)
    /// 2. scan_powershell.sections
    /// 3. "Données non disponibles" (only if key is truly absent/null)
    /// 
    /// Each row contains:
    /// - Title (display name)
    /// - Score and Grade
    /// - StatusText
    /// - DetailsRows (ordered list of Label: Value pairs)
    /// - DebugPaths (optional, for dev mode)
    /// </summary>
    public static class UiPanelRowBuilder
    {
        /// <summary>
        /// Enable debug mode to track JSON source paths for each value.
        /// Set via environment variable PCDIAG_DEBUG_PATHS=1 or programmatically.
        /// </summary>
        public static bool DebugPathsEnabled { get; set; } = 
            Environment.GetEnvironmentVariable("PCDIAG_DEBUG_PATHS") == "1";

        /// <summary>
        /// Represents a single detail row with label, value, and optional debug path.
        /// </summary>
        public class DetailRow
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            public string? JsonPath { get; set; }
            
            public override string ToString() => $"{Label}: {Value}";
        }

        /// <summary>
        /// Extract CPU details from combined JSON.
        /// Source: scan_powershell.sections.CPU.data.cpus[0]
        /// Fallback: diagnostic_snapshot.metrics where key starts with "cpu"
        /// </summary>
        public static List<DetailRow> ExtractCpuDetails(JsonElement root)
        {
            var details = new List<DetailRow>();
            
            try
            {
                // Priority 1: diagnostic_snapshot
                var snapshotCpu = GetFromDiagnosticSnapshot(root, "cpu");
                
                // Priority 2: scan_powershell.sections.CPU.data.cpus
                var cpuData = GetNestedElement(root, "scan_powershell", "sections", "CPU", "data");
                if (!cpuData.HasValue)
                    cpuData = GetNestedElement(root, "sections", "CPU", "data"); // Direct sections access
                
                JsonElement? firstCpu = null;
                string basePath = "scan_powershell.sections.CPU.data.cpus[0]";
                
                if (cpuData.HasValue)
                {
                    // Try 'cpus' first (correct PS field name), then 'cpuList' for compatibility
                    if (TryGetPropertyIgnoreCase(cpuData.Value, "cpus", out var cpusArray))
                    {
                        if (cpusArray.ValueKind == JsonValueKind.Array)
                        {
                            firstCpu = cpusArray.EnumerateArray().FirstOrDefault();
                            basePath = "scan_powershell.sections.CPU.data.cpus[0]";
                        }
                        else if (cpusArray.ValueKind == JsonValueKind.Object)
                        {
                            firstCpu = cpusArray;
                            basePath = "scan_powershell.sections.CPU.data.cpus";
                        }
                    }
                    else if (TryGetPropertyIgnoreCase(cpuData.Value, "cpuList", out var cpuListArray))
                    {
                        if (cpuListArray.ValueKind == JsonValueKind.Array)
                        {
                            firstCpu = cpuListArray.EnumerateArray().FirstOrDefault();
                            basePath = "scan_powershell.sections.CPU.data.cpuList[0]";
                        }
                        else if (cpuListArray.ValueKind == JsonValueKind.Object)
                        {
                            firstCpu = cpuListArray;
                            basePath = "scan_powershell.sections.CPU.data.cpuList";
                        }
                    }
                }

                // Extract: Modèle
                var name = GetStringValue(firstCpu, "name")?.Trim();
                if (string.IsNullOrEmpty(name)) name = snapshotCpu.GetValueOrDefault("model");
                AddDetail(details, "Modèle", name, $"{basePath}.name");

                // Extract: Cœurs
                var cores = GetIntValue(firstCpu, "cores");
                if (cores == null)
                {
                    var snapshotCores = snapshotCpu.GetValueOrDefault("cores");
                    if (int.TryParse(snapshotCores, out var c)) cores = c;
                }
                AddDetail(details, "Cœurs", cores?.ToString(), $"{basePath}.cores");

                // Extract: Threads
                var threads = GetIntValue(firstCpu, "threads");
                if (threads == null)
                {
                    var snapshotThreads = snapshotCpu.GetValueOrDefault("threads");
                    if (int.TryParse(snapshotThreads, out var t)) threads = t;
                }
                AddDetail(details, "Threads", threads?.ToString(), $"{basePath}.threads");

                // Extract: Fréquence max
                var maxClock = GetDoubleValue(firstCpu, "maxClockSpeed");
                if (maxClock.HasValue && maxClock.Value > 0)
                {
                    AddDetail(details, "Fréquence max", $"{maxClock.Value:F0} MHz", $"{basePath}.maxClockSpeed");
                }

                // Extract: Charge actuelle
                var currentLoad = GetDoubleValue(firstCpu, "currentLoad");
                if (!currentLoad.HasValue) currentLoad = GetDoubleValue(firstCpu, "load");
                if (currentLoad.HasValue)
                {
                    AddDetail(details, "Charge actuelle", $"{currentLoad.Value:F0} %", $"{basePath}.currentLoad");
                }

                // Extract: Nombre de CPU
                int? cpuCount = null;
                if (cpuData.HasValue && cpuData.Value.TryGetProperty("cpuCount", out var cpuCountEl))
                {
                    cpuCount = cpuCountEl.ValueKind == JsonValueKind.Number ? cpuCountEl.GetInt32() : null;
                }
                if (cpuCount.HasValue && cpuCount.Value > 0)
                {
                    AddDetail(details, "Nombre de CPU", cpuCount.Value.ToString(), "scan_powershell.sections.CPU.data.cpuCount");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UiPanelRowBuilder] ExtractCpuDetails error: {ex.Message}");
            }

            return details;
        }

        /// <summary>
        /// Extract GPU details from combined JSON.
        /// Source: scan_powershell.sections.GPU.data.gpuList[0] or gpus[0]
        /// Fallback: diagnostic_snapshot.metrics where key starts with "gpu"
        /// Special handling: if vramTotalMB is null, show vramNote instead
        /// </summary>
        public static List<DetailRow> ExtractGpuDetails(JsonElement root)
        {
            var details = new List<DetailRow>();
            
            try
            {
                // Priority 1: diagnostic_snapshot
                var snapshotGpu = GetFromDiagnosticSnapshot(root, "gpu");
                
                // Priority 2: scan_powershell.sections.GPU.data
                var gpuData = GetNestedElement(root, "scan_powershell", "sections", "GPU", "data");
                if (!gpuData.HasValue)
                    gpuData = GetNestedElement(root, "sections", "GPU", "data");
                
                JsonElement? firstGpu = null;
                string basePath = "scan_powershell.sections.GPU.data.gpuList[0]";
                
                if (gpuData.HasValue)
                {
                    // Try 'gpuList' first, then 'gpus' for compatibility
                    if (TryGetPropertyIgnoreCase(gpuData.Value, "gpuList", out var gpuListArray))
                    {
                        if (gpuListArray.ValueKind == JsonValueKind.Array)
                        {
                            firstGpu = gpuListArray.EnumerateArray().FirstOrDefault();
                            basePath = "scan_powershell.sections.GPU.data.gpuList[0]";
                        }
                        else if (gpuListArray.ValueKind == JsonValueKind.Object)
                        {
                            firstGpu = gpuListArray;
                            basePath = "scan_powershell.sections.GPU.data.gpuList";
                        }
                    }
                    else if (TryGetPropertyIgnoreCase(gpuData.Value, "gpus", out var gpusArray))
                    {
                        if (gpusArray.ValueKind == JsonValueKind.Array)
                        {
                            firstGpu = gpusArray.EnumerateArray().FirstOrDefault();
                            basePath = "scan_powershell.sections.GPU.data.gpus[0]";
                        }
                        else if (gpusArray.ValueKind == JsonValueKind.Object)
                        {
                            firstGpu = gpusArray;
                            basePath = "scan_powershell.sections.GPU.data.gpus";
                        }
                    }
                }

                // Extract: Nom
                var name = GetStringValue(firstGpu, "name")?.Trim();
                if (string.IsNullOrEmpty(name)) name = snapshotGpu.GetValueOrDefault("model");
                AddDetail(details, "Nom", name, $"{basePath}.name");

                // Extract: Fabricant (vendor)
                var vendor = GetStringValue(firstGpu, "vendor")?.Trim();
                if (string.IsNullOrEmpty(vendor)) vendor = snapshotGpu.GetValueOrDefault("vendor");
                AddDetail(details, "Fabricant", vendor, $"{basePath}.vendor");

                // Extract: Résolution
                var resolution = GetStringValue(firstGpu, "resolution");
                AddDetail(details, "Résolution", resolution, $"{basePath}.resolution");

                // Extract: Version pilote
                var driverVersion = GetStringValue(firstGpu, "driverVersion");
                AddDetail(details, "Version pilote", driverVersion, $"{basePath}.driverVersion");

                // Extract: Date pilote (nested: driverDate.DateTime or driverDate directly)
                string? driverDateStr = null;
                if (firstGpu.HasValue && firstGpu.Value.TryGetProperty("driverDate", out var driverDateEl))
                {
                    if (driverDateEl.ValueKind == JsonValueKind.Object && 
                        driverDateEl.TryGetProperty("DateTime", out var dateTimeEl))
                    {
                        driverDateStr = dateTimeEl.GetString();
                    }
                    else if (driverDateEl.ValueKind == JsonValueKind.String)
                    {
                        driverDateStr = driverDateEl.GetString();
                    }
                }
                AddDetail(details, "Date pilote", driverDateStr, $"{basePath}.driverDate.DateTime");

                // Extract: VRAM totale (with vramNote fallback)
                string? vramDisplay = null;
                string vramPath = $"{basePath}.vramTotalMB";
                
                if (firstGpu.HasValue)
                {
                    var vramTotalMB = GetDoubleValue(firstGpu, "vramTotalMB");
                    if (vramTotalMB.HasValue && vramTotalMB.Value > 0)
                    {
                        // Convert MB to GB for display if large
                        if (vramTotalMB.Value >= 1024)
                        {
                            vramDisplay = $"{vramTotalMB.Value / 1024:F1} GB";
                        }
                        else
                        {
                            vramDisplay = $"{vramTotalMB.Value:F0} MB";
                        }
                    }
                    else
                    {
                        // Fallback to vramNote if vramTotalMB is null/0
                        var vramNote = GetStringValue(firstGpu, "vramNote");
                        if (!string.IsNullOrEmpty(vramNote))
                        {
                            vramDisplay = vramNote;
                            vramPath = $"{basePath}.vramNote";
                        }
                    }
                }
                
                // If still empty, try sensors_csharp fallback
                if (string.IsNullOrEmpty(vramDisplay))
                {
                    var vramFromSensors = GetNestedStringValue(root, "sensors_csharp", "gpu", "vramTotalMB", "value");
                    if (!string.IsNullOrEmpty(vramFromSensors) && double.TryParse(vramFromSensors, out var vramMB) && vramMB > 0)
                    {
                        vramDisplay = vramMB >= 1024 ? $"{vramMB / 1024:F1} GB" : $"{vramMB:F0} MB";
                        vramPath = "sensors_csharp.gpu.vramTotalMB.value";
                    }
                }
                
                AddDetail(details, "VRAM totale", vramDisplay, vramPath);

                // Extract: Nombre de GPU
                int? gpuCount = null;
                if (gpuData.HasValue && gpuData.Value.TryGetProperty("gpuCount", out var gpuCountEl))
                {
                    gpuCount = gpuCountEl.ValueKind == JsonValueKind.Number ? gpuCountEl.GetInt32() : null;
                }
                if (gpuCount.HasValue && gpuCount.Value > 0)
                {
                    AddDetail(details, "Nombre de GPU", gpuCount.Value.ToString(), "scan_powershell.sections.GPU.data.gpuCount");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UiPanelRowBuilder] ExtractGpuDetails error: {ex.Message}");
            }

            return details;
        }

        /// <summary>
        /// Extract RAM details from combined JSON.
        /// </summary>
        public static List<DetailRow> ExtractRamDetails(JsonElement root)
        {
            var details = new List<DetailRow>();
            
            try
            {
                var memData = GetNestedElement(root, "scan_powershell", "sections", "Memory", "data");
                if (!memData.HasValue)
                    memData = GetNestedElement(root, "sections", "Memory", "data");
                
                string basePath = "scan_powershell.sections.Memory.data";
                
                if (memData.HasValue)
                {
                    var totalGB = GetDoubleValue(memData, "totalGB");
                    var availableGB = GetDoubleValue(memData, "availableGB");
                    var usedPercent = GetDoubleValue(memData, "usedPercent");
                    
                    if (totalGB.HasValue && totalGB.Value > 0)
                        AddDetail(details, "Total", $"{totalGB.Value:F1} GB", $"{basePath}.totalGB");
                    
                    if (availableGB.HasValue)
                        AddDetail(details, "Disponible", $"{availableGB.Value:F1} GB", $"{basePath}.availableGB");
                    
                    if (usedPercent.HasValue)
                    {
                        AddDetail(details, "Utilisation", $"{usedPercent.Value:F0} %", $"{basePath}.usedPercent");
                    }
                    else if (totalGB.HasValue && availableGB.HasValue && totalGB.Value > 0)
                    {
                        var computed = ((totalGB.Value - availableGB.Value) / totalGB.Value) * 100;
                        AddDetail(details, "Utilisation", $"{computed:F0} % (calculé)", "computed");
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UiPanelRowBuilder] ExtractRamDetails error: {ex.Message}");
            }

            return details;
        }

        /// <summary>
        /// Extract Storage details from combined JSON.
        /// </summary>
        public static List<DetailRow> ExtractStorageDetails(JsonElement root)
        {
            var details = new List<DetailRow>();
            
            try
            {
                var storageData = GetNestedElement(root, "scan_powershell", "sections", "Storage", "data");
                if (!storageData.HasValue)
                    storageData = GetNestedElement(root, "sections", "Storage", "data");
                
                string basePath = "scan_powershell.sections.Storage.data";
                
                if (storageData.HasValue)
                {
                    // Disks summary
                    if (TryGetPropertyIgnoreCase(storageData.Value, "physicalDisks", out var disksEl) && disksEl.ValueKind == JsonValueKind.Array ||
                        TryGetPropertyIgnoreCase(storageData.Value, "disks", out disksEl) && disksEl.ValueKind == JsonValueKind.Array)
                    {
                        var diskCount = disksEl.GetArrayLength();
                        double totalCapacity = 0;
                        
                        foreach (var disk in disksEl.EnumerateArray())
                        {
                            var sizeGB = GetDoubleValue(disk, "sizeGB");
                            if (sizeGB.HasValue) totalCapacity += sizeGB.Value;
                        }
                        
                        AddDetail(details, "Disques", diskCount.ToString(), $"{basePath}.disks.length");
                        if (totalCapacity > 0)
                            AddDetail(details, "Capacité totale", $"{totalCapacity:F0} GB", $"{basePath}.disks[*].sizeGB");
                    }
                    
                    // Volumes (C: drive specifically)
                    if (storageData.Value.TryGetProperty("volumes", out var volumesEl) && volumesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var vol in volumesEl.EnumerateArray())
                        {
                            var letter = (GetStringValue(vol, "driveLetter") ?? GetStringValue(vol, "letter"))?.ToUpper() ?? "";
                            if (letter == "C")
                            {
                                var sizeGB = GetDoubleValue(vol, "sizeGB") ?? GetDoubleValue(vol, "totalGB");
                                var freeGB = GetDoubleValue(vol, "freeSpaceGB") ?? GetDoubleValue(vol, "freeGB");
                                
                                if (sizeGB.HasValue && sizeGB.Value > 0)
                                {
                                    AddDetail(details, "C: Taille", $"{sizeGB.Value:F1} GB", $"{basePath}.volumes[C].sizeGB");
                                    
                                    if (freeGB.HasValue)
                                    {
                                        var usedPercent = ((sizeGB.Value - freeGB.Value) / sizeGB.Value) * 100;
                                        AddDetail(details, "C: Espace libre", $"{freeGB.Value:F1} GB ({100 - usedPercent:F0}%)", 
                                            $"{basePath}.volumes[C].freeSpaceGB");
                                    }
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UiPanelRowBuilder] ExtractStorageDetails error: {ex.Message}");
            }

            return details;
        }

        /// <summary>
        /// Extract Network details from combined JSON.
        /// </summary>
        public static List<DetailRow> ExtractNetworkDetails(JsonElement root)
        {
            var details = new List<DetailRow>();
            
            try
            {
                var netData = GetNestedElement(root, "scan_powershell", "sections", "Network", "data");
                if (!netData.HasValue)
                    netData = GetNestedElement(root, "sections", "Network", "data");
                
                string basePath = "scan_powershell.sections.Network.data";
                
                if (netData.HasValue && netData.Value.TryGetProperty("adapters", out var adaptersEl) && 
                    adaptersEl.ValueKind == JsonValueKind.Array)
                {
                    var firstAdapter = adaptersEl.EnumerateArray().FirstOrDefault();
                    if (firstAdapter.ValueKind == JsonValueKind.Object)
                    {
                        var name = GetStringValue(firstAdapter, "name");
                        var ipv4 = GetStringValue(firstAdapter, "ipv4");
                        if (string.IsNullOrEmpty(ipv4) && TryGetPropertyIgnoreCase(firstAdapter, "ip", out var ipArr) && ipArr.ValueKind == JsonValueKind.Array)
                        {
                            ipv4 = ipArr.EnumerateArray()
                                .Select(i => i.GetString())
                                .FirstOrDefault(i => !string.IsNullOrEmpty(i) && i.Contains('.'));
                        }
                        var status = GetStringValue(firstAdapter, "status");
                        var speed = GetStringValue(firstAdapter, "speed");
                        
                        AddDetail(details, "Adaptateur", name, $"{basePath}.adapters[0].name");
                        AddDetail(details, "Adresse IP", ipv4, $"{basePath}.adapters[0].ipv4");
                        AddDetail(details, "Statut", status, $"{basePath}.adapters[0].status");
                        AddDetail(details, "Vitesse", speed, $"{basePath}.adapters[0].speed");
                    }
                }
                
                // Network diagnostics from C#
                var netDiag = GetNestedElement(root, "network_diagnostics");
                if (netDiag.HasValue)
                {
                    var latency = GetDoubleValue(netDiag, "latencyMs");
                    var jitter = GetDoubleValue(netDiag, "jitterMs");
                    var packetLoss = GetDoubleValue(netDiag, "packetLossPercent");
                    
                    if (latency.HasValue)
                        AddDetail(details, "Latence (ping)", $"{latency.Value:F0} ms", "network_diagnostics.latencyMs");
                    if (jitter.HasValue)
                        AddDetail(details, "Gigue", $"{jitter.Value:F1} ms", "network_diagnostics.jitterMs");
                    if (packetLoss.HasValue)
                        AddDetail(details, "Perte paquets", $"{packetLoss.Value:F1} %", "network_diagnostics.packetLossPercent");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UiPanelRowBuilder] ExtractNetworkDetails error: {ex.Message}");
            }

            return details;
        }

        /// <summary>
        /// Extract OS details from combined JSON.
        /// </summary>
        public static List<DetailRow> ExtractOsDetails(JsonElement root)
        {
            var details = new List<DetailRow>();
            
            try
            {
                var osData = GetNestedElement(root, "scan_powershell", "sections", "OS", "data");
                if (!osData.HasValue)
                    osData = GetNestedElement(root, "sections", "OS", "data");
                
                string basePath = "scan_powershell.sections.OS.data";
                
                if (osData.HasValue)
                {
                    AddDetail(details, "Version", GetStringValue(osData, "caption"), $"{basePath}.caption");
                    AddDetail(details, "Build", GetStringValue(osData, "buildNumber"), $"{basePath}.buildNumber");
                    AddDetail(details, "Architecture", GetStringValue(osData, "architecture"), $"{basePath}.architecture");
                    AddDetail(details, "Nom machine", GetStringValue(osData, "computerName"), $"{basePath}.computerName");
                    
                    var installDate = GetStringValue(osData, "installDate");
                    if (!string.IsNullOrEmpty(installDate))
                    {
                        // Try to parse and format date
                        if (DateTime.TryParse(installDate, out var dt))
                        {
                            AddDetail(details, "Date d'installation", dt.ToString("d MMMM yyyy"), $"{basePath}.installDate");
                        }
                        else
                        {
                            AddDetail(details, "Date d'installation", installDate, $"{basePath}.installDate");
                        }
                    }
                    
                    var lastBoot = GetStringValue(osData, "lastBootUpTime");
                    if (!string.IsNullOrEmpty(lastBoot))
                    {
                        if (DateTime.TryParse(lastBoot, out var dt))
                        {
                            var uptime = DateTime.Now - dt;
                            var uptimeStr = uptime.TotalDays >= 1 
                                ? $"{(int)uptime.TotalDays}j {uptime.Hours}h" 
                                : $"{uptime.Hours}h {uptime.Minutes}min";
                            AddDetail(details, "Uptime", uptimeStr, $"{basePath}.lastBootUpTime (computed)");
                        }
                    }
                }
                
                // Windows Update status
                var updateData = GetNestedElement(root, "updates_csharp");
                if (updateData.HasValue)
                {
                    var pending = GetIntValue(updateData, "pendingCount");
                    if (pending.HasValue)
                    {
                        var status = pending.Value > 0 ? $"{pending.Value} en attente" : "À jour";
                        AddDetail(details, "Mises à jour", status, "updates_csharp.pendingCount");
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UiPanelRowBuilder] ExtractOsDetails error: {ex.Message}");
            }

            return details;
        }

        /// <summary>
        /// Convert DetailRows to EvidenceData dictionary for HealthSection.
        /// </summary>
        public static Dictionary<string, string> ToEvidenceData(List<DetailRow> details)
        {
            var evidence = new Dictionary<string, string>();
            
            foreach (var detail in details)
            {
                if (string.IsNullOrEmpty(detail.Value)) continue;
                
                var key = detail.Label;
                var value = detail.Value;
                
                // Append debug path if enabled
                if (DebugPathsEnabled && !string.IsNullOrEmpty(detail.JsonPath))
                {
                    value = $"{value} 📍[{detail.JsonPath}]";
                }
                
                evidence[key] = value;
            }
            
            return evidence;
        }

        #region Helper Methods

        private static void AddDetail(List<DetailRow> details, string label, string? value, string? jsonPath = null)
        {
            if (string.IsNullOrEmpty(value)) return;
            
            details.Add(new DetailRow
            {
                Label = label,
                Value = value,
                JsonPath = jsonPath
            });
        }

        private static Dictionary<string, string> GetFromDiagnosticSnapshot(JsonElement root, string prefix)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            try
            {
                if (root.TryGetProperty("diagnostic_snapshot", out var snapshot) &&
                    snapshot.TryGetProperty("metrics", out var metrics) &&
                    metrics.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in metrics.EnumerateObject())
                    {
                        if (prop.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var shortKey = prop.Name.Length > prefix.Length 
                                ? prop.Name.Substring(prefix.Length).TrimStart('_', '.') 
                                : prop.Name;
                            result[shortKey] = prop.Value.ToString();
                        }
                    }
                }
            }
            catch { /* Ignore snapshot errors */ }
            
            return result;
        }

        private static JsonElement? GetNestedElement(JsonElement root, params string[] path)
        {
            JsonElement current = root;
            
            foreach (var key in path)
            {
                // Try exact match first, then case-insensitive
                if (current.TryGetProperty(key, out var next))
                {
                    current = next;
                }
                else
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

        private static string? GetStringValue(JsonElement? element, string propertyName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            
            if (element.Value.TryGetProperty(propertyName, out var prop))
            {
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
            }
            
            // Case-insensitive fallback
            foreach (var p in element.Value.EnumerateObject())
            {
                if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                }
            }
            
            return null;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object) return false;
            
            if (element.TryGetProperty(propertyName, out value))
                return true;
            
            foreach (var p in element.EnumerateObject())
            {
                if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
            
            return false;
        }

        private static int? GetIntValue(JsonElement? element, string propertyName)
        {
            var str = GetStringValue(element, propertyName);
            if (int.TryParse(str, out var val)) return val;
            return null;
        }

        private static double? GetDoubleValue(JsonElement? element, string propertyName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            
            if (element.Value.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetDouble();
                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var val))
                    return val;
            }
            
            // Case-insensitive fallback
            foreach (var p in element.Value.EnumerateObject())
            {
                if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Value.ValueKind == JsonValueKind.Number)
                        return p.Value.GetDouble();
                    if (p.Value.ValueKind == JsonValueKind.String && double.TryParse(p.Value.GetString(), out var val))
                        return val;
                }
            }
            
            return null;
        }

        private static string? GetNestedStringValue(JsonElement root, params string[] path)
        {
            var element = GetNestedElement(root, path);
            if (element.HasValue)
            {
                return element.Value.ValueKind == JsonValueKind.String 
                    ? element.Value.GetString() 
                    : element.Value.ToString();
            }
            return null;
        }

        #endregion
    }
}
