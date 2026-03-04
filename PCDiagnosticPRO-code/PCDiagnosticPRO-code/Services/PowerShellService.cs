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

        // Structured markers used by the current PowerShell script:
        // [PROGRESS] Section | 14/35 | 40%
        private static readonly Regex StructuredRegex = new(
            @"^\[(?<type>PROGRESS|STATUS|DONE|ERROR|WARN|INFO|SECTION)\]\s*(?<section>[^|]+?)(?:\s*\|\s*(?<rest>.+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Legacy markers kept as a compatibility fallback.
        private static readonly Regex ProgressRegex = new(@"PROGRESS\|(\d+)\|(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StepRegex = new(@"STEP:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _percentRegex = new(@"(?<pct>\d{1,3})\s*%", RegexOptions.Compiled);
        /// <summary>
        /// Regex to parse "[INFO] TOTAL_STEPS | N" emitted by the PS script at startup.
        /// </summary>
        private static readonly Regex _totalStepsRegex = new(@"^\[INFO\]\s*TOTAL_STEPS\s*\|\s*(?<steps>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        /// <summary>
        /// Fallback value if the PS script doesn't emit TOTAL_STEPS marker.
        /// Updated dynamically at runtime when the marker is parsed.
        /// </summary>
        private int _scriptTotalSteps = 35;

        /// <summary>
        /// Sentinel value emitted on ProgressChanged to indicate "indeterminate" mode.
        /// Consumers should show IsIndeterminate=true when they receive this value.
        /// </summary>
        public const int IndeterminateProgress = -1;

        private volatile int _simulatedProgress = 0;
        private volatile bool _receivedRealProgressMarker = false;
        private int _stdoutEncodingFixCount = 0;
        private int _stderrEncodingFixCount = 0;
        private volatile Process? _currentProcess;
        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Exécute un script PowerShell de manière asynchrone
        /// </summary>
        public async Task<(int exitCode, string output, string error)> ExecuteScriptAsync(
            string scriptPath,
            int timeoutSeconds = 600,
            CancellationToken cancellationToken = default)
        {
            _simulatedProgress = 0;
            _receivedRealProgressMarker = false;
            _stdoutEncodingFixCount = 0;
            _stderrEncodingFixCount = 0;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            try
            {
                App.LogMessage($"Démarrage du script: {scriptPath}");

                // CRITICAL: Force PowerShell to output UTF-8 by setting the console code page before execution.
                // PowerShell 5.1 defaults to the system's legacy code page (1252 on French Windows) which causes
                // all French accented characters to be corrupted (é → é, è → è, etc.)
                // Using -Command with explicit UTF-8 encoding ensures proper character output BEFORE the script runs.
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // [Console]::OutputEncoding: Force .NET to output UTF-8
                    // $OutputEncoding: Force PowerShell pipeline to use UTF-8
                    // The semicolon separates commands, then we execute the script with &
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8; & '{scriptPath}'\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    // UTF-8 encoding for standard output and error streams
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

                    var normalizedLine = TextEncodingNormalizer.NormalizeIfCorrupted(e.Data);
                    if (!string.Equals(e.Data, normalizedLine, StringComparison.Ordinal))
                    {
                        _stdoutEncodingFixCount++;
                        if (_stdoutEncodingFixCount <= 3)
                            App.LogMessage($"[ENCODING] source=powershellservice.stdout normalized=true sample={_stdoutEncodingFixCount}");
                    }
                    EncodingCorruptionWatcher.CheckAndLog(normalizedLine, "powershellservice.stdout");
                    outputBuilder.AppendLine(normalizedLine);
                    ProcessOutput(normalizedLine);
                };

                _currentProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        var normalizedLine = TextEncodingNormalizer.NormalizeIfCorrupted(e.Data);
                        if (!string.Equals(e.Data, normalizedLine, StringComparison.Ordinal))
                        {
                            _stderrEncodingFixCount++;
                            if (_stderrEncodingFixCount <= 3)
                                App.LogMessage($"[ENCODING] source=powershellservice.stderr normalized=true sample={_stderrEncodingFixCount}");
                        }
                        EncodingCorruptionWatcher.CheckAndLog(normalizedLine, "powershellservice.stderr");
                        errorBuilder.AppendLine(normalizedLine);
                        App.LogMessage($"ERREUR PS: {normalizedLine}");
                    }
                };

                _currentProcess.Start();
                _currentProcess.BeginOutputReadLine();
                _currentProcess.BeginErrorReadLine();

                progressTimer.Start();
                // Emit indeterminate immediately so UI shows spinner from the start
                ProgressChanged?.Invoke(IndeterminateProgress);
                StepChanged?.Invoke("Démarrage du scan…");

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
                finally
                {
                    progressTimer.Stop();
                    progressTimer.Dispose();
                }

                var exitCode = _currentProcess.ExitCode;
                ExitCodeReceived?.Invoke(exitCode);

                // Progression à 100% à la fin
                ProgressChanged?.Invoke(100);
                StepChanged?.Invoke("Terminé");

                App.LogMessage($"Script terminé avec code: {exitCode}");
                if (_stdoutEncodingFixCount + _stderrEncodingFixCount > 0)
                {
                    App.LogMessage($"[ENCODING] source=powershellservice.summary stdout_fixes={_stdoutEncodingFixCount} stderr_fixes={_stderrEncodingFixCount}");
                }

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
                var proc = _currentProcess;
                if (proc != null)
                {
                    int pid = -1;
                    long elapsedMs = -1;
                    try { pid = proc.Id; } catch { }
                    try { elapsedMs = (long)(DateTime.Now - proc.StartTime).TotalMilliseconds; } catch { }
                    proc.Kill(true);
                    int exitCode = -1;
                    try { exitCode = proc.ExitCode; } catch { }
                    App.LogMessage($"[PS] Process killed. PID={pid} ExitCode={exitCode} ElapsedMs={elapsedMs}");
                }
            }
            catch { }
        }

        private void ProcessOutput(string line)
        {
            OutputReceived?.Invoke(line);

            // Parse TOTAL_STEPS marker dynamically from PS script output
            var totalStepsMatch = _totalStepsRegex.Match(line);
            if (totalStepsMatch.Success && int.TryParse(totalStepsMatch.Groups["steps"].Value, out var parsedSteps) && parsedSteps > 0)
            {
                _scriptTotalSteps = parsedSteps;
                App.LogMessage($"[PS] Dynamic TOTAL_STEPS parsed: {parsedSteps}");
            }

            if (TryParseLiveMarker(line, out var liveMessage))
            {
                StepChanged?.Invoke(liveMessage);
                return;
            }

            if (TryParseMachineProgressMarker(line, out var machineMarker))
            {
                _receivedRealProgressMarker = true;
                var machinePercent = machineMarker.Percent ?? (
                    machineMarker.Done.HasValue && machineMarker.Total.HasValue && machineMarker.Total.Value > 0
                        ? (int)Math.Round(Math.Min(machineMarker.Done.Value, machineMarker.Total.Value) / (double)machineMarker.Total.Value * 100d)
                        : (int?)null);

                if (machinePercent.HasValue)
                {
                    _simulatedProgress = Math.Min(Math.Max(0, machinePercent.Value), 100);
                    ProgressChanged?.Invoke(_simulatedProgress);
                }
                else
                {
                    ProgressChanged?.Invoke(IndeterminateProgress);
                }

                if (!string.IsNullOrWhiteSpace(machineMarker.Section))
                    StepChanged?.Invoke(machineMarker.Section);
                else if (!string.IsNullOrWhiteSpace(machineMarker.Phase))
                    StepChanged?.Invoke(machineMarker.Phase);
                else if (!string.IsNullOrWhiteSpace(machineMarker.Message))
                    StepChanged?.Invoke(machineMarker.Message);
                return;
            }

            var structured = StructuredRegex.Match(line);
            if (structured.Success)
            {
                var type = structured.Groups["type"].Value.Trim().ToUpperInvariant();
                var section = structured.Groups["section"].Value.Trim();
                var rest = structured.Groups["rest"].Success ? structured.Groups["rest"].Value.Trim() : string.Empty;

                if (type == "PROGRESS" && TryParseStructuredProgress(rest, out var current, out var total, out var explicitPercent))
                {
                    _receivedRealProgressMarker = true;
                    var normalized = explicitPercent ?? (int)Math.Round(Math.Min(current, total) / (double)total * 100);
                    _simulatedProgress = Math.Min(Math.Max(0, normalized), 100);
                    ProgressChanged?.Invoke(_simulatedProgress);
                    StepChanged?.Invoke(section);
                    return;
                }

                if (type is "STATUS" or "SECTION")
                {
                    StepChanged?.Invoke(section);
                    return;
                }

                if (type == "DONE")
                {
                    StepChanged?.Invoke($"{section} terminé");
                    return;
                }
            }

            // Legacy fallback markers
            var progressMatch = ProgressRegex.Match(line);
            if (progressMatch.Success && int.TryParse(progressMatch.Groups[1].Value, out var progressStep))
            {
                _receivedRealProgressMarker = true;
                var normalized = _scriptTotalSteps > 0
                    ? (int)Math.Round(Math.Min(progressStep, _scriptTotalSteps) / (double)_scriptTotalSteps * 100)
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
        }

        private static bool TryParseLiveMarker(string line, out string message) =>
            ProgressMarkerParser.TryParseLive(line, out message);

        private static bool TryParseMachineProgressMarker(string line, out MachineProgressMarker marker)
        {
            marker = default;
            if (!ProgressMarkerParser.TryParseProgress(line, out var parsed))
                return false;

            marker = new MachineProgressMarker
            {
                Phase = parsed.Phase,
                Section = parsed.Section,
                Message = parsed.Message,
                Done = parsed.Done,
                Total = parsed.Total,
                Percent = parsed.Percent
            };
            return true;
        }

        private static bool TryParseStructuredProgress(string rest, out int current, out int total, out int? explicitPercent)
        {
            current = 0;
            total = 0;
            explicitPercent = null;

            if (string.IsNullOrWhiteSpace(rest))
                return false;

            var segments = rest.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                var countParts = segments[0].Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (countParts.Length == 2 &&
                    int.TryParse(countParts[0], out var parsedCurrent) &&
                    int.TryParse(countParts[1], out var parsedTotal))
                {
                    current = parsedCurrent;
                    total = parsedTotal;
                }
            }

            var percentMatch = _percentRegex.Match(rest);
            if (percentMatch.Success &&
                int.TryParse(percentMatch.Groups["pct"].Value, out var parsedPercent))
            {
                explicitPercent = Math.Max(0, Math.Min(100, parsedPercent));
            }

            return total > 0;
        }

        private void SimulateProgress()
        {
            // If the script emits real PROGRESS markers, we trust them and do nothing here.
            // If it doesn't, we emit IndeterminateProgress so the UI shows an honest spinner
            // instead of a fabricated percentage.
            if (_receivedRealProgressMarker) return;
            ProgressChanged?.Invoke(IndeterminateProgress);
        }

        private readonly struct MachineProgressMarker
        {
            public string Phase { get; init; }
            public string Section { get; init; }
            public string Message { get; init; }
            public int? Done { get; init; }
            public int? Total { get; init; }
            public int? Percent { get; init; }
        }
    }
}
