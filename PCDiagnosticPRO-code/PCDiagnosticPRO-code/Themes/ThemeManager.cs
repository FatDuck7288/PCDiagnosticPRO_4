using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PCDiagnosticPro.Themes
{
    public static class ThemeManager
    {
        private static readonly object SyncLock = new();
        private static readonly string CurrentSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PCXRay",
            "settings.ini");

        private static readonly string LegacySettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PCDiagnosticPro",
            "settings.ini");

        private static string _currentThemeCode = ThemeDefinitions.DarkFuturisteCode;
        private static bool _isInitialized;

        public static string CurrentThemeCode => _currentThemeCode;

        public static void Initialize()
        {
            lock (SyncLock)
            {
                if (_isInitialized)
                {
                    return;
                }
                _isInitialized = true;
            }

            var savedTheme = LoadThemePreference();
            ApplyTheme(savedTheme, persistPreference: false);
        }

        public static void ApplyTheme(string? requestedThemeCode, bool persistPreference = true)
        {
            var themeDefinition = ThemeDefinitions.Resolve(requestedThemeCode);
            var app = Application.Current;
            if (app == null)
            {
                _currentThemeCode = themeDefinition.Code;
                if (persistPreference)
                {
                    SaveThemePreference(themeDefinition.Code);
                }
                return;
            }

            try
            {
                var mergedDictionaries = app.Resources.MergedDictionaries;
                var existingThemeDictionaries = mergedDictionaries
                    .Where(d => d.Source != null && IsManagedThemeDictionary(d.Source.OriginalString))
                    .ToList();

                foreach (var dictionary in existingThemeDictionaries)
                {
                    mergedDictionaries.Remove(dictionary);
                }

                var themeDictionary = new ResourceDictionary
                {
                    Source = new Uri(themeDefinition.DictionaryPath, UriKind.Relative)
                };

                mergedDictionaries.Add(themeDictionary);
                ApplyDictionaryEntries(app.Resources, themeDictionary);

                _currentThemeCode = themeDefinition.Code;
                if (persistPreference)
                {
                    SaveThemePreference(themeDefinition.Code);
                }

                App.LogMessage($"[Theme] Applied theme: {themeDefinition.Code}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[Theme] Failed to apply theme '{themeDefinition.Code}': {ex.Message}");
                _currentThemeCode = ThemeDefinitions.DarkFuturisteCode;
            }
        }

        private static bool IsManagedThemeDictionary(string source)
        {
            return ThemeDefinitions.All.Any(theme =>
                source.Contains(theme.DictionaryPath, StringComparison.OrdinalIgnoreCase));
        }

        private static void ApplyDictionaryEntries(ResourceDictionary targetResources, ResourceDictionary sourceResources)
        {
            foreach (DictionaryEntry entry in sourceResources)
            {
                if (entry.Key == null || entry.Value == null)
                {
                    continue;
                }

                MergeResourceValue(targetResources, entry.Key, entry.Value);
            }
        }

        private static void MergeResourceValue(ResourceDictionary resources, object key, object newValue)
        {
            if (!resources.Contains(key))
            {
                resources[key] = CloneResource(newValue);
                return;
            }

            var existingValue = resources[key];

            if (existingValue is SolidColorBrush existingBrush && newValue is SolidColorBrush newBrush)
            {
                if (!existingBrush.IsFrozen)
                {
                    existingBrush.Color = newBrush.Color;
                    existingBrush.Opacity = newBrush.Opacity;
                    return;
                }
            }

            if (existingValue is LinearGradientBrush existingGradient && newValue is LinearGradientBrush newGradient)
            {
                if (!existingGradient.IsFrozen && existingGradient.GradientStops.Count == newGradient.GradientStops.Count)
                {
                    for (var i = 0; i < existingGradient.GradientStops.Count; i++)
                    {
                        existingGradient.GradientStops[i].Color = newGradient.GradientStops[i].Color;
                        existingGradient.GradientStops[i].Offset = newGradient.GradientStops[i].Offset;
                    }
                    return;
                }
            }

            if (existingValue is DropShadowEffect existingEffect && newValue is DropShadowEffect newEffect)
            {
                if (!existingEffect.IsFrozen)
                {
                    existingEffect.Color = newEffect.Color;
                    existingEffect.BlurRadius = newEffect.BlurRadius;
                    existingEffect.ShadowDepth = newEffect.ShadowDepth;
                    existingEffect.Direction = newEffect.Direction;
                    existingEffect.Opacity = newEffect.Opacity;
                    return;
                }
            }

            resources[key] = CloneResource(newValue);
        }

        private static object CloneResource(object value)
        {
            if (value is Freezable freezable)
            {
                return freezable.Clone();
            }

            return value;
        }

        private static string LoadThemePreference()
        {
            try
            {
                foreach (var settingsPath in GetSettingsReadOrder())
                {
                    if (!File.Exists(settingsPath))
                    {
                        continue;
                    }

                    var lines = File.ReadAllLines(settingsPath, Encoding.UTF8);
                    foreach (var line in lines)
                    {
                        if (!line.StartsWith("Theme=", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var requestedTheme = line.Substring("Theme=".Length).Trim();
                        var resolvedTheme = ThemeDefinitions.Resolve(requestedTheme);
                        return resolvedTheme.Code;
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[Theme] Failed to load preference: {ex.Message}");
            }

            return ThemeDefinitions.DarkFuturisteCode;
        }

        private static void SaveThemePreference(string themeCode)
        {
            try
            {
                var settingsDir = Path.GetDirectoryName(CurrentSettingsPath);
                if (!string.IsNullOrWhiteSpace(settingsDir) && !Directory.Exists(settingsDir))
                {
                    Directory.CreateDirectory(settingsDir);
                }

                var lines = ReadSettingsLines();
                var normalized = ThemeDefinitions.Resolve(themeCode).Code;
                var themeLine = $"Theme={normalized}";
                var themeLineIndex = lines.FindIndex(l => l.StartsWith("Theme=", StringComparison.OrdinalIgnoreCase));

                if (themeLineIndex >= 0)
                {
                    lines[themeLineIndex] = themeLine;
                }
                else
                {
                    lines.Add(themeLine);
                }

                File.WriteAllLines(CurrentSettingsPath, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[Theme] Failed to save preference: {ex.Message}");
            }
        }

        private static List<string> ReadSettingsLines()
        {
            foreach (var settingsPath in GetSettingsReadOrder())
            {
                if (File.Exists(settingsPath))
                {
                    return File.ReadAllLines(settingsPath, Encoding.UTF8).ToList();
                }
            }

            return new List<string>();
        }

        private static IEnumerable<string> GetSettingsReadOrder()
        {
            yield return CurrentSettingsPath;
            yield return LegacySettingsPath;
        }
    }
}
