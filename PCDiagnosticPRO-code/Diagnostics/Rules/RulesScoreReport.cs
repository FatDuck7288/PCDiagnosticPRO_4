using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Diagnostics.Rules
{
    /// <summary>
    /// Rapport de score complet basé sur règles.
    /// Remplace ScoreV2 (scoring par règles indépendant).
    /// </summary>
    public class RulesScoreReport
    {
        /// <summary>Date/heure du calcul</summary>
        [JsonPropertyName("computedAt")]
        public DateTime ComputedAt { get; set; }

        /// <summary>Score santé global 0-100 (pénalise uniquement sur anomalies détectées)</summary>
        [JsonPropertyName("globalHealthScore")]
        public int GlobalHealthScore { get; set; }

        /// <summary>Grade (A+, A, B+, B, C, D, F)</summary>
        [JsonPropertyName("globalHealthGrade")]
        public string GlobalHealthGrade { get; set; } = "N/A";

        /// <summary>Label (Excellent, Bon, À surveiller, Dégradé, Critique)</summary>
        [JsonPropertyName("globalHealthLabel")]
        public string GlobalHealthLabel { get; set; } = "N/A";

        /// <summary>Si un hard cap a été appliqué, explication</summary>
        [JsonPropertyName("hardCapApplied")]
        public string? HardCapApplied { get; set; }

        /// <summary>Poids normalisés utilisés (pour audit)</summary>
        [JsonPropertyName("weights")]
        public Dictionary<string, double> Weights { get; set; } = new();

        /// <summary>Scores par section</summary>
        [JsonPropertyName("sections")]
        public List<SectionScore> Sections { get; set; } = new();

        /// <summary>Pénalités critiques appliquées après moyenne pondérée</summary>
        [JsonPropertyName("criticalPenalties")]
        public List<CriticalPenalty> CriticalPenalties { get; set; } = new();

        /// <summary>Modèle de confiance (qualité collecte)</summary>
        [JsonPropertyName("confidence")]
        public ConfidenceModel Confidence { get; set; } = new();

        /// <summary>Collecte terminée avec succès</summary>
        [JsonPropertyName("collectionComplete")]
        public bool CollectionComplete { get; set; }

        /// <summary>Raison de l'échec de collecte si applicable</summary>
        [JsonPropertyName("collectionFailureReason")]
        public string? CollectionFailureReason { get; set; }
    }

    /// <summary>
    /// Score d'une section individuelle
    /// </summary>
    public class SectionScore
    {
        [JsonPropertyName("sectionName")]
        public string SectionName { get; set; } = "";

        [JsonPropertyName("weight")]
        public double Weight { get; set; }

        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonPropertyName("status")]
        public SectionStatus Status { get; set; } = SectionStatus.Unknown;

        /// <summary>Données brutes utilisées pour le calcul</summary>
        [JsonPropertyName("rawInputs")]
        public Dictionary<string, string> RawInputs { get; set; } = new();

        /// <summary>Règles appliquées avec leur impact</summary>
        [JsonPropertyName("appliedRules")]
        public List<AppliedRule> AppliedRules { get; set; } = new();

        /// <summary>Actions recommandées</summary>
        [JsonPropertyName("recommendedActions")]
        public List<string> RecommendedActions { get; set; } = new();
    }

    /// <summary>
    /// Règle appliquée avec son impact
    /// </summary>
    public class AppliedRule
    {
        [JsonPropertyName("ruleId")]
        public string RuleId { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("impact")]
        public int Impact { get; set; }

        public AppliedRule() { }

        public AppliedRule(string ruleId, string description, int impact)
        {
            RuleId = ruleId;
            Description = description;
            Impact = impact;
        }
    }

    /// <summary>
    /// Pénalité critique appliquée après moyenne pondérée
    /// </summary>
    public class CriticalPenalty
    {
        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";

        [JsonPropertyName("penaltyPoints")]
        public int PenaltyPoints { get; set; }
    }

    /// <summary>
    /// Modèle de confiance dans la collecte
    /// </summary>
    public class ConfidenceModel
    {
        /// <summary>Score de base (100)</summary>
        [JsonPropertyName("baseScore")]
        public int BaseScore { get; set; } = 100;

        /// <summary>Score de confiance final 0-100</summary>
        [JsonPropertyName("confidenceScore")]
        public int ConfidenceScore { get; set; }

        /// <summary>Niveau (Fiable >= 90, Moyen >= 70, Faible < 70)</summary>
        [JsonPropertyName("confidenceLevel")]
        public string ConfidenceLevel { get; set; } = "N/A";

        /// <summary>Signaux manquants</summary>
        [JsonPropertyName("missingSignals")]
        public List<string> MissingSignals { get; set; } = new();

        /// <summary>Erreurs de collecte</summary>
        [JsonPropertyName("collectorErrors")]
        public List<string> CollectorErrors { get; set; } = new();

        /// <summary>Collecte échouée (PerfCounters vides)</summary>
        [JsonPropertyName("collectionFailed")]
        public bool CollectionFailed { get; set; }
    }

    /// <summary>
    /// Statut d'une section
    /// </summary>
    public enum SectionStatus
    {
        Unknown,
        OK,
        Warning,
        Degraded,
        Critical
    }
}
