namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// Niveau de gravité pour une issue (Critique, Warning, Info).
    /// </summary>
    public enum IssueLevel
    {
        Info,
        Warning,
        Critical
    }

    /// <summary>
    /// Élément d'issue/erreur/limitation affiché dans une section du rapport intégral.
    /// </summary>
    public class ReportIssue
    {
        public IssueLevel Level { get; set; }
        public string Message { get; set; } = "";
        public string? Code { get; set; }
        public string? Source { get; set; }
    }
}
