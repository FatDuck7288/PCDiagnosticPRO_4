using System.Collections.Generic;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Résultat complet d'un scan
    /// </summary>
    public class ScanResult
    {
        public ScanSummary Summary { get; set; } = new ScanSummary();
        public List<ScanItem> Items { get; set; } = new List<ScanItem>();
        public List<ResultSection> Sections { get; set; } = new List<ResultSection>();
        public string RawReport { get; set; } = string.Empty;
        public string ReportFilePath { get; set; } = string.Empty;
        public bool IsValid { get; set; } = false;
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
