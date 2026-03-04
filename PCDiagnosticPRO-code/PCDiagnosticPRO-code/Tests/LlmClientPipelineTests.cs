using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.AI;
using PCDiagnosticPro.AI.Interfaces;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.Tests
{
    public static class LlmClientPipelineTests
    {
        private static readonly List<string> Failures = new();
        private static readonly List<string> Successes = new();

        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            Failures.Clear();
            Successes.Clear();

            Run(nameof(Test_PromptTemplates_InjectLanguageAndContext), Test_PromptTemplates_InjectLanguageAndContext);
            Run(nameof(Test_MockStream_RespectsCancellation), Test_MockStream_RespectsCancellation);
            Run(nameof(Test_Sanitizer_TruncatesArtifactsAndKeepsFrenchStructure), Test_Sanitizer_TruncatesArtifactsAndKeepsFrenchStructure);
            Run(nameof(Test_Parser_ValidJson_ParsesCorrectly), Test_Parser_ValidJson_ParsesCorrectly);
            Run(nameof(Test_Parser_JsonInFence_Extracted), Test_Parser_JsonInFence_Extracted);
            Run(nameof(Test_Parser_InvalidJson_FallbackToRaw), Test_Parser_InvalidJson_FallbackToRaw);
            Run(nameof(Test_Parser_EmptyInput_FallbackNoException), Test_Parser_EmptyInput_FallbackNoException);
            Run(nameof(Test_Parser_TriggerPipeline_Preserved), Test_Parser_TriggerPipeline_Preserved);

            return (Successes.Count, Failures.Count, new List<string>(Failures));
        }

        private static void Test_PromptTemplates_InjectLanguageAndContext()
        {
            var systemPrompt = PromptLoader.SystemBase().Replace("{PREFERRED_LANGUAGE}", "en");
            var userPrompt = PromptLoader.ChatSupportBase()
                .Replace("{CONTEXT_PACK}", "## Scan Report - run-pipeline-001 (2026-01-01)")
                .Replace("{CONVERSATION_HISTORY}", "USER: hello")
                .Replace("{USER_MESSAGE}", "What should I fix first?");

            Assert(systemPrompt.Contains("PreferredLanguage: en", StringComparison.OrdinalIgnoreCase), "System prompt must include selected language.");
            Assert(userPrompt.Contains("## SCAN CONTEXT", StringComparison.Ordinal), "User prompt should include context section.");
            Assert(userPrompt.Contains("run-pipeline-001", StringComparison.OrdinalIgnoreCase), "Context should include selected run id.");
        }

        private static void Test_MockStream_RespectsCancellation()
        {
            var fake = new CapturingRuntimeClient(new[] { "token1", "token2", "token3", "token4" }, delayPerTokenMs: 80);
            using var cts = new CancellationTokenSource(70);

            var cancelled = false;
            try
            {
                var enumerator = fake.StreamAsync("sys", "user", cts.Token).GetAsyncEnumerator(cts.Token);
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert(cancelled, "Streaming should honor cancellation token.");
        }

        private static void Test_Sanitizer_TruncatesArtifactsAndKeepsFrenchStructure()
        {
            var raw =
                "Resume global : test\n" +
                "🔧 Probleme : Memoire elevee\n" +
                "📊 Impact : Ralentissements\n" +
                "🧠 Cause probable : Saturation RAM\n" +
                "🛠 Solution recommandee :\n" +
                "- Fermer les applications lourdes\n" +
                "⚡ Priorite : Elevee\n" +
                "### AnsweringMotorola\n" +
                "[LANGUAGE:frence\n";

            var sanitized = LlmOutputSanitizer.SanitizeChatAssistantOutput(raw);

            Assert(!sanitized.Text.Contains("###", StringComparison.Ordinal), "Sanitized text must not include artifact markers.");
            Assert(!sanitized.Text.Contains("[LANGUAGE:", StringComparison.OrdinalIgnoreCase), "Sanitized text must remove language leakage.");
            Assert(sanitized.TruncatedAtInvalidPattern, "Sanitizer should cut output at first invalid marker.");
            Assert(sanitized.Text.Contains("Probleme", StringComparison.OrdinalIgnoreCase), "Structured problem block should be preserved.");
        }

        private static void Test_Parser_ValidJson_ParsesCorrectly()
        {
            var json = "{\"user_response\":\"Bonjour, voici l'analyse.\",\"agent_payload\":{\"objectif\":\"diagnostic\",\"contraintes\":[],\"plan\":[],\"trigger_pipeline\":false}}";
            var result = LlmResponseParser.Parse(json, "fr");

            Assert(result.ParseSuccess, "Valid JSON should parse successfully.");
            Assert(result.UserResponse == "Bonjour, voici l'analyse.", "UserResponse must match input.");
            Assert(result.AgentPayload != null, "AgentPayload must be deserialized.");
            Assert(result.AgentPayload!.Objectif == "diagnostic", "Objectif must match.");
            Assert(!result.AgentPayload.TriggerPipeline, "TriggerPipeline should be false.");
            Assert(result.ParseError == null, "No parse error expected.");
        }

        private static void Test_Parser_JsonInFence_Extracted()
        {
            var raw = "Here is my answer:\n```json\n{\"user_response\":\"Resume global : OK\",\"agent_payload\":{\"objectif\":\"\",\"contraintes\":[],\"plan\":[]}}\n```\nDone.";
            var result = LlmResponseParser.Parse(raw, "fr");

            Assert(result.ParseSuccess, "JSON inside fence should be extracted and parsed.");
            Assert(result.UserResponse!.Contains("Resume global"), "UserResponse from fence should be preserved.");
        }

        private static void Test_Parser_InvalidJson_FallbackToRaw()
        {
            var raw = "Je suis une reponse en texte libre sans JSON.";
            var result = LlmResponseParser.Parse(raw, "fr");

            Assert(!result.ParseSuccess, "Non-JSON input should fail parse.");
            Assert(result.UserResponse == raw, "Fallback must use raw text as UserResponse.");
            Assert(result.ParseError != null, "ParseError should be set on failure.");
        }

        private static void Test_Parser_EmptyInput_FallbackNoException()
        {
            var result1 = LlmResponseParser.Parse(null, "fr");
            Assert(!result1.ParseSuccess, "Null input should not crash.");
            Assert(result1.UserResponse == string.Empty, "Null fallback should yield empty UserResponse.");

            var result2 = LlmResponseParser.Parse("", "en");
            Assert(!result2.ParseSuccess, "Empty input should not crash.");
            Assert(result2.UserResponse == string.Empty, "Empty fallback should yield empty UserResponse.");
        }

        private static void Test_Parser_TriggerPipeline_Preserved()
        {
            var json = "{\"user_response\":\"Lancement du script.\",\"agent_payload\":{\"objectif\":\"fix RAM\",\"contraintes\":[\"safe\"],\"plan\":[\"step1\"],\"trigger_pipeline\":true}}";
            var result = LlmResponseParser.Parse(json, "fr");

            Assert(result.ParseSuccess, "Should parse valid JSON with trigger_pipeline.");
            Assert(result.AgentPayload != null, "AgentPayload must exist.");
            Assert(result.AgentPayload!.TriggerPipeline, "TriggerPipeline must be true.");
            Assert(result.AgentPayload.Objectif == "fix RAM", "Objectif must match.");
            Assert(result.AgentPayload.Contraintes.Count == 1, "Contraintes count must be 1.");
            Assert(result.AgentPayload.Plan.Count == 1, "Plan count must be 1.");
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Successes.Add(name);
            }
            catch (Exception ex)
            {
                Failures.Add(name + ": " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class CapturingRuntimeClient : ILlmClient, ILlmModelLoader
        {
            private readonly IReadOnlyList<string> _tokens;
            private readonly int _delayPerTokenMs;

            public CapturingRuntimeClient(IReadOnlyList<string> tokens, int delayPerTokenMs = 0)
            {
                _tokens = tokens;
                _delayPerTokenMs = delayPerTokenMs;
                Status = ModelStatus.Ready;
                StatusMessage = "Ready";
            }

            public string? LastSystemPrompt { get; private set; }
            public string? LastUserPrompt { get; private set; }

            public bool IsReady => true;
            public string StatusMessage { get; private set; }
            public ModelStatus Status { get; private set; }

            public Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
            {
                LastSystemPrompt = systemPrompt;
                LastUserPrompt = userPrompt;
                return Task.FromResult(string.Concat(_tokens));
            }

            public async IAsyncEnumerable<string> StreamAsync(
                string systemPrompt,
                string userPrompt,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                LastSystemPrompt = systemPrompt;
                LastUserPrompt = userPrompt;

                foreach (var token in _tokens)
                {
                    if (_delayPerTokenMs > 0)
                    {
                        await Task.Delay(_delayPerTokenMs, ct).ConfigureAwait(false);
                    }

                    ct.ThrowIfCancellationRequested();
                    yield return token;
                }
            }

            public Task<bool> PingAsync(CancellationToken ct = default)
            {
                return Task.FromResult(true);
            }

            public void Unload()
            {
                Status = ModelStatus.NotInstalled;
                StatusMessage = "Model unloaded";
            }

            public ModelValidationResult ValidateModelPath(string path, bool computeChecksum = false)
            {
                return new ModelValidationResult
                {
                    Status = ModelStatus.Ready,
                    Message = "Ready",
                    NormalizedPath = path ?? string.Empty
                };
            }

            public Task<bool> TryLoadAsync(string modelPath, int contextWindow, int threads, int gpuLayers)
            {
                Status = ModelStatus.Ready;
                StatusMessage = "Ready";
                return Task.FromResult(true);
            }
        }
    }
}
