using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.Themes;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro
{
    /// <summary>
    /// Point d'entree de l'application PC X-Ray.
    /// </summary>
    public partial class App : Application
    {
        public const string BrandDisplayName = "PC X-Ray";
        public const string AppDataFolderName = "PCXRay";
        public const string LegacyAppDataFolderName = "PCDiagnosticPro";

        /// <summary>
        /// Current UI language code, updated by MainViewModel when the user changes language.
        /// Values: "fr" (Français), "en" (English), "es" (Español). Defaults to "fr".
        /// Used by the LLM prompt builder to inject the correct language directive.
        /// </summary>
        public static volatile string CurrentLanguage = "fr";

        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_ui.log");
        private static readonly string BootLogPath = Path.Combine(Path.GetTempPath(), "PCDiag_boot.log");
        private static readonly string CrashDirectoryPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPRO");
        private static readonly string BindingTracePath = Path.Combine(CrashDirectoryPath, "logs", "wpf_binding_trace.log");
        private static readonly object BootLogLock = new();
        private static readonly object CrashLogLock = new();
        private static TextWriterTraceListener? _bindingTraceListener;

        protected override void OnStartup(StartupEventArgs e)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var args = e.Args ?? Array.Empty<string>();
            BootLog("============================================================");
            BootLog($"ProcessStartUtc={DateTime.UtcNow:O}");
            BootLog($"PID={Environment.ProcessId}");
            BootLog($"Args={string.Join(" ", args.Select(QuoteArg))}");
            BootLog($"BaseDirectory={AppContext.BaseDirectory}");
            BootLog($"CurrentDirectory={Environment.CurrentDirectory}");
            BootLog("OnStartup begin");
            InitializeBindingDiagnostics();

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
            BootLog("Global exception hooks attached");

            if (SelfTestRunner.TryRun(args, out var exitCode))
            {
                BootLog($"SelfTestRunner consumed startup. exitCode={exitCode}");
                Shutdown(exitCode);
                return;
            }

            base.OnStartup(e);

            // No DI container in this app, keep milestone explicit for startup diagnostics.
            BootLog("DI container built (n/a)");

            ThemeManager.Initialize();

            MainWindow mainWindow;
            try
            {
                mainWindow = new MainWindow();
            }
            catch (Exception ex)
            {
                BootLog($"MainWindow creation failed: {ex}");
                throw;
            }

            MainWindow = mainWindow;
            BootLog("Application.MainWindow assigned");

            mainWindow.Show();
            BootLog("MainWindow.Show called");

            LogMessage("Application demarree");
        }

        /// <summary>
        /// Applies a UI theme and persists selection.
        /// </summary>
        public static void ApplyTheme(string themeCode)
        {
            ThemeManager.ApplyTheme(themeCode, persistPreference: true);
        }

        /// <summary>
        /// Gets the currently active theme code.
        /// </summary>
        public static string GetCurrentTheme() => ThemeManager.CurrentThemeCode;

        private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            var fullException = exception?.ToString() ?? "Unknown exception";
            var crashLogPath = exception != null
                ? WriteCrashLog("AppDomain.CurrentDomain.UnhandledException", exception)
                : string.Empty;

            LogMessage($"UNHANDLED EXCEPTION: {fullException} | crashLog={crashLogPath}");
            BootLog($"AppDomain.UnhandledException: {fullException} | crashLog={crashLogPath}");

            if (exception != null)
            {
                TryPublishChatSupportFailure(exception, crashLogPath);
            }
        }

        private void OnDispatcherUnhandledException(object? sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var fullException = e.Exception.ToString();
            var crashLogPath = WriteCrashLog("DispatcherUnhandledException", e.Exception);
            LogMessage($"DISPATCHER EXCEPTION: {fullException} | crashLog={crashLogPath}");
            BootLog($"DispatcherUnhandledException: {fullException} | crashLog={crashLogPath}");

            var chatFailurePublished = TryPublishChatSupportFailure(e.Exception, crashLogPath);

            var hasVisibleMainWindow = MainWindow != null && MainWindow.IsLoaded && MainWindow.IsVisible;
            if (!hasVisibleMainWindow)
            {
                // If startup failed before the window is visible, do not keep a headless process alive.
                BootLog("Fatal dispatcher exception before visible MainWindow. Shutting down.");
                e.Handled = true;
                Shutdown(-1);
                return;
            }

            if (chatFailurePublished)
            {
                e.Handled = true;
                return;
            }

            // Do not silently swallow unexpected UI exceptions.
            e.Handled = false;
        }

        private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var fullException = e.Exception.ToString();
            var root = e.Exception.GetBaseException();
            var crashLogPath = WriteCrashLog("TaskScheduler.UnobservedTaskException", root);
            LogMessage($"TASK UNOBSERVED EXCEPTION: {fullException} | crashLog={crashLogPath}");
            BootLog($"TaskScheduler.UnobservedTaskException: {fullException} | crashLog={crashLogPath}");
            TryPublishChatSupportFailure(root, crashLogPath);
            e.SetObserved();
        }

        private static void InitializeBindingDiagnostics()
        {
            try
            {
                var bindingLogDir = Path.GetDirectoryName(BindingTracePath);
                if (!string.IsNullOrWhiteSpace(bindingLogDir))
                {
                    Directory.CreateDirectory(bindingLogDir);
                }

                _bindingTraceListener ??= new TextWriterTraceListener(BindingTracePath, "WpfBindingTrace");
                if (!PresentationTraceSources.DataBindingSource.Listeners.OfType<TextWriterTraceListener>()
                    .Any(listener => string.Equals(listener.Name, "WpfBindingTrace", StringComparison.Ordinal)))
                {
                    PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingTraceListener);
                }

                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                BootLog($"Binding diagnostics initialized: {BindingTracePath}");
            }
            catch (Exception ex)
            {
                BootLog($"Binding diagnostics initialization failed: {ex}");
            }
        }

        private static string WriteCrashLog(string source, Exception exception)
        {
            try
            {
                Directory.CreateDirectory(CrashDirectoryPath);
                var crashPath = Path.Combine(
                    CrashDirectoryPath,
                    $"crash_{DateTime.Now:yyyyMMdd_HHmmssfff}.log");

                var payload = new StringBuilder()
                    .AppendLine($"TimestampLocal={DateTime.Now:O}")
                    .AppendLine($"TimestampUtc={DateTime.UtcNow:O}")
                    .AppendLine($"Source={source}")
                    .AppendLine($"ProcessId={Environment.ProcessId}")
                    .AppendLine($"ThreadId={Environment.CurrentManagedThreadId}")
                    .AppendLine()
                    .AppendLine(exception.ToString())
                    .ToString();

                lock (CrashLogLock)
                {
                    File.WriteAllText(crashPath, payload, Encoding.UTF8);
                }

                return crashPath;
            }
            catch (Exception ex)
            {
                BootLog($"WriteCrashLog failed ({source}): {ex}");
                return string.Empty;
            }
        }

        private bool TryPublishChatSupportFailure(Exception exception, string crashLogPath)
        {
            var fullException = exception.ToString();
            var isChatFailure =
                fullException.Contains("ChatSupport", StringComparison.OrdinalIgnoreCase) ||
                fullException.Contains("ChatMessage", StringComparison.OrdinalIgnoreCase) ||
                fullException.Contains("DisplayContent", StringComparison.OrdinalIgnoreCase);

            if (!isChatFailure)
            {
                return false;
            }

            try
            {
                void Publish()
                {
                    if (MainWindow?.DataContext is MainViewModel mainVm)
                    {
                        mainVm.ChatSupportVm.ReportViewLoadFailure(
                            "Chat & Support failed to load: click to open log",
                            crashLogPath,
                            exception);
                    }
                }

                if (Dispatcher.CheckAccess())
                {
                    Publish();
                }
                else
                {
                    Dispatcher.Invoke(Publish);
                }

                return true;
            }
            catch (Exception publishEx)
            {
                BootLog($"TryPublishChatSupportFailure failed: {publishEx}");
                return false;
            }
        }

        public static void LogMessage(string message)
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(LogPath, logEntry + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Ignore logging failures.
            }
        }

        public static void BootLog(string message)
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                lock (BootLogLock)
                {
                    File.AppendAllText(BootLogPath, logEntry + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Ignore logging failures.
            }
        }

        private static string QuoteArg(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return value.Contains(' ') ? $"\"{value}\"" : value;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _bindingTraceListener?.Flush();
                _bindingTraceListener?.Close();
                _bindingTraceListener = null;
            }
            catch (Exception ex)
            {
                BootLog($"Binding diagnostics shutdown failed: {ex}");
            }

            LogMessage("Application fermee");
            BootLog($"OnExit code={e.ApplicationExitCode}");
            base.OnExit(e);
        }
    }
}
