using System.Collections.Generic;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Résumé santé PC simplifié pour futur tableau UI.
    /// STUB - Logique métier à implémenter ultérieurement.
    /// </summary>
    public class HealthSummary
    {
        /// <summary>Score global santé (0-100)</summary>
        public int OverallScore { get; set; }

        /// <summary>Grade lettre (A-F)</summary>
        public string Grade { get; set; } = "N/A";

        /// <summary>État général (Excellent, Bon, Moyen, Critique)</summary>
        public string OverallStatus { get; set; } = "Non évalué";

        /// <summary>Catégories de santé individuelles</summary>
        public List<HealthCategory> Categories { get; set; } = new();

        /// <summary>Recommandations prioritaires</summary>
        public List<string> Recommendations { get; set; } = new();

        /// <summary>Données manquantes détectées</summary>
        public List<string> MissingData { get; set; } = new();
    }

    /// <summary>
    /// Catégorie de santé individuelle (CPU, RAM, Disque, etc.)
    /// </summary>
    public class HealthCategory
    {
        /// <summary>Nom de la catégorie</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Icône ou emoji représentatif</summary>
        public string Icon { get; set; } = "📊";

        /// <summary>Score catégorie (0-100)</summary>
        public int Score { get; set; }

        /// <summary>État (OK, Warning, Critical)</summary>
        public HealthStatus Status { get; set; } = HealthStatus.Unknown;

        /// <summary>Message court</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Détails additionnels (clé/valeur)</summary>
        public Dictionary<string, string> Details { get; set; } = new();
    }

    /// <summary>
    /// États de santé possibles
    /// </summary>
    public enum HealthStatus
    {
        Unknown = 0,
        Excellent = 1,
        Good = 2,
        Warning = 3,
        Critical = 4
    }
}
