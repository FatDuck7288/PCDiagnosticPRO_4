using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.AI.Agents;
using PCDiagnosticPro.AI.Interfaces;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI
{
    /// <summary>
    /// Orchestrates the 4-agent pipeline and emits an AiRunReport.
    /// Agent 1: ScriptBuilder, Agent 2: CodeReviewer (corrector),
    /// Agent 3: CodeRefiner (style + validation), Agent 4: SecurityJudge (verdict).
    /// </summary>
    public class AiOrchestrator
    {
        private readonly ILlmClient _llm;
        private readonly SafetyPolicyEngine _safety;
        private readonly AiSettings _settings;
        private ScriptBuilderAgent? _scriptBuilderAgent;
        private CodeReviewerAgent? _codeReviewerAgent;
        private CodeRefinerAgent? _codeRefinerAgent;
        private TesterJudgeAgent? _testerJudgeAgent;

        // Perf: reuse serialization options rather than allocating on every report save.
        private static readonly JsonSerializerOptions _indentedOptions = new() { WriteIndented = true };

        public event Action<AgentStepLog>? StepStarted;
        public event Action<AgentStepLog>? StepCompleted;
        public event Action<AgentStepLog>? StepFailed;

        public AiOrchestrator(ILlmClient llm, SafetyPolicyEngine safety, AiSettings settings)
        {
            _llm = llm;
            _safety = safety;
            _settings = settings;
        }

        /// <summary>Per-agent timeout (seconds). Sourced from AiSettings.TimeoutSeconds.</summary>
        private int AgentTimeoutSeconds => Math.Max(30, _settings.TimeoutSeconds);

        public async Task<AiRunReport> RunPipelineAsync(
            string userGoal,
            ContextPack context,
            string runId = "",
            RunAnalysisHeader? runHeader = null,
            CancellationToken ct = default)
        {
            var contextText = context.ToPromptText();
            App.LogMessage(
                $"[AI][Pipeline] START runId={runId} | goal={userGoal[..Math.Min(80, userGoal.Length)]} | " +
                $"contextChars={contextText.Length} | contextTokens≈{contextText.Length / 4} | " +
                $"sources=[{string.Join(", ", context.SourcesUsed)}] | " +
                $"findings={context.KeyFindings.Count} | tables={context.TablesCompact.Count} | " +
                $"agentTimeoutSeconds={AgentTimeoutSeconds}");

            var report = new AiRunReport
            {
                RunId = string.IsNullOrWhiteSpace(runId) ? context.RunId : runId,
                UserGoal = userGoal,
                RuntimeType = _settings.RuntimeType,
                ModelPath = _settings.ModelPath,
                ModelName = Path.GetFileName(_settings.ModelPath ?? string.Empty),
                ContextPackSummary = context.Summary,
                RunHeader = runHeader,
                PromptVersions = PromptLoader.CollectTemplateVersions()
            };
            var traceWriter = new AutoFixTraceWriter(report.AiRunId, report.RunId);
            report.AutoFixTraceDirectory = traceWriter.DirectoryPath;
            report.AutoFixPipelineStagesPath = traceWriter.PipelineStagesPath;
            report.Agent1PromptPath = traceWriter.ResolvePath("agent1_prompt.txt");
            report.Agent1RawOutputPath = traceWriter.ResolvePath("agent1_raw.txt");
            report.Agent2RawOutputPath = traceWriter.ResolvePath("agent2_raw.txt");
            report.Agent3RawOutputPath = traceWriter.ResolvePath("agent3_raw.txt");
            report.JudgeInputPath = traceWriter.ResolvePath("judge_input.txt");
            traceWriter.WriteStage("pipeline.start", new
            {
                report.RunId,
                report.AiRunId,
                report.ModelName,
                userGoalChars = userGoal.Length,
                contextChars = contextText.Length
            });
            report.PipelineMetrics.Add(new AiPipelineMetrics
            {
                Stage = "orchestrator_start",
                RunId = report.RunId,
                ContextChars = contextText.Length,
                ContextTokensEst = Math.Max(1, contextText.Length / 4),
                PromptChars = userGoal.Length,
                PromptTokensEst = Math.Max(1, userGoal.Length / 4),
                ModelName = report.ModelName
            });

            await RunAgent1Async(report, userGoal, context, ct, traceWriter).ConfigureAwait(false);
            var agent1Script = report.ScriptDraft?.ScriptText?.Trim() ?? string.Empty;
            if (agent1Script.Length < 200)
            {
                var failMsg = "ScriptBuilder n'a pas genere de script PowerShell valide";
                App.LogMessage($"[AI] Pipeline blocked early: {failMsg}. chars={agent1Script.Length}");
                traceWriter.WriteStageChars("agent1.extracted_script", agent1Script.Length, "below_minimum_200");
                FinalizeMissingScript(report, failMsg, traceWriter);
                SaveReport(report);
                return report;
            }

            await RunAgent2Async(report, context, ct, traceWriter).ConfigureAwait(false);
            var candidate = report.ReviewResult?.RevisedScriptText ?? report.ScriptDraft?.ScriptText ?? string.Empty;
            candidate = EnsureCapabilitiesDeclaration(candidate, report.ScriptDraft?.Capabilities ?? new List<string>());

            // Agent 3: CodeRefiner — receives script + summary (not full context) to save tokens
            await RunAgent3RefineAsync(report, candidate, context, ct, traceWriter).ConfigureAwait(false);
            candidate = report.RefineResult?.RefinedScriptText ?? candidate;

            // If syntax is invalid after refining, block the pipeline
            if (report.RefineResult != null && !report.RefineResult.SyntaxValid)
            {
                App.LogMessage($"[AI] Pipeline blocked: syntax invalid after CodeRefiner — {report.RefineResult.SyntaxError}");
                App.LogMessage("[AI] Pipeline will continue to Agent4 despite syntax issue.");
            }

            // Agent 4: SecurityJudge — final verdict
            traceWriter.WriteArtifact("judge_input.txt", candidate);
            traceWriter.WriteStageChars("judge.input_script", candidate?.Length ?? 0);
            candidate ??= string.Empty;
            await RunAgent4JudgeAsync(report, candidate, context, ct).ConfigureAwait(false);

            // Always expose the script so the user can inspect it, even if REFUSE.
            // Execution is blocked by IsApproved=false in EvaluateForAutoFix — not by emptying the script.
            report.FinalScript = candidate;

            FinalizeGate(report, report.FinalScript);
            SaveReport(report);
            return report;
        }

        private void FinalizeMissingScript(AiRunReport report, string failMessage, AutoFixTraceWriter traceWriter)
        {
            report.FinalScript = string.Empty;
            report.ReviewResult = null;
            report.RefineResult = null;

            var judge = _safety.Analyse(string.Empty);
            judge.JudgeError = true;
            judge.JudgeErrorMessage = "Judge skipped LLM because script is empty.";
            judge.BlockedByCategory = "MissingScript";
            if (!judge.Reasons.Contains(failMessage, StringComparer.OrdinalIgnoreCase))
            {
                judge.Reasons.Insert(0, failMessage);
            }

            if (!judge.Reasons.Any(r => r.Contains("JudgeError:", StringComparison.OrdinalIgnoreCase)))
            {
                judge.Reasons.Add("JudgeError: script is empty before LLM evaluation.");
            }

            report.JudgeResult = judge;
            traceWriter.WriteArtifact("judge_input.txt", string.Empty);
            traceWriter.WriteStageChars("judge.input_script", 0, "blocked_missing_script");

            FinalizeGate(report, report.FinalScript);
            if (!report.AutoFixEligibilityReasons.Contains(failMessage, StringComparer.OrdinalIgnoreCase))
            {
                report.AutoFixEligibilityReasons.Insert(0, failMessage);
            }

            report.AgentOutputs.Add(new AgentOutputSummary
            {
                Agent = "PipelineGate",
                Summary = "AutoFix failed: MissingScript",
                Notes = new List<string> { failMessage, $"Trace logs: {report.AutoFixTraceDirectory}" }
            });
        }

        /// <summary>
        /// Creates a linked CancellationTokenSource with a per-agent hard timeout.
        /// Both user cancellation and timeout will cancel the agent.
        /// Caller is responsible for Dispose (use via `using`).
        /// </summary>
        private CancellationTokenSource CreateAgentCts(CancellationToken userCt)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(userCt);
            linked.CancelAfter(TimeSpan.FromSeconds(AgentTimeoutSeconds));
            return linked;
        }

        /// <summary>
        /// Handles OperationCanceledException by distinguishing user cancel vs timeout.
        /// Surfaces a readable message and logs to %TEMP%.
        /// </summary>
        private void HandleAgentCancellation(AgentStepLog step, CancellationToken userCt, string runId)
        {
            var isTimeout = !userCt.IsCancellationRequested;
            var msg = isTimeout
                ? $"Timeout IA dépassé après {AgentTimeoutSeconds}s pour l'agent {step.AgentName}. Réessayez."
                : $"Agent {step.AgentName} annulé par l'utilisateur.";

            FailStep(step, msg);
            App.LogMessage($"[AI][Watchdog] {msg} runId={runId}");

            // Write timeout detail to %TEMP% for diagnostics
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_AI_Timeout.log");
                var entry = $"[{DateTime.Now:o}] runId={runId} agent={step.AgentName} isTimeout={isTimeout} timeoutSec={AgentTimeoutSeconds}{Environment.NewLine}";
                File.AppendAllText(logPath, entry, Encoding.UTF8);
            }
            catch { /* log failure must never crash */ }

            if (!isTimeout) throw new OperationCanceledException(userCt);
            // Timeout: do NOT rethrow — pipeline continues to FinalizeGate with partial results
        }

        public string SaveReport(AiRunReport report, string? explicitPath = null)
        {
            try
            {
                var targetPath = explicitPath;
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PCDiagnosticPro",
                        "AiReports");
                    Directory.CreateDirectory(dir);
                    targetPath = Path.Combine(
                        dir,
                        $"AiRunReport_{report.RunId}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                }
                else
                {
                    var folder = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }
                }

                var json = JsonSerializer.Serialize(report, _indentedOptions);
                File.WriteAllText(targetPath!, json, Encoding.UTF8);
                App.LogMessage($"[AI] AiRunReport saved: {targetPath}");

                // Purge old reports — keep only the 50 most recent files in the reports folder.
                try
                {
                    var reportDir = Path.GetDirectoryName(targetPath!);
                    if (!string.IsNullOrWhiteSpace(reportDir))
                    {
                        var allReports = Directory.GetFiles(reportDir, "AiRunReport_*.json")
                            .Select(f => new FileInfo(f))
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .ToList();
                        foreach (var old in allReports.Skip(50))
                        {
                            old.Delete();
                        }
                    }
                }
                catch (Exception purgeEx)
                {
                    App.LogMessage($"[AI] AiRunReport purge failed: {purgeEx.Message}");
                }

                return targetPath!;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI] Failed to save AiRunReport: {ex.Message}");
                return string.Empty;
            }
        }

        public bool TrySaveReport(AiRunReport report, string path)
        {
            return !string.IsNullOrWhiteSpace(SaveReport(report, path));
        }

        private async Task RunAgent1Async(
            AiRunReport report,
            string userGoal,
            ContextPack context,
            CancellationToken userCt,
            AutoFixTraceWriter traceWriter)
        {
            var step = StartStep(report, "ScriptBuilderAgent");
            using var agentCts = CreateAgentCts(userCt);
            try
            {
                var builder = GetScriptBuilderAgent();
                report.ScriptDraft = await builder.RunAsync(userGoal, context, agentCts.Token, traceWriter).ConfigureAwait(false);
                report.AgentOutputs.Add(new AgentOutputSummary
                {
                    Agent = "ScriptBuilderAgent",
                    Summary = "Draft script generated.",
                    Notes = report.ScriptDraft.Assumptions.Take(5).ToList()
                });

                CompleteStep(step);
            }
            catch (OperationCanceledException)
            {
                HandleAgentCancellation(step, userCt, report.RunId);
            }
            catch (Exception ex)
            {
                FailStep(step, ex.Message);
                traceWriter.WriteStage("agent1.error", new { error = ex.Message });
                App.LogMessage($"[AI] ScriptBuilderAgent failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task RunAgent2Async(
            AiRunReport report,
            ContextPack context,
            CancellationToken userCt,
            AutoFixTraceWriter traceWriter)
        {
            var step = StartStep(report, "CodeReviewerAgent");
            using var agentCts = CreateAgentCts(userCt);
            try
            {
                if (report.ScriptDraft == null)
                {
                    FailStep(step, "No draft script to review.");
                    return;
                }

                var reviewer = GetCodeReviewerAgent();
                report.ReviewResult = await reviewer.RunAsync(report.ScriptDraft, context, agentCts.Token, traceWriter).ConfigureAwait(false);
                report.AgentOutputs.Add(new AgentOutputSummary
                {
                    Agent = "CodeReviewerAgent",
                    Summary = "Script hardening completed.",
                    Notes = report.ReviewResult.Checklist.Take(6).ToList()
                });

                CompleteStep(step);
            }
            catch (OperationCanceledException)
            {
                HandleAgentCancellation(step, userCt, report.RunId);
            }
            catch (Exception ex)
            {
                FailStep(step, ex.Message);
                traceWriter.WriteStage("agent2.error", new { error = ex.Message });
                App.LogMessage($"[AI] CodeReviewerAgent failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task RunAgent3RefineAsync(
            AiRunReport report,
            string candidateScript,
            ContextPack context,
            CancellationToken userCt,
            AutoFixTraceWriter traceWriter)
        {
            var step = StartStep(report, "CodeRefinerAgent");
            using var agentCts = CreateAgentCts(userCt);
            try
            {
                var refiner = GetCodeRefinerAgent();
                // Agent 3 receives only the script + context summary (not the full scan context)
                // to stay within token budget and focus on code quality.
                report.RefineResult = await refiner.RunAsync(candidateScript, context.Summary, agentCts.Token, traceWriter).ConfigureAwait(false);
                report.AgentOutputs.Add(new AgentOutputSummary
                {
                    Agent = "CodeRefinerAgent",
                    Summary = $"Refined: {report.RefineResult.StyleFixes.Count} style fixes, {report.RefineResult.Validations.Count} validations, syntax={report.RefineResult.SyntaxValid}.",
                    Notes = report.RefineResult.StyleFixes.Take(4).ToList()
                });

                CompleteStep(step);
            }
            catch (OperationCanceledException)
            {
                HandleAgentCancellation(step, userCt, report.RunId);
            }
            catch (Exception ex)
            {
                FailStep(step, ex.Message);
                traceWriter.WriteStage("agent3.error", new { error = ex.Message });
                App.LogMessage($"[AI] CodeRefinerAgent failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task RunAgent4JudgeAsync(AiRunReport report, string candidateScript, ContextPack context, CancellationToken userCt)
        {
            var step = StartStep(report, "SecurityJudgeAgent");
            using var agentCts = CreateAgentCts(userCt);
            try
            {
                var judge = GetTesterJudgeAgent();
                report.JudgeResult = await judge.RunAsync(candidateScript, context, agentCts.Token).ConfigureAwait(false);
                report.AgentOutputs.Add(new AgentOutputSummary
                {
                    Agent = "SecurityJudgeAgent",
                    Summary = $"Verdict: {report.JudgeResult.VerdictDisplay}, overall {report.JudgeResult.OverallScore0_100}/100 (S:{report.JudgeResult.SecurityScore0_100} Rel:{report.JudgeResult.RelevanceScore0_100} Rob:{report.JudgeResult.RobustnessScore0_100} UX:{report.JudgeResult.UxScore0_100}).",
                    Notes = report.JudgeResult.Reasons.Take(6).ToList()
                });

                CompleteStep(step);
            }
            catch (OperationCanceledException)
            {
                HandleAgentCancellation(step, userCt, report.RunId);
            }
            catch (Exception ex)
            {
                FailStep(step, ex.Message);
                App.LogMessage($"[AI] SecurityJudgeAgent failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void FinalizeGate(AiRunReport report, string script)
        {
            var judge = report.JudgeResult ?? _safety.Analyse(script);
            report.JudgeResult = judge;

            var gate = _safety.EvaluateForAutoFix(script, judge);
            report.AutoFixEligible = gate.IsApproved;
            report.AutoFixEligibilityReasons = gate.Reasons;
            report.DeclaredCapabilities = gate.DeclaredCapabilities;

            if (report.ScriptDraft?.RequiresAdmin == true)
            {
                if (!report.AutoFixEligibilityReasons.Contains("Script requires administrator privileges."))
                {
                    report.AutoFixEligibilityReasons.Add("Script requires administrator privileges.");
                }
            }

            report.RebootRequired = PredictRebootRequired(report, script);
            report.RebootReason = report.RebootRequired
                ? "Predicted from updates/reboot markers in script or run context."
                : string.Empty;

            // Write structured pipeline trace log to %TEMP%\PCDiagnosticPRO\ for diagnostics.
            WritePipelineTraceLog(report, script, judge, gate);
        }

        private static void WritePipelineTraceLog(AiRunReport report, string script, Models.JudgeResult judge, AutoFixEligibilityResult gate)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "PCDiagnosticPRO");
                Directory.CreateDirectory(dir);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var path = Path.Combine(dir, $"autofix_pipeline_{report.RunId}_{ts}.log");

                var sb = new StringBuilder();
                sb.AppendLine($"=== AutoFix Pipeline Trace ===");
                sb.AppendLine($"TraceId  : {report.AiRunId}");
                sb.AppendLine($"RunId    : {report.RunId}");
                sb.AppendLine($"UTC      : {report.GeneratedAtUtc:o}");
                sb.AppendLine($"Model    : {report.ModelName}");
                sb.AppendLine();
                sb.AppendLine("--- Agent Steps ---");
                foreach (var step in report.Steps)
                    sb.AppendLine($"  [{step.Status}] {step.AgentName} — {(step.Error ?? "ok")}");
                sb.AppendLine();
                sb.AppendLine("--- Script ---");
                sb.AppendLine($"  Length : {script.Length} chars, {script.Split('\n').Length} lines");
                sb.AppendLine($"  Empty  : {string.IsNullOrWhiteSpace(script)}");
                sb.AppendLine();
                sb.AppendLine("--- Safety Scores ---");
                sb.AppendLine($"  Verdict   : {judge.VerdictDisplay}");
                sb.AppendLine($"  Global    : {judge.OverallScore0_100}/100");
                sb.AppendLine($"  Security  : {judge.SecurityScore0_100}/100");
                sb.AppendLine($"  Relevance : {judge.RelevanceScore0_100}/100");
                sb.AppendLine($"  Robustness: {judge.RobustnessScore0_100}/100");
                sb.AppendLine($"  UX        : {judge.UxScore0_100}/100");
                sb.AppendLine($"  Accuracy  : {judge.AccuracyScore0_100}/100");
                sb.AppendLine($"  Minimality: {judge.MinimalityScore0_100}/100");
                sb.AppendLine($"  Reversible: {judge.ReversibilityScore0_100}/100");
                sb.AppendLine($"  Efficiency: {judge.EfficiencyScore0_100}/100");
                sb.AppendLine($"  Readabilty: {judge.ReadabilityScore0_100}/100");
                sb.AppendLine($"  JudgeError: {judge.JudgeError} {(string.IsNullOrWhiteSpace(judge.JudgeErrorMessage) ? string.Empty : $"({judge.JudgeErrorMessage})")}");
                sb.AppendLine();
                sb.AppendLine("--- Flags ---");
                foreach (var f in judge.Flags)
                    sb.AppendLine($"  {f}");
                sb.AppendLine();
                if (judge.Violations.Count > 0)
                {
                    sb.AppendLine("--- Violations ---");
                    foreach (var v in judge.Violations.Take(10))
                        sb.AppendLine($"  {v.Severity} {v.Code} | evidence={v.EvidenceLine} | fix={v.Fix}");
                    sb.AppendLine();
                }
                sb.AppendLine("--- Top Reasons ---");
                foreach (var r in judge.Reasons.Take(5))
                    sb.AppendLine($"  {r}");
                sb.AppendLine();
                sb.AppendLine("--- AutoFix Gate ---");
                sb.AppendLine($"  IsApproved : {gate.IsApproved}");
                sb.AppendLine($"  BlockedBy  : {gate.BlockedBy}");
                if (gate.BlockingReasons.Count > 0)
                {
                    sb.AppendLine("  Blocking reasons:");
                    foreach (var br in gate.BlockingReasons)
                        sb.AppendLine($"    - {br}");
                }
                if (gate.WarningReasons.Count > 0)
                {
                    sb.AppendLine("  Warnings (non-blocking):");
                    foreach (var wr in gate.WarningReasons)
                        sb.AppendLine($"    - {wr}");
                }
                sb.AppendLine();
                sb.AppendLine("--- Static Tests ---");
                foreach (var t in judge.StaticTests)
                    sb.AppendLine($"  {t}");
                sb.AppendLine();
                sb.AppendLine("=== End of Trace ===");

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                App.LogMessage($"[AI][Gate] Pipeline trace saved: {path}");

                // Purge old traces — keep newest 20.
                var traces = Directory.GetFiles(dir, "autofix_pipeline_*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                foreach (var old in traces.Skip(20))
                    old.Delete();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI][Gate] Trace log write failed: {ex.Message}");
            }
        }

        private static bool PredictRebootRequired(AiRunReport report, string script)
        {
            if (report.RunHeader?.Summary.Contains("reboot", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            if (script.Contains("reboot", StringComparison.OrdinalIgnoreCase)
                || script.Contains("Restart-Computer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (report.JudgeResult?.Reasons.Any(r => r.Contains("reboot", StringComparison.OrdinalIgnoreCase)) == true)
            {
                return true;
            }

            return false;
        }

        private static string EnsureCapabilitiesDeclaration(string script, IReadOnlyCollection<string> capabilities)
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                return string.Empty;
            }

            var normalized = script;
            if (normalized.IndexOf("#Requires -Version", StringComparison.OrdinalIgnoreCase) < 0)
            {
                normalized = "#Requires -Version 5.1" + Environment.NewLine + normalized;
            }

            if (normalized.IndexOf("# CAPABILITIES:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return normalized;
            }

            if (capabilities == null || capabilities.Count == 0)
            {
                return normalized;
            }

            var capLine = "# CAPABILITIES: " + string.Join(", ", capabilities.Distinct(StringComparer.OrdinalIgnoreCase));
            return capLine + Environment.NewLine + normalized;
        }

        private AgentStepLog StartStep(AiRunReport report, string name)
        {
            var step = new AgentStepLog
            {
                AgentName = name,
                Status = AgentStepStatus.Running,
                StartedAt = DateTime.Now
            };

            report.Steps.Add(step);
            SafeInvokeStepEvent(StepStarted, nameof(StepStarted), step);
            return step;
        }

        private void CompleteStep(AgentStepLog step)
        {
            step.Status = AgentStepStatus.Completed;
            step.CompletedAt = DateTime.Now;
            SafeInvokeStepEvent(StepCompleted, nameof(StepCompleted), step);
        }

        private void FailStep(AgentStepLog step, string error)
        {
            step.Status = AgentStepStatus.Failed;
            step.Error = error;
            step.CompletedAt = DateTime.Now;
            SafeInvokeStepEvent(StepFailed, nameof(StepFailed), step);
        }

        private ScriptBuilderAgent GetScriptBuilderAgent()
        {
            if (_scriptBuilderAgent == null)
            {
                _scriptBuilderAgent = new ScriptBuilderAgent(_llm);
                App.LogMessage("[AI] ScriptBuilderAgent initialized lazily.");
            }

            return _scriptBuilderAgent;
        }

        private CodeReviewerAgent GetCodeReviewerAgent()
        {
            if (_codeReviewerAgent == null)
            {
                _codeReviewerAgent = new CodeReviewerAgent(_llm);
                App.LogMessage("[AI] CodeReviewerAgent initialized lazily.");
            }

            return _codeReviewerAgent;
        }

        private CodeRefinerAgent GetCodeRefinerAgent()
        {
            if (_codeRefinerAgent == null)
            {
                _codeRefinerAgent = new CodeRefinerAgent(_llm);
                App.LogMessage("[AI] CodeRefinerAgent initialized lazily.");
            }

            return _codeRefinerAgent;
        }

        private TesterJudgeAgent GetTesterJudgeAgent()
        {
            if (_testerJudgeAgent == null)
            {
                _testerJudgeAgent = new TesterJudgeAgent(_llm, _safety);
                App.LogMessage("[AI] TesterJudgeAgent initialized lazily.");
            }

            return _testerJudgeAgent;
        }

        private static void SafeInvokeStepEvent(Action<AgentStepLog>? handler, string eventName, AgentStepLog step)
        {
            if (handler == null)
            {
                return;
            }

            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<AgentStepLog>)subscriber)(step);
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[AI] {eventName} subscriber error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
