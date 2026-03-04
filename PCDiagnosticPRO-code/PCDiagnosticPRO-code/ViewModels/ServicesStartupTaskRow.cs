namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// Explicit sub-table row for Services / Startup / Tasks in Rapport Integral.
    /// </summary>
    public class ServicesStartupTaskRow
    {
        public string Category { get; set; } = "";
        public string Metric { get; set; } = "";
        public string Value { get; set; } = "";
        public string Source { get; set; } = "";
    }
}
