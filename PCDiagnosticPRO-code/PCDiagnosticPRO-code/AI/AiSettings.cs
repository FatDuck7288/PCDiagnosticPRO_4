using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using PCDiagnosticPro.AI.Providers;

namespace PCDiagnosticPro.AI
{
    public sealed class ApiProviderSettings
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = ApiProviderCatalog.OpenAi;

        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("encryptedApiKey")]
        public string EncryptedApiKey { get; set; } = string.Empty;

        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = string.Empty;

        [JsonPropertyName("contextWindow")]
        public int ContextWindow { get; set; } = 0;

        [JsonPropertyName("maxOutputTokens")]
        public int MaxOutputTokens { get; set; } = 0;

        [JsonPropertyName("temperature")]
        public float Temperature { get; set; } = 0f;

        public void Normalize(AiSettings owner)
        {
            Provider = ApiProviderCatalog.NormalizeProviderName(Provider);
            BaseUrl = (BaseUrl ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(BaseUrl))
            {
                BaseUrl = BaseUrl.TrimEnd('/');
            }

            if (!ApiProviderCatalog.RequiresBaseUrl(Provider) && !ApiProviderCatalog.SupportsOptionalBaseUrl(Provider))
            {
                BaseUrl = string.Empty;
            }

            ModelName = (ModelName ?? string.Empty).Trim();
            EncryptedApiKey = (EncryptedApiKey ?? string.Empty).Trim();
            ContextWindow = ContextWindow <= 0 ? owner.ContextWindow : Math.Max(512, ContextWindow);
            MaxOutputTokens = MaxOutputTokens <= 0 ? owner.MaxTokens : Math.Max(64, MaxOutputTokens);
            Temperature = Temperature <= 0f ? owner.Temperature : Math.Clamp(Temperature, 0.0f, 2.0f);
        }
    }

    public class AiSettings
    {
        public const string DefaultModelProfileId = "quality";
        public const string LlmModelsRootEnvironmentVariable = "PCXRAY_LLM_MODELS_ROOT";
        public const string InferenceModeLocal = "Local";
        public const string InferenceModeApi = "API";

        /// <summary>
        /// Fallback models root used when no configured or well-known directory exists.
        /// Returns &lt;exe folder&gt;\Models — the canonical installation target.
        /// </summary>
        public static string DefaultLlmModelsRoot => Path.Combine(AppContext.BaseDirectory, "Models");

        /// <summary>
        /// Resolves the best available models directory, searching in order:
        /// 1. Configured path (if non-empty and exists)
        /// 2. %LOCALAPPDATA%\PCDiagnosticPro\Models
        /// 3. %USERPROFILE%\Documents\PCDiagnosticPro\Models
        /// 4. Exe folder + \Models
        /// Returns empty string if none of the candidates exist.
        /// </summary>
        public static string ResolveModelsDirectory(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
                return configuredPath.Trim();

            var localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCDiagnosticPro", "Models");
            if (Directory.Exists(localAppData))
                return localAppData;

            var documents = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PCDiagnosticPro", "Models");
            if (Directory.Exists(documents))
                return documents;

            var exeModels = Path.Combine(AppContext.BaseDirectory, "Models");
            if (Directory.Exists(exeModels))
                return exeModels;

            return string.Empty;
        }

        [JsonPropertyName("runtimeType")]
        public string RuntimeType { get; set; } = "llamacpp";

        [JsonPropertyName("inferenceMode")]
        public string InferenceMode { get; set; } = InferenceModeLocal;

        [JsonPropertyName("apiProvider")]
        public ApiProviderSettings? ApiProvider { get; set; } = new();

        [JsonPropertyName("modelPath")]
        public string ModelPath { get; set; } = string.Empty;

        [JsonPropertyName("modelProfile")]
        public string ModelProfile { get; set; } = DefaultModelProfileId;

        [JsonPropertyName("enforceModelAllowList")]
        public bool EnforceModelAllowList { get; set; } = false;

        [JsonPropertyName("allowedModelFileNames")]
        public List<string> AllowedModelFileNames { get; set; } = new()
        {
            // EXCLUSIVE: only these two models are supported.
            "qwen2.5-coder-14b-instruct-q4_k_m.gguf",
            "Qwen3-8B-Q4_K_M.gguf"
        };

        [JsonPropertyName("llmModelsRoot")]
        public string LlmModelsRoot { get; set; } = string.Empty;

        [JsonPropertyName("qwen3ModelDirectory")]
        public string Qwen3ModelDirectory { get; set; } = string.Empty;

        [JsonPropertyName("fallbackModelPath")]
        public string FallbackModelPath { get; set; } = string.Empty;

        [JsonPropertyName("activeModelProfile")]
        public string ActiveModelProfile { get; set; } = "default";

        [JsonPropertyName("modelsDirectory")]
        public string ModelsDirectory { get; set; } = string.Empty;

        [JsonPropertyName("contextWindow")]
        public int ContextWindow { get; set; } = 32768;

        [JsonPropertyName("maxTokens")]
        public int MaxTokens { get; set; } = 800;

        [JsonPropertyName("temperature")]
        public float Temperature { get; set; } = 0.2f;

        [JsonPropertyName("topP")]
        public float TopP { get; set; } = 0.9f;

        [JsonPropertyName("topK")]
        public int TopK { get; set; } = 40;

        [JsonPropertyName("repeatPenalty")]
        public float RepeatPenalty { get; set; } = 1.15f;

        [JsonPropertyName("enableStreaming")]
        public bool EnableStreaming { get; set; } = true;

        [JsonPropertyName("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 180;

        [JsonPropertyName("threads")]
        public int Threads { get; set; }

        [JsonPropertyName("gpuLayers")]
        public int GpuLayers { get; set; }

        [JsonPropertyName("safetyPolicyLevel")]
        public string SafetyPolicyLevel { get; set; } = "Strict";

        [JsonPropertyName("blockedCommands")]
        public List<string> BlockedCommands { get; set; } = new()
        {
            "Invoke-Expression",
            "IEX",
            "EncodedCommand",
            "Remove-Item -Recurse",
            "Set-MpPreference",
            "DisableRealtimeMonitoring",
            "Add-MpPreference",
            "New-LocalUser",
            "Add-LocalGroupMember",
            "net user",
            "Invoke-WebRequest.*IEX",
            "DownloadString.*IEX"
        };

        [JsonPropertyName("allowedScriptCapabilities")]
        public List<string> AllowedScriptCapabilities { get; set; } = new()
        {
            "read-only diagnostics",
            "safe cleanup optional",
            "export logs",
            "query windows update status",
            "restart approved services",
            "registry read",
            "registry write safe",
            "service management",
            "cleanup temp files",
            "disk diagnostics",
            "windows update management",
            "driver management",
            "network diagnostics",
            "event log query",
            "performance diagnostics",
            "system maintenance"
        };

        [JsonPropertyName("requireUserConfirmation")]
        public bool RequireUserConfirmation { get; set; } = true;

        [JsonPropertyName("enableSimulationForMutatingScripts")]
        public bool EnableSimulationForMutatingScripts { get; set; } = true;

        [JsonPropertyName("modelDownloadInstructions")]
        public string ModelDownloadInstructions { get; set; } =
            "Use Chat & Support to install or select the configured local LLM model (.gguf).";

        // --- VRAM Optimization settings ---

        /// <summary>Performance profile: "balanced" (default), "performance" (max VRAM), "conservative" (low VRAM).</summary>
        [JsonPropertyName("performanceProfile")]
        public string PerformanceProfile { get; set; } = "balanced";

        /// <summary>Batch size for prompt processing. Higher = faster prompt eval, more VRAM.</summary>
        [JsonPropertyName("batchSize")]
        public int BatchSize { get; set; } = 512;

        /// <summary>Enable memory-mapped file loading (reduces RAM usage).</summary>
        [JsonPropertyName("useMmap")]
        public bool UseMmap { get; set; } = true;

        /// <summary>Lock model in memory (prevents swapping, requires sufficient RAM).</summary>
        [JsonPropertyName("useMlock")]
        public bool UseMlock { get; set; } = false;

        /// <summary>Flash attention (reduces VRAM for KV cache). Requires compatible backend.</summary>
        [JsonPropertyName("flashAttention")]
        public bool FlashAttention { get; set; } = false;

        /// <summary>
        /// Allowed context window sizes for the UI picker.
        /// </summary>
        public static readonly int[] AllowedContextSizes = { 2048, 4096, 8192, 16384, 32768, 65536, 131072 };

        /// <summary>RoPE frequency base override (0 = auto/model default). For extended context models.</summary>
        [JsonPropertyName("ropeFreqBase")]
        public float RopeFreqBase { get; set; } = 0f;

        /// <summary>RoPE scaling factor (0 = auto/model default). For YaRN/NTK scaling.</summary>
        [JsonPropertyName("ropeFreqScale")]
        public float RopeFreqScale { get; set; } = 0f;

        /// <summary>
        /// Applies a performance profile to optimize settings for the given VRAM budget.
        /// </summary>
        public void ApplyPerformanceProfile(string profile, long availableVramMb = 0)
        {
            PerformanceProfile = profile;
            switch (profile.ToLowerInvariant())
            {
                case "performance":
                    GpuLayers = 999; // offload all layers
                    BatchSize = 2048;
                    UseMmap = true;
                    UseMlock = true;
                    FlashAttention = true;
                    if (availableVramMb >= 20_000) // 20GB+: can handle larger context
                        ContextWindow = Math.Max(ContextWindow, 32768);
                    break;
                case "conservative":
                    GpuLayers = 20; // partial offload
                    BatchSize = 256;
                    UseMmap = true;
                    UseMlock = false;
                    FlashAttention = false;
                    ContextWindow = Math.Min(ContextWindow, 8192);
                    break;
                default: // balanced
                    GpuLayers = 99;
                    BatchSize = 512;
                    UseMmap = true;
                    UseMlock = false;
                    FlashAttention = false;
                    break;
            }
        }

        /// <summary>
        /// Clamps context window to a supported value, respecting model capabilities.
        /// Returns the effective context window that should be used.
        /// </summary>
        public int GetEffectiveContextWindow(string modelFileName)
        {
            var requested = ContextWindow;

            // Qwen3-8B supports up to 32K natively, 128K with YaRN
            if (modelFileName.Contains("qwen3", StringComparison.OrdinalIgnoreCase))
                return Math.Min(requested, 131072);

            // Qwen2.5 models: 32K native
            if (modelFileName.Contains("qwen2.5", StringComparison.OrdinalIgnoreCase))
                return Math.Min(requested, 32768);

            // Default: cap at 32K for unknown models
            return Math.Min(requested, 32768);
        }

        public void Normalize()
        {
            RuntimeType = "llamacpp";
            var inference = (InferenceMode ?? string.Empty).Trim();
            InferenceMode = string.Equals(inference, InferenceModeApi, StringComparison.OrdinalIgnoreCase)
                ? InferenceModeApi
                : InferenceModeLocal;
            ModelPath = ModelPath?.Trim() ?? string.Empty;
            ModelProfile = string.IsNullOrWhiteSpace(ModelProfile) ? DefaultModelProfileId : ModelProfile.Trim().ToLowerInvariant();
            ActiveModelProfile = string.IsNullOrWhiteSpace(ActiveModelProfile)
                ? "default"
                : ActiveModelProfile.Trim().ToLowerInvariant();

            var envRoot = Environment.GetEnvironmentVariable(LlmModelsRootEnvironmentVariable)?.Trim();
            string resolvedRoot;
            if (!string.IsNullOrWhiteSpace(envRoot))
            {
                resolvedRoot = envRoot!;
            }
            else
            {
                var configuredRoot = string.IsNullOrWhiteSpace(LlmModelsRoot) ? string.Empty : LlmModelsRoot.Trim();
                resolvedRoot = ResolveModelsDirectory(configuredRoot);
                // If no existing directory found, use exe+\Models as the canonical target path
                // (may not exist yet — caller should warn the user).
                if (string.IsNullOrEmpty(resolvedRoot))
                    resolvedRoot = Path.Combine(AppContext.BaseDirectory, "Models");
            }

            if (!Path.IsPathRooted(resolvedRoot))
            {
                resolvedRoot = Path.GetFullPath(resolvedRoot);
            }

            LlmModelsRoot = resolvedRoot;
            ModelsDirectory = string.IsNullOrWhiteSpace(ModelsDirectory) ? LlmModelsRoot : ModelsDirectory.Trim();
            if (!Path.IsPathRooted(ModelsDirectory))
                ModelsDirectory = Path.GetFullPath(Path.Combine(LlmModelsRoot, ModelsDirectory));

            Qwen3ModelDirectory = string.IsNullOrWhiteSpace(Qwen3ModelDirectory) ? LlmModelsRoot : Qwen3ModelDirectory.Trim();
            if (!Path.IsPathRooted(Qwen3ModelDirectory))
                Qwen3ModelDirectory = Path.GetFullPath(Path.Combine(LlmModelsRoot, Qwen3ModelDirectory));

            if (!string.IsNullOrWhiteSpace(FallbackModelPath) && !Path.IsPathRooted(FallbackModelPath))
                FallbackModelPath = Path.GetFullPath(Path.Combine(LlmModelsRoot, FallbackModelPath.Trim()));
            else
                FallbackModelPath = FallbackModelPath?.Trim() ?? string.Empty;

            ContextWindow = Math.Max(512, ContextWindow);
            MaxTokens = Math.Max(64, MaxTokens);
            Temperature = Math.Clamp(Temperature, 0.0f, 2.0f);
            TopP = Math.Clamp(TopP, 0.01f, 1.0f);
            TopK = Math.Max(0, TopK);
            RepeatPenalty = Math.Max(1.0f, RepeatPenalty);
            TimeoutSeconds = Math.Max(10, TimeoutSeconds);
            SafetyPolicyLevel = string.IsNullOrWhiteSpace(SafetyPolicyLevel) ? "Strict" : SafetyPolicyLevel.Trim();
            BlockedCommands ??= new List<string>();
            AllowedScriptCapabilities ??= new List<string>();
            AllowedModelFileNames ??= new List<string>();
            AllowedModelFileNames = AllowedModelFileNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            ApiProvider ??= new ApiProviderSettings();
            ApiProvider.Normalize(this);

        }

        public bool IsAllowedModel(string fileName)
        {
            if (!EnforceModelAllowList)
                return true;

            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            return AllowedModelFileNames.Any(
                allowed => string.Equals(allowed, fileName, StringComparison.OrdinalIgnoreCase));
        }

        public static AiSettings CreateDefaultSafe()
        {
            var settings = new AiSettings();
            settings.Normalize();
            return settings;
        }
    }
}
