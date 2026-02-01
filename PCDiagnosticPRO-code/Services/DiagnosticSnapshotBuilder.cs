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

            foreach (var group in _snapshot.Metrics.Values)
            {
                foreach (var metric in group.Values)
                {
                    total++;
                    if (metric.Available)
                        available++;
                }
            }

            _snapshot.CollectionQuality.TotalMetrics = total;
            _snapshot.CollectionQuality.AvailableMetrics = available;
            _snapshot.CollectionQuality.UnavailableMetrics = total - available;
            _snapshot.CollectionQuality.CoveragePercent = total > 0 
                ? Math.Round((double)available / total * 100, 1) 
                : 0;
            
            LogBuild($"[Build] Completed: {available}/{total} metrics available ({_snapshot.CollectionQuality.CoveragePercent}%)");
            WriteLogToTemp();

            return _snapshot;
        }
        
        #region PS Section Mapping (Schema 2.1.0)
        
        /// <summary>
        /// Maps all PowerShell sections to the snapshot
        /// </summary>
        public DiagnosticSnapshotBuilder AddPowerShellData(JsonElement? psRoot)
        {
            if (psRoot == null || psRoot.Value.ValueKind != JsonValueKind.Object)
            {
                LogBuild("[AddPowerShellData] No PS data available");
                return this;
            }
            
            var root = psRoot.Value;
            
            // Map sections to snapshot domains
            if (TryGetPropertyCaseInsensitive(root, out var sections, "sections") && sections.ValueKind == JsonValueKind.Object)
            {
                AddOsMetricsFromPs(sections);
                AddMemoryMetricsFromPs(sections);
                AddNetworkMetricsFromPs(sections);
                AddUpdatesMetricsFromPs(sections);
                AddSecurityMetricsFromPs(sections);
                AddStartupMetricsFromPs(sections);
                AddDevicesMetricsFromPs(sections);
                AddStabilityMetricsFromPs(sections);
                AddBootMetricsFromPs(sections);
                AddStorageExtrasFromPs(sections);
                AddPowerShellSummary(sections);
                
                LogBuild($"[AddPowerShellData] Mapped sections to snapshot domains");
            }
            
            // Map machine info from MachineIdentity and OS sections
            AddMachineInfoFromPs(root);
            
            return this;
        }
        
        private void AddMachineInfoFromPs(JsonElement root)
        {
            try
            {
                if (!TryGetPropertyCaseInsensitive(root, out var sections, "sections"))
                    return;
                    
                // From MachineIdentity (handle data wrapper)
                if (TryGetSectionData(sections, out var machineId, "MachineIdentity"))
                {
                    _snapshot.Machine.Hostname = GetStringValue(machineId, "computerName") ?? GetStringValue(machineId, "ComputerName") ?? _snapshot.Machine.Hostname;
                    _snapshot.Machine.CpuName = GetStringValue(machineId, "processorName") ?? GetStringValue(machineId, "ProcessorName");
                    
                    if (TryGetPropertyCaseInsensitive(machineId, out var ram, "TotalRAM_GB", "TotalRamGB", "TotalRAM", "totalRamGB"))
                        _snapshot.Machine.TotalRamGB = GetDoubleValue(ram);
                    
                    var uptimeDays = TryGetInt(machineId, "uptimeDays");
                    var uptimeHours = TryGetInt(machineId, "uptimeHours");
                    if (uptimeDays.HasValue || uptimeHours.HasValue)
                        _snapshot.Machine.Uptime = $"{uptimeDays.GetValueOrDefault()}j {uptimeHours.GetValueOrDefault()}h";
                }
                
                // From OS section (handle data wrapper)
                if (TryGetSectionData(sections, out var os, "OS"))
                {
                    _snapshot.Machine.OsVersion = GetStringValue(os, "caption") ?? GetStringValue(os, "Caption") ?? GetStringValue(os, "OSName");
                    _snapshot.Machine.OsBuild = GetStringValue(os, "buildNumber") ?? GetStringValue(os, "BuildNumber") ?? GetStringValue(os, "Version");
                    _snapshot.Machine.InstallDate = GetStringValue(os, "installDate") ?? GetStringValue(os, "InstallDate");
                    _snapshot.Machine.LastBootTime = GetStringValue(os, "lastBootUpTime") ?? GetStringValue(os, "LastBootUpTime");
                    _snapshot.Machine.Architecture = GetStringValue(os, "architecture") ?? GetStringValue(os, "OSArchitecture");
                    _snapshot.Machine.Uptime = GetStringValue(os, "uptime") ?? GetStringValue(os, "Uptime") ?? _snapshot.Machine.Uptime;
                }
                
                LogBuild("[AddMachineInfoFromPs] Machine info enriched from PS");
            }
            catch (Exception ex)
            {
                LogBuild($"[AddMachineInfoFromPs] Error: {ex.Message}");
            }
        }
        
        private void AddOsMetricsFromPs(JsonElement sections)
        {
            var osMetrics = new Dictionary<string, NormalizedMetric>();
            
            try
            {
                if (TryGetSectionData(sections, out var os, "OS"))
                {
                    osMetrics["caption"] = MetricFromStringAny(os, "PS/OS", "caption", "Caption", "osCaption");
                    osMetrics["version"] = MetricFromStringAny(os, "PS/OS", "version", "Version", "displayVersion");
                    osMetrics["buildNumber"] = MetricFromStringAny(os, "PS/OS", "buildNumber", "BuildNumber", "osBuild");
                    osMetrics["installDate"] = MetricFromStringAny(os, "PS/OS", "installDate", "InstallDate");
                    osMetrics["lastBootTime"] = MetricFromStringAny(os, "PS/OS", "lastBootUpTime", "LastBootUpTime");
                    osMetrics["uptime"] = MetricFromStringAny(os, "PS/OS", "uptime", "Uptime");
                    osMetrics["architecture"] = MetricFromStringAny(os, "PS/OS", "architecture", "OSArchitecture");
                }
                
                if (TryGetSectionData(sections, out var machine, "MachineIdentity"))
                {
                    osMetrics["caption"] = MergeMetric(osMetrics, "caption", MetricFromStringAny(machine, "PS/MachineIdentity", "osCaption"));
                    osMetrics["buildNumber"] = MergeMetric(osMetrics, "buildNumber", MetricFromStringAny(machine, "PS/MachineIdentity", "osBuild"));
                    osMetrics["lastBootTime"] = MergeMetric(osMetrics, "lastBootTime", MetricFromStringAny(machine, "PS/MachineIdentity", "lastBoot"));
                }
                
                if (TryGetSectionData(sections, out var integrity, "SystemIntegrity"))
                {
                    osMetrics["sfcStatus"] = MetricFromStringAny(integrity, "PS/SystemIntegrity", "sfcStatus", "SfcStatus");
                    osMetrics["dismHealth"] = MetricFromStringAny(integrity, "PS/SystemIntegrity", "dismHealth", "DismHealth");
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
                    memMetrics["totalGB"] = MetricFromNumberAny(mem, "GB", "PS/Memory", "totalGB", "TotalMemoryGB", "TotalPhysicalMemoryGB");
                    memMetrics["availableGB"] = MetricFromNumberAny(mem, "GB", "PS/Memory", "availableGB", "freeGB", "AvailableMemoryGB");
                    memMetrics["usedPercent"] = MetricFromNumberAny(mem, "%", "PS/Memory", "usedPercent", "UsedMemoryPercent");
                    memMetrics["freePercent"] = MetricFromNumberAny(mem, "%", "PS/Memory", "freePercent", "FreeMemoryPercent");
                    memMetrics["commitTotalGB"] = MetricFromNumberAny(mem, "GB", "PS/Memory", "commitTotalGB", "CommitTotalGB", "commitLimitGB");
                    memMetrics["commitUsedGB"] = MetricFromNumberAny(mem, "GB", "PS/Memory", "commitUsedGB", "CommitUsedGB", "virtualUsedGB");
                    memMetrics["pageFileUsagePercent"] = MetricFromNumberAny(mem, "%", "PS/Memory", "pageFileUsagePercent", "PageFileUsagePercent");
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
                    // Adapters info - handle both array and object
                    if (TryGetPropertyCaseInsensitive(net, out var adapters, "Adapters", "adapters"))
                    {
                        int adapterCount = 0;
                        if (adapters.ValueKind == JsonValueKind.Array)
                            adapterCount = adapters.GetArrayLength();
                        else if (adapters.ValueKind == JsonValueKind.Object)
                            adapterCount = 1;
                        netMetrics["adapterCount"] = MetricFactory.CreateAvailable(adapterCount, "count", "PS/Network", 100);
                    }
                    else
                    {
                        var adapterCount = TryGetInt(net, "adapterCount");
                        if (adapterCount.HasValue)
                            netMetrics["adapterCount"] = MetricFactory.CreateAvailable(adapterCount.Value, "count", "PS/Network", 100);
                    }
                    
                    netMetrics["defaultGateway"] = MetricFromStringAny(net, "PS/Network", "DefaultGateway", "defaultGateway");
                    netMetrics["dnsServers"] = MetricFromStringAny(net, "PS/Network", "DnsServers", "dnsServers");
                    netMetrics["publicIP"] = MetricFromStringAny(net, "PS/Network", "PublicIP", "publicIP");
                    
                    if (TryGetPropertyCaseInsensitive(net, out var firstAdapter, "adapters") &&
                        firstAdapter.ValueKind == JsonValueKind.Array)
                    {
                        var adapter = firstAdapter.EnumerateArray().FirstOrDefault();
                        if (adapter.ValueKind == JsonValueKind.Object)
                        {
                            netMetrics["defaultGateway"] = MergeMetric(netMetrics, "defaultGateway",
                                MetricFromStringAny(adapter, "PS/Network", "gateway"));
                            netMetrics["dnsServers"] = MergeMetric(netMetrics, "dnsServers",
                                MetricFromStringAny(adapter, "PS/Network", "dns"));
                        }
                    }
                }
                
                if (TryGetSectionData(sections, out var latency, "NetworkLatency"))
                {
                    netMetrics["pingGoogle"] = MetricFromNumber(latency, "PingGoogle", "ms", "PS/NetworkLatency");
                    netMetrics["pingCloudflare"] = MetricFromNumber(latency, "PingCloudflare", "ms", "PS/NetworkLatency");
                    netMetrics["avgLatency"] = MetricFromNumber(latency, "AvgLatency", "ms", "PS/NetworkLatency");
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
                    secMetrics["antivirusStatus"] = MetricFromStringAny(sec, "PS/Security", "AntivirusStatus", "antivirusStatus", "defenderEnabled");
                    secMetrics["antivirusName"] = MetricFromStringAny(sec, "PS/Security", "AntivirusName", "antivirusName");
                    if (TryGetPropertyCaseInsensitive(sec, out var avProducts, "antivirusProducts"))
                    {
                        secMetrics["antivirusName"] = MetricFactory.CreateAvailable(avProducts.ToString(), "", "PS/Security", 100);
                    }
                    secMetrics["firewallStatus"] = MetricFromStringAny(sec, "PS/Security", "FirewallStatus", "firewall");
                    secMetrics["uacEnabled"] = MetricFromBoolAny(sec, "PS/Security", "UacEnabled", "uacEnabled");
                    secMetrics["secureBootEnabled"] = MetricFromBoolAny(sec, "PS/Security", "SecureBootEnabled", "secureBoot");
                    secMetrics["bitlockerStatus"] = MetricFromStringAny(sec, "PS/Security", "BitlockerStatus", "bitlockerEnabled", "bitLocker");
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
                // Event Logs
                if (TryGetSectionData(sections, out var events, "EventLogs", "EventLogInfo"))
                {
                    stabMetrics["criticalEvents24h"] = MetricFromNumberAny(events, "count", "PS/EventLogs", "Critical24h", "critical24h");
                    stabMetrics["errorEvents24h"] = MetricFromNumberAny(events, "count", "PS/EventLogs", "Error24h", "error24h");
                    stabMetrics["warningEvents24h"] = MetricFromNumberAny(events, "count", "PS/EventLogs", "Warning24h", "warning24h");
                    stabMetrics["bsodCount"] = MetricFromNumberAny(events, "count", "PS/EventLogs", "bsodCount");
                    
                    if (TryGetPropertyCaseInsensitive(events, out var summary, "Summary"))
                    {
                        stabMetrics["appCrashes"] = MetricFromNumber(summary, "AppCrashes", "count", "PS/EventLogs");
                        stabMetrics["bsods"] = MetricFromNumber(summary, "BSOD", "count", "PS/EventLogs");
                    }
                }
                
                // Reliability History
                if (TryGetSectionData(sections, out var reliability, "ReliabilityHistory"))
                {
                    stabMetrics["reliabilityIndex"] = MetricFromNumberAny(reliability, "", "PS/ReliabilityHistory", "ReliabilityIndex", "reliabilityIndex");
                    stabMetrics["appFailures30d"] = MetricFromNumberAny(reliability, "count", "PS/ReliabilityHistory", "AppFailures30d", "appCrashes");
                    stabMetrics["hwFailures30d"] = MetricFromNumberAny(reliability, "count", "PS/ReliabilityHistory", "HwFailures30d");
                }
                
                // Minidump Analysis
                if (TryGetSectionData(sections, out var minidump, "MinidumpAnalysis"))
                {
                    stabMetrics["minidumpCount"] = MetricFromNumberAny(minidump, "count", "PS/MinidumpAnalysis", "Count", "minidumpCount");
                    stabMetrics["lastBsodDate"] = MetricFromStringAny(minidump, "PS/MinidumpAnalysis", "LastBsod", "lastBsod");
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
                    // Disk space info
                    if (TryGetPropertyCaseInsensitive(storage, out var drives, "Drives", "volumes") && drives.ValueKind == JsonValueKind.Array)
                    {
                        int idx = 0;
                        foreach (var drive in drives.EnumerateArray())
                        {
                            storageMetrics[$"drive_{idx}_totalGB"] = MetricFromNumberAny(drive, "GB", "PS/Storage", "TotalSizeGB", "sizeGB");
                            storageMetrics[$"drive_{idx}_freeGB"] = MetricFromNumberAny(drive, "GB", "PS/Storage", "FreeSpaceGB", "freeSpaceGB");
                            storageMetrics[$"drive_{idx}_usedPercent"] = MetricFromNumberAny(drive, "%", "PS/Storage", "UsedPercent", "usedPercent");
                            idx++;
                        }
                    }
                }
                
                // SMART data
                if (TryGetSectionData(sections, out var smart, "SmartDetails"))
                {
                    storageMetrics["smartHealthy"] = MetricFromBool(smart, "AllHealthy", "PS/SmartDetails");
                    storageMetrics["smartWarnings"] = MetricFromNumber(smart, "WarningCount", "count", "PS/SmartDetails");
                }
                
                // Temp files
                if (TryGetSectionData(sections, out var temp, "TempFiles"))
                {
                    storageMetrics["tempFilesSizeMB"] = MetricFromNumber(temp, "TotalSizeMB", "MB", "PS/TempFiles");
                    storageMetrics["tempFilesCount"] = MetricFromNumber(temp, "FileCount", "count", "PS/TempFiles");
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
        
        private void LogBuild(string message)
        {
            _buildLog.Add($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            App.LogMessage(message);
        }
        
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
        
        private static string? GetStringValue(JsonElement element, string propertyName)
        {
            if (TryGetPropertyCaseInsensitive(element, out var prop, propertyName) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
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
        
        private static NormalizedMetric MetricFromBool(JsonElement parent, string prop, string source)
        {
            if (TryGetPropertyCaseInsensitive(parent, out var val, prop))
            {
                if (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False)
                    return MetricFactory.CreateAvailable(val.GetBoolean(), "bool", source, 100);
            }
            return MetricFactory.CreateUnavailable("bool", source, "property_not_found");
        }

        private static NormalizedMetric MetricFromStringAny(JsonElement parent, string source, params string[] props)
        {
            foreach (var prop in props)
            {
                var metric = MetricFromString(parent, prop, source);
                if (metric.Available)
                    return metric;
            }
            return MetricFactory.CreateUnavailable("", source, "property_not_found");
        }

        private static NormalizedMetric MetricFromNumberAny(JsonElement parent, string unit, string source, params string[] props)
        {
            foreach (var prop in props)
            {
                var metric = MetricFromNumber(parent, prop, unit, source);
                if (metric.Available)
                    return metric;
            }
            return MetricFactory.CreateUnavailable(unit, source, "property_not_found");
        }

        private static NormalizedMetric MetricFromBoolAny(JsonElement parent, string source, params string[] props)
        {
            foreach (var prop in props)
            {
                var metric = MetricFromBool(parent, prop, source);
                if (metric.Available)
                    return metric;
            }
            return MetricFactory.CreateUnavailable("bool", source, "property_not_found");
        }

        private static NormalizedMetric MergeMetric(Dictionary<string, NormalizedMetric> metrics, string key, NormalizedMetric candidate)
        {
            if (metrics.TryGetValue(key, out var existing) && existing.Available)
                return existing;
            return candidate;
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
