using System;
using PCDiagnosticPro.AI.Interfaces;

namespace PCDiagnosticPro.AI
{
    /// <summary>
    /// Process-wide runtime host so the local model is created once and reused.
    /// </summary>
    public sealed class LlmRuntimeHost
    {
        private static readonly object Sync = new();
        private static LlmRuntimeHost? _instance;

        private readonly ILlmClient _runtimeClient;
        private readonly ILlmModelLoader _runtimeLoader;
        private readonly string _inferenceMode;
        private readonly string _modelPath;
        private readonly int _contextWindow;
        private readonly string _apiSignature;

        private LlmRuntimeHost(AiSettings settings)
        {
            _inferenceMode = string.Equals(settings.InferenceMode, AiSettings.InferenceModeApi, StringComparison.OrdinalIgnoreCase)
                ? AiSettings.InferenceModeApi
                : AiSettings.InferenceModeLocal;
            _modelPath = settings.ModelPath;
            _contextWindow = settings.ContextWindow;
            _apiSignature = BuildApiSignature(settings);

            if (string.Equals(_inferenceMode, AiSettings.InferenceModeApi, StringComparison.OrdinalIgnoreCase))
            {
                var apiClient = new OpenAiCompatibleClient(settings);
                _runtimeClient = apiClient;
                _runtimeLoader = apiClient;
                App.LogMessage("[AI] LlmRuntimeHost initialized API runtime.");
            }
            else
            {
                var local = new LocalLlamaCppClient(settings);
                _runtimeClient = local;
                _runtimeLoader = local;
                App.LogMessage("[AI] LlmRuntimeHost initialized local runtime.");
            }
        }

        public static LlmRuntimeHost GetOrCreate(AiSettings settings)
        {
            lock (Sync)
            {
                if (_instance != null)
                {
                    var requestedMode = string.Equals(settings.InferenceMode, AiSettings.InferenceModeApi, StringComparison.OrdinalIgnoreCase)
                        ? AiSettings.InferenceModeApi
                        : AiSettings.InferenceModeLocal;
                    var requestedSignature = BuildApiSignature(settings);

                    // Recreate when effective runtime settings change.
                    if (!string.Equals(_instance._inferenceMode, requestedMode, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(_instance._modelPath, settings.ModelPath, StringComparison.OrdinalIgnoreCase)
                        || _instance._contextWindow != settings.ContextWindow
                        || !string.Equals(_instance._apiSignature, requestedSignature, StringComparison.Ordinal))
                    {
                        App.LogMessage(
                            $"[AI] LlmRuntimeHost: settings changed, recreating. " +
                            $"oldMode={_instance._inferenceMode} newMode={requestedMode} " +
                            $"oldPath={_instance._modelPath} newPath={settings.ModelPath}");
                        _instance._runtimeLoader.Unload();
                        _instance = null;
                    }
                }

                _instance ??= new LlmRuntimeHost(settings);
                return _instance;
            }
        }

        public ILlmClient Client => _runtimeClient;

        public ILlmModelLoader Loader => _runtimeLoader;

        public void Unload()
        {
            _runtimeLoader.Unload();
        }

        private static string BuildApiSignature(AiSettings settings)
        {
            var p = settings.ApiProvider;
            if (p == null)
            {
                return string.Empty;
            }

            return string.Join("|",
                p.Provider ?? string.Empty,
                p.BaseUrl ?? string.Empty,
                p.ModelName ?? string.Empty,
                p.ContextWindow.ToString(),
                p.MaxOutputTokens.ToString(),
                p.Temperature.ToString("0.###"),
                p.EncryptedApiKey ?? string.Empty);
        }
    }
}
