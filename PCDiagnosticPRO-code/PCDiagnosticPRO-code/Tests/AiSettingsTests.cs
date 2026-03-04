using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.AI;

namespace PCDiagnosticPro.Tests
{
    public static class AiSettingsTests
    {
        private static readonly List<string> Failures = new();
        private static readonly List<string> Successes = new();

        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            Failures.Clear();
            Successes.Clear();

            Run(nameof(Test_DefaultSettings_AreSafe), Test_DefaultSettings_AreSafe);
            Run(nameof(Test_Save_And_Load_Roundtrip), Test_Save_And_Load_Roundtrip);
            Run(nameof(Test_DefaultModelsRoot_WhenNoOverrides), Test_DefaultModelsRoot_WhenNoOverrides);
            Run(nameof(Test_EnvironmentOverride_TakesPrecedence), Test_EnvironmentOverride_TakesPrecedence);
            Run(nameof(Test_JsonModelsRoot_UsedWithoutEnvironmentOverride), Test_JsonModelsRoot_UsedWithoutEnvironmentOverride);

            return (Successes.Count, Failures.Count, Failures.ToList());
        }

        private static void Test_DefaultSettings_AreSafe()
        {
            WithModelsRootEnv(null, () =>
            {
                var settings = AiSettings.CreateDefaultSafe();

                Assert(settings.RuntimeType == "llamacpp", "runtimeType must default to llamacpp.");
                Assert(settings.RequireUserConfirmation, "User confirmation must stay enabled.");
                Assert(string.IsNullOrWhiteSpace(settings.ModelPath), "Default modelPath should be empty.");
                Assert(settings.BlockedCommands.Count > 0, "Blocked command list should not be empty.");
                Assert(string.Equals(settings.LlmModelsRoot, @"E:\LLM\Models", StringComparison.OrdinalIgnoreCase),
                    "Default LLM models root should be E:\\LLM\\Models.");
                Assert(settings.AllowedModelFileNames.Any(n => n.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)),
                    "Allowlist should contain gguf models.");
                Assert(settings.ModelDownloadInstructions.Contains("GGUF", StringComparison.OrdinalIgnoreCase),
                    "Install instructions should mention allowed GGUF models.");
            });
        }

        private static void Test_Save_And_Load_Roundtrip()
        {
            var root = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_ai_settings_tests");
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "ai_settings_test.json");

            var settings = AiSettings.CreateDefaultSafe();
            settings.ModelPath = @"E:\LLM\Models\Qwen3-8B-Q4_K_M.gguf";
            settings.Threads = 6;

            var saved = AiSettingsLoader.Save(settings, target, out var savedPath);
            Assert(saved, "Save should succeed.");
            Assert(File.Exists(savedPath), "Saved file must exist.");

            var json = File.ReadAllText(savedPath);
            var loaded = JsonSerializer.Deserialize<AiSettings>(json);
            Assert(loaded != null, "Loaded settings should deserialize.");
            loaded!.Normalize();

            Assert(loaded.ModelPath.Equals(settings.ModelPath, StringComparison.OrdinalIgnoreCase), "ModelPath mismatch.");
            Assert(loaded.Threads == 6, "Threads should roundtrip.");
            Assert(!string.IsNullOrWhiteSpace(loaded.ModelDownloadInstructions), "ModelDownloadInstructions should be present.");
        }

        private static void Test_DefaultModelsRoot_WhenNoOverrides()
        {
            WithModelsRootEnv(null, () =>
            {
                var settings = new AiSettings
                {
                    LlmModelsRoot = "",
                    ModelsDirectory = ""
                };

                settings.Normalize();
                Assert(string.Equals(settings.LlmModelsRoot, AiSettings.DefaultLlmModelsRoot, StringComparison.OrdinalIgnoreCase),
                    "Default models root should be used when no override is provided.");
                Assert(string.Equals(settings.ModelsDirectory, AiSettings.DefaultLlmModelsRoot, StringComparison.OrdinalIgnoreCase),
                    "ModelsDirectory should align with the resolved root by default.");
            });
        }

        private static void Test_EnvironmentOverride_TakesPrecedence()
        {
            var envRoot = Path.Combine(Path.GetTempPath(), "pcdiag_env_models_root");
            WithModelsRootEnv(envRoot, () =>
            {
                var settings = new AiSettings
                {
                    LlmModelsRoot = @"E:\SHOULD_NOT_BE_USED",
                    ModelsDirectory = ""
                };

                settings.Normalize();
                Assert(string.Equals(settings.LlmModelsRoot, envRoot, StringComparison.OrdinalIgnoreCase),
                    "Environment override must take precedence over JSON/default.");
                Assert(string.Equals(settings.ModelsDirectory, envRoot, StringComparison.OrdinalIgnoreCase),
                    "ModelsDirectory should follow environment override when not explicitly set.");
            });
        }

        private static void Test_JsonModelsRoot_UsedWithoutEnvironmentOverride()
        {
            WithModelsRootEnv(null, () =>
            {
                var jsonRoot = Path.Combine(Path.GetTempPath(), "pcdiag_json_models_root");
                var settings = new AiSettings
                {
                    LlmModelsRoot = jsonRoot,
                    ModelsDirectory = ""
                };

                settings.Normalize();
                Assert(string.Equals(settings.LlmModelsRoot, jsonRoot, StringComparison.OrdinalIgnoreCase),
                    "JSON root should be used when no environment override is set.");
                Assert(string.Equals(settings.ModelsDirectory, jsonRoot, StringComparison.OrdinalIgnoreCase),
                    "ModelsDirectory should follow JSON root when not explicitly set.");
            });
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
                Failures.Add($"{name}: {ex.Message}");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void WithModelsRootEnv(string? value, Action action)
        {
            var key = AiSettings.LlmModelsRootEnvironmentVariable;
            var previous = Environment.GetEnvironmentVariable(key);
            try
            {
                Environment.SetEnvironmentVariable(key, value);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(key, previous);
            }
        }
    }
}
