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
        [JsonPropertyName("scan_powershell")]
        public JsonElement ScanPowershell { get; set; }

        [JsonPropertyName("sensors_csharp")]
        public HardwareSensorsResult SensorsCsharp { get; set; } = new HardwareSensorsResult();
        
        /// <summary>
        /// PHASE 1: DiagnosticSnapshot with schemaVersion 2.0.0
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
        /// P0.2: WMI/CIM errors with full context (namespace, query, HRESULT, duration)
        /// NO MORE "Unknown / No message"
        /// </summary>
        [JsonPropertyName("collector_diagnostics")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CollectorDiagnostics? CollectorDiagnostics { get; set; }
        
        // ===================================================================
        // NŒUDS EXPLICITES pour compatibilité JSON - TOUJOURS PRÉSENTS
        // Ces champs sont extraits de scan_powershell pour visibilité explicite
        // ===================================================================
        
        /// <summary>
        /// Données manquantes (extrait de scan_powershell.missingData ou généré)
        /// TOUJOURS PRÉSENT dans le JSON même si vide
        /// </summary>
        [JsonPropertyName("missingData")]
        public List<string> MissingData { get; set; } = new();
        
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
}
