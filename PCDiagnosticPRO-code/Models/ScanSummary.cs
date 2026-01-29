using System;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Résumé global du scan
    /// </summary>
    public class ScanSummary
    {
        public int Score { get; set; } = 0;
        public string Grade { get; set; } = "N/A";
        public int TotalItems { get; set; } = 0;
        public int OkCount { get; set; } = 0;
        public int WarningCount { get; set; } = 0;
        public int ErrorCount { get; set; } = 0;
        public int CriticalCount { get; set; } = 0;
        public bool RebootRequired { get; set; } = false;
        public double RamUsagePercent { get; set; } = 0;
        public string WindowsUpdateStatus { get; set; } = "N/A";
        public string SystemName { get; set; } = "N/A";
        public string OsVersion { get; set; } = "N/A";
        public DateTime ScanDate { get; set; } = DateTime.Now;
        public TimeSpan ScanDuration { get; set; } = TimeSpan.Zero;

        public string GradeColor => Grade switch
        {
            "A" or "A+" => "#2ED573",
            "B" or "B+" => "#7BED9F",
            "C" or "C+" => "#FFA502",
            "D" or "D+" => "#FF6348",
            "F" => "#FF4757",
            _ => "#8B949E"
        };

        public string ScoreDisplay => $"{Score}/100";
        public string RebootRequiredText => RebootRequired ? "Oui" : "Non";
        public string RamUsageDisplay => $"{RamUsagePercent:F1}%";
        public string DurationDisplay => ScanDuration.TotalSeconds > 60 
            ? $"{ScanDuration.Minutes}m {ScanDuration.Seconds}s" 
            : $"{ScanDuration.TotalSeconds:F1}s";
    }
}
