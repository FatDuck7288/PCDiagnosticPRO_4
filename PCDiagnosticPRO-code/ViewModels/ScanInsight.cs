namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// Niveau de voyant pour un constat de scan.
    /// </summary>
    public enum InsightLevel
    {
        /// <summary>Blanc : information pertinente (update pending, service arrêté, erreurs event logs élevées).</summary>
        White,
        /// <summary>Jaune : donnée manquante ou limitation (MissingData, temp indisponible, tests réseau désactivés).</summary>
        Yellow,
        /// <summary>Rouge : erreur collecteur, JSON invalide, parse fail.</summary>
        Red
    }

    /// <summary>
    /// Constat affiché dans le panneau "Constats" de l'écran de scan.
    /// Un voyant (petit cercle lumineux) + titre + détail + lien vers section rapport.
    /// </summary>
    public class ScanInsight
    {
        public InsightLevel Level { get; set; }
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        /// <summary>Section cible dans le rapport intégral (ex: "CPU", "CollectorErrors"). Null si pas de lien.</summary>
        public string? TargetSectionId { get; set; }
    }
}
