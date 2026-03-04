using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCDiagnosticPro.DiagnosticsSignals;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.Services.NetworkDiagnostics;

namespace PCDiagnosticPro.Models
{
    public class CombinedScanResult
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; set; } = SchemaRegistry.CombinedSchemaVersion;

        [JsonPropertyName("componentVersions")]
        public ComponentVersions ComponentVersions { get; set; } = new();

        [JsonPropertyName("trace")]
        public TraceContext Trace { get; set; } = new();

        [JsonPropertyName("run_status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RunStatusEnvelope? RunStatus { get; set; }

        [JsonPropertyName("timings")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScanTimingEnvelope? Timings { get; set; }

        [JsonPropertyName("scan_powershell")]
        public JsonElement ScanPowershell { get; set; }

        [JsonPropertyName("sensors_csharp")]
        public HardwareSensorsResult SensorsCsharp { get; set; } = new HardwareSensorsResult();

        [JsonPropertyName("cpu")]
        public CpuTemperatureSummary Cpu { get; set; } = new CpuTemperatureSummary();
        
        /// <summary>
        /// DiagnosticSnapshot with schemaVersion 2.3.0 (contractual)
        /// Contains all normalized metrics and findings
        /// </summary>
        [JsonPropertyName("diagnostic_snapshot")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DiagnosticSnapshot? DiagnosticSnapshot { get; set; }
        
        /// <summary>
        /// GOD TIER: 10 diagnostic signals (WHEA, TDR, CPU throttle, etc.)
        /// </summary>
        [JsonPropertyName("diagnostic_signals")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, SignalResult>? DiagnosticSignals { get; set; }
        
        /// <summary>
        /// Process telemetry fallback (C#) when PowerShell fails
        /// </summary>
        [JsonPropertyName("process_telemetry")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ProcessTelemetryResult? ProcessTelemetry { get; set; }
        
        /// <summary>
        /// Complete network diagnostics (latency, jitter, loss, DNS, throughput)
        /// </summary>
        [JsonPropertyName("network_diagnostics")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public NetworkDiagnosticsResult? NetworkDiagnostics { get; set; }

        /// <summary>
        /// Driver inventory collected via C# (WMI)
        /// </summary>
        [JsonPropertyName("driver_inventory")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DriverInventoryResult? DriverInventory { get; set; }

        /// <summary>
        /// Windows Update availability collected via C# (Windows Update Agent)
        /// </summary>
        [JsonPropertyName("updates_csharp")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WindowsUpdateResult? UpdatesCsharp { get; set; }
        
        /// <summary>
        /// Security info collected via C# (BitLocker, RDP, SMBv1)
        /// Fills gaps where PowerShell doesn't collect this data
        /// </summary>
        [JsonPropertyName("security_info_csharp")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SecurityInfoCollector.SecurityInfoResult? SecurityInfoCsharp { get; set; }
        
        /// <summary>
        /// P0.2: WMI/CIM errors with full context (namespace, query, HRESULT, duration)
        /// NO MORE "Unknown / No message"
        /// </summary>
        [JsonPropertyName("collector_diagnostics")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CollectorDiagnostics? CollectorDiagnostics { get; set; }

        /// <summary>
        /// Performance timeseries: min/max/avg over sampling interval (CPU, RAM, disk IO, network).
        /// </summary>
        [JsonPropertyName("performance_timeseries_summary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PerformanceTimeseriesSummary? PerformanceTimeseriesSummary { get; set; }

        /// <summary>
        /// Last N critical/error event log entries (EventId, ProviderName, Message, Timestamp).
        /// </summary>
        [JsonPropertyName("event_logs_detailed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<EventLogDetailedEntry>? EventLogsDetailed { get; set; }

        /// <summary>
        /// SMART attributes per disk (PredictFailure + Reallocated, Pending, Wear, etc.).
        /// </summary>
        [JsonPropertyName("smart_attributes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SmartDiskEntry>? SmartAttributes { get; set; }

        /// <summary>
        /// NVMe / modern storage reliability data collected via Get-StorageReliabilityCounter.
        /// Works on NVMe + SATA + SAS where WMI MSStorageDriver_FailurePredictData is blind.
        /// source field identifies the collection path used.
        /// </summary>
        [JsonPropertyName("storage_reliability")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public StorageReliabilityResult? StorageReliability { get; set; }

        /// <summary>
        /// List of recent minidumps (file name, date, optional BugCheck code / driver hint).
        /// </summary>
        [JsonPropertyName("minidumps_detailed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<MinidumpEntry>? MinidumpsDetailed { get; set; }
        
        // ===================================================================
        // NŒUDS EXPLICITES pour compatibilité JSON - TOUJOURS PRÉSENTS
        // Ces champs sont extraits de scan_powershell pour visibilité explicite
        // ===================================================================
        
        /// <summary>
        /// Données manquantes legacy (strings) — rétrocompatibilité
        /// TOUJOURS PRÉSENT dans le JSON même si vide
        /// </summary>
        [JsonPropertyName("missingData")]
        public List<string> MissingData { get; set; } = new();

        /// <summary>
        /// Données manquantes structurées v2 — préserve reason/source/confidence/timestamp.
        /// Extrait de scan_powershell.missingData (format objet).
        /// TOUJOURS PRÉSENT dans le JSON même si vide.
        /// </summary>
        [JsonPropertyName("missingData_v2")]
        public List<MissingDataEntry> MissingDataStructured { get; set; } = new();
        
        /// <summary>
        /// Métadonnées du scan (extrait de scan_powershell.metadata)
        /// TOUJOURS PRÉSENT dans le JSON même si vide
        /// </summary>
        [JsonPropertyName("metadata")]
        public ScanMetadataExtract Metadata { get; set; } = new();
        
        /// <summary>
        /// Findings/problèmes détectés (extrait de scan_powershell.findings ou diagnostic_snapshot)
        /// TOUJOURS PRÉSENT dans le JSON même si vide
        /// </summary>
        [JsonPropertyName("findings")]
        public List<FindingExtract> Findings { get; set; } = new();
        
        /// <summary>
        /// Erreurs de collecte (extrait de scan_powershell.errors)
        /// TOUJOURS PRÉSENT dans le JSON même si vide
        /// </summary>
        [JsonPropertyName("errors")]
        public List<ErrorExtract> Errors { get; set; } = new();
        
        /// <summary>
        /// Sections PS disponibles (clés des sections présentes)
        /// TOUJOURS PRÉSENT dans le JSON même si vide
        /// </summary>
        [JsonPropertyName("sections")]
        public List<string> Sections { get; set; } = new();
        
        /// <summary>
        /// Chemins des fichiers générés
        /// TOUJOURS PRÉSENT dans le JSON même si vide
        /// </summary>
        [JsonPropertyName("paths")]
        public PathsExtract Paths { get; set; } = new();
        
        /// <summary>
        /// P0 Bloc C: Métriques de qualité (Coverage, Reliability, Actionability). Rempli après construction du HealthReport.
        /// </summary>
        [JsonPropertyName("diagnostics_quality")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DiagnosticQualityResult? DiagnosticsQuality { get; set; }

        /// <summary>
        /// Unified technical contract (versioned, additive) for end-to-end coherence.
        /// </summary>
        [JsonPropertyName("technical_contract")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TechnicalContract? TechnicalContract { get; set; }

        /// <summary>
        /// Rapport du quality gate — toujours présent, synthèse des gates contractuels.
        /// Passed=true signifie que tous les gates sont verts.
        /// </summary>
        [JsonPropertyName("quality_gate_report")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public QualityGateReport? QualityGateReport { get; set; }

        /// <summary>
        /// JS-1: Content integrity checksum. SHA-256 of the JSON content (excluding this field itself).
        /// Allows detection of file corruption or tampering after write.
        /// </summary>
        [JsonPropertyName("integrity")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScanIntegrity? Integrity { get; set; }
    }
    
    /// <summary>
    /// Entrée de donnée manquante structurée v2.
    /// Format additionnel qui préserve source/confidence/timestamp sans casser le format legacy.
    /// </summary>
    public class MissingDataEntry
    {
        [JsonPropertyName("section")]
        public string Section { get; set; } = "";

        [JsonPropertyName("item")]
        public string Item { get; set; } = "";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "PowerShell";

        [JsonPropertyName("confidence")]
        public string Confidence { get; set; } = "low";

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = "";
    }

    /// <summary>
    /// Métadonnées extraites pour visibilité explicite
    /// </summary>
    public class ScanMetadataExtract
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";
        
        [JsonPropertyName("runId")]
        public string RunId { get; set; } = "";
        
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = "";
        
        [JsonPropertyName("isAdmin")]
        public bool IsAdmin { get; set; }
        
        [JsonPropertyName("partialFailure")]
        public bool PartialFailure { get; set; }
        
        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }
    }
    
    /// <summary>
    /// Finding extrait pour visibilité explicite
    /// </summary>
    public class FindingExtract
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";
        
        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "";
        
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
        
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";
    }
    
    /// <summary>
    /// Erreur extraite pour visibilité explicite
    /// </summary>
    public class ErrorExtract
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("section")]
        public string Section { get; set; } = "";

        /// <summary>Composant ou collecteur ayant produit l'erreur (ex: "PowerShell/Get-GpuInfo", "WMI/Win32_VideoController").</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        /// <summary>Impact fonctionnel de l'erreur ("data_loss" | "ui_gap" | "score_penalty" | "none").</summary>
        [JsonPropertyName("impact")]
        public string Impact { get; set; } = "";
    }

    /// <summary>
    /// Rapport du quality gate — toujours présent dans le JSON combiné.
    /// Résume l'état de tous les gates contractuels.
    /// </summary>
    public class QualityGateReport
    {
        [JsonPropertyName("passed")]
        public bool Passed { get; set; }

        [JsonPropertyName("failedGates")]
        public List<string> FailedGates { get; set; } = new();

        [JsonPropertyName("messages")]
        public List<string> Messages { get; set; } = new();

        [JsonPropertyName("uiCoveragePercent")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? UiCoveragePercent { get; set; }

        [JsonPropertyName("generatedAt")]
        public string GeneratedAt { get; set; } = "";
    }
    
    /// <summary>
    /// Chemins extraits pour visibilité explicite
    /// </summary>
    public class PathsExtract
    {
        [JsonPropertyName("jsonOutput")]
        public string JsonOutput { get; set; } = "";
        
        [JsonPropertyName("txtOutput")]
        public string TxtOutput { get; set; } = "";
        
        [JsonPropertyName("combinedJson")]
        public string CombinedJson { get; set; } = "";
        
        [JsonPropertyName("unifiedTxt")]
        public string UnifiedTxt { get; set; } = "";
    }

    public class ComponentVersions
    {
        [JsonPropertyName("ps")]
        public string Ps { get; set; } = SchemaRegistry.AppVersion;

        [JsonPropertyName("snapshot")]
        public string Snapshot { get; set; } = SchemaRegistry.SnapshotSchemaVersion;

        [JsonPropertyName("app")]
        public string App { get; set; } = SchemaRegistry.AppVersion;
    }

    public class TraceContext
    {
        [JsonPropertyName("runId")]
        public string RunId { get; set; } = "";

        [JsonPropertyName("traceId")]
        public string TraceId { get; set; } = "";
    }

    public class ScanTimingEnvelope
    {
        [JsonPropertyName("runId")]
        public string RunId { get; set; } = "";

        [JsonPropertyName("phaseTotals")]
        public Dictionary<string, long> PhaseTotals { get; set; } = new();

        [JsonPropertyName("psCollectors")]
        public List<CollectorTimingEntry> PsCollectors { get; set; } = new();

        [JsonPropertyName("csCollectors")]
        public List<CollectorTimingEntry> CsCollectors { get; set; } = new();

        [JsonPropertyName("slowCollectors")]
        public List<CollectorTimingEntry> SlowCollectors { get; set; } = new();
    }

    public class CollectorTimingEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "ok";
    }
    
    /// <summary>
    /// P0.2: Container for all collector diagnostic information
    /// </summary>
    public class CollectorDiagnostics
    {
        [JsonPropertyName("wmi_errors")]
        public List<WmiErrorInfo> WmiErrors { get; set; } = new();
        
        [JsonPropertyName("total_errors")]
        public int TotalErrors => WmiErrors.Count;
        
        [JsonPropertyName("has_critical_errors")]
        public bool HasCriticalErrors => WmiErrors.Exists(e => e.Severity == "error");
    }

    /// <summary>
    /// JS-1: Content integrity envelope written alongside the combined JSON.
    /// ContentSha256 is computed over the full JSON string before this field is added.
    /// </summary>
    public class ScanIntegrity
    {
        [JsonPropertyName("contentSha256")]
        public string? ContentSha256 { get; set; }

        [JsonPropertyName("contentBytes")]
        public long ContentBytes { get; set; }
    }
}
