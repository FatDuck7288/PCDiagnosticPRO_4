using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.AI.Interfaces;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI.Agents
{
    /// <summary>
    /// Agent 4: Static security analysis and final verdict.
    /// Performs deterministic judge first, then optional LLM judge with strict schema parsing.
    /// </summary>
    public class TesterJudgeAgent
    {
        private readonly ILlmClient _llm;
        private readonly SafetyPolicyEngine _safetyEngine;

        public TesterJudgeAgent(ILlmClient llm, SafetyPolicyEngine safetyEngine)
        {
            _llm = llm;
            _safetyEngine = safetyEngine;
        }

        public async Task<JudgeResult> RunAsync(string finalScript, ContextPack context, CancellationToken ct = default)
        {
            var deterministicResult = _safetyEngine.Analyse(finalScript);

            // Empty script should fail fast with deterministic reason.
            if (string.IsNullOrWhiteSpace(finalScript))
            {
                deterministicResult.BlockedByCategory = "MissingScript";
                deterministicResult.JudgeError = true;
                deterministicResult.JudgeErrorMessage = "Judge skipped LLM because script is empty.";
                if (!deterministicResult.Reasons.Contains("JudgeError: script is empty before LLM evaluation.", StringComparer.OrdinalIgnoreCase))
                {
                    deterministicResult.Reasons.Add("JudgeError: script is empty before LLM evaluation.");
                }
                return deterministicResult;
            }

            var template = PromptLoader.AgentTesterJudge()
                .Replace("{FINAL_SCRIPT}", finalScript)
                .Replace("{CONTEXT_PACK}", context.ToPromptText())
                .Replace("{SAFETY_POLICY}", PromptLoader.SafetyPolicy());

            var system = PromptLoader.AgentSystemBase();
            var (llmResult, errorMessage, retried) = await TryRunLlmJudgeAsync(system, template, ct).ConfigureAwait(false);

            if (llmResult == null)
            {
                deterministicResult.JudgeError = true;
                deterministicResult.JudgeErrorMessage = errorMessage ?? "Unknown judge parsing error";
                deterministicResult.JudgeRetried = retried;
                deterministicResult.Reasons.Add($"JudgeError: {deterministicResult.JudgeErrorMessage}");
                deterministicResult.Flags.Add("JUDGE_ERROR");
                return deterministicResult;
            }

            llmResult.JudgeRetried = retried;
            return MergeResults(deterministicResult, llmResult);
        }

        private async Task<(JudgeResult? Result, string? Error, bool Retried)> TryRunLlmJudgeAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken ct)
        {
            string? lastError = null;
            var retried = false;

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var raw = await _llm.GenerateAsync(systemPrompt, userPrompt, ct).ConfigureAwait(false);
                    var cleaned = LlmOutputSanitizer.TrimAtFirstControlPattern(raw, out var trigger);
                    if (!string.IsNullOrWhiteSpace(trigger))
                    {
                        App.LogMessage($"[AI] TesterJudgeAgent: output trimmed at control token '{trigger}'.");
                    }

                    if (TryParseJudgeResult(cleaned, out var parsed, out var parseError))
                    {
                        return (parsed, null, retried);
                    }

                    lastError = parseError ?? "Unknown parse error";
                    App.LogMessage($"[AI] TesterJudgeAgent: parse failed on attempt {attempt}: {lastError}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    App.LogMessage($"[AI] TesterJudgeAgent: LLM error on attempt {attempt}: {ex.Message}");
                }

                if (attempt == 1)
                {
                    retried = true;
                }
            }

            return (null, lastError, retried);
        }

        private static JudgeResult MergeResults(JudgeResult deterministic, JudgeResult llm)
        {
            var merged = new JudgeResult
            {
                SecurityScore0_100 = Math.Min(NormalizeScore(deterministic.SecurityScore0_100), NormalizeScore(llm.SecurityScore0_100)),
                RelevanceScore0_100 = Math.Min(
                    NormalizeScore(deterministic.RelevanceScore0_100 > 0 ? deterministic.RelevanceScore0_100 : deterministic.AccuracyScore0_100),
                    NormalizeScore(llm.RelevanceScore0_100 > 0 ? llm.RelevanceScore0_100 : llm.AccuracyScore0_100)),
                RobustnessScore0_100 = Math.Min(
                    NormalizeScore(deterministic.RobustnessScore0_100 > 0
                        ? deterministic.RobustnessScore0_100
                        : (int)Math.Round((NormalizeScore(deterministic.MinimalityScore0_100) + NormalizeScore(deterministic.ReversibilityScore0_100)) / 2.0)),
                    NormalizeScore(llm.RobustnessScore0_100 > 0 ? llm.RobustnessScore0_100 : llm.MinimalityScore0_100)),
                UxScore0_100 = Math.Min(
                    NormalizeScore(deterministic.UxScore0_100 > 0 ? deterministic.UxScore0_100 : deterministic.ReadabilityScore0_100),
                    NormalizeScore(llm.UxScore0_100 > 0 ? llm.UxScore0_100 : llm.ReadabilityScore0_100))
            };

            merged.AccuracyScore0_100 = merged.RelevanceScore0_100;
            merged.MinimalityScore0_100 = merged.RobustnessScore0_100;
            merged.ReversibilityScore0_100 = merged.RobustnessScore0_100;
            merged.EfficiencyScore0_100 = Math.Min(NormalizeScore(deterministic.EfficiencyScore0_100), NormalizeScore(llm.EfficiencyScore0_100));
            merged.ReadabilityScore0_100 = merged.UxScore0_100;

            merged.OverallScore0_100 = (int)Math.Round(
                (merged.SecurityScore0_100 * 0.40)
                + (merged.RelevanceScore0_100 * 0.30)
                + (merged.RobustnessScore0_100 * 0.20)
                + (merged.UxScore0_100 * 0.10));
            merged.ScriptQualityComposite0_100 = merged.OverallScore0_100;

            merged.Violations = MergeViolations(
                BuildDeterministicViolations(deterministic)
                    .Concat(deterministic.Violations)
                    .Concat(llm.Violations));

            merged.Flags = deterministic.Flags
                .Concat(llm.Flags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged.Reasons = deterministic.Reasons
                .Concat(llm.Reasons)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged.StaticTests = deterministic.StaticTests
                .Concat(llm.StaticTests)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged.ContainsBlockedCommand = deterministic.ContainsBlockedCommand || llm.ContainsBlockedCommand;
            merged.HasMandatoryGuardViolations = deterministic.HasMandatoryGuardViolations || llm.HasMandatoryGuardViolations;
            merged.HasExplicitCapabilities = deterministic.HasExplicitCapabilities || llm.HasExplicitCapabilities;
            merged.DeclaredCapabilities = deterministic.DeclaredCapabilities
                .Concat(llm.DeclaredCapabilities)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged.UnauthorizedCapabilities = deterministic.UnauthorizedCapabilities
                .Concat(llm.UnauthorizedCapabilities)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged.HardBlockFlags = deterministic.HardBlockFlags
                .Concat(llm.HardBlockFlags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged.MandatoryViolationFlags = deterministic.MandatoryViolationFlags
                .Concat(llm.MandatoryViolationFlags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged.Rationale = string.IsNullOrWhiteSpace(llm.Rationale) ? deterministic.Rationale : llm.Rationale;
            merged.SuggestedPatch = llm.SuggestedPatch;
            merged.JudgeError = deterministic.JudgeError || llm.JudgeError;
            merged.JudgeErrorMessage = string.IsNullOrWhiteSpace(llm.JudgeErrorMessage)
                ? deterministic.JudgeErrorMessage
                : llm.JudgeErrorMessage;
            merged.JudgeRetried = llm.JudgeRetried || deterministic.JudgeRetried;

            var pass = merged.OverallScore0_100 >= 75
                && merged.SecurityScore0_100 >= 70
                && !merged.HasCriticalViolation
                && !merged.ContainsBlockedCommand;
            merged.Verdict = pass ? SecurityVerdict.APPROUVE : SecurityVerdict.REFUSE;

            if (!pass)
            {
                if (merged.SecurityScore0_100 < 70)
                {
                    merged.Reasons.Add($"Security below threshold: {merged.SecurityScore0_100}/100 (<70).");
                }
                if (merged.OverallScore0_100 < 75)
                {
                    merged.Reasons.Add($"Global score below threshold: {merged.OverallScore0_100}/100 (<75).");
                }
                if (merged.HasCriticalViolation)
                {
                    merged.Reasons.Add("Critical violation detected.");
                }
            }

            return merged;
        }

        private static List<JudgeViolation> BuildDeterministicViolations(JudgeResult deterministic)
        {
            var list = new List<JudgeViolation>();

            foreach (var flag in deterministic.HardBlockFlags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new JudgeViolation
                {
                    Code = flag,
                    Severity = "Critical",
                    EvidenceLine = flag,
                    Fix = "Remove this dangerous command/pattern."
                });
            }

            foreach (var flag in deterministic.MandatoryViolationFlags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new JudgeViolation
                {
                    Code = flag,
                    Severity = "High",
                    EvidenceLine = flag,
                    Fix = "Apply the mandatory reliability guard for this rule."
                });
            }

            return list;
        }

        private static List<JudgeViolation> MergeViolations(IEnumerable<JudgeViolation> violations)
        {
            var merged = new List<JudgeViolation>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var violation in violations)
            {
                var code = violation.Code?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                var evidence = violation.EvidenceLine?.Trim() ?? string.Empty;
                var key = $"{code}|{evidence}";
                if (!keys.Add(key))
                {
                    continue;
                }

                merged.Add(new JudgeViolation
                {
                    Code = code,
                    Severity = string.IsNullOrWhiteSpace(violation.Severity) ? "High" : violation.Severity.Trim(),
                    EvidenceLine = evidence,
                    Fix = violation.Fix?.Trim() ?? string.Empty
                });
            }

            return merged;
        }

        private static bool TryParseJudgeResult(string raw, out JudgeResult result, out string? error)
        {
            result = new JudgeResult();
            error = null;

            var json = ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Judge schema parse failed: empty JSON payload.";
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("verdict", out var verdictEl) || verdictEl.ValueKind != JsonValueKind.String)
                {
                    error = "Judge schema parse failed: missing verdict.";
                    return false;
                }

                var verdictRaw = verdictEl.GetString()?.Trim() ?? string.Empty;
                if (!string.Equals(verdictRaw, "PASS", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(verdictRaw, "REFUSE", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Judge schema parse failed: invalid verdict '{verdictRaw}'.";
                    return false;
                }

                if (!root.TryGetProperty("scores", out var scores) || scores.ValueKind != JsonValueKind.Object)
                {
                    error = "Judge schema parse failed: missing scores object.";
                    return false;
                }

                if (!TryGetScore(scores, "security", out var security)
                    || !TryGetScore(scores, "relevance", out var relevance)
                    || !TryGetScore(scores, "robustness", out var robustness)
                    || !TryGetScore(scores, "ux", out var ux))
                {
                    error = "Judge schema parse failed: missing one or more score fields.";
                    return false;
                }

                var global = TryGetScore(scores, "global", out var globalParsed)
                    ? globalParsed
                    : (int)Math.Round((security * 0.40) + (relevance * 0.30) + (robustness * 0.20) + (ux * 0.10));

                var violations = new List<JudgeViolation>();
                if (root.TryGetProperty("violations", out var violationsEl) && violationsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in violationsEl.EnumerateArray())
                    {
                        if (v.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var code = TryGetString(v, "code");
                        if (string.IsNullOrWhiteSpace(code))
                        {
                            continue;
                        }

                        violations.Add(new JudgeViolation
                        {
                            Code = code!,
                            Severity = TryGetString(v, "severity") ?? "High",
                            EvidenceLine = TryGetString(v, "evidenceLine") ?? string.Empty,
                            Fix = TryGetString(v, "fix") ?? string.Empty
                        });
                    }
                }

                var rationale = TryGetString(root, "rationale") ?? string.Empty;
                var suggestedPatch = TryGetString(root, "suggestedPatch") ?? string.Empty;

                result.SecurityScore0_100 = security;
                result.RelevanceScore0_100 = relevance;
                result.RobustnessScore0_100 = robustness;
                result.UxScore0_100 = ux;
                result.AccuracyScore0_100 = relevance;
                result.MinimalityScore0_100 = robustness;
                result.ReversibilityScore0_100 = robustness;
                result.EfficiencyScore0_100 = ux;
                result.ReadabilityScore0_100 = ux;
                result.OverallScore0_100 = global;
                result.ScriptQualityComposite0_100 = global;
                result.Verdict = string.Equals(verdictRaw, "PASS", StringComparison.OrdinalIgnoreCase)
                    ? SecurityVerdict.APPROUVE
                    : SecurityVerdict.REFUSE;
                result.Violations = violations;
                result.Reasons = violations.Select(v =>
                        $"{v.Code} [{v.Severity}] evidence='{v.EvidenceLine}' fix='{v.Fix}'")
                    .ToList();
                result.Rationale = rationale;
                result.SuggestedPatch = suggestedPatch;
                result.Flags = violations.Select(v => $"{v.Severity}:{v.Code}").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                result.ContainsBlockedCommand = violations.Any(v =>
                    string.Equals(v.Severity, "Critical", StringComparison.OrdinalIgnoreCase));
                result.HasMandatoryGuardViolations = violations.Any(v =>
                    string.Equals(v.Severity, "High", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v.Severity, "Medium", StringComparison.OrdinalIgnoreCase));

                return true;
            }
            catch (Exception ex)
            {
                error = $"Judge schema parse failed: {ex.Message}";
                return false;
            }
        }

        private static int NormalizeScore(int score)
        {
            if (score <= 0)
            {
                return 0;
            }

            return Math.Clamp(score, 0, 100);
        }

        private static bool TryGetScore(JsonElement parent, string propertyName, out int score)
        {
            score = 0;
            if (!parent.TryGetProperty(propertyName, out var el))
            {
                return false;
            }

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var numeric))
            {
                score = Math.Clamp(numeric, 0, 100);
                return true;
            }

            if (el.ValueKind == JsonValueKind.String
                && int.TryParse(el.GetString(), out var parsed))
            {
                score = Math.Clamp(parsed, 0, 100);
                return true;
            }

            return false;
        }

        private static string? TryGetString(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var el))
            {
                return null;
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }

            return null;
        }

        private static string ExtractJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var fenced = Regex.Match(raw, @"```json\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (fenced.Success)
            {
                return fenced.Groups[1].Value.Trim();
            }

            var objectMatch = Regex.Match(raw, @"\{[\s\S]*\}");
            return objectMatch.Success ? objectMatch.Value.Trim() : string.Empty;
        }
    }
}
