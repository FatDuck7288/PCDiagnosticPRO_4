using System;
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
    /// Agent 2: Reviews and improves the script draft from Agent 1.
    /// </summary>
    public class CodeReviewerAgent
    {
        private readonly ILlmClient _llm;

        public CodeReviewerAgent(ILlmClient llm)
        {
            _llm = llm;
        }

        public async Task<ReviewResult> RunAsync(
            ScriptDraft draft,
            ContextPack context,
            CancellationToken ct = default,
            AutoFixTraceWriter? traceWriter = null)
        {
            var template = PromptLoader.AgentReviewer()
                .Replace("{SCRIPT_DRAFT}", draft.ScriptText)
                .Replace("{CONTEXT_PACK}", context.ToPromptText())
                .Replace("{SAFETY_POLICY}", PromptLoader.SafetyPolicy());

            var system = PromptLoader.AgentSystemBase();
            var raw = await _llm.GenerateAsync(system, template, ct).ConfigureAwait(false);
            traceWriter?.WriteArtifact("agent2_raw.txt", raw);
            traceWriter?.WriteStageSnapshot("agent2.raw_output", raw);
            var cleaned = LlmOutputSanitizer.TrimAtFirstControlPattern(raw, out var trigger);
            if (!string.IsNullOrWhiteSpace(trigger))
            {
                App.LogMessage($"[AI] CodeReviewerAgent: output trimmed at control token '{trigger}'.");
            }

            var review = ParseReview(cleaned);
            traceWriter?.WriteStageChars("agent2.review_script", review.RevisedScriptText?.Length ?? 0);
            return review;
        }

        private static ReviewResult ParseReview(string raw)
        {
            var result = new ReviewResult();

            // Extract revised script
            var psMatch = Regex.Match(raw, @"```(?:powershell|ps1)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            result.RevisedScriptText = psMatch.Success ? psMatch.Groups[1].Value.Trim() : raw.Trim();

            // Extract JSON metadata
            var jsonMatch = Regex.Match(raw, @"```json\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (jsonMatch.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonMatch.Groups[1].Value);
                    var root = doc.RootElement;
                    result.Fixes = ParseStringArray(root, "fixes");
                    result.Notes = ParseStringArray(root, "notes");
                    result.Checklist = ParseStringArray(root, "checklist");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[AI] CodeReviewerAgent: JSON parse error: {ex.Message}");
                }
            }

            return result;
        }

        private static System.Collections.Generic.List<string> ParseStringArray(JsonElement root, string prop)
        {
            var result = new System.Collections.Generic.List<string>();
            if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String)
                        result.Add(el.GetString()!);
            return result;
        }
    }
}
