using System;
using PCDiagnosticPro.Themes;

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

        public string StatusColor
        {
            get
            {
                var isXRay = IsXRayTheme();
                return Severity switch
                {
                    ScanSeverity.OK => "#2ED573",
                    ScanSeverity.Info => isXRay ? "#1E90FF" : "#3742FA",
                    ScanSeverity.Warning => "#FFA502",
                    ScanSeverity.Error => isXRay ? "#00BFFF" : "#FF4757",
                    ScanSeverity.Critical => isXRay ? "#007ACC" : "#FF0000",
                    _ => "#8B949E"
                };
            }
        }

        private static bool IsXRayTheme()
        {
            try
            {
                return string.Equals(App.GetCurrentTheme(), ThemeDefinitions.PCXRayCode, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
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
