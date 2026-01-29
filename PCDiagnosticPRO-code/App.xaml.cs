using System;
using System.IO;
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
            
            LogMessage("Application démarrée");
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
