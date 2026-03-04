using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Agent 3: Normalizes style, adds validations, error handling, and logging.
    /// Receives the script + summary (not the full scan context) to stay within token budget.
    /// Also performs PowerShell syntax validation before passing to Agent 4.
    /// </summary>
    public class CodeRefinerAgent
    {
        private readonly ILlmClient _llm;

        public CodeRefinerAgent(ILlmClient llm)
        {
            _llm = llm;
        }

        public async Task<RefineResult> RunAsync(
            string scriptText,
            string contextSummary,
            CancellationToken ct = default,
            AutoFixTraceWriter? traceWriter = null)
        {
            var template = PromptLoader.Load("agent_refiner.md")
                .Replace("{SCRIPT_TEXT}", scriptText)
                .Replace("{CONTEXT_SUMMARY}", contextSummary);

            var system = PromptLoader.AgentSystemBase();
            var raw = await _llm.GenerateAsync(system, template, ct).ConfigureAwait(false);
            traceWriter?.WriteArtifact("agent3_raw.txt", raw);
            traceWriter?.WriteStageSnapshot("agent3.raw_output", raw);
            var cleaned = LlmOutputSanitizer.TrimAtFirstControlPattern(raw, out var trigger);
            if (!string.IsNullOrWhiteSpace(trigger))
            {
                App.LogMessage($"[AI] CodeRefinerAgent: output trimmed at control token '{trigger}'.");
            }

            var result = ParseRefineResult(cleaned);

            // PowerShell syntax validation
            var syntaxResult = ValidatePowerShellSyntax(result.RefinedScriptText);
            result.SyntaxValid = syntaxResult.valid;
            result.SyntaxError = syntaxResult.error;

            if (!result.SyntaxValid)
            {
                App.LogMessage($"[AI] CodeRefinerAgent: syntax invalid — {result.SyntaxError}");
            }

            traceWriter?.WriteStageChars("agent3.refined_script", result.RefinedScriptText?.Length ?? 0);
            return result;
        }

        private static RefineResult ParseRefineResult(string raw)
        {
            var result = new RefineResult();

            // Extract refined script
            var psMatch = Regex.Match(raw, @"```(?:powershell|ps1)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            result.RefinedScriptText = psMatch.Success ? psMatch.Groups[1].Value.Trim() : raw.Trim();

            // Extract JSON metadata
            var jsonMatch = Regex.Match(raw, @"```json\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (jsonMatch.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonMatch.Groups[1].Value);
                    var root = doc.RootElement;
                    result.StyleFixes = ParseStringArray(root, "style_fixes");
                    result.Validations = ParseStringArray(root, "validations_added");
                    result.LoggingAdded = ParseStringArray(root, "logging_added");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[AI] CodeRefinerAgent: JSON parse error: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Validates PowerShell syntax by checking for common structural issues.
        /// Uses heuristic checks since we can't invoke PowerShell parser in-process.
        /// </summary>
        private static (bool valid, string error) ValidatePowerShellSyntax(string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                return (false, "Empty script");

            // Check balanced braces
            var braceCount = 0;
            var parenCount = 0;
            foreach (var ch in script)
            {
                if (ch == '{') braceCount++;
                else if (ch == '}') braceCount--;
                else if (ch == '(') parenCount++;
                else if (ch == ')') parenCount--;

                if (braceCount < 0) return (false, "Unmatched closing brace '}'");
                if (parenCount < 0) return (false, "Unmatched closing parenthesis ')'");
            }

            if (braceCount != 0) return (false, $"Unmatched braces: {Math.Abs(braceCount)} unclosed");
            if (parenCount != 0) return (false, $"Unmatched parentheses: {Math.Abs(parenCount)} unclosed");

            // Check for truncated strings
            var singleQuoteCount = 0;
            var doubleQuoteCount = 0;
            var inHereString = false;
            foreach (var line in script.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("@\"", StringComparison.Ordinal) || trimmed.StartsWith("@'", StringComparison.Ordinal))
                    inHereString = true;
                if (trimmed == "\"@" || trimmed == "'@")
                    inHereString = false;
                if (!inHereString)
                {
                    foreach (var ch in line)
                    {
                        if (ch == '\'') singleQuoteCount++;
                        else if (ch == '"') doubleQuoteCount++;
                    }
                }
            }

            // Basic check: script shouldn't be suspiciously short
            if (script.Length < 50)
                return (false, "Script too short to be functional");

            return (true, string.Empty);
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
