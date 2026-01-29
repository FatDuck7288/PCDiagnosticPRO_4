using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Service pour exécuter des scripts PowerShell de manière asynchrone
    /// </summary>
    public class PowerShellService
    {
        // Événements pour la progression
        public event Action<string>? OutputReceived;
        public event Action<int>? ProgressChanged;
        public event Action<string>? StepChanged;
        public event Action<int>? ExitCodeReceived;

        // Regex pour détecter les marqueurs de progression
        private static readonly Regex ProgressRegex = new(@"PROGRESS\|(\d+)\|(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StepRegex = new(@"STEP:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const int ScriptTotalSteps = 27;

        // Étapes connues pour simulation de progression
        private static readonly string[] KnownSteps = new[]
        {
            "Initialisation",
            "Analyse système",
            "Vérification services",
            "Analyse disques",
            "Vérification réseau",
            "Analyse mémoire",
            "Vérification sécurité",
            "Windows Update",
            "Applications",
            "Génération rapport"
        };

        private int _currentStepIndex = 0;
        private int _simulatedProgress = 0;
        private Process? _currentProcess;
        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Exécute un script PowerShell de manière asynchrone
        /// </summary>
        public async Task<(int exitCode, string output, string error)> ExecuteScriptAsync(
            string scriptPath,
            int timeoutSeconds = 600,
            CancellationToken cancellationToken = default)
        {
            _currentStepIndex = 0;
            _simulatedProgress = 0;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            try
            {
                App.LogMessage($"Démarrage du script: {scriptPath}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _currentProcess = new Process { StartInfo = startInfo };
                _currentProcess.EnableRaisingEvents = true;

                // Timer pour simulation de progression si pas de marqueurs
                var progressTimer = new System.Timers.Timer(2000);
                progressTimer.Elapsed += (s, e) => SimulateProgress();
                
                _currentProcess.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;

                    outputBuilder.AppendLine(e.Data);
                    ProcessOutput(e.Data);
                };

                _currentProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                        App.LogMessage($"ERREUR PS: {e.Data}");
                    }
                };

                _currentProcess.Start();
                _currentProcess.BeginOutputReadLine();
                _currentProcess.BeginErrorReadLine();

                progressTimer.Start();
                StepChanged?.Invoke(KnownSteps[0]);

                // Attendre la fin du processus avec timeout
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _cancellationTokenSource.Token, timeoutCts.Token);

                try
                {
                    await _currentProcess.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (timeoutCts.IsCancellationRequested)
                    {
                        App.LogMessage("Timeout atteint, arrêt du script");
                        errorBuilder.AppendLine("Le script a dépassé le délai maximum d'exécution.");
                    }
                    
                    try
                    {
                        _currentProcess.Kill(true);
                    }
                    catch { }
                }

                progressTimer.Stop();
                progressTimer.Dispose();

                var exitCode = _currentProcess.ExitCode;
                ExitCodeReceived?.Invoke(exitCode);

                // Progression à 100% à la fin
                ProgressChanged?.Invoke(100);
                StepChanged?.Invoke("Terminé");

                App.LogMessage($"Script terminé avec code: {exitCode}");

                return (exitCode, outputBuilder.ToString(), errorBuilder.ToString());
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur d'exécution: {ex.Message}");
                return (-1, outputBuilder.ToString(), $"Erreur: {ex.Message}\n{errorBuilder}");
            }
            finally
            {
                _currentProcess?.Dispose();
                _currentProcess = null;
            }
        }

        /// <summary>
        /// Annule l'exécution en cours
        /// </summary>
        public void Cancel()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _currentProcess?.Kill(true);
            }
            catch { }
        }

        private void ProcessOutput(string line)
        {
            OutputReceived?.Invoke(line);

            // Vérifier les marqueurs de progression
            var progressMatch = ProgressRegex.Match(line);
            if (progressMatch.Success && int.TryParse(progressMatch.Groups[1].Value, out int progressStep))
            {
                var normalized = ScriptTotalSteps > 0
                    ? (int)Math.Round(Math.Min(progressStep, ScriptTotalSteps) / (double)ScriptTotalSteps * 100)
                    : Math.Min(progressStep, 100);

                _simulatedProgress = Math.Min(normalized, 100);
                ProgressChanged?.Invoke(_simulatedProgress);
                StepChanged?.Invoke(progressMatch.Groups[2].Value.Trim());
            }

            var stepMatch = StepRegex.Match(line);
            if (stepMatch.Success)
            {
                StepChanged?.Invoke(stepMatch.Groups[1].Value.Trim());
            }

            // Détection automatique d'étapes connues
            foreach (var step in KnownSteps)
            {
                if (line.Contains(step, StringComparison.OrdinalIgnoreCase))
                {
                    StepChanged?.Invoke(step);
                    break;
                }
            }
        }

        private void SimulateProgress()
        {
            if (_simulatedProgress >= 95) return;

            // Avancer la progression de manière réaliste
            _simulatedProgress = Math.Min(_simulatedProgress + 3, 95);
            ProgressChanged?.Invoke(_simulatedProgress);

            // Changer d'étape périodiquement
            if (_simulatedProgress % 10 == 0 && _currentStepIndex < KnownSteps.Length - 1)
            {
                _currentStepIndex++;
                StepChanged?.Invoke(KnownSteps[_currentStepIndex]);
            }
        }
    }
}
