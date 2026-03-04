using System.Collections.Generic;

namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// Tableau (arrays) affiché dans une section du rapport.
    /// </summary>
    public class ReportTableViewModel
    {
        public string Title { get; set; } = "";
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
    }
}
