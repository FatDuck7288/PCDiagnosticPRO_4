using System;
using System.Collections.Generic;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Résultat du moteur UDIS (Unified Diagnostic Intelligence Scoring).
    /// UDIS = 0.7 * MachineHealthScore + 0.2 * DataReliabilityScore + 0.1 * DiagnosticClarityScore.
    /// </summary>
    public class UdisReport
    {
        /// <summary>Score final UDIS 0-100</summary>
        public int UdisScore { get; set; }

        /// <summary>Machine Health Score 0-100 (70% du total) — priorité critique/haute/moyenne/faible</summary>
        public int MachineHealthScore { get; set; }

        /// <summary>Data Reliability Score 0-100 (20% du total) — erreurs collecte + missing pondéré</summary>
        public int DataReliabilityScore { get; set; }

        /// <summary>Diagnostic Clarity Score 0-100 (10% du total) — qualité JSON, findings, sentinelles</summary>
        public int DiagnosticClarityScore { get; set; }

        /// <summary>Grade affiché (A+, A, B+, B, C, D, F)</summary>
        public string Grade { get; set; } = "N/A";

        /// <summary>Message utilisateur</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Détails par priorité (critique/haute/moyenne/faible) pour MHS</summary>
        public Dictionary<string, int> MachineHealthBreakdown { get; set; } = new();

        /// <summary>Findings normalisés pour LLM AutoFix</summary>
        public List<DiagnosticFinding> Findings { get; set; } = new();

        /// <summary>AutoFix autorisé (Safety Gate)</summary>
        public bool AutoFixAllowed { get; set; }

        /// <summary>Raison blocage AutoFix si non autorisé</summary>
        public string? AutoFixBlockReason { get; set; }

        /// <summary>Profil CPU (Gaming/Workstation/Office)</summary>
        public string CpuPerformanceTier { get; set; } = "N/A";

        /// <summary>Index stabilité système 0-100</summary>
        public int SystemStabilityIndex { get; set; }

        /// <summary>Réseau : tier débit (Navigation only / Streaming HD / Gaming OK / Cloud ready)</summary>
        public string NetworkSpeedTier { get; set; } = "N/A";

        /// <summary>Download Mbps (si mesuré)</summary>
        public double? DownloadMbps { get; set; }

        /// <summary>Upload Mbps (si mesuré)</summary>
        public double? UploadMbps { get; set; }

        /// <summary>Latence ms (si mesuré)</summary>
        public double? LatencyMs { get; set; }

        /// <summary>Recommandation réseau</summary>
        public string NetworkRecommendation { get; set; } = "";

        // ─────────────────────────────────────
        // NOUVELLES SECTIONS UDIS
        // ─────────────────────────────────────

        /// <summary>Boot Time Health Score 0-100</summary>
        public int BootHealthScore { get; set; } = 100;

        /// <summary>Boot Time Tier (Excellent/Bon/Moyen/Lent)</summary>
        public string BootHealthTier { get; set; } = "N/A";

        /// <summary>Temps de boot en secondes</summary>
        public double? BootTimeSeconds { get; set; }

        /// <summary>Thermal Envelope Score 0-100</summary>
        public int ThermalScore { get; set; } = 100;

        /// <summary>Thermal Status (OK/À surveiller/Throttling/Critique)</summary>
        public string ThermalStatus { get; set; } = "N/A";

        /// <summary>Storage IO Health Score 0-100</summary>
        public int StorageIoHealthScore { get; set; } = 100;

        /// <summary>Storage IO Status (OK/Chargé/Saturé)</summary>
        public string StorageIoStatus { get; set; } = "N/A";

        /// <summary>Résumé sections détaillé</summary>
        public List<UdisSectionSummary> SectionsSummary { get; set; } = new();
    }

    /// <summary>
    /// Résumé par section pour l'affichage UI.
    /// </summary>
    public class UdisSectionSummary
    {
        public string SectionName { get; set; } = "";
        public int Score { get; set; }
        public string Status { get; set; } = "";
        public string Priority { get; set; } = "";
        public bool HasData { get; set; }
        public string Recommendation { get; set; } = "";
    }

    /// <summary>
    /// Finding normalisé pour LLM AutoFix — structure exploitable.
    /// </summary>
    public class DiagnosticFinding
    {
        public string IssueType { get; set; } = string.Empty;
        public string Severity { get; set; } = "Medium";
        public int Confidence { get; set; }
        public bool AutoFixPossible { get; set; }
        public string RiskLevel { get; set; } = "Low";
        public string Description { get; set; } = string.Empty;
        public string? SuggestedAction { get; set; }
        public string Source { get; set; } = string.Empty;
    }
}
