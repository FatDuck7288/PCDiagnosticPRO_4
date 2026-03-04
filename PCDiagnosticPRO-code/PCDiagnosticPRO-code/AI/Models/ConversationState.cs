using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.AI.Models
{
    public enum ConversationUserIntent
    {
        General,
        DiagnoseTop,
        HowTo,
        Why,
        ScriptRequest
    }

    public enum ConversationAnswerType
    {
        Unknown,
        Diagnosis,
        HowTo,
        Explanation,
        Checklist,
        ScriptRequest
    }

    public sealed class ConversationIssue
    {
        [JsonPropertyName("issue")]
        public string Issue { get; set; } = string.Empty;

        [JsonPropertyName("evidence")]
        public string Evidence { get; set; } = string.Empty;
    }

    public sealed class ConversationState
    {
        [JsonPropertyName("runId")]
        public string RunId { get; set; } = string.Empty;

        [JsonPropertyName("lastTopIssues")]
        public List<ConversationIssue> LastTopIssues { get; set; } = new();

        [JsonPropertyName("lastRecommendationSet")]
        public List<string> LastRecommendationSet { get; set; } = new();

        [JsonPropertyName("pendingFollowUps")]
        public List<string> PendingFollowUps { get; set; } = new();

        [JsonPropertyName("lastUserIntent")]
        public ConversationUserIntent LastUserIntent { get; set; } = ConversationUserIntent.General;

        [JsonPropertyName("lastAnswerType")]
        public ConversationAnswerType LastAnswerType { get; set; } = ConversationAnswerType.Unknown;

        [JsonPropertyName("updatedUtc")]
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
