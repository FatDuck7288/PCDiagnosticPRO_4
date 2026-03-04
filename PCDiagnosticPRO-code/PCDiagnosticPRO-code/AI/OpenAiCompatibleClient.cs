using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.AI.Interfaces;
using PCDiagnosticPro.AI.Models;
using PCDiagnosticPro.AI.Providers;

namespace PCDiagnosticPro.AI
{
    /// <summary>
    /// OpenAI-compatible HTTP client for remote/local API endpoints
    /// (OpenAI-compatible servers, Ollama bridge, LM Studio server, localhost custom).
    /// </summary>
    public sealed class OpenAiCompatibleClient : ILlmClient, ILlmModelLoader, IDisposable
    {
        private static readonly HttpClient SharedHttpClient = new();

        private readonly AiSettings _settings;
        private readonly ApiSecretProtector _secretProtector;
        private readonly SemaphoreSlim _requestLock = new(1, 1);
        private bool _disposed;

        public ModelStatus Status { get; private set; } = ModelStatus.NotInstalled;
        public string StatusMessage { get; private set; } = "API not configured";
        public bool IsReady => Status == ModelStatus.Ready;

        public OpenAiCompatibleClient(AiSettings settings, ApiSecretProtector? secretProtector = null)
        {
            _settings = settings;
            _secretProtector = secretProtector ?? new ApiSecretProtector();
        }

        public ModelValidationResult ValidateModelPath(string path, bool computeChecksum = false)
        {
            var profile = _settings.ApiProvider ?? new ApiProviderSettings();
            var provider = ApiProviderCatalog.Create(profile);
            return provider.Validate(profile, _settings);
        }

        public Task<bool> TryLoadAsync(string modelPath, int contextWindow, int threads, int gpuLayers)
        {
            var validation = ValidateModelPath(modelPath, computeChecksum: false);
            Status = validation.Status;
            StatusMessage = validation.Message;
            return Task.FromResult(validation.Status == ModelStatus.Ready);
        }

        public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            if (!IsReady)
            {
                var loaded = await TryLoadAsync(string.Empty, _settings.ContextWindow, _settings.Threads, _settings.GpuLayers).ConfigureAwait(false);
                if (!loaded)
                {
                    throw new InvalidOperationException(StatusMessage);
                }
            }

            await _requestLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var profile = _settings.ApiProvider ?? new ApiProviderSettings();
                var provider = ApiProviderCatalog.Create(profile);
                var validation = provider.Validate(profile, _settings);
                if (validation.Status != ModelStatus.Ready)
                {
                    Status = validation.Status;
                    StatusMessage = validation.Message;
                    throw new InvalidOperationException(validation.Message);
                }

                var apiKey = _secretProtector.Unprotect(profile.EncryptedApiKey);
                using var request = provider.BuildRequest(profile, systemPrompt, userPrompt, apiKey, _settings);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _settings.TimeoutSeconds)));

                using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Status = ModelStatus.Error;
                    StatusMessage = $"API HTTP {(int)response.StatusCode}";
                    throw new InvalidOperationException($"API request failed: HTTP {(int)response.StatusCode}");
                }

                var parsed = provider.ExtractAssistantText(body);
                if (string.IsNullOrWhiteSpace(parsed))
                {
                    Status = ModelStatus.Error;
                    StatusMessage = "API returned empty content.";
                    throw new InvalidOperationException("API returned empty content.");
                }

                Status = ModelStatus.Ready;
                StatusMessage = $"Ready ({provider.DisplayName})";
                return parsed;
            }
            finally
            {
                _requestLock.Release();
            }
        }

        public async IAsyncEnumerable<string> StreamAsync(
            string systemPrompt,
            string userPrompt,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Minimal robust implementation: return one chunk from non-streaming endpoint.
            var text = await GenerateAsync(systemPrompt, userPrompt, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }

        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await GenerateAsync(
                    "You are a concise assistant.",
                    "Reply with exactly: pong",
                    ct).ConfigureAwait(false);
                return !string.IsNullOrWhiteSpace(response);
            }
            catch (Exception ex)
            {
                Status = ModelStatus.Error;
                StatusMessage = $"API ping failed: {ex.Message}";
                App.LogMessage($"[AI][OpenAI API] Ping failed: {ex.Message}");
                return false;
            }
        }

        public void Unload()
        {
            Status = ModelStatus.NotInstalled;
            StatusMessage = "API runtime reset.";
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _requestLock.Dispose();
        }

    }
}
