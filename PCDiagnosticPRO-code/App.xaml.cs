using System;
using System.IO;
using System.Linq;
using System.Windows;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro
{
    /// <summary>
    /// Point d'entrée de l'application PC Diagnostic Pro
    /// </summary>
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_ui.log");
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PCDiagnosticPro", "settings.ini");

        private const string EmpireThemeUri = "Styles/Themes/EmpireStarWars.xaml";
        private static string _currentTheme = "Default";

        protected override void OnStartup(StartupEventArgs e)
        {
            if (SelfTestRunner.TryRun(e.Args, out var exitCode))
            {
                Shutdown(exitCode);
                return;
            }

            base.OnStartup(e);
            
            // Configuration du gestionnaire d'exceptions global
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            
            // Load saved theme preference
            LoadThemePreference();
            
            LogMessage("Application démarrée");
        }

        /// <summary>
        /// Applies the specified theme. "Default" removes Empire theme, "Empire" adds it.
        /// </summary>
        public static void ApplyTheme(string themeCode)
        {
            if (Current == null) return;
            
            var resources = Current.Resources;
            var mergedDicts = resources.MergedDictionaries;
            
            // Find and remove Empire theme if present
            var empireDict = mergedDicts.FirstOrDefault(d => 
                d.Source != null && d.Source.OriginalString.Contains("EmpireStarWars", StringComparison.OrdinalIgnoreCase));
            
            if (empireDict != null)
            {
                mergedDicts.Remove(empireDict);
                LogMessage("[Theme] Removed Empire theme");
            }

            if (themeCode == "Empire")
            {
                // Add Empire theme (it will override the base colors)
                try
                {
                    var newDict = new ResourceDictionary
                    {
                        Source = new Uri(EmpireThemeUri, UriKind.Relative)
                    };
                    mergedDicts.Add(newDict);
                    LogMessage("[Theme] Applied Empire Star Wars theme");
                }
                catch (Exception ex)
                {
                    LogMessage($"[Theme] Failed to load Empire theme: {ex.Message}");
                }
            }
            else
            {
                LogMessage("[Theme] Applied Default theme");
            }

            _currentTheme = themeCode;
            SaveThemePreference(themeCode);
        }

        /// <summary>
        /// Gets the currently active theme code.
        /// </summary>
        public static string GetCurrentTheme() => _currentTheme;

        private static void LoadThemePreference()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var lines = File.ReadAllLines(SettingsPath);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("Theme=", StringComparison.OrdinalIgnoreCase))
                        {
                            var theme = line.Substring(6).Trim();
                            if (!string.IsNullOrEmpty(theme))
                            {
                                _currentTheme = theme;
                                if (theme == "Empire")
                                {
                                    // Apply on next dispatcher cycle to ensure resources are loaded
                                    Current?.Dispatcher.BeginInvoke(new Action(() => ApplyTheme(theme)));
                                }
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[Theme] Failed to load preference: {ex.Message}");
            }
        }

        private static void SaveThemePreference(string themeCode)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Read existing settings or create new
                var lines = File.Exists(SettingsPath) ? File.ReadAllLines(SettingsPath).ToList() : new System.Collections.Generic.List<string>();
                
                // Update or add Theme line
                var themeLineIndex = lines.FindIndex(l => l.StartsWith("Theme=", StringComparison.OrdinalIgnoreCase));
                var newLine = $"Theme={themeCode}";
                
                if (themeLineIndex >= 0)
                    lines[themeLineIndex] = newLine;
                else
                    lines.Add(newLine);

                File.WriteAllLines(SettingsPath, lines);
                LogMessage($"[Theme] Saved preference: {themeCode}");
            }
            catch (Exception ex)
            {
                LogMessage($"[Theme] Failed to save preference: {ex.Message}");
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            var fullException = exception?.ToString() ?? "Exception inconnue";
            LogMessage($"ERREUR NON GÉRÉE: {fullException}");
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var fullException = e.Exception.ToString();
            LogMessage($"ERREUR DISPATCHER: {fullException}");

            e.Handled = true;
        }

        public static void LogMessage(string message)
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(LogPath, logEntry + Environment.NewLine);
            }
            catch
            {
                // Ignorer les erreurs de logging
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            LogMessage("Application fermée");
            base.OnExit(e);
        }
    }
}
