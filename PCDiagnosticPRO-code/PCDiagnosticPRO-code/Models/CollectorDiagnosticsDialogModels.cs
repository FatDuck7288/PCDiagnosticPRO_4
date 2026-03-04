using System.Collections.Generic;

namespace PCDiagnosticPro.Models
{
    public sealed class CollectorDiagnosticsDialogData
    {
        public int CollectorErrorsLogical { get; set; }
        public List<CollectorDiagnosticDetailItem> Errors { get; set; } = new();
        public List<CollectorDiagnosticDetailItem> MissingData { get; set; } = new();
        public List<CollectorDiagnosticDetailItem> CsharpExceptions { get; set; } = new();
    }

    public sealed class CollectorDiagnosticDetailItem
    {
        public string Section { get; set; } = "Général";
        public string Reason { get; set; } = string.Empty;
        public string Source { get; set; } = "PS";
        public string Timestamp { get; set; } = string.Empty;
        public string ConfidenceImpact { get; set; } = "Moyen";
        public string TechnicalDetails { get; set; } = string.Empty;
        public bool HasTimestamp => !string.IsNullOrWhiteSpace(Timestamp);
        public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(TechnicalDetails);
    }
}
