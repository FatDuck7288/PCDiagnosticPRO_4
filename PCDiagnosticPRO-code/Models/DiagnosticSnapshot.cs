using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// PHASE 1.1: DiagnosticSnapshot - Central normalized data structure.
    /// Schema version 2.2.0 contract - Extended with PS + C# data mapping.
    /// </summary>
    public class DiagnosticSnapshot
    {
        /// <summary>Version contractuelle unique. Toute modification de structure doit incrémenter cette version.</summary>
        public const string CURRENT_SCHEMA_VERSION = "2.2.0";

        /// <summary>Fixed schema version for contract compliance</summary>
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; } = CURRENT_SCHEMA_VERSION;

        /// <summary>Generation timestamp (ISO 8601 UTC)</summary>
        [JsonPropertyName("generatedAt")]
        public string GeneratedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>Machine identification (extended with PS data)</summary>
        [JsonPropertyName("machine")]
        public MachineInfo Machine { get; set; } = new();

        /// <summary>
        /// Metrics organized by group.
        /// Keys: cpu, gpu, storage, memory, network, boot, drivers, whea, power, os, updates, 
        /// security, startup, devices, stability, processes, performance
        /// </summary>
        [JsonPropertyName("metrics")]
        public Dictionary<string, Dictionary<string, NormalizedMetric>> Metrics { get; set; } = new();

        /// <summary>Normalized findings (can be empty)</summary>
        [JsonPropertyName("findings")]
        public List<NormalizedFinding> Findings { get; set; } = new();

        /// <summary>Collection diagnostics and quality</summary>
        [JsonPropertyName("collectionQuality")]
        public CollectionQuality CollectionQuality { get; set; } = new();
        
        /// <summary>Process telemetry summary (top CPU/memory consumers)</summary>
        [JsonPropertyName("processSummary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ProcessSummary? ProcessSummary { get; set; }
        
        /// <summary>Network diagnostics summary (latency, jitter, loss, throughput)</summary>
        [JsonPropertyName("networkSummary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public NetworkSummary? NetworkSummary { get; set; }
        
        /// <summary>Sensor collection status (Defender/WinRing0 blocking detection)</summary>
        [JsonPropertyName("sensorStatus")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SensorCollectionStatus? SensorStatus { get; set; }

        /// <summary>PowerShell summary (minimal normalized PS section data)</summary>
        [JsonPropertyName("psSummary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PowerShellSummary? PsSummary { get; set; }
    }

    /// <summary>
    /// Machine identification info - Extended with PS data
    /// </summary>
    public class MachineInfo
    {
        [JsonPropertyName("hostname")]
        public string Hostname { get; set; } = Environment.MachineName;

        [JsonPropertyName("os")]
        public string Os { get; set; } = Environment.OSVersion.ToString();

        [JsonPropertyName("isAdmin")]
        public bool IsAdmin { get; set; }
        
        // === Extended fields from PowerShell ===
        
        [JsonPropertyName("osVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OsVersion { get; set; }
        
        [JsonPropertyName("osBuild")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OsBuild { get; set; }
        
        [JsonPropertyName("installDate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InstallDate { get; set; }
        
        [JsonPropertyName("lastBootTime")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastBootTime { get; set; }
        
        [JsonPropertyName("uptime")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Uptime { get; set; }
        
        [JsonPropertyName("cpuName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CpuName { get; set; }
        
        [JsonPropertyName("totalRamGB")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TotalRamGB { get; set; }
        
        [JsonPropertyName("architecture")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Architecture { get; set; }
    }
    
    /// <summary>
    /// Process telemetry summary for snapshot
    /// </summary>
    public class ProcessSummary
    {
        [JsonPropertyName("totalProcessCount")]
        public int TotalProcessCount { get; set; }
        
        [JsonPropertyName("topCpuProcess")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TopCpuProcess { get; set; }
        
        [JsonPropertyName("topCpuPercent")]
        public double TopCpuPercent { get; set; }
        
        [JsonPropertyName("topMemoryProcess")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TopMemoryProcess { get; set; }
        
        [JsonPropertyName("topMemoryMB")]
        public double TopMemoryMB { get; set; }
        
        [JsonPropertyName("available")]
        public bool Available { get; set; }
        
        [JsonPropertyName("source")]
        public string Source { get; set; } = "ProcessTelemetryCollector";
    }
    
    /// <summary>
    /// Network diagnostics summary for snapshot
    /// </summary>
    public class NetworkSummary
    {
        [JsonPropertyName("latencyP50Ms")]
        public double LatencyP50Ms { get; set; }
        
        [JsonPropertyName("latencyP95Ms")]
        public double LatencyP95Ms { get; set; }
        
        [JsonPropertyName("jitterP95Ms")]
        public double JitterP95Ms { get; set; }
        
        [JsonPropertyName("packetLossPercent")]
        public double PacketLossPercent { get; set; }
        
        [JsonPropertyName("dnsP95Ms")]
        public double DnsP95Ms { get; set; }
        
        [JsonPropertyName("downloadMbps")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? DownloadMbps { get; set; }
        
        [JsonPropertyName("gateway")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Gateway { get; set; }
        
        [JsonPropertyName("available")]
        public bool Available { get; set; }
        
        [JsonPropertyName("source")]
        public string Source { get; set; } = "NetworkDiagnosticsCollector";
    }
    
    /// <summary>
    /// Sensor collection status - detects Defender/WinRing0 blocking
    /// </summary>
    public class SensorCollectionStatus
    {
        [JsonPropertyName("sensorsAvailable")]
        public bool SensorsAvailable { get; set; } = true;
        
        [JsonPropertyName("blockedByDefender")]
        public bool BlockedByDefender { get; set; }
        
        [JsonPropertyName("blockedByDriver")]
        public bool BlockedByDriver { get; set; }
        
        [JsonPropertyName("blockReason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BlockReason { get; set; }
        
        [JsonPropertyName("userMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UserMessage { get; set; }
        
        [JsonPropertyName("exceptions")]
        public List<string> Exceptions { get; set; } = new();
    }

    /// <summary>
    /// Minimal summary of PowerShell sections (updates, startup, apps, devices, printers, audio).
    /// </summary>
    public class PowerShellSummary
    {
        [JsonPropertyName("updates")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public UpdatesSummary? Updates { get; set; }

        [JsonPropertyName("startup")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public StartupSummary? Startup { get; set; }

        [JsonPropertyName("apps")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ApplicationsSummary? Apps { get; set; }

        [JsonPropertyName("devices")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DevicesSummary? Devices { get; set; }

        [JsonPropertyName("printers")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PrintersSummary? Printers { get; set; }

        [JsonPropertyName("audio")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AudioSummary? Audio { get; set; }
    }

    public class UpdatesSummary
    {
        [JsonPropertyName("pendingCount")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PendingCount { get; set; }

        [JsonPropertyName("rebootRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? RebootRequired { get; set; }

        [JsonPropertyName("lastUpdate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastUpdate { get; set; }
    }

    public class StartupSummary
    {
        [JsonPropertyName("count")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Count { get; set; }

        [JsonPropertyName("topItems")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? TopItems { get; set; }
    }

    public class ApplicationsSummary
    {
        [JsonPropertyName("installedCount")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? InstalledCount { get; set; }

        [JsonPropertyName("lastInstalled")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastInstalled { get; set; }
    }

    public class DevicesSummary
    {
        [JsonPropertyName("problemDeviceCount")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ProblemDeviceCount { get; set; }

        [JsonPropertyName("topProblemDevices")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? TopProblemDevices { get; set; }
    }

    public class PrintersSummary
    {
        [JsonPropertyName("count")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Count { get; set; }

        [JsonPropertyName("names")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Names { get; set; }
    }

    public class AudioSummary
    {
        [JsonPropertyName("count")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Count { get; set; }

        [JsonPropertyName("names")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Names { get; set; }
    }

    /// <summary>
    /// Normalized finding/issue
    /// </summary>
    public class NormalizedFinding
    {
        [JsonPropertyName("issueType")]
        public string IssueType { get; set; } = "";

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "info"; // critical, high, medium, low, info

        [JsonPropertyName("confidence")]
        public int Confidence { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("suggestedAction")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SuggestedAction { get; set; }

        [JsonPropertyName("autoFixPossible")]
        public bool AutoFixPossible { get; set; }

        [JsonPropertyName("riskLevel")]
        public string RiskLevel { get; set; } = "low"; // critical, high, medium, low

        [JsonPropertyName("evidencePaths")]
        public List<string> EvidencePaths { get; set; } = new();
    }

    /// <summary>
    /// Collection quality and diagnostics
    /// </summary>
    public class CollectionQuality
    {
        [JsonPropertyName("totalMetrics")]
        public int TotalMetrics { get; set; }

        [JsonPropertyName("availableMetrics")]
        public int AvailableMetrics { get; set; }

        [JsonPropertyName("unavailableMetrics")]
        public int UnavailableMetrics { get; set; }

        [JsonPropertyName("coveragePercent")]
        public double CoveragePercent { get; set; }

        [JsonPropertyName("errors")]
        public List<CollectionError> Errors { get; set; } = new();

        [JsonPropertyName("signalsCollected")]
        public int SignalsCollected { get; set; }

        [JsonPropertyName("signalsUnavailable")]
        public int SignalsUnavailable { get; set; }
    }

    /// <summary>
    /// Collection error info
    /// </summary>
    public class CollectionError
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "warning";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("isLimitation")]
        public bool IsLimitation { get; set; }
    }
}
