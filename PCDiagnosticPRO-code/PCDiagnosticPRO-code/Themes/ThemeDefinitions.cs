using System;
using System.Collections.Generic;
using System.Linq;

namespace PCDiagnosticPro.Themes
{
    public sealed class ThemeDefinition
    {
        public ThemeDefinition(string code, string displayName, string description, string dictionaryPath, params string[] legacyCodes)
        {
            Code = code;
            DisplayName = displayName;
            Description = description;
            DictionaryPath = dictionaryPath;
            LegacyCodes = legacyCodes ?? Array.Empty<string>();
        }

        public string Code { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string DictionaryPath { get; }
        public IReadOnlyList<string> LegacyCodes { get; }
    }

    public static class ThemeDefinitions
    {
        public const string DarkFuturisteCode = "DarkFuturiste";
        public const string PCXRayCode = "PCXRAY";

        public static readonly ThemeDefinition DarkFuturiste = new(
            DarkFuturisteCode,
            "Dark Futuriste",
            "Style actuel recommandé",
            "Themes/DarkFuturiste.xaml",
            "Default",
            "Dark");

        public static readonly ThemeDefinition PCXRay = new(
            PCXRayCode,
            "PC X-RAY",
            "Style scan/radiographie",
            "Themes/XRayTheme.xaml",
            "PCXRay",
            "PC X-RAY",
            "XRay");

        public static IReadOnlyList<ThemeDefinition> All { get; } = new[]
        {
            DarkFuturiste,
            PCXRay
        };

        public static ThemeDefinition Resolve(string? themeCode)
        {
            if (string.IsNullOrWhiteSpace(themeCode))
            {
                return DarkFuturiste;
            }

            var normalized = themeCode.Trim();

            var exact = All.FirstOrDefault(t =>
                string.Equals(t.Code, normalized, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            var legacy = All.FirstOrDefault(t => t.LegacyCodes.Any(c =>
                string.Equals(c, normalized, StringComparison.OrdinalIgnoreCase)));
            return legacy ?? DarkFuturiste;
        }
    }
}
