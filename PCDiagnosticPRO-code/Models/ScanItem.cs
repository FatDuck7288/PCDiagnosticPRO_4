using System;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Représente un élément individuel du scan
    /// </summary>
    public class ScanItem
    {
        public string Category { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = "N/A";
        public string Detail { get; set; } = string.Empty;
        public ScanSeverity Severity { get; set; } = ScanSeverity.Info;
        public string Recommendation { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public string SeverityText => Severity switch
        {
            ScanSeverity.OK => "OK",
            ScanSeverity.Info => "INFO",
            ScanSeverity.Warning => "WARN",
            ScanSeverity.Error => "FAIL",
            ScanSeverity.Critical => "CRIT",
            _ => "N/A"
        };

        public string StatusColor => Severity switch
        {
            ScanSeverity.OK => "#2ED573",
            ScanSeverity.Info => "#3742FA",
            ScanSeverity.Warning => "#FFA502",
            ScanSeverity.Error => "#FF4757",
            ScanSeverity.Critical => "#FF0000",
            _ => "#8B949E"
        };
    }

    public enum ScanSeverity
    {
        OK,
        Info,
        Warning,
        Error,
        Critical
    }
}
