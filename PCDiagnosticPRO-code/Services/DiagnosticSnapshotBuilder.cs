using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.DiagnosticsSignals;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services.NetworkDiagnostics;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// PHASE 1.3: Builds DiagnosticSnapshot from collected data.
    /// Schema 2.2.0: Maps PS sections + C# collectors to normalized snapshot.
    /// Applies all normalization rules and sentinel validation.
    /// </summary>
    public class DiagnosticSnapshotBuilder
    {
        private readonly DiagnosticSnapshot _snapshot;
        private readonly List<string> _buildLog = new();
        private readonly HashSet<string> _mappedPsSections = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unmappedPsSections = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The 10 mandatory metric domains that must be present in the snapshot.</summary>
        private static readonly string[] MandatoryDomains = { "os", "memory", "security", "stability", "storage", "network", "updates", "startup", "devices", "boot" };

        public DiagnosticSnapshotBuilder()
        {
            _snapshot = new DiagnosticSnapshot
            {
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                Machine = new MachineInfo
                {
                    Hostname = Environment.MachineName,
                    Os = Environment.OSVersion.ToString(),
                    IsAdmin = AdminHelper.IsRunningAsAdmin()
                },
                SensorStatus = new SensorCollectionStatus()
            };
            LogBuild("[DiagnosticSnapshotBuilder] Initialized (schemaVersion=2.2.0)");
        }

        /// <summary>
        /// Add CPU metrics from HardwareSensorsResult
        /// </summary>
        public DiagnosticSnapshotBuilder AddCpuMetrics(HardwareSensorsResult? sensors)
        {
            var cpuMetrics = new Dictionary<string, NormalizedMetric>();

            if (sensors?.Cpu != null)
            {
                // CPU Temperature with sentinel validation
                cpuMetrics["temperature"] = NormalizeCpuTemp(sensors.Cpu.CpuTempC, sensors.Cpu.CpuTempSource);
            }
            else
            {
                cpuMetrics["temperature"] = MetricFactory.CreateUnavailable("°C", "HardwareSensorsCollector", "sensor_not_available");
            }

            _snapshot.Metrics["cpu"] = cpuMetrics;
            return this;
        }

        /// <summary>
        /// Add GPU metrics from HardwareSensorsResult
        /// </summary>
        public DiagnosticSnapshotBuilder AddGpuMetrics(HardwareSensorsResult? sensors)
        {
            var gpuMetrics = new Dictionary<string, NormalizedMetric>();

            if (sensors?.Gpu != null)
            {
                gpuMetrics["name"] = sensors.Gpu.Name.Available
                    ? MetricFactory.CreateAvailable(sensors.Gpu.Name.Value ?? "", "", "LHM", 100)
                    : MetricFactory.CreateUnavailable("", "LHM", sensors.Gpu.Name.Reason ?? "not_detected");

                gpuMetrics["temperature"] = NormalizeGpuTemp(sensors.Gpu.GpuTempC);
                gpuMetrics["load"] = NormalizePercent(sensors.Gpu.GpuLoadPercent, "LHM", "gpu_load");
                gpuMetrics["vramTotalMB"] = NormalizeVramTotal(sensors.Gpu.VramTotalMB);
                gpuMetrics["vramUsedMB"] = NormalizeVramUsed(sensors.Gpu.VramUsedMB, sensors.Gpu.VramTotalMB);
            }
            else
            {
                gpuMetrics["temperature"] = MetricFactory.CreateUnavailable("°C", "LHM", "gpu_not_detected");
                gpuMetrics["load"] = MetricFactory.CreateUnavailable("%", "LHM", "gpu_not_detected");
            }

            _snapshot.Metrics["gpu"] = gpuMetrics;
            return this;
        }

        /// <summary>
        /// Add storage metrics from HardwareSensorsResult
        /// </summary>
        public DiagnosticSnapshotBuilder AddStorageMetrics(HardwareSensorsResult? sensors)
        {
            var storageMetrics = new Dictionary<string, NormalizedMetric>();

            if (sensors?.Disks != null && sensors.Disks.Count > 0)
            {
                var validTemps = new List<double>();
                int diskIndex = 0;

                foreach (var disk in sensors.Disks)
                {
                    var diskName = disk.Name.Value ?? $"disk_{diskIndex}";
                    var tempMetric = NormalizeDiskTemp(disk.TempC, diskName);
                    storageMetrics[$"disk_{diskIndex}_temp"] = tempMetric;

                    if (tempMetric.Available && tempMetric.Value is double t)
                        validTemps.Add(t);

                    diskIndex++;
                }

                storageMetrics["diskCount"] = MetricFactory.FromCount(sensors.Disks.Count, "LHM");
                storageMetrics["maxDiskTemp"] = validTemps.Count > 0
                    ? MetricFactory.CreateAvailable(validTemps.Max(), "°C", "Derived", 100)
                    : MetricFactory.CreateUnavailable("°C", "Derived", "no_valid_disk_temps");
            }
            else
            {
                storageMetrics["diskCount"] = MetricFactory.FromCount(0, "LHM", "no_disks_detected");
            }

            _snapshot.Metrics["storage"] = storageMetrics;
            return this;
        }

        /// <summary>
        /// Add diagnostic signals (10 signals)
        /// </summary>
        public DiagnosticSnapshotBuilder AddDiagnosticSignals(Dictionary<string, SignalResult>? signals)
        {
            if (signals == null)
            {
                _snapshot.CollectionQuality.SignalsCollected = 0;
                _snapshot.CollectionQuality.SignalsUnavailable = 10;
                return this;
            }

            int collected = 0;
            int unavailable = 0;

            foreach (var kvp in signals)
            {
                var signalName = kvp.Key;
                var signal = kvp.Value;

                var metricsGroup = ConvertSignalToMetrics(signalName, signal);
                if (metricsGroup.Count > 0)
                {
                    _snapshot.Metrics[signalName] = metricsGroup;
                }

                if (signal.Available)
                    collected++;
                else
                    unavailable++;
            }

            _snapshot.CollectionQuality.SignalsCollected = collected;
            _snapshot.CollectionQuality.SignalsUnavailable = unavailable;

            return this;
        }

        /// <summary>
        /// Add findings
        /// </summary>
        public DiagnosticSnapshotBuilder AddFindings(List<NormalizedFinding>? findings)
        {
            if (findings != null)
                _snapshot.Findings.AddRange(findings);
            return this;
        }

        /// <summary>
        /// Build the final snapshot
        /// </summary>
        public DiagnosticSnapshot Build()
        {
            // Calculate collection quality
            int total = 0;
            int available = 0;
            var violations = new List<string>();

            foreach (var (domain, group) in _snapshot.Metrics)
            {
                foreach (var (key, metric) in group)
                {
                    total++;
                    if (metric.Available)
                    {
                        available++;
                    }
                    else
                    {
                        // VALIDATION CONTRACTUELLE : toute métrique unavailable doit avoir une reason
                        if (string.IsNullOrWhiteSpace(metric.Reason))
                        {
                            metric.Reason = "reason_not_provided";
                            violations.Add($"{domain}.{key}: available=false mais reason manquante (auto-corrigé)");
                        }
                        // Confidence doit être 0 quand unavailable
                        if (metric.Confidence != 0)
                        {
                            violations.Add($"{domain}.{key}: available=false mais confidence={metric.Confidence} (forcé à 0)");
                            metric.Confidence = 0;
                        }
                    }
                }
            }

            _snapshot.CollectionQuality.TotalMetrics = total;
            _snapshot.CollectionQuality.AvailableMetrics = available;
            _snapshot.CollectionQuality.UnavailableMetrics = total - available;
            _snapshot.CollectionQuality.CoveragePercent = total > 0 
                ? Math.Round((double)available / total * 100, 1) 
                : 0;
            
            if (violations.Count > 0)
            {
                LogBuild($"[Build] ⚠ {violations.Count} violation(s) contractuelle(s) auto-corrigées:");
                foreach (var v in violations)
                    LogBuild($"  - {v}");
            }
            
            LogBuild($"[Build] Completed: {available}/{total} metrics available ({_snapshot.CollectionQuality.CoveragePercent}%)");

            // === PS COVERAGE: ensure sentinel metrics for missing mandatory domains ===
            var missingSectionNames = new List<string>();
            var mappedSectionNames = new List<string>();
            foreach (var domain in MandatoryDomains)
            {
                if (_snapshot.Metrics.ContainsKey(domain) && _snapshot.Metrics[domain].Count > 0)
                {
                    mappedSectionNames.Add(domain);
                }
                else
                {
                    missingSectionNames.Add(domain);
                    // Create sentinel metric so the domain exists with available=false + explicit reason
                    _snapshot.Metrics[domain] = new Dictionary<string, NormalizedMetric>
                    {
                        ["available"] = MetricFactory.CreateUnavailable("bool", "DiagnosticSnapshotBuilder", $"ps_section_{domain}_not_collected")
                    };
                    LogBuild($"[Build] Sentinel metric created for missing domain '{domain}'");
                }
            }

            _snapshot.PsCoverage = new PsCoverage
            {
                TotalExpectedSections = MandatoryDomains.Length,
                MappedSections = mappedSectionNames.Count,
                MissingSections = missingSectionNames.Count,
                CoveragePercent = MandatoryDomains.Length > 0
                    ? Math.Round((double)mappedSectionNames.Count / MandatoryDomains.Length * 100, 1)
                    : 0,
                MappedSectionNames = mappedSectionNames,
                MissingSectionNames = missingSectionNames,
                UnmappedPsSections = _unmappedPsSections.Except(missingSectionNames, StringComparer.OrdinalIgnoreCase).ToList()
            };

            LogBuild($"[Build] PsCoverage: {_snapshot.PsCoverage.MappedSections}/{_snapshot.PsCoverage.TotalExpectedSections} sections mapped ({_snapshot.PsCoverage.CoveragePercent}%)");
            LogBuild($"[Build] SchemaVersion={_snapshot.SchemaVersion}");
            WriteLogToTemp();

            return _snapshot;
        }
        
        #region PS Section Mapping (Schema 2.2.0)
        
        // ═══════════════════════════════════════════════════════════════
        // CONTRAT DE MAPPING PS → SNAPSHOT (schemaVersion 2.2.0)
        // ═══════════════════════════════════════════════════════════════
        //
        // SECTIONS PRIORITAIRES (consommées par le scoring et l'IA) :
        //   1. OS          → snapshot.metrics["os"][...]         (uptime, version, build)
        //   2. Memory      → snapshot.metrics["memory"][...]     (RAM, usage, pagefile)
        //   3. Security    → snapshot.metrics["security"][...]   (AV, firewall, UAC, BitLocker)
        //   4. Stability   → snapshot.metrics["stability"][...]  (BSOD, WHEA, reliability)
        //   5. Storage     → snapshot.metrics["storage"][...]    (SMART, disk health)
        //   6. Network     → snapshot.metrics["network"][...]    (adapters, speed)
        //   7. Updates     → snapshot.metrics["updates"][...]    (pending, last install)
        //
        // SECTIONS SECONDAIRES (contexte, pas de scoring direct) :
        //   8. Startup     → snapshot.metrics["startup"][...]    (boot items count)
        //   9. Devices     → snapshot.metrics["devices"][...]    (driver issues)
        //  10. Boot        → snapshot.metrics["boot"][...]       (UEFI, SecureBoot)
        //
        // HIÉRARCHIE IA (ordre de priorité pour l'interprétation) :
        //   CRITIQUE : WHEA/Kernel-Power 41/BugCheck/SMART failure > Sécurité désactivée
        //   ÉLEVÉ    : Driver TDR/GPU crash > Mises à jour critiques > RAM saturée
        //   MOYEN    : Startup excessif > Performances dégradées > Réseau lent
        //   FAIBLE   : Logs applicatifs > Cosmétique
        //
        // RÈGLE CONTRACTUELLE :
        //   Toute métrique absente DOIT avoir available=false + reason explicite.
        //   Le JSON final ne contient aucun champ null implicite pour les métriques critiques.
        // ═══════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Maps all PowerShell sections to the snapshot (see mapping contract above)
        /// </summary>
        public DiagnosticSnapshotBuilder AddPowerShellData(JsonElement? psRoot)
        {
            if (psRoot == null || psRoot.Value.ValueKind != JsonValueKind.Object)
            {
                LogBuild("[AddPowerShellData] No PS data available");
                return this;
            }
            
            var root = psRoot.Value;
            // Resolve sections: top-level "sections" or "scan_powershell"."sections" (depending on JSON source)
            if (!TryGetPropertyCaseInsensitive(root, out var sections, "sections") && root.ValueKind == JsonValueKind.Object
                && TryGetPropertyCaseInsensitive(root, out var scanPs, "scan_powershell", "scanPowershell")
                && scanPs.ValueKind == JsonValueKind.Object)
                TryGetPropertyCaseInsensitive(scanPs, out sections, "sections");
            
            // Map sections to snapshot domains (skip empty/error sections = noise filter)
            if (sections.ValueKind == JsonValueKind.Object)
            {
                int mapped = 0, skipped = 0;
                
                // Prioritaires (scoring + IA) — alias pour tolérer les variations de noms PS
                mapped += TryMapSection(sections, AddOsMetricsFromPs, ref skipped, "OS");
                mapped += TryMapSection(sections, AddMemoryMetricsFromPs, ref skipped, "Memory", "MemoryInfo");
                mapped += TryMapSection(sections, AddSecurityMetricsFromPs, ref skipped, "Security");
                mapped += TryMapSection(sections, AddStabilityMetricsFromPs, ref skipped, "Stability", "EventLogs", "ReliabilityHistory");
                mapped += TryMapSection(sections, AddStorageExtrasFromPs, ref skipped, "Storage");
                mapped += TryMapSection(sections, AddNetworkMetricsFromPs, ref skipped, "Network");
                mapped += TryMapSection(sections, AddUpdatesMetricsFromPs, ref skipped, "WindowsUpdate", "Updates", "WindowsUpdateInfo");
                
                // Secondaires (contexte)
                mapped += TryMapSection(sections, AddStartupMetricsFromPs, ref skipped, "StartupPrograms", "Startup", "StartupInfo");
                mapped += TryMapSection(sections, AddDevicesMetricsFromPs, ref skipped, "DevicesDrivers", "Devices", "PnPDevices");
                mapped += TryMapSection(sections, AddBootMetricsFromPs, ref skipped, "Boot", "PerformanceCounters", "DynamicSignals");
                
                AddPowerShellSummary(sections);
                
                LogBuild($"[AddPowerShellData] Mapped {mapped} sections, skipped {skipped} empty/error sections");
            }
            
            // Map machine info from MachineIdentity and OS sections
            AddMachineInfoFromPs(root);
            
            return this;
        }
        
        private void AddMachineInfoFromPs(JsonElement root)
        {
            try
            {
                if (!TryGetPropertyCaseInsensitive(root, out var sections, "sections")
                    && (!TryGetPropertyCaseInsensitive(root, out var scanPs, "scan_powershell", "scanPowershell") || scanPs.ValueKind != JsonValueKind.Object
                        || !TryGetPropertyCaseInsensitive(scanPs, out sections, "sections")))
                    return;
                
                JsonElement machineId = default;
                // From MachineIdentity (script uses: computerName, osBuild, lastBoot, uptimeDays, uptimeHours)
                if (TryGetSectionData(sections, out machineId, "MachineIdentity"))
                {
                    _snapshot.Machine.Hostname = GetStringValue(machineId, "ComputerName", "computerName") ?? _snapshot.Machine.Hostname;
                    _snapshot.Machine.OsBuild = GetStringValue(machineId, "osBuild", "OsBuild") ?? _snapshot.Machine.OsBuild;
                    _snapshot.Machine.LastBootTime = GetStringValue(machineId, "lastBoot", "LastBoot", "LastBootUpTime") ?? _snapshot.Machine.LastBootTime;
                    var uptimeDays = TryGetInt(machineId, "uptimeDays", "UptimeDays");
                    var uptimeHours = TryGetInt(machineId, "uptimeHours", "UptimeHours");
                    if (uptimeDays.HasValue || uptimeHours.HasValue)
                        _snapshot.Machine.Uptime = $"{(uptimeDays ?? 0)}j {(uptimeHours ?? 0)}h";
                    if (TryGetPropertyCaseInsensitive(machineId, out var ram, "TotalRAM_GB", "TotalRamGB", "TotalRAM"))
                        _snapshot.Machine.TotalRamGB = GetDoubleValue(ram);
                }
                
                // From OS section (script uses: caption, architecture, displayVersion — no Build/LastBoot in OS)
                if (TryGetSectionData(sections, out var os, "OS"))
                {
                    _snapshot.Machine.OsVersion = GetStringValue(os, "Caption", "caption", "OSName") ?? _snapshot.Machine.OsVersion;
                    _snapshot.Machine.OsBuild = GetStringValue(os, "BuildNumber", "buildNumber", "Version", "version") ?? _snapshot.Machine.OsBuild;
                    _snapshot.Machine.Architecture = GetStringValue(os, "OSArchitecture", "architecture") ?? _snapshot.Machine.Architecture;
                    _snapshot.Machine.InstallDate = GetStringValue(os, "InstallDate", "installDate");
                    _snapshot.Machine.LastBootTime = GetStringValue(os, "LastBootUpTime", "lastBootUpTime") ?? _snapshot.Machine.LastBootTime;
                    _snapshot.Machine.Uptime = GetStringValue(os, "Uptime", "uptime") ?? _snapshot.Machine.Uptime;
                }
                
                // From CPU section (script uses: cpus[0].name)
                if (TryGetSectionData(sections, out var cpuSection, "CPU") && TryGetPropertyCaseInsensitive(cpuSection, out var cpusEl, "cpus") && cpusEl.ValueKind == JsonValueKind.Array)
                {
                    var first = cpusEl.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object)
                        _snapshot.Machine.CpuName = GetStringValue(first, "name", "Name") ?? _snapshot.Machine.CpuName;
                }
                if (string.IsNullOrEmpty(_snapshot.Machine.CpuName) && machineId.ValueKind == JsonValueKind.Object)
                    _snapshot.Machine.CpuName = GetStringValue(machineId, "ProcessorName", "processorName");
                
                // From Memory section (script uses: totalGB)
                if (TryGetSectionData(sections, out var mem, "Memory", "MemoryInfo") && TryGetPropertyCaseInsensitive(mem, out var totalEl, "totalGB", "TotalGB", "TotalMemoryGB"))
                    _snapshot.Machine.TotalRamGB = GetDoubleValue(totalEl);
                
                FillMachineFallbacksFromEnvironment();
                LogBuild("[AddMachineInfoFromPs] Machine info enriched from PS");
            }
            catch (Exception ex)
            {
                LogBuild($"[AddMachineInfoFromPs] Error: {ex.Message}");
            }
        }
        
        /// <summary>Fill any still-empty Machine fields from C# so the report never shows "Non disponible" when we can derive a value.</summary>
        private void FillMachineFallbacksFromEnvironment()
        {
            if (string.IsNullOrEmpty(_snapshot.Machine.OsBuild))
                _snapshot.Machine.OsBuild = Environment.OSVersion.Version?.ToString();
            if (string.IsNullOrEmpty(_snapshot.Machine.Architecture))
                _snapshot.Machine.Architecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
        }
        
        private void AddOsMetricsFromPs(JsonElement sections)
        {
            var osMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                if (TryGetSectionData(sections, out var os, "OS"))
                {
                    osMetrics["caption"] = MetricFromString(os, "Caption", "PS/OS");
                    osMetrics["version"] = MetricFromString(os, "Version", "PS/OS");
                    osMetrics["buildNumber"] = MetricFromString(os, "BuildNumber", "PS/OS");
                    osMetrics["installDate"] = MetricFromString(os, "InstallDate", "PS/OS");
                    osMetrics["lastBootTime"] = MetricFromString(os, "LastBootUpTime", "PS/OS");
                    osMetrics["uptime"] = MetricFromString(os, "Uptime", "PS/OS");
                    osMetrics["architecture"] = MetricFromString(os, "OSArchitecture", "PS/OS");
                }
                
                if (TryGetSectionData(sections, out var integrity, "SystemIntegrity"))
                {
                    osMetrics["sfcStatus"] = MetricFromString(integrity, "SfcStatus", "PS/SystemIntegrity");
                    osMetrics["dismHealth"] = MetricFromString(integrity, "DismHealth", "PS/SystemIntegrity");
                }
                
                if (osMetrics.Count > 0)
                {
                    _snapshot.Metrics["os"] = osMetrics;
                    LogBuild($"[AddOsMetricsFromPs] Added {osMetrics.Count} OS metrics");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddOsMetricsFromPs] Error: {ex.Message}");
            }
        }
        
        private void AddMemoryMetricsFromPs(JsonElement sections)
        {
            var memMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                if (TryGetSectionData(sections, out var mem, "Memory", "MemoryInfo"))
                {
                    // Script keys first: totalGB, freeGB, usedPercent (Total_PS_PC_Scan_v7.0.ps1)
                    memMetrics["totalGB"] = MetricFromNumber(mem, "GB", "PS/Memory", "totalGB", "TotalMemoryGB");
                    memMetrics["availableGB"] = MetricFromNumber(mem, "GB", "PS/Memory", "freeGB", "AvailableMemoryGB", "FreeMemoryGB");
                    memMetrics["usedPercent"] = MetricFromNumber(mem, "%", "PS/Memory", "usedPercent", "UsedMemoryPercent");
                    memMetrics["freePercent"] = MetricFromNumber(mem, "%", "PS/Memory", "freePercent", "FreeMemoryPercent");
                    memMetrics["commitTotalGB"] = MetricFromNumber(mem, "GB", "PS/Memory", "commitTotalGB", "CommitTotalGB");
                    memMetrics["commitUsedGB"] = MetricFromNumber(mem, "GB", "PS/Memory", "commitUsedGB", "CommitUsedGB");
                    memMetrics["pageFileUsagePercent"] = MetricFromNumber(mem, "%", "PS/Memory", "pageFileUsagePercent", "PageFileUsagePercent");
                }
                
                if (memMetrics.Count > 0)
                {
                    _snapshot.Metrics["memory"] = memMetrics;
                    LogBuild($"[AddMemoryMetricsFromPs] Added {memMetrics.Count} memory metrics");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddMemoryMetricsFromPs] Error: {ex.Message}");
            }
        }
        
        private void AddNetworkMetricsFromPs(JsonElement sections)
        {
            var netMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                if (TryGetSectionData(sections, out var net, "Network"))
                {
                    // Adapters info - script may use "adapters" or "Adapters"; handle array and object
                    if (TryGetPropertyCaseInsensitive(net, out var adapters, "adapters", "Adapters"))
                    {
                        int adapterCount = 0;
                        if (adapters.ValueKind == JsonValueKind.Array)
                            adapterCount = adapters.GetArrayLength();
                        else if (adapters.ValueKind == JsonValueKind.Object)
                            adapterCount = 1;
                        netMetrics["adapterCount"] = MetricFactory.CreateAvailable(adapterCount, "count", "PS/Network", 100);
                    }
                    var adapterCountVal = TryGetInt(net, "adapterCount", "AdapterCount");
                    if (adapterCountVal.HasValue && !netMetrics.ContainsKey("adapterCount"))
                        netMetrics["adapterCount"] = MetricFactory.CreateAvailable(adapterCountVal.Value, "count", "PS/Network", 100);

                    netMetrics["defaultGateway"] = MetricFromString(net, "PS/Network", "defaultGateway", "DefaultGateway");
                    netMetrics["dnsServers"] = MetricFromString(net, "PS/Network", "dnsServers", "DnsServers");
                    netMetrics["publicIP"] = MetricFromString(net, "PS/Network", "publicIP", "PublicIP");
                }
                
                if (TryGetSectionData(sections, out var latency, "NetworkLatency"))
                {
                    // Structure réelle : latency.ping[] = [{target, latencyMs, success, skipped}, ...]
                    if (latency.TryGetProperty("ping", out var pingArray) && pingArray.ValueKind == JsonValueKind.Array)
                    {
                        double? googleLatency = null, cloudflareLatency = null;
                        var latencies = new List<double>();

                        foreach (var entry in pingArray.EnumerateArray())
                        {
                            if (!entry.TryGetProperty("success", out var success) || !success.GetBoolean()) continue;
                            if (!entry.TryGetProperty("latencyMs", out var lat) || lat.ValueKind != JsonValueKind.Number) continue;

                            var ms = lat.GetDouble();
                            latencies.Add(ms);

                            if (entry.TryGetProperty("target", out var target))
                            {
                                var t = target.GetString() ?? "";
                                if (t.Contains("8.8.8.8"))  googleLatency = ms;
                                if (t.Contains("1.1.1.1"))  cloudflareLatency = ms;
                            }
                        }

                        if (googleLatency.HasValue)
                            netMetrics["pingGoogle"] = MetricFactory.CreateAvailable(googleLatency.Value, "ms", "PS/NetworkLatency", 100);
                        if (cloudflareLatency.HasValue)
                            netMetrics["pingCloudflare"] = MetricFactory.CreateAvailable(cloudflareLatency.Value, "ms", "PS/NetworkLatency", 100);
                        if (latencies.Count > 0)
                            netMetrics["avgLatency"] = MetricFactory.CreateAvailable(latencies.Average(), "ms", "PS/NetworkLatency", 100);
                    }
                }
                
                if (netMetrics.Count > 0)
                {
                    _snapshot.Metrics["network"] = netMetrics;
                    LogBuild($"[AddNetworkMetricsFromPs] Added {netMetrics.Count} network metrics");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddNetworkMetricsFromPs] Error: {ex.Message}");
            }
        }
        
        private void AddUpdatesMetricsFromPs(JsonElement sections)
        {
            var updateMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                if (TryGetSectionData(sections, out var updates, "WindowsUpdate", "Updates", "WindowsUpdateInfo"))
                {
                    var pending = TryGetInt(updates, "pendingCount", "PendingCount", "PendingUpdatesCount", "pending_count");
                    if (pending.HasValue)
                        updateMetrics["pendingCount"] = MetricFactory.CreateAvailable(pending.Value, "count", "PS/WindowsUpdate", 100);

                    var lastCheck = TryGetStringAny(updates, "LastSearchDate", "lastSearchDate", "LastCheck", "lastCheck");
                    if (!string.IsNullOrEmpty(lastCheck))
                        updateMetrics["lastCheckDate"] = MetricFactory.CreateAvailable(lastCheck, "", "PS/WindowsUpdate", 100);

                    var lastInstall = TryGetStringAny(updates, "LastInstallDate", "lastInstallDate", "LastUpdateDate", "lastUpdateDate", "LastInstalled");
                    if (!string.IsNullOrEmpty(lastInstall))
                        updateMetrics["lastInstallDate"] = MetricFactory.CreateAvailable(lastInstall, "", "PS/WindowsUpdate", 100);
                    
                    if (TryGetPropertyCaseInsensitive(updates, out var pendingUpdates, "PendingUpdates", "pendingUpdates", "updates"))
                    {
                        int pendingCount = 0;
                        if (pendingUpdates.ValueKind == JsonValueKind.Array)
                            pendingCount = pendingUpdates.GetArrayLength();
                        if (pendingCount > 0)
                            updateMetrics["pendingUpdatesCount"] = MetricFactory.CreateAvailable(pendingCount, "count", "PS/WindowsUpdate", 100);
                    }

                    var reboot = TryGetBool(updates, "rebootRequired", "RebootRequired", "RebootPending", "NeedsReboot");
                    if (reboot.HasValue)
                        updateMetrics["rebootRequired"] = MetricFactory.CreateAvailable(reboot.Value, "bool", "PS/WindowsUpdate", 100);
                }
                
                if (updateMetrics.Count > 0)
                {
                    _snapshot.Metrics["updates"] = updateMetrics;
                    LogBuild($"[AddUpdatesMetricsFromPs] Added {updateMetrics.Count} update metrics");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddUpdatesMetricsFromPs] Error: {ex.Message}");
            }
        }
        
        private void AddSecurityMetricsFromPs(JsonElement sections)
        {
            var secMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                if (TryGetSectionData(sections, out var sec, "Security"))
                {
                    // Script keys: defenderRTP, defenderEnabled, antivirusProducts (array), firewall (object), uacEnabled
                    var avStatus = GetStringValue(sec, "AntivirusStatus", "antivirusStatus");
                    if (string.IsNullOrEmpty(avStatus) && TryGetPropertyCaseInsensitive(sec, out var avProducts, "antivirusProducts"))
                    {
                        if (avProducts.ValueKind == JsonValueKind.Array && avProducts.GetArrayLength() > 0)
                            avStatus = string.Join(", ", avProducts.EnumerateArray().Select(e => e.GetString() ?? ""));
                        else if (TryGetPropertyCaseInsensitive(sec, out var defEnabled, "defenderEnabled", "defenderRTP"))
                            avStatus = defEnabled.ValueKind == JsonValueKind.True ? "Actif" : (defEnabled.ValueKind == JsonValueKind.False ? "Inactif" : defEnabled.GetRawText());
                    }
                    if (!string.IsNullOrEmpty(avStatus))
                        secMetrics["antivirusStatus"] = MetricFactory.CreateAvailable(avStatus, "", "PS/Security", 100);
                    else
                        secMetrics["antivirusStatus"] = MetricFromString(sec, "PS/Security", "antivirusStatus", "AntivirusStatus");

                    secMetrics["antivirusName"] = MetricFromString(sec, "PS/Security", "antivirusName", "AntivirusName");
                    if (!secMetrics["antivirusName"].Available && TryGetPropertyCaseInsensitive(sec, out var avList, "antivirusProducts") && avList.ValueKind == JsonValueKind.Array && avList.GetArrayLength() > 0)
                        secMetrics["antivirusName"] = MetricFactory.CreateAvailable(avList[0].GetString() ?? "", "", "PS/Security", 100);

                    var fwStatus = GetStringValue(sec, "FirewallStatus", "firewallStatus");
                    if (string.IsNullOrEmpty(fwStatus) && sec.TryGetProperty("firewall", out var fwObj) && fwObj.ValueKind == JsonValueKind.Object)
                    {
                        var parts = new List<string>();
                        foreach (var p in fwObj.EnumerateObject())
                        {
                            var on = p.Value.ValueKind == JsonValueKind.True || (p.Value.ValueKind == JsonValueKind.Number && p.Value.GetInt32() != 0);
                            parts.Add($"{p.Name}: {(on ? "Actif" : "Inactif")}");
                        }
                        if (parts.Count > 0) fwStatus = string.Join(", ", parts);
                    }
                    if (!string.IsNullOrEmpty(fwStatus))
                        secMetrics["firewallStatus"] = MetricFactory.CreateAvailable(fwStatus, "", "PS/Security", 100);
                    else
                        secMetrics["firewallStatus"] = MetricFromString(sec, "PS/Security", "firewallStatus", "FirewallStatus");

                    secMetrics["uacEnabled"] = MetricFromBool(sec, "PS/Security", "uacEnabled", "UacEnabled");
                    secMetrics["defenderRTP"] = MetricFromBool(sec, "PS/Security", "defenderRTP", "DefenderRTP", "RealTimeProtectionEnabled");
                    secMetrics["defenderEnabled"] = MetricFromBool(sec, "PS/Security", "defenderEnabled", "DefenderEnabled", "AntivirusEnabled");
                    secMetrics["secureBootEnabled"] = MetricFromBool(sec, "PS/Security", "secureBootEnabled", "SecureBootEnabled");
                    secMetrics["bitlockerStatus"] = MetricFromString(sec, "PS/Security", "bitlockerStatus", "BitlockerStatus");
                }
                
                if (secMetrics.Count > 0)
                {
                    _snapshot.Metrics["security"] = secMetrics;
                    LogBuild($"[AddSecurityMetricsFromPs] Added {secMetrics.Count} security metrics");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddSecurityMetricsFromPs] Error: {ex.Message}");
            }
        }
        
        private void AddStartupMetricsFromPs(JsonElement sections)
        {
            var startupMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                if (TryGetSectionData(sections, out var startup, "StartupPrograms", "Startup", "StartupInfo"))
                {
                    int count = 0;
                    int highImpact = 0;
                    
                    if (startup.ValueKind == JsonValueKind.Array)
                    {
                        count = startup.GetArrayLength();
                        foreach (var item in startup.EnumerateArray())
                        {
                            var impact = GetStringValue(item, "StartupImpact");
                            if (impact?.Equals("High", StringComparison.OrdinalIgnoreCase) == true)
                                highImpact++;
                        }
                    }
                    else if (TryGetPropertyCaseInsensitive(startup, out var items, "Items", "startupItems", "StartupItems", "items", "list"))
                    {
                        if (items.ValueKind == JsonValueKind.Array)
                            count = items.GetArrayLength();
                    }
                    else
                    {
                        var countValue = TryGetInt(startup, "startupCount", "StartupCount", "count", "total", "Total");
                        if (countValue.HasValue)
                            count = countValue.Value;
                    }
                    
                    startupMetrics["totalCount"] = MetricFactory.CreateAvailable(count, "count", "PS/StartupPrograms", 100);
                    startupMetrics["highImpactCount"] = MetricFactory.CreateAvailable(highImpact, "count", "PS/StartupPrograms", 100);
                }
                
                if (startupMetrics.Count > 0)
                {
                    _snapshot.Metrics["startup"] = startupMetrics;
                    LogBuild($"[AddStartupMetricsFromPs] Added {startupMetrics.Count} startup metrics");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddStartupMetricsFromPs] Error: {ex.Message}");
            }
        }
        
        private void AddDevicesMetricsFromPs(JsonElement sections)
        {
            var devMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                if (TryGetSectionData(sections, out var devices, "DevicesDrivers", "Devices", "PnPDevices"))
                {
                    int totalDevices = 0;
                    int problemDevices = 0;
                    int outdatedDrivers = 0;
                    
                    // Handle different formats
                    JsonElement? deviceList = null;
                    if (devices.ValueKind == JsonValueKind.Array)
                        deviceList = devices;
                    else if (TryGetPropertyCaseInsensitive(devices, out var devArray, "Devices", "devices", "items", "list"))
                        deviceList = devArray;
                    
                    if (deviceList.HasValue && deviceList.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var dev in deviceList.Value.EnumerateArray())
                        {
                            totalDevices++;
                            var status = GetStringValue(dev, "Status");
                            if (status != null && !status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                                problemDevices++;
                        }
                    }

                    // Prefer explicit counts if provided
                    var totalCountValue = TryGetInt(devices, "deviceCount", "DeviceCount", "totalDevices", "TotalDevices", "total", "Total");
                    if (totalCountValue.HasValue)
                        totalDevices = totalCountValue.Value;

                    var problemCountValue = TryGetInt(devices, "problemDeviceCount", "ProblemDeviceCount", "problemCount", "ProblemCount", "errorCount", "ErrorCount");
                    if (problemCountValue.HasValue)
                        problemDevices = problemCountValue.Value;
                    
                    devMetrics["totalDevices"] = MetricFactory.CreateAvailable(totalDevices, "count", "PS/DevicesDrivers", 100);
                    devMetrics["problemDevices"] = MetricFactory.CreateAvailable(problemDevices, "count", "PS/DevicesDrivers", 100);
                    devMetrics["outdatedDrivers"] = MetricFactory.CreateAvailable(outdatedDrivers, "count", "PS/DevicesDrivers", 100);
                }
                
                if (devMetrics.Count > 0)
                {
                    _snapshot.Metrics["devices"] = devMetrics;
                    LogBuild($"[AddDevicesMetricsFromPs] Added {devMetrics.Count} device metrics");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddDevicesMetricsFromPs] Error: {ex.Message}");
            }
        }

        /// <summary>Essaie de mapper une section PS (supporte plusieurs alias). Retourne 1 si mappée, 0 sinon.</summary>
        private int TryMapSection(JsonElement sections, Action<JsonElement> mapper, ref int skipped, params string[] sectionNames)
        {
            try
            {
                if (!TryGetSectionData(sections, out var data, sectionNames))
                {
                    skipped++;
                    _unmappedPsSections.Add(sectionNames[0]);
                    LogBuild($"[NoiseFilter] Section '{string.Join("/", sectionNames)}' absente ou vide — ignorée");
                    return 0;
                }
                // Vérifier si la section est en erreur (status = "Error" ou "Failed")
                foreach (var name in sectionNames)
                {
                    if (TryGetPropertyCaseInsensitive(sections, out var sectionObj, name)
                        && sectionObj.ValueKind == JsonValueKind.Object
                        && TryGetPropertyCaseInsensitive(sectionObj, out var statusEl, "status")
                        && statusEl.ValueKind == JsonValueKind.String)
                    {
                        var status = statusEl.GetString()?.ToLowerInvariant();
                        if (status == "error" || status == "failed")
                        {
                            skipped++;
                            _unmappedPsSections.Add(name);
                            LogBuild($"[NoiseFilter] Section '{name}' en erreur (status={status}) — ignorée");
                            return 0;
                        }
                        break; // Trouvé une section non-erreur, on continue
                    }
                }
                mapper(sections);
                _mappedPsSections.Add(sectionNames[0]);
                return 1;
            }
            catch (Exception ex)
            {
                skipped++;
                _unmappedPsSections.Add(sectionNames[0]);
                LogBuild($"[NoiseFilter] Exception mapping section '{sectionNames[0]}': {ex.Message}");
                return 0;
            }
        }
        
        private void AddPowerShellSummary(JsonElement sections)
        {
            try
            {
                var summary = new PowerShellSummary();
                var hasAny = false;

                // Updates
                if (TryGetSectionData(sections, out var updates, "WindowsUpdate", "Updates", "WindowsUpdateInfo") && IsNonEmpty(updates))
                {
                    var updatesSummary = new UpdatesSummary();
                    var pending = TryGetInt(updates, "pendingCount", "PendingCount", "PendingUpdatesCount", "pending_count");
                    if (pending.HasValue)
                    {
                        updatesSummary.PendingCount = pending.Value;
                        hasAny = true;
                    }
                    var reboot = TryGetBool(updates, "rebootRequired", "RebootRequired", "RebootPending", "NeedsReboot");
                    if (reboot.HasValue)
                    {
                        updatesSummary.RebootRequired = reboot.Value;
                        hasAny = true;
                    }
                    var lastUpdate = TryGetStringAny(updates, "lastUpdateDate", "LastUpdateDate", "lastInstallDate", "LastInstallDate", "LastInstalled", "LastCheck");
                    if (!string.IsNullOrEmpty(lastUpdate))
                    {
                        updatesSummary.LastUpdate = lastUpdate;
                        hasAny = true;
                    }
                    summary.Updates = updatesSummary;
                }

                // Startup
                if (TryGetSectionData(sections, out var startup, "StartupPrograms", "Startup", "StartupInfo") && IsNonEmpty(startup))
                {
                    var startupSummary = new StartupSummary();
                    var count = TryGetInt(startup, "startupCount", "StartupCount", "count", "total", "Total");
                    if (count.HasValue)
                    {
                        startupSummary.Count = count.Value;
                        hasAny = true;
                    }

                    if (TryGetPropertyCaseInsensitive(startup, out var items, "startupItems", "StartupItems", "items", "list", "apps", "programs") &&
                        items.ValueKind == JsonValueKind.Array)
                    {
                        var names = items.EnumerateArray()
                            .Select(i => TryGetStringAny(i, "name", "Name", "DisplayName", "Command"))
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => n!)
                            .Take(10)
                            .ToList();
                        if (names.Count > 0)
                        {
                            startupSummary.TopItems = names;
                            hasAny = true;
                        }
                    }

                    summary.Startup = startupSummary;
                }

                // Installed applications
                if (TryGetSectionData(sections, out var apps, "InstalledApplications", "Applications") && IsNonEmpty(apps))
                {
                    var appsSummary = new ApplicationsSummary();
                    var count = TryGetInt(apps, "totalCount", "TotalCount", "installedCount", "InstalledCount", "count", "total");
                    if (count.HasValue)
                    {
                        appsSummary.InstalledCount = count.Value;
                        hasAny = true;
                    }
                    var lastInstalled = TryGetStringAny(apps, "lastInstallDate", "LastInstallDate", "lastInstalled", "LastInstalled");
                    if (!string.IsNullOrEmpty(lastInstalled))
                    {
                        appsSummary.LastInstalled = lastInstalled;
                        hasAny = true;
                    }
                    summary.Apps = appsSummary;
                }

                // Devices / Drivers
                if (TryGetSectionData(sections, out var devices, "DevicesDrivers", "Devices", "PnPDevices") && IsNonEmpty(devices))
                {
                    var devicesSummary = new DevicesSummary();
                    var problemCount = TryGetInt(devices, "problemDeviceCount", "ProblemDeviceCount", "problemCount", "ProblemCount", "errorCount", "ErrorCount");
                    if (problemCount.HasValue)
                    {
                        devicesSummary.ProblemDeviceCount = problemCount.Value;
                        hasAny = true;
                    }

                    if (TryGetPropertyCaseInsensitive(devices, out var problems, "problemDevices", "ProblemDevices", "errors", "Errors") &&
                        problems.ValueKind == JsonValueKind.Array)
                    {
                        var names = problems.EnumerateArray()
                            .Select(i => TryGetStringAny(i, "name", "Name", "deviceName", "DeviceName"))
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => n!)
                            .Take(5)
                            .ToList();
                        if (names.Count > 0)
                        {
                            devicesSummary.TopProblemDevices = names;
                            hasAny = true;
                        }
                    }

                    summary.Devices = devicesSummary;
                }

                // Printers
                if (TryGetSectionData(sections, out var printers, "Printers", "PrinterInfo") && IsNonEmpty(printers))
                {
                    var printersSummary = new PrintersSummary();
                    var count = TryGetInt(printers, "printerCount", "PrinterCount", "count", "total");
                    if (count.HasValue)
                    {
                        printersSummary.Count = count.Value;
                        hasAny = true;
                    }

                    if (TryGetPropertyCaseInsensitive(printers, out var printerArr, "printers", "Printers", "items", "list") &&
                        printerArr.ValueKind == JsonValueKind.Array)
                    {
                        var names = printerArr.EnumerateArray()
                            .Select(p => TryGetStringAny(p, "name", "Name", "printerName", "PrinterName"))
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => n!)
                            .Take(5)
                            .ToList();
                        if (names.Count > 0)
                        {
                            printersSummary.Names = names;
                            hasAny = true;
                        }
                    }

                    summary.Printers = printersSummary;
                }

                // Audio
                if (TryGetSectionData(sections, out var audio, "Audio", "AudioDevices") && IsNonEmpty(audio))
                {
                    var audioSummary = new AudioSummary();
                    var count = TryGetInt(audio, "deviceCount", "DeviceCount", "count", "total");
                    if (count.HasValue)
                    {
                        audioSummary.Count = count.Value;
                        hasAny = true;
                    }

                    if (TryGetPropertyCaseInsensitive(audio, out var audioArr, "devices", "Devices", "items", "list") &&
                        audioArr.ValueKind == JsonValueKind.Array)
                    {
                        var names = audioArr.EnumerateArray()
                            .Select(a => TryGetStringAny(a, "name", "Name", "deviceName", "DeviceName"))
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => n!)
                            .Take(5)
                            .ToList();
                        if (names.Count > 0)
                        {
                            audioSummary.Names = names;
                            hasAny = true;
                        }
                    }

                    summary.Audio = audioSummary;
                }

                if (hasAny)
                {
                    _snapshot.PsSummary = summary;
                    LogBuild("[AddPowerShellSummary] Added PS summary");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddPowerShellSummary] Error: {ex.Message}");
            }
        }
        
        private void AddStabilityMetricsFromPs(JsonElement sections)
        {
            var stabMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                // Event Logs - Structure réelle : events.logs.System.criticalCount / errorCount
                //                                 events.logs.Application.criticalCount / errorCount
                if (TryGetSectionData(sections, out var events, "EventLogs", "EventLogInfo"))
                {
                    int totalCritical = 0, totalError = 0;

                    if (events.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var logSource in logs.EnumerateObject())
                        {
                            if (logSource.Value.TryGetProperty("criticalCount", out var cc) && cc.ValueKind == JsonValueKind.Number)
                                totalCritical += cc.GetInt32();
                            if (logSource.Value.TryGetProperty("errorCount", out var ec) && ec.ValueKind == JsonValueKind.Number)
                                totalError += ec.GetInt32();
                        }
                    }

                    stabMetrics["criticalEvents24h"] = MetricFactory.CreateAvailable(totalCritical, "count", "PS/EventLogs", 100);
                    stabMetrics["errorEvents24h"] = MetricFactory.CreateAvailable(totalError, "count", "PS/EventLogs", 100);

                    // bsodCount existe à la racine de events
                    if (events.TryGetProperty("bsodCount", out var bsod) && bsod.ValueKind == JsonValueKind.Number)
                        stabMetrics["bsods"] = MetricFactory.CreateAvailable(bsod.GetInt32(), "count", "PS/EventLogs", 100);

                    // warningEvents24h : pas collecté par PS → marquer indisponible explicitement
                    stabMetrics["warningEvents24h"] = MetricFactory.CreateUnavailable("count", "PS/EventLogs", "not_collected_by_ps");
                }
                
                // Reliability History - appCrashes existe → mapper vers appFailures30d
                if (TryGetSectionData(sections, out var reliability, "ReliabilityHistory"))
                {
                    // appCrashes existe → mapper vers appFailures30d
                    if (reliability.TryGetProperty("appCrashes", out var ac) && ac.ValueKind == JsonValueKind.Number)
                        stabMetrics["appFailures30d"] = MetricFactory.CreateAvailable(ac.GetInt32(), "count", "PS/ReliabilityHistory", 100);

                    // eventCount existe comme indicateur général
                    if (reliability.TryGetProperty("eventCount", out var ec) && ec.ValueKind == JsonValueKind.Number)
                        stabMetrics["reliabilityEventCount"] = MetricFactory.CreateAvailable(ec.GetInt32(), "count", "PS/ReliabilityHistory", 100);

                    // ReliabilityIndex et HwFailures30d ne sont pas collectés par PS
                    stabMetrics["reliabilityIndex"] = MetricFactory.CreateUnavailable("", "PS/ReliabilityHistory", "not_collected_by_ps");
                    stabMetrics["hwFailures30d"] = MetricFactory.CreateUnavailable("count", "PS/ReliabilityHistory", "not_collected_by_ps");
                }
                
                // Minidump Analysis - Propriété réelle : minidumpCount (pas "Count")
                if (TryGetSectionData(sections, out var minidump, "MinidumpAnalysis"))
                {
                    if (minidump.TryGetProperty("minidumpCount", out var mc) && mc.ValueKind == JsonValueKind.Number)
                        stabMetrics["minidumpCount"] = MetricFactory.CreateAvailable(mc.GetInt32(), "count", "PS/MinidumpAnalysis", 100);

                    // LastBsod n'est jamais collecté par PS
                    stabMetrics["lastBsodDate"] = MetricFactory.CreateUnavailable("date", "PS/MinidumpAnalysis", "not_collected_by_ps");
                }
                
                if (stabMetrics.Count > 0)
                {
                    _snapshot.Metrics["stability"] = stabMetrics;
                    LogBuild($"[AddStabilityMetricsFromPs] Added {stabMetrics.Count} stability metrics");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddStabilityMetricsFromPs] Error: {ex.Message}");
            }
        }
        
        private void AddBootMetricsFromPs(JsonElement sections)
        {
            var bootMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                if (TryGetSectionData(sections, out var perf, "PerformanceCounters"))
                {
                    bootMetrics["bootTimeSeconds"] = MetricFromNumber(perf, "BootTimeSeconds", "s", "PS/PerformanceCounters");
                    bootMetrics["loginTimeSeconds"] = MetricFromNumber(perf, "LoginTimeSeconds", "s", "PS/PerformanceCounters");
                }
                
                // From DynamicSignals if available
                if (TryGetSectionData(sections, out var signals, "DynamicSignals"))
                {
                    if (signals.TryGetProperty("BootPerformance", out var boot))
                    {
                        bootMetrics["fullBootMs"] = MetricFromNumber(boot, "FullBootMs", "ms", "PS/DynamicSignals");
                        bootMetrics["mainPathMs"] = MetricFromNumber(boot, "MainPathMs", "ms", "PS/DynamicSignals");
                    }
                }
                
                if (bootMetrics.Count > 0)
                {
                    _snapshot.Metrics["boot"] = bootMetrics;
                    LogBuild($"[AddBootMetricsFromPs] Added {bootMetrics.Count} boot metrics");
                }
            }
            catch (Exception ex)
            {
                LogBuild($"[AddBootMetricsFromPs] Error: {ex.Message}");
            }
        }
        
        private void AddStorageExtrasFromPs(JsonElement sections)
        {
            try
            {
                if (!_snapshot.Metrics.ContainsKey("storage"))
                    _snapshot.Metrics["storage"] = new Dictionary<string, NormalizedMetric>();
                    
                var storageMetrics = _snapshot.Metrics["storage"];
                
                if (TryGetSectionData(sections, out var storage, "Storage"))
                {
                    // Script uses "volumes" array with letter, totalGB, freeGB, usedPercent; fallback "Drives" with TotalSizeGB, FreeSpaceGB, UsedPercent
                    var drivesArray = default(JsonElement);
                    if (TryGetPropertyCaseInsensitive(storage, out drivesArray, "volumes", "Volumes", "Drives"))
                    { }
                    else
                        storage.TryGetProperty("Drives", out drivesArray);

                    if (drivesArray.ValueKind == JsonValueKind.Array)
                    {
                        int idx = 0;
                        foreach (var drive in drivesArray.EnumerateArray())
                        {
                            storageMetrics[$"drive_{idx}_totalGB"] = MetricFromNumber(drive, "GB", "PS/Storage", "totalGB", "TotalSizeGB");
                            storageMetrics[$"drive_{idx}_freeGB"] = MetricFromNumber(drive, "GB", "PS/Storage", "freeGB", "FreeSpaceGB");
                            storageMetrics[$"drive_{idx}_usedPercent"] = MetricFromNumber(drive, "%", "PS/Storage", "usedPercent", "UsedPercent");
                            idx++;
                        }
                    }
                }

                // SMART: script has "smart" array under Storage; also try SmartDetails section
                if (TryGetSectionData(sections, out var storageForSmart, "Storage") && TryGetPropertyCaseInsensitive(storageForSmart, out var smartArr, "smart") && smartArr.ValueKind == JsonValueKind.Array)
                {
                    int warnings = 0;
                    foreach (var s in smartArr.EnumerateArray())
                    {
                        if (TryGetPropertyCaseInsensitive(s, out var pred, "predictFailure", "PredictFailure") && pred.ValueKind == JsonValueKind.True)
                            warnings++;
                    }
                    storageMetrics["smartHealthy"] = MetricFactory.CreateAvailable(warnings == 0, "bool", "PS/Storage", 100);
                    storageMetrics["smartWarnings"] = MetricFactory.CreateAvailable(warnings, "count", "PS/Storage", 100);
                }
                if (TryGetSectionData(sections, out var smart, "SmartDetails"))
                {
                    if (!storageMetrics.ContainsKey("smartHealthy"))
                        storageMetrics["smartHealthy"] = MetricFromBool(smart, "PS/SmartDetails", "AllHealthy", "allHealthy");
                    if (!storageMetrics.ContainsKey("smartWarnings"))
                        storageMetrics["smartWarnings"] = MetricFromNumber(smart, "count", "PS/SmartDetails", "WarningCount", "warningCount");
                }

                // Temp files
                if (TryGetSectionData(sections, out var temp, "TempFiles"))
                {
                    storageMetrics["tempFilesSizeMB"] = MetricFromNumber(temp, "MB", "PS/TempFiles", "TotalSizeMB", "totalSizeMB");
                    storageMetrics["tempFilesCount"] = MetricFromNumber(temp, "count", "PS/TempFiles", "FileCount", "fileCount");
                }
                
                LogBuild($"[AddStorageExtrasFromPs] Extended storage with PS data");
            }
            catch (Exception ex)
            {
                LogBuild($"[AddStorageExtrasFromPs] Error: {ex.Message}");
            }
        }
        
        #endregion
        
        #region C# Collector Integration (Schema 2.1.0)
        
        /// <summary>
        /// Add process telemetry from C# collector
        /// </summary>
        public DiagnosticSnapshotBuilder AddProcessTelemetry(ProcessTelemetryResult? telemetry)
        {
            if (telemetry == null || !telemetry.Available)
            {
                _snapshot.ProcessSummary = new ProcessSummary { Available = false };
                LogBuild("[AddProcessTelemetry] No process telemetry available");
                return this;
            }
            
            var summary = new ProcessSummary
            {
                Available = true,
                TotalProcessCount = telemetry.TotalProcessCount,
                Source = telemetry.Source
            };
            
            // Top CPU process
            if (telemetry.TopByCpu?.Count > 0)
            {
                var top = telemetry.TopByCpu[0];
                summary.TopCpuProcess = top.Name;
                summary.TopCpuPercent = top.CpuPercent;
            }
            
            // Top memory process
            if (telemetry.TopByMemory?.Count > 0)
            {
                var top = telemetry.TopByMemory[0];
                summary.TopMemoryProcess = top.Name;
                summary.TopMemoryMB = top.WorkingSetMB;
            }
            
            _snapshot.ProcessSummary = summary;
            
            // Also add to metrics
            var procMetrics = new Dictionary<string, NormalizedMetric>
            {
                ["totalProcessCount"] = MetricFactory.CreateAvailable(telemetry.TotalProcessCount, "count", "ProcessTelemetryCollector", 100),
                ["accessDeniedCount"] = MetricFactory.CreateAvailable(telemetry.AccessDeniedCount, "count", "ProcessTelemetryCollector", 100),
                ["topCpuPercent"] = MetricFactory.CreateAvailable(summary.TopCpuPercent, "%", "ProcessTelemetryCollector", 100),
                ["topMemoryMB"] = MetricFactory.CreateAvailable(summary.TopMemoryMB, "MB", "ProcessTelemetryCollector", 100)
            };
            _snapshot.Metrics["processes"] = procMetrics;
            
            LogBuild($"[AddProcessTelemetry] Added process summary: {telemetry.TotalProcessCount} processes");
            return this;
        }
        
        /// <summary>
        /// Add network diagnostics from C# collector
        /// </summary>
        public DiagnosticSnapshotBuilder AddNetworkDiagnostics(NetworkDiagnosticsResult? netDiag)
        {
            if (netDiag == null || !netDiag.Available)
            {
                _snapshot.NetworkSummary = new NetworkSummary { Available = false };
                LogBuild("[AddNetworkDiagnostics] No network diagnostics available");
                return this;
            }
            
            var summary = new NetworkSummary
            {
                Available = true,
                LatencyP50Ms = netDiag.OverallLatencyMsP50,
                LatencyP95Ms = netDiag.OverallLatencyMsP95,
                JitterP95Ms = netDiag.OverallJitterMsP95,
                PacketLossPercent = netDiag.OverallLossPercent,
                DnsP95Ms = netDiag.DnsP95Ms,
                Gateway = netDiag.Gateway,
                Source = netDiag.Source
            };
            
            if (netDiag.Throughput != null)
            {
                summary.DownloadMbps = netDiag.Throughput.DownloadMbpsMedian;
            }
            
            _snapshot.NetworkSummary = summary;
            
            // Also merge into network metrics
            if (!_snapshot.Metrics.ContainsKey("network"))
                _snapshot.Metrics["network"] = new Dictionary<string, NormalizedMetric>();
                
            var netMetrics = _snapshot.Metrics["network"];
            netMetrics["latencyP50Ms"] = MetricFactory.CreateAvailable(summary.LatencyP50Ms, "ms", "NetworkDiagnosticsCollector", 100);
            netMetrics["latencyP95Ms"] = MetricFactory.CreateAvailable(summary.LatencyP95Ms, "ms", "NetworkDiagnosticsCollector", 100);
            netMetrics["jitterP95Ms"] = MetricFactory.CreateAvailable(summary.JitterP95Ms, "ms", "NetworkDiagnosticsCollector", 100);
            netMetrics["packetLossPercent"] = MetricFactory.CreateAvailable(summary.PacketLossPercent, "%", "NetworkDiagnosticsCollector", 100);
            netMetrics["dnsP95Ms"] = MetricFactory.CreateAvailable(summary.DnsP95Ms, "ms", "NetworkDiagnosticsCollector", 100);
            
            if (summary.DownloadMbps.HasValue)
                netMetrics["downloadMbps"] = MetricFactory.CreateAvailable(summary.DownloadMbps.Value, "Mbps", "NetworkDiagnosticsCollector", 100);
            
            // Backfill pingGoogle / pingCloudflare / avgLatency avec données du collecteur C#
            // uniquement si PS n'a pas réussi à les peupler
            if (!netMetrics.ContainsKey("pingGoogle") || !netMetrics["pingGoogle"].Available)
                netMetrics["pingGoogle"] = MetricFactory.CreateAvailable(summary.LatencyP50Ms, "ms", "NetworkDiagnosticsCollector", 80);
            if (!netMetrics.ContainsKey("pingCloudflare") || !netMetrics["pingCloudflare"].Available)
                netMetrics["pingCloudflare"] = MetricFactory.CreateAvailable(summary.LatencyP50Ms, "ms", "NetworkDiagnosticsCollector", 80);
            if (!netMetrics.ContainsKey("avgLatency") || !netMetrics["avgLatency"].Available)
                netMetrics["avgLatency"] = MetricFactory.CreateAvailable(summary.LatencyP50Ms, "ms", "NetworkDiagnosticsCollector", 80);
            
            LogBuild($"[AddNetworkDiagnostics] Added network summary: latency={summary.LatencyP50Ms}ms, loss={summary.PacketLossPercent}%");
            return this;
        }
        
        /// <summary>
        /// Record sensor collection status (Defender/WinRing0 blocking detection)
        /// </summary>
        public DiagnosticSnapshotBuilder RecordSensorStatus(HardwareSensorsResult? sensors, List<string>? exceptions = null)
        {
            var status = _snapshot.SensorStatus ?? new SensorCollectionStatus();
            
            if (exceptions != null && exceptions.Count > 0)
            {
                status.Exceptions = exceptions;
                
                // Detect Defender blocking patterns
                foreach (var ex in exceptions)
                {
                    var exLower = ex.ToLowerInvariant();
                    if (exLower.Contains("access denied") || exLower.Contains("defender") || 
                        exLower.Contains("antivirus") || exLower.Contains("blocked"))
                    {
                        status.BlockedByDefender = true;
                        status.BlockReason = "Antivirus/Defender blocking sensor access";
                        status.UserMessage = "Capteurs bloqués par la sécurité. Exécuter en tant qu'administrateur ou ajouter une exclusion sur le dossier de l'application.";
                    }
                    else if (exLower.Contains("winring0") || exLower.Contains("driver") || 
                             exLower.Contains("kernel") || exLower.Contains("ring0"))
                    {
                        status.BlockedByDriver = true;
                        status.BlockReason = "WinRing0/kernel driver not loaded";
                        status.UserMessage = "Driver de capteurs non chargé. Exécuter en tant qu'administrateur pour activer les capteurs matériels.";
                    }
                }
            }
            
            // Check sensor availability
            if (sensors != null)
            {
                bool hasSensors = false;
                if (sensors.Cpu?.CpuTempC?.Available == true) hasSensors = true;
                if (sensors.Gpu?.GpuTempC?.Available == true) hasSensors = true;
                if (sensors.Disks?.Any(d => d.TempC?.Available == true) == true) hasSensors = true;
                
                status.SensorsAvailable = hasSensors;
                
                if (!hasSensors && !status.BlockedByDefender && !status.BlockedByDriver)
                {
                    // Check individual reasons
                    var reasons = new List<string>();
                    if (sensors.Cpu?.CpuTempC?.Reason != null)
                        reasons.Add($"CPU: {sensors.Cpu.CpuTempC.Reason}");
                    if (sensors.Gpu?.GpuTempC?.Reason != null)
                        reasons.Add($"GPU: {sensors.Gpu.GpuTempC.Reason}");
                        
                    if (reasons.Count > 0)
                    {
                        status.BlockReason = string.Join("; ", reasons);
                        
                        if (reasons.Any(r => r.Contains("Erreur globale")))
                        {
                            status.BlockedByDriver = true;
                            status.UserMessage = "Capteurs bloqués par la sécurité. Exécuter en tant qu'administrateur ou ajouter une exclusion sur le dossier de l'application.";
                        }
                    }
                }
            }
            
            _snapshot.SensorStatus = status;
            LogBuild($"[RecordSensorStatus] SensorsAvailable={status.SensorsAvailable}, BlockedByDefender={status.BlockedByDefender}, BlockedByDriver={status.BlockedByDriver}");
            return this;
        }
        
        #endregion
        
        #region Logging
        
        [System.Diagnostics.Conditional("DEBUG")]
        private void LogBuild(string message)
        {
            _buildLog.Add($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            App.LogMessage(message);
        }
        
        [System.Diagnostics.Conditional("DEBUG")]
        private void WriteLogToTemp()
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_SnapshotBuilder.log");
                var logContent = $"=== DiagnosticSnapshotBuilder Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n" +
                                 string.Join("\n", _buildLog);
                File.AppendAllText(tempPath, logContent + "\n\n");
            }
            catch { /* Ignore logging errors */ }
        }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>Gets a string value from JSON; tries given names case-insensitively so PS output (e.g. caption vs Caption) is found.</summary>
        private static string? GetStringValue(JsonElement element, params string[] propertyNames)
        {
            if (propertyNames == null || propertyNames.Length == 0) return null;
            if (!TryGetPropertyCaseInsensitive(element, out var prop, propertyNames)) return null;
            if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
            if (prop.ValueKind == JsonValueKind.Number) return prop.GetDouble().ToString();
            return null;
        }

        private static bool TryGetSectionData(JsonElement sections, out JsonElement data, params string[] sectionNames)
        {
            data = default;
            if (!TryGetPropertyCaseInsensitive(sections, out var section, sectionNames))
            {
                return false;
            }

            if (section.ValueKind == JsonValueKind.Object && TryGetPropertyCaseInsensitive(section, out var innerData, "data"))
            {
                data = innerData;
                return true;
            }

            // If no "data" wrapper, return section itself
            data = section;
            return true;
        }
        
        private static double? GetDoubleValue(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
                return element.GetDouble();
            if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
            return null;
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement element, out JsonElement value, params string[] names)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out value))
                {
                    return true;
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }

            return false;
        }

        private static int? TryGetInt(JsonElement element, params string[] names)
        {
            if (!TryGetPropertyCaseInsensitive(element, out var val, names))
                return null;

            if (val.ValueKind == JsonValueKind.Number)
                return val.GetInt32();

            if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var parsed))
                return parsed;

            return null;
        }

        private static bool? TryGetBool(JsonElement element, params string[] names)
        {
            if (!TryGetPropertyCaseInsensitive(element, out var val, names))
                return null;

            if (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False)
                return val.GetBoolean();

            if (val.ValueKind == JsonValueKind.Number)
                return val.GetInt32() != 0;

            if (val.ValueKind == JsonValueKind.String)
            {
                var s = val.GetString();
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1")
                    return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0")
                    return false;
            }

            return null;
        }

        private static string? TryGetStringAny(JsonElement element, params string[] names)
        {
            if (!TryGetPropertyCaseInsensitive(element, out var val, names))
                return null;

            if (val.ValueKind == JsonValueKind.String)
                return val.GetString();

            if (val.ValueKind == JsonValueKind.Number)
                return val.GetRawText();

            return null;
        }

        private static bool IsNonEmpty(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Array => element.GetArrayLength() > 0,
                JsonValueKind.Object => element.EnumerateObject().Any(),
                JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
                JsonValueKind.Number => true,
                JsonValueKind.True => true,
                JsonValueKind.False => true,
                _ => false
            };
        }
        
        private static NormalizedMetric MetricFromString(JsonElement parent, string prop, string source)
        {
            if (TryGetPropertyCaseInsensitive(parent, out var val, prop))
            {
                if (val.ValueKind == JsonValueKind.String)
                    return MetricFactory.CreateAvailable(val.GetString() ?? "", "", source, 100);
                if (val.ValueKind == JsonValueKind.Number)
                    return MetricFactory.CreateAvailable(val.GetDouble().ToString(), "", source, 100);
            }
            return MetricFactory.CreateUnavailable("", source, "property_not_found");
        }
        
        private static NormalizedMetric MetricFromNumber(JsonElement parent, string prop, string unit, string source)
        {
            if (TryGetPropertyCaseInsensitive(parent, out var val, prop))
            {
                if (val.ValueKind == JsonValueKind.Number)
                    return MetricFactory.CreateAvailable(Math.Round(val.GetDouble(), 2), unit, source, 100);
                if (val.ValueKind == JsonValueKind.String && double.TryParse(val.GetString(), out var d))
                    return MetricFactory.CreateAvailable(Math.Round(d, 2), unit, source, 100);
            }
            return MetricFactory.CreateUnavailable(unit, source, "property_not_found");
        }

        /// <summary>Tries each property name in order; returns first available metric (script keys first, then fallbacks).</summary>
        private static NormalizedMetric MetricFromNumber(JsonElement parent, string unit, string source, params string[] propNames)
        {
            foreach (var prop in propNames)
            {
                var m = MetricFromNumber(parent, prop, unit, source);
                if (m.Available) return m;
            }
            return MetricFactory.CreateUnavailable(unit, source, "property_not_found");
        }

        private static NormalizedMetric MetricFromString(JsonElement parent, string source, params string[] propNames)
        {
            foreach (var prop in propNames)
            {
                var m = MetricFromString(parent, prop, source);
                if (m.Available) return m;
            }
            return MetricFactory.CreateUnavailable("", source, "property_not_found");
        }

        private static NormalizedMetric MetricFromBool(JsonElement parent, string source, params string[] propNames)
        {
            foreach (var prop in propNames)
            {
                var m = MetricFromBool(parent, prop, source);
                if (m.Available) return m;
            }
            return MetricFactory.CreateUnavailable("bool", source, "property_not_found");
        }
        
        private static NormalizedMetric MetricFromBool(JsonElement parent, string prop, string source)
        {
            if (parent.TryGetProperty(prop, out var val))
            {
                if (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False)
                    return MetricFactory.CreateAvailable(val.GetBoolean(), "bool", source, 100);
            }
            return MetricFactory.CreateUnavailable("bool", source, "property_not_found");
        }
        
        #endregion

        #region Normalization Helpers

        private NormalizedMetric NormalizeCpuTemp(MetricValue<double>? metric, string source)
        {
            string actualSource = source ?? "LHM";
            
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("°C", actualSource, 
                    metric?.Reason ?? "sensor_not_available");

            var value = metric.Value;

            // Sentinel checks
            if (double.IsNaN(value) || double.IsInfinity(value))
                return MetricFactory.CreateUnavailable("°C", actualSource, "nan_or_infinite");

            if (Math.Abs(value) < 0.001)
                return MetricFactory.CreateUnavailable("°C", actualSource, "sentinel_zero");

            if (value == -1)
                return MetricFactory.CreateUnavailable("°C", actualSource, "sentinel_minus_one");

            // Range check (5-115°C for CPU)
            if (value < 5 || value > 115)
                return MetricFactory.CreateUnavailable("°C", actualSource, 
                    $"out_of_range ({value:F1}°C not in [5,115])");

            return MetricFactory.CreateAvailable(Math.Round(value, 1), "°C", actualSource, 100);
        }

        private NormalizedMetric NormalizeGpuTemp(MetricValue<double>? metric)
        {
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("°C", "LHM", 
                    metric?.Reason ?? "sensor_not_available");

            var value = metric.Value;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return MetricFactory.CreateUnavailable("°C", "LHM", "nan_or_infinite");

            if (value < 5 || value > 120)
                return MetricFactory.CreateUnavailable("°C", "LHM", 
                    $"out_of_range ({value:F1}°C not in [5,120])");

            return MetricFactory.CreateAvailable(Math.Round(value, 1), "°C", "LHM", 100);
        }

        private NormalizedMetric NormalizeDiskTemp(MetricValue<double>? metric, string diskName)
        {
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("°C", "LHM", 
                    metric?.Reason ?? "disk_temp_not_available", diskName);

            var value = metric.Value;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return MetricFactory.CreateUnavailable("°C", "LHM", "nan_or_infinite", diskName);

            if (Math.Abs(value) < 0.001)
                return MetricFactory.CreateUnavailable("°C", "LHM", "sentinel_zero", diskName);

            if (value < 0 || value > 90)
                return MetricFactory.CreateUnavailable("°C", "LHM", 
                    $"out_of_range ({value:F1}°C not in [0,90])", diskName);

            return MetricFactory.CreateAvailable(Math.Round(value, 1), "°C", "LHM", 100, diskName);
        }

        private NormalizedMetric NormalizePercent(MetricValue<double>? metric, string source, string name)
        {
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("%", source, metric?.Reason ?? "not_available");

            var value = metric.Value;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return MetricFactory.CreateUnavailable("%", source, "nan_or_infinite");

            if (value < 0 || value > 100)
                return MetricFactory.CreateUnavailable("%", source, $"out_of_range ({value:F1}% not in [0,100])");

            return MetricFactory.CreateAvailable(Math.Round(value, 1), "%", source, 100);
        }

        private NormalizedMetric NormalizeVramTotal(MetricValue<double>? metric)
        {
            if (metric == null || !metric.Available)
                return MetricFactory.CreateUnavailable("MB", "LHM", metric?.Reason ?? "not_available");

            var value = metric.Value;

            if (value <= 0)
                return MetricFactory.CreateUnavailable("MB", "LHM", "sentinel_zero_or_negative");

            return MetricFactory.CreateAvailable(Math.Round(value, 0), "MB", "LHM", 100);
        }

        private NormalizedMetric NormalizeVramUsed(MetricValue<double>? usedMetric, MetricValue<double>? totalMetric)
        {
            if (usedMetric == null || !usedMetric.Available)
                return MetricFactory.CreateUnavailable("MB", "LHM", usedMetric?.Reason ?? "not_available");

            var used = usedMetric.Value;
            var total = totalMetric?.Value ?? 0;

            if (used < 0)
                return MetricFactory.CreateUnavailable("MB", "LHM", "negative_value");

            if (total > 0 && used > total * 1.1)
                return MetricFactory.CreateUnavailable("MB", "LHM", $"vram_used_exceeds_total ({used:F0} > {total:F0})");

            return MetricFactory.CreateAvailable(Math.Round(used, 1), "MB", "LHM", 100);
        }

        private Dictionary<string, NormalizedMetric> ConvertSignalToMetrics(string signalName, SignalResult signal)
        {
            var metrics = new Dictionary<string, NormalizedMetric>();

            if (!signal.Available)
            {
                metrics["available"] = MetricFactory.CreateUnavailable("bool", signal.Source, 
                    signal.Reason ?? "signal_unavailable");
                return metrics;
            }

            // Add availability marker
            metrics["available"] = MetricFactory.CreateAvailable(true, "bool", signal.Source, 100);

            // Convert signal value to metrics based on signal type
            if (signal.Value is JsonElement jsonElement)
            {
                ExtractMetricsFromJson(metrics, jsonElement, signal.Source);
            }
            else if (signal.Value is IDictionary<string, object> dict)
            {
                foreach (var kvp in dict)
                {
                    metrics[kvp.Key] = ConvertValueToMetric(kvp.Value, signal.Source);
                }
            }
            else if (signal.Value != null)
            {
                // Try to extract properties using reflection for known types
                var valueType = signal.Value.GetType();
                foreach (var prop in valueType.GetProperties())
                {
                    try
                    {
                        var propValue = prop.GetValue(signal.Value);
                        if (propValue != null)
                        {
                            metrics[prop.Name] = ConvertValueToMetric(propValue, signal.Source);
                        }
                    }
                    catch { /* Ignore reflection errors */ }
                }
            }

            return metrics;
        }

        private void ExtractMetricsFromJson(Dictionary<string, NormalizedMetric> metrics, JsonElement element, string source)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        var value = prop.Value.GetDouble();
                        metrics[prop.Name] = MetricFactory.CreateAvailable(value, "", source, 100);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        metrics[prop.Name] = MetricFactory.CreateAvailable(prop.Value.GetString() ?? "", "", source, 100);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                    {
                        metrics[prop.Name] = MetricFactory.CreateAvailable(prop.Value.GetBoolean(), "bool", source, 100);
                    }
                }
            }
        }

        private NormalizedMetric ConvertValueToMetric(object value, string source)
        {
            return value switch
            {
                int i => MetricFactory.CreateAvailable(i, "count", source, 100),
                long l => MetricFactory.CreateAvailable(l, "count", source, 100),
                double d => MetricFactory.CreateAvailable(Math.Round(d, 2), "", source, 100),
                float f => MetricFactory.CreateAvailable(Math.Round(f, 2), "", source, 100),
                string s => MetricFactory.CreateAvailable(s, "", source, 100),
                bool b => MetricFactory.CreateAvailable(b, "bool", source, 100),
                DateTime dt => MetricFactory.CreateAvailable(dt.ToString("o"), "timestamp", source, 100),
                _ => MetricFactory.CreateAvailable(value.ToString() ?? "", "", source, 100)
            };
        }

        #endregion
    }
}
