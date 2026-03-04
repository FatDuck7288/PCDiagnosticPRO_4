using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.AI.Models
{
    /// <summary>
    /// Normalized metrics emitted by the JSON -> ContextPack -> LLM pipeline.
    /// </summary>
    public sealed class AiPipelineMetrics
    {
        [JsonPropertyName("stage")]
        public string Stage { get; set; } = string.Empty;

        [JsonPropertyName("runId")]
        public string RunId { get; set; } = string.Empty;

        [JsonPropertyName("chatRequestId")]
        public string ChatRequestId { get; set; } = string.Empty;

        [JsonPropertyName("jsonBytes")]
        public long JsonBytes { get; set; }

        [JsonPropertyName("parseMs")]
        public long ParseMs { get; set; }

        [JsonPropertyName("contextBuildMs")]
        public long ContextBuildMs { get; set; }

        [JsonPropertyName("contextChars")]
        public int ContextChars { get; set; }

        [JsonPropertyName("contextTokensEst")]
        public int ContextTokensEst { get; set; }

        [JsonPropertyName("promptChars")]
        public int PromptChars { get; set; }

        [JsonPropertyName("promptTokensEst")]
        public int PromptTokensEst { get; set; }

        [JsonPropertyName("inferenceMs")]
        public long InferenceMs { get; set; }

        [JsonPropertyName("ttftMs")]
        public long TtftMs { get; set; }

        [JsonPropertyName("retrievalMs")]
        public long RetrievalMs { get; set; }

        [JsonPropertyName("responseParseMs")]
        public long ResponseParseMs { get; set; }

        [JsonPropertyName("generatedTokens")]
        public int GeneratedTokens { get; set; }

        [JsonPropertyName("tokensPerSecond")]
        public double TokensPerSecond { get; set; }

        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = string.Empty;

        [JsonPropertyName("temperature")]
        public float Temperature { get; set; }

        [JsonPropertyName("maxTokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("streamingEnabled")]
        public bool StreamingEnabled { get; set; }

        [JsonPropertyName("cacheHit")]
        public bool CacheHit { get; set; }

        [JsonPropertyName("sourceHash")]
        public string SourceHash { get; set; } = string.Empty;

        public string ToLogLine()
        {
            var model = string.IsNullOrWhiteSpace(ModelName) ? "unknown" : ModelName;
            return $"stage={Stage} runId={RunId} chatRequestId={ChatRequestId} jsonBytes={JsonBytes} parseMs={ParseMs} contextBuildMs={ContextBuildMs} retrievalMs={RetrievalMs} responseParseMs={ResponseParseMs} contextChars={ContextChars} contextTokens={ContextTokensEst} promptChars={PromptChars} promptTokens={PromptTokensEst} inferenceMs={InferenceMs} ttftMs={TtftMs} generatedTokens={GeneratedTokens} tps={TokensPerSecond:F2} model={model} temp={Temperature:F2} maxTokens={MaxTokens} streaming={StreamingEnabled} cacheHit={CacheHit} sourceHash={SourceHash}";
        }
    }
}
