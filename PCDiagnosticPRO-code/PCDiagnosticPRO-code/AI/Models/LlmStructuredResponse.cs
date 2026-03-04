using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.AI.Models
{
    /// <summary>
    /// Structured payload produced by the chat LLM alongside the user-facing response.
    /// When <see cref="TriggerPipeline"/> is true, the orchestrator pipeline is launched.
    /// </summary>
    public sealed class AgentPayload
    {
        [JsonPropertyName("objectif")]
        public string Objectif { get; set; } = string.Empty;

        [JsonPropertyName("contraintes")]
        public List<string> Contraintes { get; set; } = new();

        [JsonPropertyName("plan")]
        public List<string> Plan { get; set; } = new();

        [JsonPropertyName("trigger_pipeline")]
        public bool TriggerPipeline { get; set; }
    }

    /// <summary>
    /// Envelope returned by <see cref="LlmResponseParser.Parse"/>.
    /// <para><see cref="UserResponse"/> is displayed in the UI.</para>
    /// <para><see cref="AgentPayload"/> is forwarded to the agent pipeline (never shown).</para>
    /// </summary>
    public sealed class LlmStructuredResponse
    {
        [JsonPropertyName("user_response")]
        public string? UserResponse { get; set; }

        [JsonPropertyName("agent_payload")]
        public AgentPayload? AgentPayload { get; set; }

        // --- Parser metadata (not deserialized from JSON) ---

        [JsonIgnore]
        public bool ParseSuccess { get; set; }

        [JsonIgnore]
        public string RawInput { get; set; } = string.Empty;

        [JsonIgnore]
        public string? ParseError { get; set; }
    }
}
