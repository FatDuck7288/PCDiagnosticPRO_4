using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCDiagnosticPro.AI
{
    public sealed class ScriptCapabilitiesValidation
    {
        public bool HasExplicitDeclaration { get; set; }
        public List<string> DeclaredCapabilities { get; set; } = new();
        public List<string> UnauthorizedCapabilities { get; set; } = new();
        public bool IsAllowed => HasExplicitDeclaration && UnauthorizedCapabilities.Count == 0;
    }

    public sealed class ScriptCapabilitiesParser
    {
        private static readonly Regex HeaderLine = new(
            @"^\s*#\s*CAPABILITIES\s*:\s*(?<caps>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex JsonCapsLine = new(
            @"""capabilities""\s*:\s*\[(?<caps>[^\]]*)\]",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public ScriptCapabilitiesValidation Validate(string scriptText, IEnumerable<string> allowedCapabilities)
        {
            var allowed = new HashSet<string>(
                allowedCapabilities
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => Normalize(v)),
                StringComparer.OrdinalIgnoreCase);

            var declared = ParseDeclaredCapabilities(scriptText);
            var unauthorized = declared
                .Where(cap => !allowed.Contains(Normalize(cap)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ScriptCapabilitiesValidation
            {
                HasExplicitDeclaration = declared.Count > 0,
                DeclaredCapabilities = declared,
                UnauthorizedCapabilities = unauthorized
            };
        }

        public List<string> ParseDeclaredCapabilities(string scriptText)
        {
            var values = new List<string>();
            if (string.IsNullOrWhiteSpace(scriptText))
            {
                return values;
            }

            var headerMatch = HeaderLine.Match(scriptText);
            if (headerMatch.Success)
            {
                values.AddRange(SplitCapabilities(headerMatch.Groups["caps"].Value));
            }

            if (values.Count == 0)
            {
                var jsonMatch = JsonCapsLine.Match(scriptText);
                if (jsonMatch.Success)
                {
                    values.AddRange(SplitCapabilities(jsonMatch.Groups["caps"].Value.Replace("\"", string.Empty)));
                }
            }

            return values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> SplitCapabilities(string raw)
        {
            return raw
                .Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim());
        }

        private static string Normalize(string value)
        {
            return value.Trim().ToLowerInvariant();
        }
    }
}
