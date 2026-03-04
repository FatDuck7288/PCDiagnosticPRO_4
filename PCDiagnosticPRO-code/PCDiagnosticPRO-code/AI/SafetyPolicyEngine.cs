using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI
{
    public sealed class AutoFixEligibilityResult
    {
        public bool IsApproved { get; set; }
        public JudgeResult Judge { get; set; } = new();

        /// <summary>All reasons (blocking + warnings) — for legacy callers.</summary>
        public List<string> Reasons { get; set; } = new();

        /// <summary>Reasons that caused IsApproved=false. Empty = approved.</summary>
        public List<string> BlockingReasons { get; set; } = new();

        /// <summary>Reasons shown as warnings but do NOT block approval.</summary>
        public List<string> WarningReasons { get; set; } = new();

        public List<string> DeclaredCapabilities { get; set; } = new();

        /// <summary>"HardBlock" | "ScoreGate" | "MissingScript" | "None"</summary>
        public string BlockedBy { get; set; } = "None";

        /// <summary>Sub-score name that failed the score gate, if BlockedBy="ScoreGate".</summary>
        public string FailedSubScore { get; set; } = string.Empty;

        public string BlockedByDisplay => BlockedBy switch
        {
            "HardBlock" => "Blocage dur — motif dangereux detecte",
            "ScoreGate" => $"Score insuffisant ({FailedSubScore})",
            "MissingScript" => "Aucun script genere par le pipeline",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Deterministic safety and quality analysis for generated PowerShell scripts.
    /// </summary>
    public class SafetyPolicyEngine
    {
        private readonly AiSettings _settings;
        private readonly ScriptCapabilitiesParser _capabilitiesParser = new();

        private static readonly (Regex Pattern, string Flag, int Penalty)[] HardBlockedPatterns =
        {
            (new Regex(@"\bIEX\b|\bInvoke-Expression\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "EXEC_DYNAMIC", 90),
            (new Regex(@"EncodedCommand|-enc\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ENCODED_CMD", 90),
            (new Regex(@"Invoke-WebRequest.*\|.*IEX", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled), "DOWNLOAD_EXEC", 90),
            (new Regex(@"DownloadString.*IEX|IEX.*DownloadString", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled), "DOWNLOAD_EXEC", 90),
            (new Regex(@"Remove-Item\s+-Recurse\s+(['""]?C:[/\\]|['""]?%SystemDrive%[/\\])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "MASS_DELETE", 85),
            (new Regex(@"Set-MpPreference|DisableRealtimeMonitoring", RegexOptions.IgnoreCase | RegexOptions.Compiled), "DISABLE_AV", 85),
            (new Regex(@"Add-MpPreference.*ExclusionPath", RegexOptions.IgnoreCase | RegexOptions.Compiled), "AV_EXCLUSION", 80),
            (new Regex(@"netsh.*firewall.*disable|Set-NetFirewallProfile.*Disabled", RegexOptions.IgnoreCase | RegexOptions.Compiled), "FIREWALL_OFF", 80),
            (new Regex(@"New-LocalUser|net\s+user\s+\S+\s+/add", RegexOptions.IgnoreCase | RegexOptions.Compiled), "CREATE_USER", 80),
            (new Regex(@"Add-LocalGroupMember.*Administrator", RegexOptions.IgnoreCase | RegexOptions.Compiled), "ESCALATE_PRIV", 85),
            (new Regex(@"\bClear-Disk\b|\bFormat-Volume\b|\bdiskpart\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "DISK_DESTRUCTIVE", 95),
            (new Regex(@"vssadmin\s+delete\s+shadows", RegexOptions.IgnoreCase | RegexOptions.Compiled), "DELETE_SHADOWS", 90),
            (new Regex(@"bcdedit\s+/set", RegexOptions.IgnoreCase | RegexOptions.Compiled), "BOOT_CONFIG_EDIT", 85)
        };

        private static readonly (Regex Pattern, string Flag, int Penalty)[] WarningPatterns =
        {
            (new Regex(@"schtasks\s+/create", RegexOptions.IgnoreCase | RegexOptions.Compiled), "SCHEDULED_TASK", 20),
            (new Regex(@"reg\s+add\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "REGISTRY_WRITE", 15),
            (new Regex(@"Set-ExecutionPolicy", RegexOptions.IgnoreCase | RegexOptions.Compiled), "EXEC_POLICY", 10),
            (new Regex(@"\bStart-Process\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "START_PROCESS", 5)
        };

        private static readonly (Regex Pattern, string Flag)[] Ps7OnlyPatterns =
        {
            (new Regex(@"ForEach-Object\s+-Parallel", RegexOptions.IgnoreCase | RegexOptions.Compiled), "PS7_ONLY_FOREACH_PARALLEL"),
            (new Regex(@"\|\|", RegexOptions.Compiled), "PS7_ONLY_PIPELINE_OR"),
            (new Regex(@"\?\?", RegexOptions.Compiled), "PS7_ONLY_NULL_COALESCING")
        };

        /// <summary>
        /// Strip single-line PS comments before checking PS7-only patterns
        /// to avoid false positives when || or ?? appear inside # comment lines.
        /// </summary>
        private static readonly Regex SingleLineCommentRegex = new(
            @"(?m)^\s*#.*$", RegexOptions.Compiled);

        private static readonly Regex TryRegex = new(@"\btry\s*\{", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CatchRegex = new(@"\bcatch\s*\{", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LoggingRegex = new(@"\bWrite-Host\b|\bWrite-Output\b|\bStart-Transcript\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnyWebCallRegex = new(@"\bInvoke-WebRequest\b|\bInvoke-RestMethod\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TimedWebCallRegex = new(@"\bInvoke-WebRequest\b[^\r\n]*-TimeoutSec\b|\bInvoke-RestMethod\b[^\r\n]*-TimeoutSec\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnyStartProcessRegex = new(@"\bStart-Process\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TimedProcessWaitRegex = new(@"\bWait-Process\b[^\r\n]*-Timeout\b|\bStart-Process\b[^\r\n]*-Wait\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RequiresVersionRegex = new(@"^\s*#Requires\s+-Version\s+5(\.1)?\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex RequiresPs7Regex = new(@"^\s*#Requires\s+-Version\s+7", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex HeavyRecursiveRegex = new(@"Get-ChildItem[^\r\n]*-Recurse", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MutatingCommandRegex = new(
            @"\b(Set-|Remove-|New-|Disable-|Enable-|Restart-Service|Stop-Service|Start-Service|reg\s+add|sc\.exe\s+config)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WhatIfRegex = new(@"\b-WhatIf\b|\bShouldProcess\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] RequiredHeaders =
        {
            "# SUMMARY:",
            "# DOES_NOT:",
            "# RISKS:",
            "# ROLLBACK:",
            "# REQUIRES_ADMIN:",
            "# CAPABILITIES:"
        };

        public SafetyPolicyEngine(AiSettings settings)
        {
            _settings = settings;
        }

        public JudgeResult Analyse(string scriptText)
        {
            var result = new JudgeResult();

            if (string.IsNullOrWhiteSpace(scriptText))
            {
                result.SecurityScore0_100 = 0;
                result.AccuracyScore0_100 = 0;
                result.MinimalityScore0_100 = 0;
                result.ReversibilityScore0_100 = 0;
                result.EfficiencyScore0_100 = 0;
                result.ReadabilityScore0_100 = 0;
                result.RelevanceScore0_100 = 0;
                result.RobustnessScore0_100 = 0;
                result.UxScore0_100 = 0;
                result.ScriptQualityComposite0_100 = 0;
                result.OverallScore0_100 = 0;
                result.Verdict = SecurityVerdict.REFUSE;
                result.BlockedByCategory = "MissingScript";
                result.JudgeError = true;
                result.JudgeErrorMessage = "Judge skipped LLM because script is empty.";
                result.Flags.Add("EMPTY_SCRIPT");
                result.Reasons.Add("Generated script is empty.");
                result.Violations.Add(new JudgeViolation
                {
                    Code = "EMPTY_SCRIPT",
                    Severity = "Critical",
                    EvidenceLine = "(empty output)",
                    Fix = "Ensure Agent 1 produces a non-empty PowerShell script before review."
                });
                result.HasMandatoryGuardViolations = true;
                return result;
            }

            var flags = new List<string>();
            var reasons = new List<string>();
            var staticTests = new List<string>();
            var violations = new List<JudgeViolation>();
            var securityPenalty = 0;
            var efficiencyPenalty = 0;
            var readabilityPenalty = 0;
            var hasHardBlock = false;
            var blockedByConfig = false;
            var mandatoryGuardViolation = false;

            var hardBlockFlags = new List<string>();
            var mandatoryViolationFlags = new List<string>();

            foreach (var (pattern, flag, penalty) in HardBlockedPatterns)
            {
                var match = pattern.Match(scriptText);
                if (!match.Success)
                {
                    staticTests.Add($"PASS [{flag}]");
                    continue;
                }

                hasHardBlock = true;
                flags.Add(flag);
                hardBlockFlags.Add(flag);
                reasons.Add($"Dangerous pattern detected: {flag} ({match.Value.Trim()})");
                staticTests.Add($"FAIL [{flag}]");
                violations.Add(new JudgeViolation
                {
                    Code = flag,
                    Severity = "Critical",
                    EvidenceLine = match.Value.Trim(),
                    Fix = "Remove this dangerous pattern and replace with a safe built-in command."
                });
                securityPenalty += penalty;
            }

            foreach (var (pattern, flag, penalty) in WarningPatterns)
            {
                var match = pattern.Match(scriptText);
                if (!match.Success)
                {
                    continue;
                }

                flags.Add(flag);
                reasons.Add($"Pattern to review: {flag} ({match.Value.Trim()})");
                staticTests.Add($"WARN [{flag}]");
                securityPenalty += penalty;
            }

            foreach (var blocked in _settings.BlockedCommands)
            {
                if (string.IsNullOrWhiteSpace(blocked))
                {
                    continue;
                }

                if (!scriptText.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                blockedByConfig = true;
                flags.Add($"BLOCKED:{blocked}");
                reasons.Add($"Blocked command from policy: {blocked}");
                staticTests.Add($"FAIL [CONFIG_BLOCK:{blocked}]");
                violations.Add(new JudgeViolation
                {
                    Code = "CONFIG_BLOCK",
                    Severity = "Critical",
                    EvidenceLine = blocked,
                    Fix = "Delete this blocked command and keep only approved safe operations."
                });
                securityPenalty += 35;
            }

            var hasMutatingOrExternalOps = MutatingCommandRegex.IsMatch(scriptText)
                || AnyWebCallRegex.IsMatch(scriptText)
                || AnyStartProcessRegex.IsMatch(scriptText);

            var capValidation = _capabilitiesParser.Validate(scriptText, _settings.AllowedScriptCapabilities);
            if (!capValidation.HasExplicitDeclaration)
            {
                flags.Add("CAPABILITIES_MISSING");
                reasons.Add("Script does not declare '# CAPABILITIES:' header.");
                staticTests.Add("FAIL [CAPABILITIES_MISSING]");
                violations.Add(new JudgeViolation
                {
                    Code = "CAPABILITIES_MISSING",
                    Severity = "High",
                    EvidenceLine = "# CAPABILITIES:",
                    Fix = "Add '# CAPABILITIES:' and list the exact safe capabilities used by the script."
                });
                // Reduce penalty for missing capabilities header: it's documentation debt,
                // while mutating/external scripts still receive stronger pressure.
                var capMissingSecPenalty = hasMutatingOrExternalOps ? 15 : 8;
                securityPenalty += capMissingSecPenalty;
                readabilityPenalty += 8;
            }
            else
            {
                staticTests.Add("PASS [CAPABILITIES_DECLARED]");
            }

            foreach (var unauthorized in capValidation.UnauthorizedCapabilities)
            {
                flags.Add($"CAPABILITY_BLOCKED:{unauthorized}");
                reasons.Add($"Capability not allowed by policy: {unauthorized}");
                staticTests.Add($"FAIL [CAPABILITY_BLOCKED:{unauthorized}]");
                violations.Add(new JudgeViolation
                {
                    Code = "CAPABILITY_BLOCKED",
                    Severity = "Critical",
                    EvidenceLine = unauthorized,
                    Fix = "Remove unauthorized capability and keep only allowed capabilities."
                });
                securityPenalty += 20;
            }

            // Determine whether script has mutating or external-call operations.
            // Missing try/catch is only a MANDATORY violation when there are such operations.
            var hasTryCatch = TryRegex.IsMatch(scriptText) && CatchRegex.IsMatch(scriptText);

            if (!hasTryCatch && hasMutatingOrExternalOps)
            {
                // Mutating/external script without error handling: serious reliability issue (A_REVOIR, not REFUSE)
                flags.Add("MANDATORY_TRY_CATCH_MISSING");
                mandatoryViolationFlags.Add("MANDATORY_TRY_CATCH_MISSING");
                reasons.Add("Script has mutating or external operations but no try/catch error handling.");
                staticTests.Add("WARN [MANDATORY_TRY_CATCH_MISSING]");
                violations.Add(new JudgeViolation
                {
                    Code = "MANDATORY_TRY_CATCH_MISSING",
                    Severity = "High",
                    EvidenceLine = "missing try/catch around mutating/external operations",
                    Fix = "Wrap risky operations in try/catch and emit explicit error logs."
                });
                mandatoryGuardViolation = true;
                securityPenalty += 25;
                readabilityPenalty += 12;
            }
            else if (!hasTryCatch)
            {
                // Read-only script without try/catch: soft recommendation only
                flags.Add("TRY_CATCH_RECOMMENDED");
                reasons.Add("Script does not include try/catch (recommended for robustness).");
                staticTests.Add("WARN [TRY_CATCH_RECOMMENDED]");
                readabilityPenalty += 8;
            }
            else
            {
                staticTests.Add("PASS [TRY_CATCH]");
            }

            if (AnyWebCallRegex.IsMatch(scriptText) && !TimedWebCallRegex.IsMatch(scriptText))
            {
                flags.Add("MANDATORY_TIMEOUT_WEB_MISSING");
                mandatoryViolationFlags.Add("MANDATORY_TIMEOUT_WEB_MISSING");
                reasons.Add("Web requests must include -TimeoutSec.");
                staticTests.Add("WARN [MANDATORY_TIMEOUT_WEB_MISSING]");
                violations.Add(new JudgeViolation
                {
                    Code = "MANDATORY_TIMEOUT_WEB_MISSING",
                    Severity = "High",
                    EvidenceLine = "Invoke-WebRequest/Invoke-RestMethod without -TimeoutSec",
                    Fix = "Add -TimeoutSec to every web request command."
                });
                mandatoryGuardViolation = true;
                securityPenalty += 12;
                efficiencyPenalty += 20;
            }
            else if (AnyWebCallRegex.IsMatch(scriptText))
            {
                staticTests.Add("PASS [TIMEOUT_WEB]");
            }

            if (AnyStartProcessRegex.IsMatch(scriptText) && !TimedProcessWaitRegex.IsMatch(scriptText))
            {
                flags.Add("MANDATORY_TIMEOUT_PROCESS_MISSING");
                mandatoryViolationFlags.Add("MANDATORY_TIMEOUT_PROCESS_MISSING");
                reasons.Add("Start-Process usage requires -Wait or Wait-Process -Timeout handling.");
                staticTests.Add("WARN [MANDATORY_TIMEOUT_PROCESS_MISSING]");
                violations.Add(new JudgeViolation
                {
                    Code = "MANDATORY_TIMEOUT_PROCESS_MISSING",
                    Severity = "High",
                    EvidenceLine = "Start-Process without timeout guard",
                    Fix = "Use -Wait or Wait-Process -Timeout for process execution."
                });
                mandatoryGuardViolation = true;
                securityPenalty += 10;
                efficiencyPenalty += 15;
            }
            else if (AnyStartProcessRegex.IsMatch(scriptText))
            {
                staticTests.Add("PASS [TIMEOUT_PROCESS]");
            }

            if (!LoggingRegex.IsMatch(scriptText))
            {
                flags.Add("LOGGING_WEAK");
                reasons.Add("Script should contain explicit execution logs (Write-Host/Write-Output/Start-Transcript).");
                staticTests.Add("WARN [LOGGING_WEAK]");
                readabilityPenalty += 14;
            }
            else
            {
                staticTests.Add("PASS [LOGGING]");
            }

            if (!RequiresVersionRegex.IsMatch(scriptText))
            {
                flags.Add("PS_VERSION_HEADER_MISSING");
                reasons.Add("Script should declare '#Requires -Version 5.1' for Windows compatibility baseline.");
                staticTests.Add("WARN [PS_VERSION_HEADER_MISSING]");
                readabilityPenalty += 10;
            }
            else
            {
                staticTests.Add("PASS [PS_VERSION_HEADER]");
            }

            if (RequiresPs7Regex.IsMatch(scriptText))
            {
                flags.Add("PS7_VERSION_LOCK");
                mandatoryViolationFlags.Add("PS7_VERSION_LOCK");
                reasons.Add("Script is locked to PowerShell 7 and may break compatibility with Windows PowerShell 5.1.");
                staticTests.Add("WARN [PS7_VERSION_LOCK]");
                mandatoryGuardViolation = true;
                securityPenalty += 12;
            }

            // Use comment-stripped version for PS7-only checks to avoid false positives
            // when || or ?? appear inside # comment lines.
            var scriptNoComments = SingleLineCommentRegex.Replace(scriptText, string.Empty);

            foreach (var (pattern, flag) in Ps7OnlyPatterns)
            {
                if (!pattern.IsMatch(scriptNoComments))
                {
                    continue;
                }

                flags.Add(flag);
                mandatoryViolationFlags.Add(flag);
                reasons.Add($"PowerShell 7-only feature detected: {flag}");
                staticTests.Add($"WARN [{flag}]");
                mandatoryGuardViolation = true;
                securityPenalty += 10;
                readabilityPenalty += 5;
            }

            foreach (var header in RequiredHeaders)
            {
                if (scriptText.IndexOf(header, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    staticTests.Add($"PASS [HEADER:{header}]");
                    continue;
                }

                flags.Add($"HEADER_MISSING:{header}");
                reasons.Add($"Missing required script header field: {header}");
                staticTests.Add($"WARN [HEADER_MISSING:{header}]");
                readabilityPenalty += 6;
            }

            var nonEmptyLines = scriptText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Length;
            if (nonEmptyLines > 320)
            {
                flags.Add("SCRIPT_TOO_LONG");
                reasons.Add("Script is very long and may be harder to audit quickly.");
                staticTests.Add("WARN [SCRIPT_TOO_LONG]");
                efficiencyPenalty += 18;
                readabilityPenalty += 8;
            }
            else if (nonEmptyLines > 220)
            {
                flags.Add("SCRIPT_LENGTH_REVIEW");
                reasons.Add("Script length is high; consider reducing complexity.");
                staticTests.Add("WARN [SCRIPT_LENGTH_REVIEW]");
                efficiencyPenalty += 8;
            }

            var heavyRecursiveCount = HeavyRecursiveRegex.Matches(scriptText).Count;
            if (heavyRecursiveCount > 2)
            {
                flags.Add("HEAVY_RECURSIVE_SCAN");
                reasons.Add("Multiple recursive scans detected; this can slow down execution.");
                staticTests.Add("WARN [HEAVY_RECURSIVE_SCAN]");
                efficiencyPenalty += 10;
            }

            if (!scriptText.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                flags.Add("HEADER_MISSING");
                reasons.Add("Script must start with a comment header.");
                staticTests.Add("WARN [HEADER_MISSING]");
                readabilityPenalty += 8;
            }

            var mutatingCommands = MutatingCommandRegex.Matches(scriptText).Count;
            var hasRollbackHeader = scriptText.IndexOf("# ROLLBACK:", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasWhatIf = WhatIfRegex.IsMatch(scriptText);

            var securityScore = Math.Clamp(100 - securityPenalty, 0, 100);
            var efficiencyScore = Math.Clamp(100 - efficiencyPenalty, 0, 100);
            var readabilityScore = Math.Clamp(100 - readabilityPenalty, 0, 100);
            var accuracyPenalty = 0;
            if (mandatoryGuardViolation) accuracyPenalty += 30;
            if (hasHardBlock || blockedByConfig) accuracyPenalty += 35;
            if (capValidation.UnauthorizedCapabilities.Count > 0) accuracyPenalty += 25;
            if (!hasTryCatch) accuracyPenalty += 15;
            var accuracyScore = Math.Clamp(100 - accuracyPenalty, 0, 100);

            var minimalityPenalty = 0;
            if (nonEmptyLines > 320) minimalityPenalty += 28;
            else if (nonEmptyLines > 220) minimalityPenalty += 14;
            minimalityPenalty += Math.Min(20, heavyRecursiveCount * 6);
            if (flags.Contains("SCRIPT_TOO_LONG", StringComparer.OrdinalIgnoreCase)) minimalityPenalty += 8;
            var minimalityScore = Math.Clamp(100 - minimalityPenalty, 0, 100);

            var reversibilityPenalty = 0;
            if (mutatingCommands > 0) reversibilityPenalty += 20;
            if (mutatingCommands > 0 && !hasRollbackHeader) reversibilityPenalty += 35;
            if (mutatingCommands > 0 && !hasWhatIf) reversibilityPenalty += 15;
            var reversibilityScore = Math.Clamp(100 - reversibilityPenalty, 0, 100);

            // Rubric: A-Security(40) + B-Accuracy(25) + C-Minimality(15) + D-Reversibility(10) + E-Quality(10)
            var qualityScore = (int)Math.Round((efficiencyScore + readabilityScore) / 2.0);
            var compositeScore = (int)Math.Round(
                (securityScore * 0.40)
                + (accuracyScore * 0.25)
                + (minimalityScore * 0.15)
                + (reversibilityScore * 0.10)
                + (qualityScore * 0.10));
            var overallScore = compositeScore;

            // REFUSE only for hard blocks (dangerous patterns), config-blocked commands,
            // or unauthorized capabilities. mandatoryGuardViolation alone = A_REVOIR.
            // Rubric thresholds: APPROUVE global>=80 & security>=80; WARN global 65-79 or security 70-79;
            // REFUSE hard block, global<65, or security<70.
            var verdict = SecurityVerdict.APPROUVE;
            if (hasHardBlock || blockedByConfig || capValidation.UnauthorizedCapabilities.Count > 0)
            {
                verdict = SecurityVerdict.REFUSE;
            }
            else if (compositeScore >= 80 && securityScore >= 80 && !mandatoryGuardViolation && flags.Count == 0)
            {
                verdict = SecurityVerdict.APPROUVE;
            }
            else
            {
                verdict = SecurityVerdict.A_REVOIR;
            }

            result.SecurityScore0_100 = securityScore;
            result.AccuracyScore0_100 = accuracyScore;
            result.MinimalityScore0_100 = minimalityScore;
            result.ReversibilityScore0_100 = reversibilityScore;
            result.EfficiencyScore0_100 = efficiencyScore;
            result.ReadabilityScore0_100 = readabilityScore;
            result.RelevanceScore0_100 = accuracyScore;
            result.RobustnessScore0_100 = (int)Math.Round((minimalityScore + reversibilityScore) / 2.0);
            result.UxScore0_100 = readabilityScore;
            result.ScriptQualityComposite0_100 = compositeScore;
            result.OverallScore0_100 = overallScore;
            result.Verdict = verdict;
            result.Flags = flags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.HardBlockFlags = hardBlockFlags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.MandatoryViolationFlags = mandatoryViolationFlags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Reasons = reasons;
            result.StaticTests = staticTests;
            result.Violations = violations;
            result.ContainsBlockedCommand = blockedByConfig || hasHardBlock;
            result.HasMandatoryGuardViolations = mandatoryGuardViolation;
            result.HasExplicitCapabilities = capValidation.HasExplicitDeclaration;
            result.DeclaredCapabilities = capValidation.DeclaredCapabilities;
            result.UnauthorizedCapabilities = capValidation.UnauthorizedCapabilities;
            return result;
        }

        public bool RequiresSimulation(string scriptText)
        {
            if (string.IsNullOrWhiteSpace(scriptText))
                return false;

            var isMutating = MutatingCommandRegex.IsMatch(scriptText);
            var hasWhatIf = WhatIfRegex.IsMatch(scriptText);
            return isMutating && !hasWhatIf;
        }

        /// <summary>
        /// PASS gate for AutoFix execution:
        /// - Global score >= 75
        /// - Security score >= 70 (equivalent to 28/40)
        /// - No critical violation
        /// </summary>
        private const int AutoFixMinCompositeScore = 75;
        private const int AutoFixMinSecurityScore = 70;

        public AutoFixEligibilityResult EvaluateForAutoFix(string scriptText, JudgeResult? precomputedJudge = null)
        {
            var judge = precomputedJudge ?? Analyse(scriptText);
            var gate = new AutoFixEligibilityResult
            {
                Judge = judge,
                DeclaredCapabilities = judge.DeclaredCapabilities.ToList()
            };

            var blocking = gate.BlockingReasons;
            var warnings = gate.WarningReasons;

            // Missing script — hard block, distinct category.
            var scriptLen = scriptText?.Trim().Length ?? 0;
            if (scriptLen < 200)
            {
                blocking.Add(scriptLen == 0
                    ? "No script was generated by the pipeline."
                    : $"Script too short ({scriptLen} chars): ScriptBuilder did not generate a valid PowerShell script.");
                gate.BlockedBy = "MissingScript";
                judge.BlockedByCategory = "MissingScript";
            }
            // REFUSE verdict = hard block (dangerous pattern, blocked command, or unauthorized capability).
            else if (judge.Verdict == SecurityVerdict.REFUSE)
            {
                if (judge.ContainsBlockedCommand)
                    blocking.Add($"Hard block — dangerous pattern(s): {string.Join(", ", judge.HardBlockFlags.Take(3))}.");
                else if (judge.UnauthorizedCapabilities.Count > 0)
                    blocking.Add($"Unauthorized capabilities declared: {string.Join(", ", judge.UnauthorizedCapabilities.Take(3))}.");
                else
                    blocking.Add("Safety verdict is REFUSE — script contains blocked patterns.");

                gate.BlockedBy = "HardBlock";
                judge.BlockedByCategory = "HardBlock";
            }
            // A_REVOIR or APPROUVE — apply score gates.
            else
            {
                if (judge.HasCriticalViolation)
                {
                    var criticalSummaries = judge.Violations
                        .Where(v => string.Equals(v.Severity, "Critical", StringComparison.OrdinalIgnoreCase))
                        .Take(3)
                        .Select(v => $"{v.Code} ({v.EvidenceLine}) => fix: {v.Fix}")
                        .ToList();
                    blocking.Add($"Critical violation(s): {string.Join("; ", criticalSummaries)}.");
                    gate.BlockedBy = "HardBlock";
                    judge.BlockedByCategory = "HardBlock";
                }
                // Gate 1: composite score must be >= 75
                else if (judge.ScriptQualityComposite0_100 < AutoFixMinCompositeScore)
                {
                    var lowestName = LowestSubScoreName(judge);
                    blocking.Add(
                        $"Score global {judge.ScriptQualityComposite0_100}/100 insuffisant (minimum {AutoFixMinCompositeScore}). " +
                        $"Sous-score le plus faible: {lowestName}. " +
                        "Pour passer: ajouter try/catch, en-tetes requis, et rendre le script idempotent.");
                    gate.BlockedBy = "ScoreGate";
                    gate.FailedSubScore = lowestName;
                    judge.BlockedByCategory = "ScoreGate";
                }
                // Gate 2: security score must be >= 70 (= 28/40 rubric points)
                else if (judge.SecurityScore0_100 < AutoFixMinSecurityScore)
                {
                    blocking.Add(
                        $"Score securite {judge.SecurityScore0_100}/100 insuffisant (minimum {AutoFixMinSecurityScore}). " +
                        "Pour passer: supprimer les patterns a risque, ajouter le header CAPABILITIES, " +
                        "et s'assurer qu'il n'y a pas de commandes reseau non necessaires.");
                    gate.BlockedBy = "ScoreGate";
                    gate.FailedSubScore = $"Security={judge.SecurityScore0_100}/100";
                    judge.BlockedByCategory = "ScoreGate";
                }
                else
                {
                    // Score is sufficient — approved (with potential warnings).
                    gate.BlockedBy = "None";
                    judge.BlockedByCategory = "None";
                }

                // Mandatory violations are surfaced as warnings (non-blocking when score is OK).
                if (judge.HasMandatoryGuardViolations)
                {
                    warnings.Add(
                        $"Avertissements de fiabilite: {string.Join(", ", judge.MandatoryViolationFlags.Take(3))}. " +
                        "Verifiez avant d'executer.");
                }

                // Low score warning (approved but not perfect)
                if (gate.BlockedBy == "None" && judge.ScriptQualityComposite0_100 < 80)
                {
                    warnings.Add(
                        $"Score global {judge.ScriptQualityComposite0_100}/100 (correct mais perfectible). " +
                        "Verifiez les sous-scores avant execution.");
                }
            }

            // Merge for legacy Reasons property.
            gate.Reasons = blocking.Concat(warnings).ToList();

            gate.IsApproved = blocking.Count == 0;
            judge.AutoFixApproved = gate.IsApproved;
            judge.AutoFixGateReasons = gate.Reasons.ToList();
            return gate;
        }

        private static string LowestSubScoreName(JudgeResult j)
        {
            var scores = new[]
            {
                ("Security", j.SecurityScore0_100),
                ("Accuracy", j.AccuracyScore0_100),
                ("Minimality", j.MinimalityScore0_100),
                ("Reversibility", j.ReversibilityScore0_100),
                ("Efficiency", j.EfficiencyScore0_100),
                ("Readability", j.ReadabilityScore0_100),
            };
            var min = scores.OrderBy(s => s.Item2).First();
            return $"{min.Item1}={min.Item2}/100";
        }
    }
}
