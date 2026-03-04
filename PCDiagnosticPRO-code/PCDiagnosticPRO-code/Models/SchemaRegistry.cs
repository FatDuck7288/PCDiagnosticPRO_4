namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Single authoritative source for all schema/version constants.
    /// Update ONLY here when schema changes — all other files reference these.
    /// </summary>
    public static class SchemaRegistry
    {
        /// <summary>
        /// DiagnosticSnapshot schema version.
        /// Increment when snapshot model fields are added/removed/renamed.
        /// </summary>
        public const string SnapshotSchemaVersion = "2.3.0";

        /// <summary>
        /// CombinedScanResult top-level schema version.
        /// Increment when combined output structure changes.
        /// </summary>
        public const string CombinedSchemaVersion = "combined-1.2";

        /// <summary>
        /// AiRunReport schema version.
        /// </summary>
        public const string AiRunReportSchemaVersion = "ai-run-2.0";

        /// <summary>
        /// Collector/builder component version (maps to app release).
        /// </summary>
        public const string CollectorVersion = "2.3.0";

        /// <summary>
        /// App display version.
        /// </summary>
        public const string AppVersion = "2.3.0";

        /// <summary>
        /// PF-2: Single authoritative score-to-grade conversion (UDIS scale).
        /// All C# code (HealthReportBuilder, ContextPackBuilder, PowerShellJsonMapper)
        /// and the PS script Get-ScoreV2 must use these identical thresholds.
        /// A+(≥95) · A(≥90) · B+(≥80) · B(≥70) · C(≥60) · D(≥50) · F
        /// </summary>
        public static string ScoreToGrade(int score) => score switch
        {
            >= 95 => "A+",
            >= 90 => "A",
            >= 80 => "B+",
            >= 70 => "B",
            >= 60 => "C",
            >= 50 => "D",
            _ => "F"
        };
    }
}
