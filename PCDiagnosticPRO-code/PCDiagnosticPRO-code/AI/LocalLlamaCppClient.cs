using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;
using PCDiagnosticPro.AI.Interfaces;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI
{
    /// <summary>
    /// Local LLM client backed by llama.cpp via LLamaSharp.
    /// Model file must match configured allowlist when enforcement is enabled.
    /// </summary>
    public sealed class LocalLlamaCppClient : ILlmClient, ILlmModelLoader, IDisposable
    {
        private readonly AiSettings _settings;
        private LLamaWeights? _model;
        private string _loadedModelPath = string.Empty;
        private bool _disposed;
        private readonly SemaphoreSlim _inferenceLock = new(1, 1);

        public ModelStatus Status { get; private set; } = ModelStatus.NotInstalled;
        public string StatusMessage { get; private set; } = "Model not loaded";
        public bool IsReady => Status == ModelStatus.Ready && _model != null;

        public LocalLlamaCppClient(AiSettings settings)
        {
            _settings = settings;
        }

        public ModelValidationResult ValidateModelPath(string path, bool computeChecksum = false)
        {
            var result = new ModelValidationResult();

            if (string.IsNullOrWhiteSpace(path))
            {
                result.Status = ModelStatus.NotInstalled;
                result.Message = "No model configured. Choose a local .gguf file.";
                return result;
            }

            var normalizedPath = path.Trim().Trim('"');
            result.NormalizedPath = normalizedPath;

            if (!File.Exists(normalizedPath))
            {
                result.Status = ModelStatus.InvalidPath;
                result.Message = $"Model not found: {normalizedPath}";
                return result;
            }

            if (!normalizedPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = ModelStatus.InvalidPath;
                result.Message = "Invalid file format. Expected .gguf";
                return result;
            }

            var fileName = Path.GetFileName(normalizedPath);
            if (!_settings.IsAllowedModel(fileName))
            {
                result.Status = ModelStatus.InvalidPath;
                var allowed = _settings.AllowedModelFileNames.Count > 0
                    ? string.Join(", ", _settings.AllowedModelFileNames)
                    : "no configured models";
                result.Message = $"Invalid model file '{fileName}'. Allowed: {allowed}";
                return result;
            }

            try
            {
                var fileInfo = new FileInfo(normalizedPath);
                result.FileSizeBytes = fileInfo.Length;
                if (fileInfo.Length <= 0)
                {
                    result.Status = ModelStatus.InvalidPath;
                    result.Message = "Model file is empty.";
                    return result;
                }

                using (var stream = File.Open(normalizedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    result.CanRead = stream.CanRead;
                    if (!stream.CanRead)
                    {
                        result.Status = ModelStatus.InvalidPath;
                        result.Message = "Model file is not readable.";
                        return result;
                    }

                    if (computeChecksum)
                    {
                        stream.Position = 0;
                        using var sha256 = SHA256.Create();
                        var hash = sha256.ComputeHash(stream);
                        result.OptionalSha256 = Convert.ToHexString(hash);
                    }
                }

                result.Status = ModelStatus.Ready;
                result.Message = $"Model file validated ({fileInfo.Length / (1024 * 1024)} MB).";
                return result;
            }
            catch (Exception ex)
            {
                result.Status = ModelStatus.Error;
                result.Message = $"Model validation error: {ex.Message}";
                return result;
            }
        }

        public async Task<bool> TryLoadAsync(string modelPath, int contextWindow, int threads, int gpuLayers)
        {
            var validation = ValidateModelPath(modelPath);
            if (validation.Status != ModelStatus.Ready)
            {
                Status = validation.Status;
                StatusMessage = validation.Message;
                return false;
            }

            if (_model != null
                && Status == ModelStatus.Ready
                && string.Equals(_loadedModelPath, validation.NormalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "Ready";
                App.LogMessage($"[AI] Model already loaded, skipping reload: {validation.NormalizedPath}");
                return true;
            }

            Unload();
            Status = ModelStatus.Loading;
            StatusMessage = "Loading local model...";

            try
            {
                var modelName = Path.GetFileName(validation.NormalizedPath);
                var effectiveContext = _settings.GetEffectiveContextWindow(modelName);
                if (contextWindow > effectiveContext)
                {
                    App.LogMessage($"[AI] Requested context {contextWindow} exceeds model support ({effectiveContext}). Using summary + retrieval strategy.");
                }
                else if (effectiveContext >= 131072)
                {
                    App.LogMessage("[AI] 128K context enabled. Expect higher VRAM usage and lower token throughput.");
                }
                if (effectiveContext != contextWindow)
                    App.LogMessage($"[AI] Context window adjusted for {modelName}: {contextWindow}→{effectiveContext} tokens.");

                await Task.Run(() =>
                {
                    var physicalCores = Math.Max(1, Environment.ProcessorCount / 2);
                    var modelParams = new ModelParams(validation.NormalizedPath)
                    {
                        ContextSize = (uint)Math.Max(512, effectiveContext),
                        GpuLayerCount = gpuLayers,
                        Threads = threads > 0 ? threads : physicalCores,
                        BatchSize = (uint)Math.Max(64, _settings.BatchSize),
                        UseMemorymap = _settings.UseMmap,
                        UseMemoryLock = _settings.UseMlock
                    };
                    if (_settings.FlashAttention)
                        modelParams.FlashAttention = true;
                    if (_settings.RopeFreqBase > 0)
                        modelParams.RopeFrequencyBase = _settings.RopeFreqBase;
                    if (_settings.RopeFreqScale > 0)
                        modelParams.RopeFrequencyScale = _settings.RopeFreqScale;

                    App.LogMessage($"[AI] Loading model: ctx={effectiveContext} gpu={gpuLayers} batch={_settings.BatchSize} mmap={_settings.UseMmap} mlock={_settings.UseMlock} flash={_settings.FlashAttention} profile={_settings.PerformanceProfile}");
                    _model = LLamaWeights.LoadFromFile(modelParams);
                    _loadedModelPath = validation.NormalizedPath;
                }).ConfigureAwait(false);

                Status = ModelStatus.Ready;
                StatusMessage = "Ready";
                App.LogMessage($"[AI] Model loaded: {validation.NormalizedPath}");
                return true;
            }
            catch (OutOfMemoryException ex)
            {
                Status = ModelStatus.Error;
                StatusMessage = "Not enough memory to load the model.";
                App.LogMessage($"[AI] OOM loading model: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Status = ModelStatus.Error;
                // Detect likely RAM exhaustion for large models (llama.cpp returns null → generic RuntimeError).
                long fileSizeMb = 0;
                try { fileSizeMb = new FileInfo(validation.NormalizedPath).Length / (1024 * 1024); } catch { }
                var sizeNote = fileSizeMb > 8000
                    ? $" (modele {fileSizeMb / 1024}GB — RAM insuffisante probable)"
                    : string.Empty;
                StatusMessage = $"Load error: {ex.Message}{sizeNote}";
                App.LogMessage($"[AI] Error loading model ({fileSizeMb}MB): {ex.Message}");
                return false;
            }
        }

        public void Unload()
        {
            // Block until any in-progress StreamAsync call finishes before nulling _model.
            // Prevents NullReferenceException if Unload() is called during inference.
            _inferenceLock.Wait();
            try
            {
                _model?.Dispose();
                _model = null;
                _loadedModelPath = string.Empty;
                Status = ModelStatus.NotInstalled;
                StatusMessage = "Model unloaded";
            }
            finally
            {
                _inferenceLock.Release();
            }
        }

        public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            await foreach (var token in StreamAsync(systemPrompt, userPrompt, ct).ConfigureAwait(false))
            {
                sb.Append(token);
            }

            return sb.ToString().Trim();
        }

        public async IAsyncEnumerable<string> StreamAsync(
            string systemPrompt,
            string userPrompt,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!IsReady || _model == null)
            {
                yield return "[LLM unavailable: model not loaded]";
                yield break;
            }

            await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-check after acquiring lock: Unload() may have run between IsReady check and WaitAsync.
                if (_model == null)
                {
                    yield return "[LLM unavailable: model was unloaded]";
                    yield break;
                }

                var fullPrompt = BuildPrompt(systemPrompt, userPrompt, _loadedModelPath);
                var modelName = Path.GetFileName(_loadedModelPath);
                var antiPrompts = GetAntiPrompts(modelName);

                var modelParams = BuildModelParams();
                var executor = new StatelessExecutor(_model, modelParams);

                var inferParams = new InferenceParams
                {
                    MaxTokens = _settings.MaxTokens,
                    SamplingPipeline = new DefaultSamplingPipeline
                    {
                        Temperature = _settings.Temperature,
                        TopP = _settings.TopP,
                        TopK = _settings.TopK,
                        RepeatPenalty = _settings.RepeatPenalty
                    },
                    AntiPrompts = antiPrompts
                };

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

                var promptTemplate = GetPromptTemplate(modelName);
                App.LogMessage($"[LLM] Inference start | model={modelName} | template={promptTemplate} | " +
                               $"temp={_settings.Temperature} topP={_settings.TopP} topK={_settings.TopK} " +
                               $"repeatPenalty={_settings.RepeatPenalty} maxTokens={_settings.MaxTokens} " +
                               $"ctx={_settings.ContextWindow} promptLen={fullPrompt.Length} chars");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var tokenCount = 0;

                await foreach (var token in executor.InferAsync(fullPrompt, inferParams, timeoutCts.Token))
                {
                    if (timeoutCts.Token.IsCancellationRequested)
                    {
                        App.LogMessage("[LLM] Inference cancelled (timeout).");
                        break;
                    }

                    tokenCount++;
                    yield return token;
                }

                sw.Stop();
                var tps = sw.Elapsed.TotalSeconds > 0 ? tokenCount / sw.Elapsed.TotalSeconds : 0;
                App.LogMessage($"[LLM] Inference done | tokens={tokenCount} | elapsed={sw.Elapsed.TotalSeconds:F1}s | {tps:F1} tok/s");
            }
            finally
            {
                _inferenceLock.Release();
            }
        }

        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            try
            {
                var modelName = Path.GetFileName(_loadedModelPath);
                var lang = App.CurrentLanguage;
                var promptTemplate = GetPromptTemplate(modelName);
                App.LogMessage($"[AI] PingAsync: model={modelName} | template={promptTemplate} | lang={lang} | ctx={_settings.ContextWindow} | maxTokens={_settings.MaxTokens}");

                var (sysPrompt, userPrompt) = lang switch
                {
                    "en" => ("You are an IT assistant. Reply in English only.", "Say hello in one short sentence."),
                    "es" => ("Eres un asistente IT. Responde solo en espanol.", "Di hola en una frase corta."),
                    _ => ("Tu es un assistant IT. Reponds en francais uniquement.", "Dis bonjour en une phrase courte.")
                };

                var result = await GenerateAsync(sysPrompt, userPrompt, ct).ConfigureAwait(false);

                var ok = !string.IsNullOrWhiteSpace(result) && result.Length >= 5 && result != "[LLM unavailable: model not loaded]";
                App.LogMessage($"[AI] PingAsync result: ok={ok} response='{(result?.Length > 80 ? result[..80] + "..." : result)}'");
                return ok;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI] PingAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Builds a fresh <see cref="ModelParams"/> instance for each inference call.
        /// Uses the performance profile settings for VRAM optimization.
        /// </summary>
        private ModelParams BuildModelParams()
        {
            var modelName = Path.GetFileName(_loadedModelPath);
            var effectiveCtx = _settings.GetEffectiveContextWindow(modelName);
            var physicalCores = Math.Max(1, Environment.ProcessorCount / 2);

            var mp = new ModelParams(_loadedModelPath)
            {
                ContextSize = (uint)Math.Max(512, effectiveCtx),
                GpuLayerCount = _settings.GpuLayers,
                Threads = _settings.Threads > 0 ? _settings.Threads : physicalCores,
                BatchSize = (uint)Math.Max(64, _settings.BatchSize),
                UseMemorymap = _settings.UseMmap,
                UseMemoryLock = _settings.UseMlock
            };

            // RoPE scaling for extended context (e.g., Qwen3 128K with YaRN)
            if (_settings.RopeFreqBase > 0)
                mp.RopeFrequencyBase = _settings.RopeFreqBase;
            if (_settings.RopeFreqScale > 0)
                mp.RopeFrequencyScale = _settings.RopeFreqScale;

            // Flash attention — set via property if available (LLamaSharp ≥0.26)
            if (_settings.FlashAttention)
                mp.FlashAttention = true;

            var device = _settings.GpuLayers > 0 ? "GPU" : "CPU";
            var kvCacheMb = EstimateKvCacheMb(effectiveCtx, _settings.GpuLayers > 0);
            App.LogMessage($"[LLM] ModelParams: device={device} vramUsedMB~{kvCacheMb} ctxSize={effectiveCtx} kvCache~{kvCacheMb}MB gpu_layers={_settings.GpuLayers} batch={_settings.BatchSize} " +
                          $"mmap={_settings.UseMmap} mlock={_settings.UseMlock} flash_attn={_settings.FlashAttention} " +
                          $"rope_base={_settings.RopeFreqBase} rope_scale={_settings.RopeFreqScale} " +
                          $"threads={mp.Threads} profile={_settings.PerformanceProfile}");

            return mp;
        }

        private static int EstimateKvCacheMb(int contextSize, bool gpuEnabled)
        {
            var mbPer1kTokens = gpuEnabled ? 1.2 : 0.8;
            return (int)Math.Round((contextSize / 1024.0) * mbPer1kTokens);
        }

        private static string BuildPrompt(string system, string user, string modelPath)
        {
            var modelName = Path.GetFileName(modelPath ?? string.Empty);
            // Qwen3: ChatML + inject /no_think to disable chain-of-thought (avoids thousands of
            // reasoning tokens and the <think>-as-first-token anti-prompt trap).
            if (modelName.Contains("qwen3", StringComparison.OrdinalIgnoreCase))
                return BuildQwenPrompt(system, user, noThink: true);

            // Qwen2.5: ChatML, no reasoning mode.
            if (modelName.Contains("qwen", StringComparison.OrdinalIgnoreCase))
                return BuildQwenPrompt(system, user, noThink: false);

            return BuildGenericInstructPrompt(system, user);
        }

        private static string BuildQwenPrompt(string system, string user, bool noThink = false)
        {
            var sb = new StringBuilder();
            sb.Append("<|im_start|>system\n");
            sb.Append(string.IsNullOrWhiteSpace(system) ? "You are a helpful IT assistant." : system.Trim());
            sb.Append("\n<|im_end|>\n");
            sb.Append("<|im_start|>user\n");
            // /no_think disables Qwen3 chain-of-thought reasoning block entirely.
            if (noThink) sb.Append("/no_think\n");
            sb.Append(user?.Trim() ?? string.Empty);
            sb.Append("\n<|im_end|>\n");
            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        private static string BuildGenericInstructPrompt(string system, string user)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(system))
            {
                sb.Append("<|system|>\n");
                sb.Append(system.Trim());
                sb.Append("\n<|end|>\n");
            }

            sb.Append("<|user|>\n");
            sb.Append(user?.Trim() ?? string.Empty);
            sb.Append("\n<|end|>\n");
            sb.Append("<|assistant|>\n");
            return sb.ToString();
        }

        /// <summary>
        /// Get anti-prompts based on model type for proper generation stopping.
        /// NEVER include &lt;think&gt; here: Qwen3 emits it as its very first token when
        /// reasoning mode is active, which stops generation immediately at 7 chars.
        /// The LlmResponseParser already strips &lt;think&gt;...&lt;/think&gt; blocks post-generation.
        /// </summary>
        private static string[] GetAntiPrompts(string modelName)
        {
            if (modelName.Contains("qwen", StringComparison.OrdinalIgnoreCase))
            {
                // ChatML stop tokens only — <think> intentionally excluded (see summary above).
                return new[] { "<|im_end|>", "<|im_start|>" };
            }

            // Generic fallback for configured models using <|system|>/<|user|> style.
            return new[] { "<|end|>", "<|user|>", "<|system|>" };
        }

        /// <summary>
        /// Get prompt template name for logging purposes.
        /// </summary>
        private static string GetPromptTemplate(string modelName)
        {
            if (modelName.Contains("qwen3", StringComparison.OrdinalIgnoreCase))
                return "qwen3-chatml-nothink";

            if (modelName.Contains("qwen", StringComparison.OrdinalIgnoreCase))
                return "qwen2.5-chatml";

            return "generic-instruct";
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Unload();
            _inferenceLock.Dispose();
        }
    }
}
