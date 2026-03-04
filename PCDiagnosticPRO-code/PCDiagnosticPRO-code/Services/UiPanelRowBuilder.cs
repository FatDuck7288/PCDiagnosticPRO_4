using System;
using System.Collections.Generic;
using System.Globalization;
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
                var snapshotCpu = GetFromDiagnosticSnapshot(root, "cpu");

                var cpuData = GetNestedElement(root, "scan_powershell", "sections", "CPU", "data");
                if (!cpuData.HasValue)
                    cpuData = GetNestedElement(root, "sections", "CPU", "data");

                JsonElement? firstCpu = null;
                var basePath = "scan_powershell.sections.CPU.data.cpus[0]";

                if (cpuData.HasValue)
                {
                    if (cpuData.Value.TryGetProperty("cpus", out var cpusArray) && cpusArray.ValueKind == JsonValueKind.Array)
                    {
                        firstCpu = cpusArray.EnumerateArray().FirstOrDefault();
                        basePath = "scan_powershell.sections.CPU.data.cpus[0]";
                    }
                    else if (cpuData.Value.TryGetProperty("cpuList", out var cpuListArray) && cpuListArray.ValueKind == JsonValueKind.Array)
                    {
                        firstCpu = cpuListArray.EnumerateArray().FirstOrDefault();
                        basePath = "scan_powershell.sections.CPU.data.cpuList[0]";
                    }
                }

                var name = GetStringValue(firstCpu, "name")?.Trim();
                if (string.IsNullOrEmpty(name)) name = snapshotCpu.GetValueOrDefault("model");
                AddDetail(details, "Modèle", name, $"{basePath}.name");

                var cores = GetIntValue(firstCpu, "cores");
                if (cores == null)
                {
                    var snapshotCores = snapshotCpu.GetValueOrDefault("cores");
                    if (int.TryParse(snapshotCores, out var c)) cores = c;
                }
                AddDetail(details, "CÅ“urs", cores?.ToString(), $"{basePath}.cores");

                var threads = GetIntValue(firstCpu, "threads");
                if (threads == null)
                {
                    var snapshotThreads = snapshotCpu.GetValueOrDefault("threads");
                    if (int.TryParse(snapshotThreads, out var t)) threads = t;
                }
                AddDetail(details, "Threads", threads?.ToString(), $"{basePath}.threads");

                var maxClock = GetDoubleValue(firstCpu, "maxClockSpeed");
                if (maxClock.HasValue && maxClock.Value > 0)
                    AddDetail(details, "Fréquence max", $"{maxClock.Value:F0} MHz", $"{basePath}.maxClockSpeed");

                var currentLoad = GetDoubleValue(firstCpu, "currentLoad");
                if (!currentLoad.HasValue) currentLoad = GetDoubleValue(firstCpu, "load");
                if (currentLoad.HasValue)
                    AddDetail(details, "Charge actuelle", $"{currentLoad.Value:F0} %", $"{basePath}.currentLoad");

                int? cpuCount = null;
                if (cpuData.HasValue && cpuData.Value.TryGetProperty("cpuCount", out var cpuCountEl))
                    cpuCount = cpuCountEl.ValueKind == JsonValueKind.Number ? cpuCountEl.GetInt32() : null;

                if (cpuCount.HasValue && cpuCount.Value > 0)
                    AddDetail(details, "Nombre de CPU", cpuCount.Value.ToString(), "scan_powershell.sections.CPU.data.cpuCount");

                var cpuTempMetric = GetNestedElement(root, "sensors_csharp", "cpu", "cpuTempC");
                var cpuTempSource = GetNestedStringValue(root, "sensors_csharp", "cpu", "cpuTempSource");
                if (TryReadMetric(cpuTempMetric, out var cpuTempValue, out var cpuTempReason))
                {
                    AddDetail(details, "Température CPU", $"{cpuTempValue:F0} °C", "sensors_csharp.cpu.cpuTempC.value");
                }
                else
                {
                    AddDetail(details, "Température CPU", FormatUnavailableValue(cpuTempReason), "sensors_csharp.cpu.cpuTempC.available");
                }

                var cpuThrottleSignal =
                    GetNestedElement(root, "diagnostic_signals", "cpu_throttle") ??
                    GetNestedElement(root, "diagnostic_signals", "cpuThrottle") ??
                    GetNestedElement(root, "diagnostic_signals", "CpuThrottle");

                if (TryReadSignalBool(cpuThrottleSignal, out var throttleDetected, out var throttleReason))
                {
                    var throttleValue = throttleDetected ? "Détecté" : "Non détecté";
                    if (throttleDetected && !string.IsNullOrWhiteSpace(throttleReason))
                        throttleValue = $"{throttleValue} ({throttleReason})";
                    AddDetail(details, "Throttling", throttleValue, "diagnostic_signals.cpu_throttle");
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
                var snapshotGpu = GetFromDiagnosticSnapshot(root, "gpu");

                var gpuData = GetNestedElement(root, "scan_powershell", "sections", "GPU", "data");
                if (!gpuData.HasValue)
                    gpuData = GetNestedElement(root, "sections", "GPU", "data");

                JsonElement? firstGpu = null;
                var basePath = "scan_powershell.sections.GPU.data.gpuList[0]";

                if (gpuData.HasValue)
                {
                    if (gpuData.Value.TryGetProperty("gpuList", out var gpuListArray) && gpuListArray.ValueKind == JsonValueKind.Array)
                    {
                        firstGpu = gpuListArray.EnumerateArray().FirstOrDefault();
                        basePath = "scan_powershell.sections.GPU.data.gpuList[0]";
                    }
                    else if (gpuData.Value.TryGetProperty("gpus", out var gpusArray) && gpusArray.ValueKind == JsonValueKind.Array)
                    {
                        firstGpu = gpusArray.EnumerateArray().FirstOrDefault();
                        basePath = "scan_powershell.sections.GPU.data.gpus[0]";
                    }
                }

                var name = GetStringValue(firstGpu, "name")?.Trim();
                if (string.IsNullOrEmpty(name)) name = snapshotGpu.GetValueOrDefault("model");
                AddDetail(details, "Nom", name, $"{basePath}.name");

                var vendor = GetStringValue(firstGpu, "vendor")?.Trim();
                if (string.IsNullOrEmpty(vendor)) vendor = snapshotGpu.GetValueOrDefault("vendor");
                AddDetail(details, "Fabricant", vendor, $"{basePath}.vendor");

                AddDetail(details, "Résolution", GetStringValue(firstGpu, "resolution"), $"{basePath}.resolution");
                AddDetail(details, "Version pilote", GetStringValue(firstGpu, "driverVersion"), $"{basePath}.driverVersion");

                string? driverDateStr = null;
                if (firstGpu.HasValue && firstGpu.Value.TryGetProperty("driverDate", out var driverDateEl))
                {
                    if (driverDateEl.ValueKind == JsonValueKind.Object && driverDateEl.TryGetProperty("DateTime", out var dateTimeEl))
                        driverDateStr = dateTimeEl.GetString();
                    else if (driverDateEl.ValueKind == JsonValueKind.String)
                        driverDateStr = driverDateEl.GetString();
                }
                AddDetail(details, "Date pilote", driverDateStr, $"{basePath}.driverDate.DateTime");

                // VRAM totale
                string? vramDisplay = null;
                string vramPath = $"{basePath}.vramTotalMB";

                if (firstGpu.HasValue)
                {
                    var vramTotalMB = GetDoubleValue(firstGpu, "vramTotalMB");
                    if (vramTotalMB.HasValue && vramTotalMB.Value > 0)
                    {
                        vramDisplay = FormatMegabytes(vramTotalMB.Value);
                    }
                    else
                    {
                        var vramNote = GetStringValue(firstGpu, "vramNote");
                        if (!string.IsNullOrEmpty(vramNote))
                        {
                            vramDisplay = vramNote;
                            vramPath = $"{basePath}.vramNote";
                        }
                    }
                }

                if (string.IsNullOrEmpty(vramDisplay))
                {
                    var vramFromSensors = GetNestedStringValue(root, "sensors_csharp", "gpu", "vramTotalMB", "value");
                    if (!string.IsNullOrEmpty(vramFromSensors) &&
                        double.TryParse(vramFromSensors.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var vramMbFromSensors) &&
                        vramMbFromSensors > 0)
                    {
                        vramDisplay = FormatMegabytes(vramMbFromSensors);
                        vramPath = "sensors_csharp.gpu.vramTotalMB.value";
                    }
                }

                var vramReason = GetNestedStringValue(root, "sensors_csharp", "gpu", "vramTotalMB", "reason");
                AddDetail(details, "VRAM totale", vramDisplay ?? FormatUnavailableValue(vramReason), vramPath);
                AddMetricTraceRows(details, root, "gpu", "vramTotalMB", "VRAM totale", null, null);

                int? gpuCount = null;
                if (gpuData.HasValue && gpuData.Value.TryGetProperty("gpuCount", out var gpuCountEl))
                    gpuCount = gpuCountEl.ValueKind == JsonValueKind.Number ? gpuCountEl.GetInt32() : null;

                if (gpuCount.HasValue && gpuCount.Value > 0)
                    AddDetail(details, "Nombre de GPU", gpuCount.Value.ToString(), "scan_powershell.sections.GPU.data.gpuCount");

                // VRAM dédiée used / total / percent (Task Manager-like)
                var vramUsedMetric = GetNestedElement(root, "sensors_csharp", "gpu", "vramDedicatedUsedMB");
                if (!vramUsedMetric.HasValue)
                    vramUsedMetric = GetNestedElement(root, "sensors_csharp", "gpu", "vramUsedMB");
                var vramUsedSource = GetNestedStringValue(root, "sensors_csharp", "gpu", "vramDedicatedSource");
                if (string.IsNullOrWhiteSpace(vramUsedSource))
                    vramUsedSource = GetNestedStringValue(root, "sensors_csharp", "gpu", "vramUsedSource");
                double? vramPercent = null;

                if (TryReadMetric(vramUsedMetric, out var vramUsedValue, out var vramUsedReason))
                {
                    double? vramTotalValue = null;
                    var vramTotalMetric = GetNestedElement(root, "sensors_csharp", "gpu", "vramDedicatedTotalMB");
                    if (!vramTotalMetric.HasValue)
                        vramTotalMetric = GetNestedElement(root, "sensors_csharp", "gpu", "vramTotalMB");
                    if (TryReadMetric(vramTotalMetric, out var vramTotalSensorValue, out _))
                        vramTotalValue = vramTotalSensorValue;
                    else if (TryParseMegabytes(vramDisplay, out var parsedTotalMb))
                        vramTotalValue = parsedTotalMb;

                    var usedDisplay = FormatMegabytes(vramUsedValue);
                    if (vramTotalValue.HasValue && vramTotalValue.Value > 0)
                    {
                        vramPercent = Math.Clamp((vramUsedValue / vramTotalValue.Value) * 100.0, 0.0, 100.0);
                        usedDisplay = $"{vramUsedValue / 1024.0:F1} Go / {vramTotalValue.Value / 1024.0:F1} Go ({vramPercent.Value:F0}%)";
                    }

                    AddDetail(details, "VRAM Dédiée", usedDisplay, "sensors_csharp.gpu.vramDedicatedUsedMB.value");
                    AddMetricTraceRows(details, root, "gpu", "vramDedicatedUsedMB", "VRAM Dédiée", vramUsedSource, vramUsedReason);
                }
                else
                {
                    AddDetail(details, "VRAM Dédiée", FormatUnavailableValue(vramUsedReason), "sensors_csharp.gpu.vramDedicatedUsedMB.available");
                    AddMetricTraceRows(details, root, "gpu", "vramDedicatedUsedMB", "VRAM Dédiée", vramUsedSource, vramUsedReason);
                }

                double? gpuLoadPercent = null;
                var gpuLoadMetric = GetNestedElement(root, "sensors_csharp", "gpu", "gpuLoadPercent");
                if (TryReadMetric(gpuLoadMetric, out var gpuLoadValue, out var gpuLoadReason))
                {
                    gpuLoadPercent = Math.Clamp(gpuLoadValue, 0.0, 100.0);
                    AddDetail(details, "Charge GPU", $"{gpuLoadPercent.Value:F0} %", "sensors_csharp.gpu.gpuLoadPercent.value");
                    AddMetricTraceRows(details, root, "gpu", "load", "Charge GPU", null, gpuLoadReason);
                }
                else
                {
                    AddDetail(details, "Charge GPU", FormatUnavailableValue(gpuLoadReason), "sensors_csharp.gpu.gpuLoadPercent.available");
                    AddMetricTraceRows(details, root, "gpu", "load", "Charge GPU", null, gpuLoadReason);
                }

                var gpuTempMetric = GetNestedElement(root, "sensors_csharp", "gpu", "gpuTempC");
                var gpuTempSource = GetNestedStringValue(root, "sensors_csharp", "gpu", "gpuTempSource");
                if (TryReadMetric(gpuTempMetric, out var gpuTempValue, out var gpuTempReason))
                {
                    AddDetail(details, "Température GPU", $"{gpuTempValue:F0} °C", "sensors_csharp.gpu.gpuTempC.value");
                    AddMetricTraceRows(details, root, "gpu", "temperature", "Température GPU", gpuTempSource, gpuTempReason);
                }
                else
                {
                    AddDetail(details, "Température GPU", FormatUnavailableValue(gpuTempReason), "sensors_csharp.gpu.gpuTempC.available");
                    AddMetricTraceRows(details, root, "gpu", "temperature", "Température GPU", gpuTempSource, gpuTempReason);
                }

                if (gpuLoadPercent.HasValue && vramPercent.HasValue && gpuLoadPercent.Value >= 85 && vramPercent.Value >= 85)
                {
                    AddDetail(
                        details,
                        "Contexte GPU",
                        "Charge et VRAM élevées en même temps : cohérent en charge 3D soutenue. Surveillez si des saccades apparaissent.",
                        "computed.gpu.load_vram_context");
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
                    if (storageData.Value.TryGetProperty("disks", out var disksEl) && disksEl.ValueKind == JsonValueKind.Array)
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
                            var letter = GetStringValue(vol, "driveLetter")?.ToUpper() ?? "";
                            if (letter == "C")
                            {
                                var sizeGB = GetDoubleValue(vol, "sizeGB");
                                var freeGB = GetDoubleValue(vol, "freeSpaceGB");
                                
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
                    var latency = GetDoubleValue(netDiag, "overallLatencyMsP50");
                    var jitter = GetDoubleValue(netDiag, "overallJitterMsP95");
                    var packetLoss = GetDoubleValue(netDiag, "overallLossPercent");
                    var download = GetDoubleValue(netDiag, "throughput", "downloadMbpsMedian");
                    var upload = GetDoubleValue(netDiag, "throughput", "uploadMbpsMedian");
                    
                    if (latency.HasValue)
                        AddDetail(details, "Latence (P50)", $"{latency.Value:F0} ms", "network_diagnostics.overallLatencyMsP50");
                    if (jitter.HasValue)
                        AddDetail(details, "Gigue (P95)", $"{jitter.Value:F1} ms", "network_diagnostics.overallJitterMsP95");
                    if (packetLoss.HasValue)
                        AddDetail(details, "Perte paquets", $"{packetLoss.Value:F1} %", "network_diagnostics.overallLossPercent");
                    if (download.HasValue)
                        AddDetail(details, "Débit descendant", $"{download.Value:F1} Mbps", "network_diagnostics.throughput.downloadMbpsMedian");
                    if (upload.HasValue)
                        AddDetail(details, "Débit montant", $"{upload.Value:F1} Mbps", "network_diagnostics.throughput.uploadMbpsMedian");
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
                
                // Windows Update status retiré de la section OS (affiché dans la section Mises à jour dédiée)
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
                    value = $"{value} [path:{detail.JsonPath}]";
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

        private static void AddMetricTraceRows(
            List<DetailRow> details,
            JsonElement root,
            string domain,
            string metric,
            string displayLabel,
            string? preferredSource,
            string? fallbackReason)
        {
            var snapshotMetric = GetNestedElement(root, "diagnostic_snapshot", "metrics", domain, metric);
            var source = GetStringValue(snapshotMetric, "source");
            var confidence = GetIntValue(snapshotMetric, "confidence");
            var reason = GetStringValue(snapshotMetric, "reason");

            if (string.IsNullOrWhiteSpace(source))
                source = preferredSource;

            if (string.IsNullOrWhiteSpace(reason))
                reason = fallbackReason;

            var normalizedSource = NormalizeMetricSource(source);
            var confidenceLabel = ToConfidenceLabel(confidence, normalizedSource);
            var reasonLabel = ToUserFriendlyReason(reason);

            AddDetail(details, $"Source {displayLabel}", normalizedSource, $"diagnostic_snapshot.metrics.{domain}.{metric}.source");
            AddDetail(details, $"Confiance {displayLabel}", confidenceLabel, $"diagnostic_snapshot.metrics.{domain}.{metric}.confidence");

            if (!string.IsNullOrWhiteSpace(reasonLabel))
                AddDetail(details, $"ReasonIfMissing {displayLabel}", reasonLabel, $"diagnostic_snapshot.metrics.{domain}.{metric}.reason");
        }

        private static string NormalizeMetricSource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return "Indisponible";

            var normalized = source.Trim();
            var lower = normalized.ToLowerInvariant();

            if (lower.Contains("nvml")) return "NVIDIA NVML (usermode)";
            if (lower.Contains("perf") || lower.Contains("counter")) return "Compteurs de performance Windows";
            if (lower.Contains("wmi")) return "WMI";
            if (lower.Contains("lhm") || lower.Contains("librehardwaremonitor")) return "LibreHardwareMonitor";

            return normalized;
        }

        private static string ToConfidenceLabel(int? confidence, string sourceLabel)
        {
            if (confidence.HasValue)
            {
                if (confidence.Value >= 85) return $"Élevée ({confidence.Value}%)";
                if (confidence.Value >= 60) return $"Moyenne ({confidence.Value}%)";
                if (confidence.Value > 0) return $"Faible ({confidence.Value}%)";
                return "Aucune (0%)";
            }

            var source = sourceLabel.ToLowerInvariant();
            if (source.Contains("librehardwaremonitor") || source.Contains("nvml"))
                return "Élevée";
            if (source.Contains("compteurs de performance"))
                return "Moyenne";
            if (source.Contains("wmi"))
                return "Faible";
            if (source.Contains("indisponible"))
                return "Aucune";
            return "Moyenne";
        }

        private static string? ToUserFriendlyReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return null;

            var key = reason.Trim().ToLowerInvariant();
            return key switch
            {
                "not_available" => "Capteur non disponible sur ce système",
                "sensor_not_available" => "Capteur non disponible sur ce système",
                "sensor_not_found" => "Capteur introuvable",
                "value_not_collected" => "Valeur non collectée",
                "access_denied" => "Accès refusé (droits insuffisants)",
                "sentinel_zero" => "Valeur invalide (0)",
                "sentinel_minus_one" => "Valeur invalide (-1)",
                _ => reason
            };
        }

        private static string FormatUnavailableValue(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "Indisponible";

            var reasonLabel = ToUserFriendlyReason(reason);
            if (string.IsNullOrWhiteSpace(reasonLabel))
                return "Indisponible";

            return $"Indisponible - {reasonLabel}";
        }

        private static string FormatMegabytes(double mb)
        {
            if (mb >= 1024)
                return $"{mb / 1024.0:F1} GB";

            return $"{mb:F0} MB";
        }

        private static bool TryParseMegabytes(string? text, out double mb)
        {
            mb = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>GB|MB)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!match.Success ||
                !double.TryParse(match.Groups["value"].Value.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            var unit = match.Groups["unit"].Value.ToUpperInvariant();
            mb = unit == "GB" ? value * 1024.0 : value;
            return mb > 0;
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

        private static double? GetDoubleValue(JsonElement? element, params string[] path)
        {
            if (!element.HasValue) return null;
            var nested = GetNestedElement(element.Value, path);
            if (!nested.HasValue) return null;
            return nested.Value.ValueKind switch
            {
                JsonValueKind.Number => nested.Value.GetDouble(),
                JsonValueKind.String when double.TryParse(nested.Value.GetString(), out var val) => val,
                _ => null
            };
        }

        private static bool TryReadMetric(JsonElement? metricElement, out double value, out string? reason)
        {
            value = 0;
            reason = null;
            if (!metricElement.HasValue || metricElement.Value.ValueKind != JsonValueKind.Object)
                return false;

            var availableRaw = GetStringValue(metricElement, "available");
            bool isAvailable = string.Equals(availableRaw, "true", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(availableRaw, "1", StringComparison.OrdinalIgnoreCase);

            var metricValue = GetDoubleValue(metricElement, "value");
            reason = GetStringValue(metricElement, "reason");
            if (isAvailable && metricValue.HasValue)
            {
                value = metricValue.Value;
                return true;
            }

            return false;
        }

        private static bool TryReadSignalBool(JsonElement? signalElement, out bool detected, out string? reason)
        {
            detected = false;
            reason = null;
            if (!signalElement.HasValue || signalElement.Value.ValueKind != JsonValueKind.Object)
                return false;

            var source = signalElement.Value;
            var valueNode = GetNestedElement(source, "value");
            if (valueNode.HasValue && valueNode.Value.ValueKind == JsonValueKind.Object)
                source = valueNode.Value;

            var detectedRaw = GetStringValue(source, "detected")
                              ?? GetStringValue(source, "throttleSuspected")
                              ?? GetStringValue(source, "ThrottleSuspected");
            if (string.IsNullOrWhiteSpace(detectedRaw))
                return false;

            if (bool.TryParse(detectedRaw, out var boolValue))
            {
                detected = boolValue;
            }
            else if (int.TryParse(detectedRaw, out var intValue))
            {
                detected = intValue != 0;
            }
            else
            {
                return false;
            }

            reason = GetStringValue(source, "reason") ?? GetStringValue(source, "Reason");
            return true;
        }

        #endregion
    }
}
