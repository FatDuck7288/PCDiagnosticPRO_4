using System;
using System.Collections.Generic;
using System.Linq;

namespace PCDiagnosticPro.AI.Models
{
    public sealed class JudgeViolation
    {
        public string Code { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string EvidenceLine { get; set; } = string.Empty;
        public string Fix { get; set; } = string.Empty;
    }

    public enum SecurityVerdict
    {
        APPROUVE,
        A_REVOIR,
        REFUSE
    }

    public class JudgeResult
    {
        public int SecurityScore0_100 { get; set; }
        public int AccuracyScore0_100 { get; set; }
        public int MinimalityScore0_100 { get; set; }
        public int ReversibilityScore0_100 { get; set; }
        public int EfficiencyScore0_100 { get; set; }
        public int ReadabilityScore0_100 { get; set; }
        public int RelevanceScore0_100 { get; set; }
        public int RobustnessScore0_100 { get; set; }
        public int UxScore0_100 { get; set; }
        public int ScriptQualityComposite0_100 { get; set; }
        public int OverallScore0_100 { get; set; }
        public SecurityVerdict Verdict { get; set; } = SecurityVerdict.A_REVOIR;
        public List<string> Flags { get; set; } = new();
        public List<string> Reasons { get; set; } = new();
        public List<string> StaticTests { get; set; } = new();
        public List<JudgeViolation> Violations { get; set; } = new();
        public string Rationale { get; set; } = string.Empty;
        public string SuggestedPatch { get; set; } = string.Empty;
        public bool JudgeError { get; set; }
        public string JudgeErrorMessage { get; set; } = string.Empty;
        public bool JudgeRetried { get; set; }

        public bool ContainsBlockedCommand { get; set; }
        public bool HasMandatoryGuardViolations { get; set; }
        public bool HasExplicitCapabilities { get; set; }
        public List<string> DeclaredCapabilities { get; set; } = new();
        public List<string> UnauthorizedCapabilities { get; set; } = new();

        public bool AutoFixApproved { get; set; }
        public List<string> AutoFixGateReasons { get; set; } = new();

        /// <summary>
        /// Populated by SafetyPolicyEngine: which hard-block rule IDs matched (EXEC_DYNAMIC, DOWNLOAD_EXEC, etc.)
        /// </summary>
        public List<string> HardBlockFlags { get; set; } = new();

        /// <summary>
        /// Populated by SafetyPolicyEngine: reliability/quality flags (MANDATORY_TRY_CATCH_MISSING, PS7_ONLY_*, etc.)
        /// These cause A_REVOIR but NOT REFUSE. AutoFix may still be allowed if score is sufficient.
        /// </summary>
        public List<string> MandatoryViolationFlags { get; set; } = new();

        /// <summary>
        /// Human-readable category of the blocking gate: "HardBlock" | "ScoreGate" | "MissingScript" | "None"
        /// </summary>
        public string BlockedByCategory { get; set; } = "None";

        /// <summary>Top 3 reasons that determined the verdict (for compact UI display).</summary>
        public List<string> TopReasons => Reasons.Take(3).ToList();

        public bool HasCriticalViolation =>
            Violations.Any(v => string.Equals(v.Severity, "Critical", System.StringComparison.OrdinalIgnoreCase))
            || HardBlockFlags.Count > 0;

        public bool IsMissingScriptError =>
            string.Equals(BlockedByCategory, "MissingScript", StringComparison.OrdinalIgnoreCase)
            || Flags.Any(f => string.Equals(f, "EMPTY_SCRIPT", StringComparison.OrdinalIgnoreCase));

        public string VerdictDisplay => Verdict switch
        {
            _ when IsMissingScriptError => "ERROR: MissingScript",
            SecurityVerdict.APPROUVE => "APPROUVE",
            SecurityVerdict.REFUSE => "REFUSE",
            _ => "A_REVOIR"
        };

        /// <summary>Friendly label for the blocking category shown in UI.</summary>
        public string BlockedByCategoryDisplay => BlockedByCategory switch
        {
            "HardBlock" => "Blocage dur (motif dangereux detecte)",
            "ScoreGate" => "Score insuffisant",
            "MissingScript" => "Aucun script genere",
            _ => string.Empty
        };

        public string ScoreDisplay => IsMissingScriptError
            ? "N/A"
            : $"{OverallScore0_100}/100 (S:{SecurityScore0_100} Rel:{RelevanceScore0_100} Rob:{RobustnessScore0_100} UX:{UxScore0_100})";
    }
}
