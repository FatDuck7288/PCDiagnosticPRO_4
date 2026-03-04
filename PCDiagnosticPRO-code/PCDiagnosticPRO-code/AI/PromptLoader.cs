using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PCDiagnosticPro.AI
{
    /// <summary>
    /// Loads versioned prompt templates from AI/PromptTemplates directory.
    /// Templates are cached in memory after the first read — disk I/O happens once per session.
    /// </summary>
    public static class PromptLoader
    {
        private static readonly string TemplateDir = Path.Combine(AppContext.BaseDirectory, "AI", "PromptTemplates");

        // Perf: templates are static at runtime; cache after first read to avoid repeated disk I/O.
        private static readonly ConcurrentDictionary<string, string> _templateCache = new(StringComparer.OrdinalIgnoreCase);

        public static string Load(string templateName)
        {
            // Return cached success immediately.
            if (_templateCache.TryGetValue(templateName, out var cached))
                return cached;

            var path = Path.Combine(TemplateDir, templateName);
            if (File.Exists(path))
            {
                try
                {
                    var content = File.ReadAllText(path);
                    // Only cache on success — errors are NOT cached so the next call retries.
                    _templateCache.TryAdd(templateName, content);
                    return content;
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[AI] PromptLoader: failed to read {path}: {ex.Message}");
                    return $"[Template '{templateName}' not found — read error]";
                }
            }

            App.LogMessage($"[AI] PromptLoader: template not found: {path}");
            return $"[Template '{templateName}' not found]";
        }

        public static string ComputeVersion(string templateName)
        {
            try
            {
                var content = Load(templateName);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
                return Convert.ToHexString(hash).Substring(0, 12);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI] PromptLoader: failed to compute version for {templateName}: {ex.Message}");
                return "unknown";
            }
        }

        public static Dictionary<string, string> CollectTemplateVersions()
        {
            return new Dictionary<string, string>
            {
                ["system_base.md"] = ComputeVersion("system_base.md"),
                ["chat_system_base.md"] = ComputeVersion("chat_system_base.md"),
                ["agent_system_base.md"] = ComputeVersion("agent_system_base.md"),
                ["chat_support_base.md"] = ComputeVersion("chat_support_base.md"),
                ["agent_script_builder.md"] = ComputeVersion("agent_script_builder.md"),
                ["agent_reviewer.md"] = ComputeVersion("agent_reviewer.md"),
                ["agent_tester_judge.md"] = ComputeVersion("agent_tester_judge.md"),
                ["agent_refiner.md"] = ComputeVersion("agent_refiner.md"),
                ["safety_policy.md"] = ComputeVersion("safety_policy.md")
            };
        }

        // LEGACY - not used in v2 chat flow.
        public static string SystemBase() => Load("system_base.md");
        public static string ChatSystemBase() => Load("chat_system_base.md");
        public static string AgentSystemBase() => Load("agent_system_base.md");
        public static string ChatSupportBase() => Load("chat_support_base.md");
        public static string AgentScriptBuilder() => Load("agent_script_builder.md");
        public static string AgentReviewer() => Load("agent_reviewer.md");
        public static string AgentRefiner() => Load("agent_refiner.md");
        public static string AgentTesterJudge() => Load("agent_tester_judge.md");
        public static string SafetyPolicy() => Load("safety_policy.md");
    }
}
