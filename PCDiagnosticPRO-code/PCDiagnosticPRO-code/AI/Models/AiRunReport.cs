using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.AI.Models
{
    public sealed class AgentOutputSummary
    {
        [JsonPropertyName("agent")]
        public string Agent { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("notes")]
        public List<string> Notes { get; set; } = new();
    }

    /// <summary>
    /// Full serializable record of one AI analysis run and optional AutoFix execution.
    /// </summary>
    public class AiRunReport
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; set; } = "ai-run-2.0";

        [JsonPropertyName("aiRunId")]
        public string AiRunId { get; set; } = Guid.NewGuid().ToString("N")[..8];

        [JsonPropertyName("runId")]
        public string RunId { get; set; } = string.Empty;

        [JsonPropertyName("generatedAtUtc")]
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("userGoal")]
        public string UserGoal { get; set; } = string.Empty;

        [JsonPropertyName("runtimeType")]
        public string RuntimeType { get; set; } = "llamacpp";

        [JsonPropertyName("modelPath")]
        public string ModelPath { get; set; } = string.Empty;

        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = string.Empty;

        [JsonPropertyName("promptVersions")]
        public Dictionary<string, string> PromptVersions { get; set; } = new();

        [JsonPropertyName("autoFixTraceDirectory")]
        public string AutoFixTraceDirectory { get; set; } = string.Empty;

        [JsonPropertyName("autoFixPipelineStagesPath")]
        public string AutoFixPipelineStagesPath { get; set; } = string.Empty;

        [JsonPropertyName("agent1PromptPath")]
        public string Agent1PromptPath { get; set; } = string.Empty;

        [JsonPropertyName("agent1RawOutputPath")]
        public string Agent1RawOutputPath { get; set; } = string.Empty;

        [JsonPropertyName("agent2RawOutputPath")]
        public string Agent2RawOutputPath { get; set; } = string.Empty;

        [JsonPropertyName("agent3RawOutputPath")]
        public string Agent3RawOutputPath { get; set; } = string.Empty;

        [JsonPropertyName("judgeInputPath")]
        public string JudgeInputPath { get; set; } = string.Empty;

        [JsonPropertyName("runHeader")]
        public RunAnalysisHeader? RunHeader { get; set; }

        [JsonPropertyName("contextPackSummary")]
        public string ContextPackSummary { get; set; } = string.Empty;

        [JsonPropertyName("scriptDraft")]
        public ScriptDraft? ScriptDraft { get; set; }

        [JsonPropertyName("reviewResult")]
        public ReviewResult? ReviewResult { get; set; }

        [JsonPropertyName("refineResult")]
        public RefineResult? RefineResult { get; set; }

        [JsonPropertyName("judgeResult")]
        public JudgeResult? JudgeResult { get; set; }

        [JsonPropertyName("agentOutputs")]
        public List<AgentOutputSummary> AgentOutputs { get; set; } = new();

        [JsonPropertyName("manualActionPlan")]
        public List<ActionPlanItem> ManualActionPlan { get; set; } = new();

        [JsonPropertyName("autoFixActionPlan")]
        public List<ActionPlanItem> AutoFixActionPlan { get; set; } = new();

        [JsonPropertyName("finalScript")]
        public string FinalScript { get; set; } = string.Empty;

        [JsonPropertyName("autoFixEligible")]
        public bool AutoFixEligible { get; set; }

        [JsonPropertyName("autoFixEligibilityReasons")]
        public List<string> AutoFixEligibilityReasons { get; set; } = new();

        [JsonPropertyName("declaredCapabilities")]
        public List<string> DeclaredCapabilities { get; set; } = new();

        [JsonPropertyName("rebootRequired")]
        public bool RebootRequired { get; set; }

        [JsonPropertyName("rebootReason")]
        public string RebootReason { get; set; } = string.Empty;

        [JsonPropertyName("executionLogs")]
        public List<AiExecutionLog> ExecutionLogs { get; set; } = new();

        [JsonPropertyName("executionWorkingDirectory")]
        public string ExecutionWorkingDirectory { get; set; } = string.Empty;

        [JsonPropertyName("executionLogPath")]
        public string ExecutionLogPath { get; set; } = string.Empty;

        [JsonPropertyName("transcriptPath")]
        public string TranscriptPath { get; set; } = string.Empty;

        [JsonPropertyName("steps")]
        public List<AgentStepLog> Steps { get; set; } = new();

        [JsonPropertyName("pipelineMetrics")]
        public List<AiPipelineMetrics> PipelineMetrics { get; set; } = new();
    }
}
