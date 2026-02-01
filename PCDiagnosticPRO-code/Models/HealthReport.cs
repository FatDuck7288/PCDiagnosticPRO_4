using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Taxonomie métier des sévérités - projection directe vers couleurs UI
    /// </summary>
    public enum HealthSeverity
    {
        /// <summary>État inconnu - données manquantes</summary>
        Unknown = 0,
        /// <summary>100% - Fonctionnement optimal</summary>
        Excellent = 1,
        /// <summary>70-99% - Bon état général</summary>
        Healthy = 2,
        /// <summary>60-69% - Dégradation légère, attention recommandée</summary>
        Warning = 3,
        /// <summary>40-59% - Dégradation significative, action requise</summary>
        Degraded = 4,
        /// <summary>&lt;40% - État critique, intervention urgente</summary>
        Critical = 5
    }

    /// <summary>
    /// Domaines de diagnostic machine - Extended with Applications and Performance
    /// </summary>
    public enum HealthDomain
    {
        OS,
        CPU,
        GPU,
        RAM,
        Storage,
        Network,
        SystemStability,
        Drivers,
        /// <summary>Applications: StartupPrograms, InstalledApplications, ScheduledTasks</summary>
        Applications,
        /// <summary>Performance: ProcessTelemetry, PerformanceCounters, real-time metrics</summary>
        Performance,
        /// <summary>Security: Antivirus, Firewall, UAC, SecureBoot, Bitlocker</summary>
        Security,
        /// <summary>Power: Battery, PowerSettings</summary>
        Power
    }

    /// <summary>
    /// Rapport de santé complet - modèle industriel production-grade
    /// Source de vérité : scoreV2 du script PowerShell
    /// </summary>
    public class HealthReport
    {
        /// <summary>Score global 0-100</summary>
        public int GlobalScore { get; set; }
        
        /// <summary>Sévérité globale calculée depuis le score</summary>
        public HealthSeverity GlobalSeverity { get; set; }
        
        /// <summary>Grade affiché (A, B, C, D, F)</summary>
        public string Grade { get; set; } = "N/A";
        
        /// <summary>Message principal pour l'utilisateur</summary>
        public string GlobalMessage { get; set; } = string.Empty;
        
        /// <summary>Sections de diagnostic par domaine</summary>
        public List<HealthSection> Sections { get; set; } = new();
        
        /// <summary>Recommandations prioritaires</summary>
        public List<HealthRecommendation> Recommendations { get; set; } = new();
        
        /// <summary>Métadonnées du scan</summary>
        public ScanMetadata Metadata { get; set; } = new();
        
        /// <summary>Données brutes du scoreV2 PowerShell</summary>
        public ScoreV2Data ScoreV2 { get; set; } = new();
        
        /// <summary>Erreurs rencontrées pendant le scan</summary>
        public List<ScanErrorInfo> Errors { get; set; } = new();
        
        /// <summary>Données manquantes (capteurs indisponibles, etc.)</summary>
        public List<string> MissingData { get; set; } = new();
        
        /// <summary>Nombre d'erreurs collecteur dérivé de errors[] (sans toucher PS). Si errors non vide ou partialFailure => ≥1.</summary>
        public int CollectorErrorsLogical { get; set; }
        
        /// <summary>Statut global de collecte : OK / PARTIAL / FAILED. Détermine badge UI et cap score.</summary>
        public string CollectionStatus { get; set; } = "OK";
        
        /// <summary>Date de génération du rapport</summary>
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        
        /// <summary>Modèle de confiance (coverage + cohérence)</summary>
        public ConfidenceModel ConfidenceModel { get; set; } = new();
        
        /// <summary>Divergence entre score PS et score GradeEngine (legacy)</summary>
        public ScoreDivergence Divergence { get; set; } = new();

        /// <summary>UDIS — Machine Health Score 0-100 (70% du total)</summary>
        public int MachineHealthScore { get; set; }

        /// <summary>UDIS — Data Reliability Score 0-100 (20% du total)</summary>
        public int DataReliabilityScore { get; set; }

        /// <summary>UDIS — Diagnostic Clarity Score 0-100 (10% du total)</summary>
        public int DiagnosticClarityScore { get; set; }

        /// <summary>Findings normalisés pour LLM AutoFix</summary>
        public List<DiagnosticFinding> UdisFindings { get; set; } = new();

        /// <summary>AutoFix autorisé (Safety Gate)</summary>
        public bool AutoFixAllowed { get; set; }

        /// <summary>Rapport UDIS complet (optionnel)</summary>
        public UdisReport? UdisReport { get; set; }

        /// <summary>Calcule la sévérité depuis un score</summary>
        public static HealthSeverity ScoreToSeverity(int score)
        {
            return score switch
            {
                100 => HealthSeverity.Excellent,
                >= 70 => HealthSeverity.Healthy,
                >= 60 => HealthSeverity.Warning,
                >= 40 => HealthSeverity.Degraded,
                _ => HealthSeverity.Critical
            };
        }
        
        /// <summary>Retourne la couleur hexadécimale pour une sévérité</summary>
        public static string SeverityToColor(HealthSeverity severity)
        {
            return severity switch
            {
                HealthSeverity.Excellent => "#FFD700",  // Gold
                HealthSeverity.Healthy => "#4CAF50",    // Green
                HealthSeverity.Warning => "#FFC107",    // Yellow/Amber
                HealthSeverity.Degraded => "#FF9800",   // Orange
                HealthSeverity.Critical => "#F44336",   // Red
                _ => "#9E9E9E"                          // Grey for Unknown
            };
        }
        
        /// <summary>Retourne l'icône pour une sévérité</summary>
        public static string SeverityToIcon(HealthSeverity severity)
        {
            return severity switch
            {
                HealthSeverity.Excellent => "✓",
                HealthSeverity.Healthy => "✓",
                HealthSeverity.Warning => "⚠",
                HealthSeverity.Degraded => "⚠",
                HealthSeverity.Critical => "✕",
                _ => "?"
            };
        }
    }

    /// <summary>
    /// Section de diagnostic pour un domaine spécifique
    /// </summary>
    public class HealthSection
    {
        /// <summary>Domaine de cette section</summary>
        public HealthDomain Domain { get; set; }
        
        /// <summary>Nom affiché (localisé)</summary>
        public string DisplayName { get; set; } = string.Empty;
        
        /// <summary>Icône du domaine</summary>
        public string Icon { get; set; } = "📊";
        
        /// <summary>Score de la section 0-100</summary>
        public int Score { get; set; }
        
        /// <summary>Sévérité calculée</summary>
        public HealthSeverity Severity { get; set; }
        
        /// <summary>Message court pour l'utilisateur</summary>
        public string StatusMessage { get; set; } = string.Empty;
        
        /// <summary>Explication détaillée (pour expansion)</summary>
        public string DetailedExplanation { get; set; } = string.Empty;
        
        /// <summary>Données utilisées pour calculer le score</summary>
        public Dictionary<string, string> EvidenceData { get; set; } = new();
        
        /// <summary>Recommandations spécifiques à cette section</summary>
        public List<string> SectionRecommendations { get; set; } = new();
        
        /// <summary>Findings/problèmes détectés</summary>
        public List<HealthFinding> Findings { get; set; } = new();
        
        /// <summary>La section a-t-elle des données disponibles</summary>
        public bool HasData { get; set; } = true;
        
        /// <summary>Statut de collecte (OK, PARTIAL, FAILED)</summary>
        public string CollectionStatus { get; set; } = "OK";
    }

    /// <summary>
    /// Problème/finding détecté
    /// </summary>
    public class HealthFinding
    {
        public HealthSeverity Severity { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public int PenaltyApplied { get; set; }
    }

    /// <summary>
    /// Recommandation pour l'utilisateur
    /// </summary>
    public class HealthRecommendation
    {
        public HealthSeverity Priority { get; set; }
        public HealthDomain? RelatedDomain { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ActionText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Métadonnées du scan PowerShell
    /// </summary>
    public class ScanMetadata
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "unknown";
        
        [JsonPropertyName("runId")]
        public string RunId { get; set; } = string.Empty;
        
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
        
        [JsonPropertyName("isAdmin")]
        public bool IsAdmin { get; set; }
        
        [JsonPropertyName("redactLevel")]
        public string RedactLevel { get; set; } = "standard";
        
        [JsonPropertyName("quickScan")]
        public bool QuickScan { get; set; }
        
        [JsonPropertyName("monitorSeconds")]
        public int MonitorSeconds { get; set; }
        
        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }
        
        [JsonPropertyName("partialFailure")]
        public bool PartialFailure { get; set; }
    }

    /// <summary>
    /// Données scoreV2 du PowerShell - source de vérité pour le score
    /// </summary>
    public class ScoreV2Data
    {
        [JsonPropertyName("score")]
        public int Score { get; set; } = 100;
        
        [JsonPropertyName("baseScore")]
        public int BaseScore { get; set; } = 100;
        
        [JsonPropertyName("totalPenalty")]
        public int TotalPenalty { get; set; }
        
        [JsonPropertyName("breakdown")]
        public ScoreBreakdown Breakdown { get; set; } = new();
        
        [JsonPropertyName("grade")]
        public string Grade { get; set; } = "N/A";
        
        [JsonPropertyName("topPenalties")]
        public List<PenaltyInfo> TopPenalties { get; set; } = new();
    }

    /// <summary>
    /// Détail des pénalités par catégorie
    /// </summary>
    public class ScoreBreakdown
    {
        [JsonPropertyName("critical")]
        public int Critical { get; set; }
        
        [JsonPropertyName("collectorErrors")]
        public int CollectorErrors { get; set; }
        
        [JsonPropertyName("warnings")]
        public int Warnings { get; set; }
        
        [JsonPropertyName("timeouts")]
        public int Timeouts { get; set; }
        
        [JsonPropertyName("infoIssues")]
        public int InfoIssues { get; set; }
        
        [JsonPropertyName("excludedLimitations")]
        public int ExcludedLimitations { get; set; }
    }

    /// <summary>
    /// Information sur une pénalité spécifique
    /// </summary>
    public class PenaltyInfo
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;
        
        [JsonPropertyName("penalty")]
        public int Penalty { get; set; }
        
        [JsonPropertyName("msg")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Erreur rencontrée pendant le scan
    /// </summary>
    public class ScanErrorInfo
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;
        
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
        
        [JsonPropertyName("section")]
        public string Section { get; set; } = string.Empty;
        
        [JsonPropertyName("exceptionType")]
        public string ExceptionType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Modèle de confiance du score - coverage + cohérence
    /// </summary>
    public class ConfidenceModel
    {
        /// <summary>Score de confiance global 0-100</summary>
        public int ConfidenceScore { get; set; } = 100;
        
        /// <summary>Niveau de confiance textuel</summary>
        public string ConfidenceLevel { get; set; } = "Élevé";
        
        /// <summary>Ratio de couverture des sections PS (0-1)</summary>
        public double SectionsCoverage { get; set; } = 1.0;
        
        /// <summary>Ratio de couverture des capteurs hardware (0-1)</summary>
        public double SensorsCoverage { get; set; } = 0.0;
        
        /// <summary>Nombre de capteurs disponibles</summary>
        public int SensorsAvailable { get; set; }
        
        /// <summary>Nombre total de capteurs attendus</summary>
        public int SensorsTotal { get; set; }
        
        /// <summary>Avertissements sur la qualité des données</summary>
        public List<string> Warnings { get; set; } = new();
        
        /// <summary>Indique si le score est fiable</summary>
        public bool IsReliable => ConfidenceScore >= 70;
    }

    /// <summary>
    /// Traçabilité de la divergence entre score PS et score GradeEngine
    /// </summary>
    public class ScoreDivergence
    {
        /// <summary>Score original du PowerShell (scoreV2)</summary>
        public int PowerShellScore { get; set; }
        
        /// <summary>Grade original du PowerShell</summary>
        public string PowerShellGrade { get; set; } = "N/A";
        
        /// <summary>Score calculé par GradeEngine (UI)</summary>
        public int GradeEngineScore { get; set; }
        
        /// <summary>Grade calculé par GradeEngine (UI)</summary>
        public string GradeEngineGrade { get; set; } = "N/A";
        
        /// <summary>Différence absolue entre les deux scores</summary>
        public int Delta => Math.Abs(GradeEngineScore - PowerShellScore);
        
        /// <summary>Indique si les deux scores sont cohérents (delta &lt;= 10)</summary>
        public bool IsCoherent => Delta <= 10;
        
        /// <summary>Explication de la divergence</summary>
        public string Explanation { get; set; } = "";
        
        /// <summary>Source de vérité utilisée pour l'affichage UI</summary>
        public string SourceOfTruth { get; set; } = "GradeEngine";
    }
}
