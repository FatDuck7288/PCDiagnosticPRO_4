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
    }
}
