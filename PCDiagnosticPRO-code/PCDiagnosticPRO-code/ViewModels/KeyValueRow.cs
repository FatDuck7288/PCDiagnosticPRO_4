namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// Ligne clé / valeur pour les tableaux de détail (DataGrid).
    /// </summary>
    public class KeyValueRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public string Unit { get; set; } = "";
        /// <summary>Optionnel : niveau pour colorer la ligne (Info, Warning, Critical).</summary>
        public IssueLevel Level { get; set; } = IssueLevel.Info;

        /// <summary>Official provenance type for this UI field (collecte/calcule/deduit/indisponible).</summary>
        public string Provenance { get; set; } = "";

        /// <summary>Canonical JSON path linked to this row.</summary>
        public string JsonPath { get; set; } = "";

        /// <summary>User-facing reason for unavailable or partial data.</summary>
        public string Reason { get; set; } = "";

        /// <summary>User-facing confidence level.</summary>
        public string Confidence { get; set; } = "";

        /// <summary>Critical row marker (for missing-data explained indicator).</summary>
        public bool IsCritical { get; set; }

        /// <summary>True when missing data is explicitly explained (source + reason + confidence).</summary>
        public bool MissingDataExplained { get; set; }

        public string MissingDataDisplay => MissingDataExplained ? "Oui" : "";

        /// <summary>True for the Kernel-Power row when KP events exist; shows the clickable detail button.</summary>
        public bool ShouldShowKernelPowerButton { get; set; }
    }
}
