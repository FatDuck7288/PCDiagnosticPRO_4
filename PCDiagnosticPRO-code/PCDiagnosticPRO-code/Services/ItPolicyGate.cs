using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// IT-Only Policy Gate: validates that all diagnostic findings are strictly
    /// IT-related and have proper evidence paths before allowing AutoFix.
    /// Findings without evidence are downgraded to SuggestOnly.
    /// Findings outside allowed IT domains are rejected.
    /// </summary>
    public static class ItPolicyGate
    {
        /// <summary>
        /// Whitelist of allowed IT diagnostic domains.
        /// Any finding with an IssueType not mapping to these domains is rejected.
        /// </summary>
        private static readonly HashSet<string> AllowedDomains = new(StringComparer.OrdinalIgnoreCase)
        {
            "OS", "Memory", "Security", "Stability", "Storage",
            "Network", "Updates", "Startup", "Devices", "Boot",
            "CPU", "GPU", "Drivers", "Performance", "Temperature",
            "CollectorError", "WindowsUpdate"
        };

        /// <summary>
        /// Validates a single finding against the IT-only policy.
        /// </summary>
        public static PolicyResult ValidateFindingWithPolicy(DiagnosticFinding finding)
        {
            if (finding == null)
                return new PolicyResult { IsValid = false, Reason = "finding_is_null" };

            // 1. Verify IssueType maps to an allowed IT domain
            if (!AllowedDomains.Contains(finding.IssueType))
            {
                return new PolicyResult
                {
                    IsValid = false,
                    IsDowngraded = false,
                    Reason = $"IssueType '{finding.IssueType}' is not an allowed IT domain"
                };
            }

            // 2. If EvidencePaths is empty and AutoFix is requested → downgrade to SuggestOnly
            if (finding.AutoFixPossible && (finding.EvidencePaths == null || finding.EvidencePaths.Count == 0))
            {
                return new PolicyResult
                {
                    IsValid = true,
                    IsDowngraded = true,
                    Reason = "No evidence paths — downgraded to suggest-only"
                };
            }

            return new PolicyResult { IsValid = true, IsDowngraded = false };
        }

        /// <summary>
        /// Applies the IT-only policy to a list of findings.
        /// - Removes findings outside allowed IT domains.
        /// - Downgrades AutoFix to suggest-only when evidence paths are missing.
        /// </summary>
        public static List<DiagnosticFinding> ApplyPolicy(List<DiagnosticFinding> findings)
        {
            if (findings == null || findings.Count == 0)
                return findings ?? new List<DiagnosticFinding>();

            var validated = new List<DiagnosticFinding>();
            int rejected = 0;
            int downgraded = 0;

            foreach (var finding in findings)
            {
                var result = ValidateFindingWithPolicy(finding);

                if (!result.IsValid)
                {
                    rejected++;
                    App.LogMessage($"[ItPolicyGate] REJECTED: {finding.IssueType} — {result.Reason}");
                    continue;
                }

                if (result.IsDowngraded)
                {
                    finding.AutoFixPossible = false;
                    finding.RiskLevel = "Low";
                    finding.SuggestedAction = finding.SuggestedAction != null
                        ? $"[SUGGEST-ONLY] {finding.SuggestedAction}"
                        : "[SUGGEST-ONLY] Pas de preuve explicite — suggestion uniquement";
                    downgraded++;
                }

                validated.Add(finding);
            }

            if (rejected > 0 || downgraded > 0)
            {
                App.LogMessage($"[ItPolicyGate] Applied policy: {validated.Count} valid, {rejected} rejected, {downgraded} downgraded");
            }

            return validated;
        }
    }
}
