using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.AI;
using PCDiagnosticPro.AI.Interfaces;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI.Agents
{
    /// <summary>
    /// Agent 1: Generates an initial PowerShell script draft from a user goal + context pack.
    /// </summary>
    public class ScriptBuilderAgent
    {
        private readonly ILlmClient _llm;

        public ScriptBuilderAgent(ILlmClient llm)
        {
            _llm = llm;
        }

        public async Task<ScriptDraft> RunAsync(
            string userGoal,
            ContextPack context,
            CancellationToken ct = default,
            AutoFixTraceWriter? traceWriter = null)
        {
            var system = PromptLoader.AgentSystemBase();
            var promptLog = new StringBuilder();
            var rawLog = new StringBuilder();
            ScriptDraft? lastDraft = null;

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var forceFencedOutput = attempt > 1;
                var template = BuildTemplate(userGoal, context, forceFencedOutput);
                promptLog.AppendLine($"=== attempt {attempt} (forceFence={forceFencedOutput}) ===");
                promptLog.AppendLine(template);
                promptLog.AppendLine();
                traceWriter?.WriteArtifact("agent1_prompt.txt", promptLog.ToString());

                var raw = await _llm.GenerateAsync(system, template, ct).ConfigureAwait(false);
                rawLog.AppendLine($"=== attempt {attempt} ===");
                rawLog.AppendLine(raw ?? string.Empty);
                rawLog.AppendLine();
                traceWriter?.WriteArtifact("agent1_raw.txt", rawLog.ToString());
                traceWriter?.WriteStageSnapshot("agent1.raw_output", raw);

                var cleaned = LlmOutputSanitizer.TrimAtFirstControlPattern(raw ?? string.Empty, out var trigger);
                if (!string.IsNullOrWhiteSpace(trigger))
                {
                    App.LogMessage($"[AI] ScriptBuilderAgent: output trimmed at control token '{trigger}'.");
                }

                var draft = ParseDraft(cleaned);
                lastDraft = draft;
                traceWriter?.WriteStageChars("agent1.extracted_script", draft.ScriptText?.Length ?? 0);

                if (!string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(draft.ScriptText))
                {
                    return draft;
                }

                if (attempt == 1)
                {
                    App.LogMessage("[AI] ScriptBuilderAgent: empty/invalid script extraction, retrying with forced fenced output.");
                    traceWriter?.WriteStage("agent1.retry", new
                    {
                        reason = "raw_or_extracted_empty",
                        attempt = 2
                    });
                }
            }

            throw new InvalidOperationException(
                $"MissingScript: ScriptBuilderAgent failed to produce a PowerShell script. " +
                $"LastExtractedChars={lastDraft?.ScriptText?.Length ?? 0}");
        }

        /// <summary>Public static accessor so external callers (e.g. ChatSupportViewModel) can reuse the parser.</summary>
        public static ScriptDraft ParseDraftStatic(string raw) => ParseDraft(raw);

        private static ScriptDraft ParseDraft(string raw)
        {
            var draft = new ScriptDraft();

            // Extract PowerShell script block (between ```powershell...``` or just the PS code)
            var psMatch = Regex.Match(raw, @"```(?:powershell|ps1)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            draft.ScriptText = psMatch.Success
                ? psMatch.Groups[1].Value.Trim()
                : ExtractScriptFallback(raw);

            // Extract JSON metadata block
            var jsonMatch = Regex.Match(raw, @"```json\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (jsonMatch.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonMatch.Groups[1].Value);
                    var root = doc.RootElement;
                    draft.Assumptions = ParseStringArray(root, "assumptions");
                    draft.Risks = ParseStringArray(root, "risks");
                    draft.Rollback = ParseStringArray(root, "rollback");
                    draft.RequiresAdmin = root.TryGetProperty("requiresAdmin", out var ra) && ra.GetBoolean();
                    draft.Capabilities = ParseStringArray(root, "capabilities");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[AI] ScriptBuilderAgent: JSON parse error: {ex.Message}");
                }
            }

            return draft;
        }

        private static string BuildTemplate(string userGoal, ContextPack context, bool forceFencedOutput)
        {
            var template = PromptLoader.AgentScriptBuilder()
                .Replace("{USER_GOAL}", userGoal)
                .Replace("{CONTEXT_PACK}", context.ToPromptText());

            if (!forceFencedOutput)
            {
                return template;
            }

            return template
                + Environment.NewLine
                + Environment.NewLine
                + "RETRY RULE:"
                + Environment.NewLine
                + "- Your response MUST start with ```powershell and end with ```."
                + Environment.NewLine
                + "- Do not emit prose before the first code fence.";
        }

        private static string ExtractScriptFallback(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            var scriptLines = new List<string>(lines.Length);
            var started = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var looksLikePowerShell = LooksLikePowerShellLine(trimmed);
                if (!started && looksLikePowerShell)
                {
                    started = true;
                }

                if (!started)
                {
                    continue;
                }

                if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.Contains(':'))
                {
                    break;
                }

                if (trimmed.StartsWith("\"", StringComparison.Ordinal))
                {
                    break;
                }

                if (looksLikePowerShell || string.IsNullOrWhiteSpace(trimmed) || trimmed is "{" or "}" or ");")
                {
                    scriptLines.Add(line);
                }
            }

            return string.Join(Environment.NewLine, scriptLines).Trim();
        }

        private static bool LooksLikePowerShellLine(string trimmed)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return false;
            }

            return trimmed.StartsWith("#", StringComparison.Ordinal)
                   || trimmed.StartsWith("$", StringComparison.Ordinal)
                   || Regex.IsMatch(trimmed, @"^(Set|Get|Test|Write|New|Remove|Invoke|Start|Stop|Restart|Enable|Disable|Import|Export|Clear|Add|Out|Select|Where|ForEach)-", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(trimmed, @"^(if|elseif|else|foreach|for|while|switch|try|catch|finally|param)\b", RegexOptions.IgnoreCase)
                   || trimmed.StartsWith("[CmdletBinding", StringComparison.OrdinalIgnoreCase)
                   || trimmed.StartsWith("function ", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ParseStringArray(JsonElement root, string prop)
        {
            var result = new List<string>();
            if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String)
                        result.Add(el.GetString()!);
            return result;
        }
    }
}
