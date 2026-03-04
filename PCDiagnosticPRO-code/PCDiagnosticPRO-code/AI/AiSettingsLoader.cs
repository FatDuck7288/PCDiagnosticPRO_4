using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PCDiagnosticPro.AI
{
    public static class AiSettingsLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true
        };

        private static readonly UTF8Encoding Utf8NoBom = new(false);

        private static string? _lastLoadedPath;

        public static AiSettings Load()
        {
            return LoadOrCreate();
        }

        public static AiSettings LoadOrCreate()
        {
            foreach (var path in EnumerateCandidatePaths())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var parsed = JsonSerializer.Deserialize<AiSettings>(json, JsonOptions) ?? AiSettings.CreateDefaultSafe();
                    var rawRuntime = parsed.RuntimeType?.Trim() ?? string.Empty;
                    parsed.Normalize();
                    if (!string.Equals(rawRuntime, "llamacpp", StringComparison.OrdinalIgnoreCase))
                    {
                        App.LogMessage($"[AI] ERREUR: runtimeType='{rawRuntime}' non supporte. Seul 'llamacpp' est autorise. Force a 'llamacpp'.");
                    }

                    var rootExists = Directory.Exists(parsed.LlmModelsRoot);
                    App.LogMessage($"[AI] Models root active: {parsed.LlmModelsRoot} (exists={rootExists}) profile={parsed.ActiveModelProfile}");
                    if (!rootExists)
                    {
                        App.LogMessage($"[AI] MODELS_ROOT_MISSING: Aucun dossier Models trouvé. " +
                            $"Créez l'un de ces dossiers et placez-y un modèle .gguf : " +
                            $"%LOCALAPPDATA%\\PCDiagnosticPro\\Models, " +
                            $"%USERPROFILE%\\Documents\\PCDiagnosticPro\\Models, " +
                            $"ou {Path.Combine(AppContext.BaseDirectory, "Models")}. " +
                            $"Var env: {AiSettings.LlmModelsRootEnvironmentVariable}");
                    }

                    _lastLoadedPath = path;
                    return parsed;
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[AI] Failed to parse ai_settings at {path}: {ex.Message}");
                }
            }

            var defaults = AiSettings.CreateDefaultSafe();
            if (Save(defaults, out var savedPath))
            {
                _lastLoadedPath = savedPath;
                App.LogMessage($"[AI] Created default ai_settings.json at {savedPath}");
            }
            else
            {
                App.LogMessage("[AI] Failed to create ai_settings.json, using in-memory defaults.");
            }

            return defaults;
        }

        public static bool Save(AiSettings settings, out string savedPath)
        {
            return Save(settings, GetEffectiveConfigPath(), out savedPath);
        }

        public static bool Save(AiSettings settings, string? preferredPath, out string savedPath)
        {
            settings.Normalize();
            foreach (var path in EnumerateSaveTargets(preferredPath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var json = JsonSerializer.Serialize(settings, JsonOptions);
                    File.WriteAllText(path, json, Utf8NoBom);
                    _lastLoadedPath = path;
                    savedPath = path;
                    return true;
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[AI] Failed to save ai_settings at {path}: {ex.Message}");
                }
            }

            savedPath = string.Empty;
            return false;
        }

        public static string GetEffectiveConfigPath()
        {
            if (!string.IsNullOrWhiteSpace(_lastLoadedPath))
            {
                return _lastLoadedPath!;
            }

            var appPath = GetAppConfigPath();
            if (File.Exists(appPath))
            {
                return appPath;
            }

            return GetUserConfigPath();
        }

        private static string[] EnumerateCandidatePaths()
        {
            return new[]
            {
                GetAppConfigPath(),
                GetUserConfigPath(),
                GetLegacyUserConfigPath()
            };
        }

        private static string[] EnumerateSaveTargets(string? preferredPath)
        {
            if (!string.IsNullOrWhiteSpace(preferredPath))
            {
                return new[]
                {
                    preferredPath!,
                    GetUserConfigPath(),
                    GetAppConfigPath()
                };
            }

            return new[]
            {
                GetAppConfigPath(),
                GetUserConfigPath()
            };
        }

        private static string GetAppConfigPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "config", "ai_settings.json");
        }

        private static string GetUserConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                App.AppDataFolderName,
                "config",
                "ai_settings.json");
        }

        private static string GetLegacyUserConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                App.LegacyAppDataFolderName,
                "config",
                "ai_settings.json");
        }
    }
}
