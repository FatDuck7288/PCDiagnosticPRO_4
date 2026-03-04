using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Data;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.Services.NetworkDiagnostics;
using PCDiagnosticPro.Themes;
using PCDiagnosticPro.Views;

namespace PCDiagnosticPro.ViewModels
{
    public partial class MainViewModel
    {

        private async Task StartScanAsync()
        {
            App.BootLog("First scan started");

            lock (_scanLock)
            {
                if (_scanProcess != null && !_scanProcess.HasExited)
                {
                    App.LogMessage("Scan déjà en cours");
                    return;
                }
            }

            // VÉRIFICATION MODE ADMIN - Proposer relance si non-admin
            _skipHardwareSensors = false; // Reset at each scan start
            if (!Services.AdminHelper.IsRunningAsAdmin())
            {
                App.LogMessage("[Admin] Application non en mode administrateur");
                StatusMessage = GetString("AdminRequiredWarning");

                // Show the consent dialog with 3 choices
                var consentDialog = new Views.ScanConsentDialog();
                try { consentDialog.Owner = Application.Current.MainWindow; } catch { }
                var dialogResult = consentDialog.ShowDialog();

                if (dialogResult != true || consentDialog.UserChoice == null)
                {
                    App.LogMessage("[Admin] Utilisateur a annulé le scan");
                    return;
                }

                if (consentDialog.UserChoice == "UAC")
                {
                    App.LogMessage("[Admin] Utilisateur demande relance en admin (UAC)");
                    Services.AdminHelper.RestartAsAdmin();
                    return;
                }

                // "Limited" mode - continue without full hardware sensors
                _skipHardwareSensors = true;
                App.LogMessage("[Admin] Utilisateur continue en mode limité (sans capteurs matériels complets)");
            }

            try
            {
                // Mise à jour immédiate de l'état pour que l'UI réagisse (évite freeze perçu au clic)
                ScanState = "Scanning";
                StatusMessage = GetString("StatusScanning");
                OnPropertyChanged(nameof(IsScanning));
                OnPropertyChanged(nameof(CanStartScan));

                // Résolution des chemins en arrière-plan : thread UI libre, l’UI peut traiter le BeginInvoke ci‑dessus
                var resolvedScriptPath = ResolveScriptPath();
                if (!string.IsNullOrWhiteSpace(resolvedScriptPath))
                    _scriptPath = resolvedScriptPath;

                var outputDir = string.IsNullOrWhiteSpace(ReportDirectory) ? _reportsDir : ReportDirectory;
                var powerShellExe = ResolvePowerShellExecutable();
                var scriptExists = !string.IsNullOrWhiteSpace(_scriptPath) && File.Exists(_scriptPath);
                var dirExists = Directory.Exists(outputDir);

                if (!scriptExists)
                {
                    ErrorMessage = $"Script introuvable";
                    StatusMessage = GetString("StatusScriptMissing");
                    ScanState = "Error";
                    App.LogMessage($"Script non trouvé: {_scriptPath}");
                    App.LogMessage($"BaseDir: {_baseDir}");
                    App.LogMessage($"CurrentDirectory: {Environment.CurrentDirectory}");
                    return;
                }

                _resultJsonPath = Path.Combine(outputDir, "scan_result.json");
                _reportParserService.ReportDirectory = outputDir;

                if (!dirExists)
                {
                    try
                    {
                        Directory.CreateDirectory(outputDir);
                    }
                    catch (Exception ex)
                    {
                        ErrorMessage = $"Impossible de créer le dossier Rapports: {ex.Message}";
                        StatusMessage = GetString("StatusFolderError");
                        ScanState = "Error";
                        return;
                    }
                }

                if (!IsAdmin)
                {
                    StatusMessage = GetString("AdminRequiredWarning");
                    App.LogMessage("Scan lancé sans droits administrateur.");
                }

                if (string.IsNullOrWhiteSpace(powerShellExe))
                {
                    ErrorMessage = "PowerShell introuvable";
                    StatusMessage = GetString("StatusPowerShellMissing");
                    ScanState = "Error";
                    App.LogMessage("PowerShell introuvable (powershell.exe/pwsh.exe).");
                    return;
                }

                App.LogMessage($"[Scan] PowerShell exe: {powerShellExe}");
                App.LogMessage($"[Scan] Script path: {_scriptPath}");
                AddLiveFeedItem("[INFO] PowerShell | Exécution du script de collecte système (QuickScan)");
                App.LogMessage($"[Scan] Output dir: {outputDir}");

                // Laisser l'UI se mettre à jour avant le gros bloc Clear/Refresh
                // Yield so the UI can paint before the heavy Clear/Refresh block (fixes empty/frozen window).
                if (Application.Current?.Dispatcher != null)
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                else
                    await Task.Yield();
                App.LogMessage($"=== DÉMARRAGE SCAN ===");
                WmiQueryRunner.ClearErrors();
                UpdateProgress(0, "Scan reset", allowDecrease: true);
                ResetScanProgressEngine();
                ProgressCount = 0;
                CurrentStep = GetString("InitStep");
                CurrentSection = string.Empty;
                _lastPowerShellSection = string.Empty;
                _powerShellCollectorPercent = 0;
                _stdoutEncodingFixCount = 0;
                _stderrEncodingFixCount = 0;
                _activeRunId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
                lock (_scanStepLock)
                {
                    _scanSteps.Clear();
                }
                App.LogMessage($"[RunId:{_activeRunId}] Scan pipeline started");
                try
                {
                    var runFolder = Services.ScanStorageService.EnsureRunFolder(_activeRunId);
                    App.LogMessage($"[RunId:{_activeRunId}] Canonical run folder ready: {runFolder}");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[RunId:{_activeRunId}] EnsureRunFolder failed: {ex.Message}");
                }

                // Persist scan start (Status=Running survives app crash/kill)
                PersistScanMetaSafe(new Models.ScanMeta
                {
                    RunId       = _activeRunId,
                    StartTime   = DateTime.UtcNow,
                    MachineName = Environment.MachineName,
                    Status      = Models.ScanStatus.Running,
                    AppVersion  = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "unknown"
                }, "scan_start");
                OnPropertyChanged(nameof(CurrentSectionDisplay));
                StatusMessage = GetString("StatusScanning");
                ErrorMessage = string.Empty;
                ResultsMessage = string.Empty;
                LiveFeedItems.Clear();
                LiveFeedEntries.Clear();
                _filteredLiveFeedView?.Refresh();
                ScanItems.Clear();
                ResultSections.Clear();
                InitializeSectionPhases();
                OnPropertyChanged(nameof(HasResultSections));
                ScanResult = null;
                _combinedJsonPath = string.Empty;
                SetCombinedJsonContent(null, null);
                _lastRunStatus = null;
                ContractGateBannerText = string.Empty;
                ContractGateBannerDetails = string.Empty;
                _cancelHandled = false;
                // Second yield so the UI can paint the cleared feed and phase list before process start.
                if (Application.Current?.Dispatcher != null)
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                else
                    await Task.Yield();
                _scanStopwatch.Restart();
                _scanTimingTracker = new ScanTimingTracker(_activeRunId);
                _scanTimingTracker.StartPhase("PowerShell", "PS");
                _liveFeedTimer.Start();
                _scanStartTime = DateTimeOffset.Now;
                _jsonPathFromOutput = null;
                _jsonCompletionMarkerPath = Path.Combine(outputDir, $"scan_result_{_activeRunId}.ready");
                try
                {
                    if (!string.IsNullOrWhiteSpace(_jsonCompletionMarkerPath) && File.Exists(_jsonCompletionMarkerPath))
                        File.Delete(_jsonCompletionMarkerPath);
                }
                catch (Exception markerCleanupEx)
                {
                    App.LogMessage($"[Marker] Cleanup warning: {markerCleanupEx.Message}");
                }
                AddLiveFeedItem(GetString("LiveFeed_PhaseStart_PowerShell"));
                BeginScanProgressPhase(
                    ScanProgressPhase.PowerShellScan,
                    GetString("PhaseLabel_PowerShell"),
                    GetString("LiveFeed_PhaseStart_PowerShell"),
                    indeterminate: true);
                _scanCts = new CancellationTokenSource();

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();
                _scanOutputBuilder = outputBuilder;
                _scanErrorBuilder = errorBuilder;
                lock (_pendingLinesLock)
                {
                    _pendingOutputLines.Clear();
                    _pendingErrorLines.Clear();
                    _pendingFlushScheduled = false;
                }
                var scriptArgs = new StringBuilder();
                scriptArgs.Append("-NoProfile -ExecutionPolicy Bypass ");
                scriptArgs.Append($"-File \"{_scriptPath}\" ");
                scriptArgs.Append($"-OutputDir \"{outputDir}\" ");
                scriptArgs.Append("-QuickScan ");
                scriptArgs.Append($"-RunId \"{_activeRunId}\" ");
                if (!string.IsNullOrWhiteSpace(_jsonCompletionMarkerPath))
                    scriptArgs.Append($"-CompletionMarkerPath \"{_jsonCompletionMarkerPath}\" ");
                if (_allowExternalNetworkTests)
                    scriptArgs.Append("-AllowExternalNetworkTests ");

                var startInfo = new ProcessStartInfo
                {
                    FileName = powerShellExe,
                    Arguments = scriptArgs.ToString().Trim(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                // Démarrer le processus en arrière-plan pour ne pas bloquer l'UI.
                // Use a local variable and only assign to field after successful Start()
                // to ensure Dispose is called if Start() throws.
                var process = new Process { StartInfo = startInfo };
                process.EnableRaisingEvents = true;
                // Batch output/error lines and flush on UI thread in one callback to avoid saturating the UI (fixes scan freeze).
                process.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    var normalizedLine = TextEncodingNormalizer.NormalizeIfCorrupted(e.Data);
                    if (!string.Equals(e.Data, normalizedLine, StringComparison.Ordinal))
                    {
                        _stdoutEncodingFixCount++;
                        if (_stdoutEncodingFixCount <= 3)
                            App.LogMessage($"[ENCODING] source=ps.stdout normalized=true sample={_stdoutEncodingFixCount}");
                    }
                    lock (_pendingLinesLock)
                    {
                        _pendingOutputLines.Add(normalizedLine);
                        if (_pendingFlushScheduled) return;
                        _pendingFlushScheduled = true;
                    }
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPendingScanOutput));
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    var normalizedLine = TextEncodingNormalizer.NormalizeIfCorrupted(e.Data);
                    if (!string.Equals(e.Data, normalizedLine, StringComparison.Ordinal))
                    {
                        _stderrEncodingFixCount++;
                        if (_stderrEncodingFixCount <= 3)
                            App.LogMessage($"[ENCODING] source=ps.stderr normalized=true sample={_stderrEncodingFixCount}");
                    }
                    lock (_pendingLinesLock)
                    {
                        _pendingErrorLines.Add(normalizedLine);
                        if (_pendingFlushScheduled) return;
                        _pendingFlushScheduled = true;
                    }
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPendingScanOutput));
                };
                try
                {
                    process.Start();
                }
                catch
                {
                    process.Dispose();
                    throw;
                }
                _scanProcess = process;
                _scanProcess.BeginOutputReadLine();
                _scanProcess.BeginErrorReadLine();

                IsScanProgressIndeterminate = true; // Start indeterminate until real PROGRESS markers arrive
                StartScanProgressTimer(0);
                SetSectionPhase(0, "Running");

                // Attendre la fin du processus
                var timedOut = false;
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_scanCts.Token, timeoutCts.Token);

                try
                {
                    await _scanProcess.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (timeoutCts.IsCancellationRequested && !_scanCts.IsCancellationRequested)
                    {
                        timedOut = true;
                    }
                    else
                    {
                        throw;
                    }
                }

                // FIX #1: Ne pas arrêter le stopwatch ici - il doit continuer jusqu'à FinalizeScan()
                // _scanStopwatch.Stop();
                // FIX #6: DO NOT stop live feed timer here - it should continue until report generation is complete
                // _liveFeedTimer will be stopped at the end of FinalizeScan() when everything is truly finished

                if (timedOut)
                {
                    App.LogMessage("Timeout atteint lors du scan PowerShell.");
                    errorBuilder.AppendLine("Timeout atteint lors du scan PowerShell.");
                    try
                    {
                        _scanProcess.Kill(true);
                    }
                    catch
                    {
                        // Ignorer
                    }
                }

                var exitCode = _scanProcess.ExitCode;
                _scanTimingTracker?.EndPhase("PowerShell", exitCode == 0);

                if (exitCode != 0 && errorBuilder.Length > 0)
                {
                    App.LogMessage($"Script terminé avec erreur: {errorBuilder}");
                }

                AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_PowerShell"));
                App.LogMessage($"Scan terminé. ExitCode={exitCode}");
                CompleteScanProgressPhase(ScanProgressPhase.PowerShellScan, GetString("LiveFeed_PhaseEnd_PowerShell"));
                SetSectionPhase(0, "Done");
                BeginScanProgressPhase(
                    ScanProgressPhase.Sensors,
                    GetString("PhaseLabel_Capteurs"),
                    GetString("LiveFeed_PhaseStart_Capteurs"));
                
                // === OPTIMISATION: Phases 1-3 en parallèle (Sensors, Counters, Signals) ===
                AddLiveFeedItem(GetString("LiveFeed_PhaseStart_Capteurs"));
                AddLiveFeedItem(GetString("LiveFeed_PhaseStart_Compteurs"));
                AddLiveFeedItem(GetString("LiveFeed_PhaseStart_Signaux"));
                SetSectionPhase(1, "Running");
                SetSectionPhase(2, "Running");
                SetSectionPhase(3, "Running");
                ReportScanProgressStep(
                    section: GetString("PhaseLabel_Capteurs"),
                    message: "Collecte C# en cours",
                    explicitPercent: 10);

                HardwareSensorsResult sensorsResult;
                PerfCounterCollector.PerfCounterResult? perfResult = null;
                DiagnosticsSignals.DiagnosticSignalsResult? signalsResult = null;

                try
                {
                    App.LogMessage("[Parallel Collection] Démarrage collecte parallèle Sensors/Counters/Signals");
                    
                    // Préparer le SignalsOrchestrator avant le parallélisme
                    var signalsOrchestrator = new DiagnosticsSignals.SignalsOrchestrator();
                    signalsOrchestrator.SetAllowExternalNetworkTests(_allowExternalNetworkTests);

                    // Respect user setting: when hardware monitoring is enabled, verify Defender exclusion
                    // before using LHM (which loads WinRing0 kernel driver and triggers Defender without exclusion).
                    // IsUnsafeHardwareMonitoringAllowedAsync checks exclusion, logs, and disables HW monitoring if missing.
                    var unsafeMonitoringAllowed = !_skipHardwareSensors && await IsUnsafeHardwareMonitoringAllowedAsync();
                    _hardwareSensorsCollector.ForceUnsafeMode = unsafeMonitoringAllowed;

                    App.LogMessage($"[Parallel Collection] Sensor mode: " + (unsafeMonitoringAllowed ? "LHM (LibreHardwareMonitor)" : "SAFE (WMI/NVML)") +
                                   $" [enableHW={_enableHardwareMonitoring}, skip={_skipHardwareSensors}, admin={IsAdmin}]");
                    AddLiveFeedItem("[SECTION] Capteurs matériels | Collecte température hardware (mode sécurisé)");
                    AddLiveFeedItem("[SECTION] Performances temps réel | Echantillonnage des compteurs système");
                    AddLiveFeedItem("[SECTION] Stabilité et intégrité | Corrélation des signaux de diagnostic");

                    _scanTimingTracker?.StartPhase("Sensors", "C#");
                    _scanTimingTracker?.StartPhase("Counters", "C#");
                    _scanTimingTracker?.StartPhase("Signals", "C#");
                    // Lancer les 3 collecteurs en parallèle
                    var sensorsTask = Task.Run(() => _hardwareSensorsCollector.CollectAsync(_scanCts.Token), _scanCts.Token);
                    var countersTask = Task.Run(() => PerfCounterCollector.CollectAsync(_scanCts.Token), _scanCts.Token);
                    var signalsTask = Task.Run(() => signalsOrchestrator.CollectAllAsync(_scanCts.Token), _scanCts.Token);
                    
                    // Attendre que tous les collecteurs terminent
                    await Task.WhenAll(sensorsTask, countersTask, signalsTask);
                    
                    // Récupérer les résultats
                    sensorsResult = await sensorsTask;
                    perfResult = await countersTask;
                    signalsResult = await signalsTask;
                    _scanTimingTracker?.EndPhase("Sensors", true);
                    _scanTimingTracker?.EndPhase("Counters", perfResult != null);
                    _scanTimingTracker?.EndPhase("Signals", signalsResult?.SuccessCount > 0);
                    
                    _lastSensorsResult = sensorsResult;
                    _lastPerfCounterResult = perfResult;
                    _lastDiagnosticSignals = signalsResult;
                    
                    var (avail, total) = sensorsResult.GetAvailabilitySummary();
                    App.LogMessage($"[Parallel Collection] Terminé: Sensors {avail}/{total}, Counters OK, Signals {signalsResult?.SuccessCount ?? 0}");
                    
                    // Check for security blocking
                    if (sensorsResult.BlockedBySecurity)
                    {
                        App.LogMessage($"[Sensors] âš ï¸ BLOCKED BY SECURITY: {sensorsResult.BlockingMessage}");
                    }
                    
                    // Log counters
                    if (perfResult is not null)
                    {
                        App.LogMessage($"[PerfCounters] CPU={perfResult.CpuPercent:F1}%, Mem={perfResult.MemoryAvailableMB:F0}MB, DiskTime={perfResult.DiskTimePercent:F1}%");
                    }
                    
                    // Log signals
                    if (signalsResult is not null)
                    {
                        App.LogMessage($"[DiagnosticSignals] Collected: {signalsResult.SuccessCount} success, {signalsResult.FailCount} fail, {signalsResult.TotalDurationMs}ms");
                    }
                    
                    // Notify UI of sensor blocking status
                    NotifySensorBlockingChanged();
                    
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[Parallel Collection] Erreur: {ex.Message}. Fallback mode séquentiel.");
                    
                    // Fallback séquentiel si la collecte parallèle échoue
                    try
                    {
                        sensorsResult = await _hardwareSensorsCollector.CollectAsync(_scanCts.Token);
                        _lastSensorsResult = sensorsResult;
                    }
                    catch (Exception exSensors)
                    {
                        App.LogMessage($"[Sensors Fallback] Erreur: {exSensors.Message}");
                        sensorsResult = new HardwareSensorsResult();
                    }
                    
                    try
                    {
                        perfResult = await PerfCounterCollector.CollectAsync(_scanCts.Token);
                        _lastPerfCounterResult = perfResult;
                    }
                    catch (Exception exPerf)
                    {
                        App.LogMessage($"[Counters Fallback] Erreur: {exPerf.Message}");
                        _lastPerfCounterResult = null;
                    }
                    
                    try
                    {
                        var signalsOrchestrator = new DiagnosticsSignals.SignalsOrchestrator();
                        signalsOrchestrator.SetAllowExternalNetworkTests(_allowExternalNetworkTests);
                        signalsResult = await signalsOrchestrator.CollectAllAsync(_scanCts.Token);
                        _lastDiagnosticSignals = signalsResult;
                    }
                    catch (Exception exSignals)
                    {
                        App.LogMessage($"[Signals Fallback] Erreur: {exSignals.Message}");
                        _lastDiagnosticSignals = null;
                    }
                }

                SetSectionPhase(1, "Done");
                SetSectionPhase(2, "Done");
                SetSectionPhase(3, "Done");
                AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_Capteurs"));
                AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_Compteurs"));
                AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_Signaux"));
                ReportScanProgressStep(
                    section: GetString("PhaseLabel_Capteurs"),
                    message: "Collecte système terminée",
                    explicitPercent: 55);

                // Extract Kernel Power data for the detail window
                _kernelPowerData = ExtractKernelPowerData(signalsResult);
                
                // Phase 4: Télémetrie (Process Telemetry)
                AddLiveFeedItem(GetString("LiveFeed_PhaseStart_Telemetrie"));
                SetSectionPhase(4, "Running");

                // Start performance timeseries in parallel (15s sampling) so it overlaps with other collectors
                _perfTimeseriesTask = PerformanceTimeseriesCollector.CollectAsync(PerformanceTimeseriesCollector.DefaultIntervalSeconds, _scanCts.Token);

                // === PHASE 2D: Process Telemetry C# Fallback (si PS a échoué) ===
                _scanTimingTracker?.StartPhase("ProcessTelemetry", "C#");
                try
                {
                    ReportScanProgressStep(
                        section: GetString("PhaseLabel_Telemetrie"),
                        message: "Collecte télémétrie en cours",
                        explicitPercent: 70);
                    var processCollector = new ProcessTelemetryCollector();
                    _lastProcessTelemetry = await processCollector.CollectAsync(_scanCts.Token);
                    App.LogMessage($"[ProcessTelemetry] Collected: {_lastProcessTelemetry.TotalProcessCount} processes, available={_lastProcessTelemetry.Available}");
                    
                    // Notify UI of new process telemetry data
                    NotifyProcessTelemetryChanged();
                }
                catch (Exception ex)
                {
                    _lastProcessTelemetry = null;
                    App.LogMessage($"[ProcessTelemetry] Erreur: {ex.Message}");
                }
                _scanTimingTracker?.EndPhase("ProcessTelemetry", _lastProcessTelemetry?.Available ?? false);

                // === PHASE 2E: Network Diagnostics Complets (internet autorisé) ===
                try
                {
                    AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_Telemetrie"));
                    SetSectionPhase(4, "Done");
                    
                    // Phase 5: Réseau (Network)
                    _scanTimingTracker?.StartPhase("Network", "C#");
                    AddLiveFeedItem(GetString("LiveFeed_PhaseStart_Reseau"));
                    SetSectionPhase(5, "Running");
                    ReportScanProgressStep(
                        section: GetString("PhaseLabel_Reseau"),
                        message: "Diagnostic réseau en cours",
                        explicitPercent: 80);
                    var networkCollector = new NetworkDiagnosticsCollector();
                    _lastNetworkDiagnostics = await networkCollector.CollectAsync(_scanCts.Token);
                    App.LogMessage($"[NetworkDiagnostics] Completed: latency={_lastNetworkDiagnostics.OverallLatencyMsP50}ms, loss={_lastNetworkDiagnostics.OverallLossPercent}%");
                    
                    // Notify UI of new network diagnostics data
                    NotifyNetworkDiagnosticsChanged();
                    
                }
                catch (Exception ex)
                {
                    _lastNetworkDiagnostics = null;
                    App.LogMessage($"[NetworkDiagnostics] Erreur: {ex.Message}");
                }
                _scanTimingTracker?.EndPhase("Network", _lastNetworkDiagnostics != null);
                AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_Reseau"));
                SetSectionPhase(5, "Done");
                
                // Phase 6: Rapport (Report generation)
                AddLiveFeedItem(GetString("LiveFeed_PhaseStart_Rapport"));
                SetSectionPhase(6, "Running");

                // === PHASE 2F: Driver inventory + updates + security in parallel ===
                ReportScanProgressStep(
                    section: GetString("PhaseLabel_Rapport"),
                    message: "Préparation du rapport",
                    explicitPercent: 90);
                AddLiveFeedItem("[SECTION] Rapport | Enrichissement C# (pilotes, mises à jour, sécurité)");

                var driverTask = Task.Run(async () =>
                {
                    _scanTimingTracker?.StartPhase("DriverInventory", "C#");
                    try
                    {
                        var driverCollector = new DriverInventoryCollector();
                        _lastDriverInventory = await driverCollector.CollectAsync(
                            _scanCts.Token,
                            includeUpdateLookup: true,
                            onlineUpdateSearch: _allowExternalNetworkTests).ConfigureAwait(false);
                        App.LogMessage($"[DriverInventory] Completed: total={_lastDriverInventory.TotalCount}, available={_lastDriverInventory.Available}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _lastDriverInventory = null;
                        App.LogMessage($"[DriverInventory] Erreur: {ex.Message}");
                    }
                    finally
                    {
                        _scanTimingTracker?.EndPhase("DriverInventory", _lastDriverInventory != null);
                    }
                }, _scanCts.Token);

                var windowsUpdateTask = Task.Run(async () =>
                {
                    _scanTimingTracker?.StartPhase("WindowsUpdate", "C#");
                    try
                    {
                        var updateCollector = new WindowsUpdateCollector();
                        _lastWindowsUpdateResult = await updateCollector.CollectAsync(_scanCts.Token, _allowExternalNetworkTests).ConfigureAwait(false);
                        App.LogMessage($"[WindowsUpdate] Completed: pending={_lastWindowsUpdateResult.PendingCount}, available={_lastWindowsUpdateResult.Available}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _lastWindowsUpdateResult = null;
                        App.LogMessage($"[WindowsUpdate] Erreur: {ex.Message}");
                    }
                    finally
                    {
                        _scanTimingTracker?.EndPhase("WindowsUpdate", _lastWindowsUpdateResult != null);
                    }
                }, _scanCts.Token);

                var securityTask = Task.Run(async () =>
                {
                    _scanTimingTracker?.StartPhase("SecurityInfo", "C#");
                    try
                    {
                        var securityCollector = new SecurityInfoCollector();
                        _lastSecurityInfo = await securityCollector.CollectAsync(_scanCts.Token).ConfigureAwait(false);
                        App.LogMessage($"[SecurityInfo] Completed: BitLocker={_lastSecurityInfo.BitLockerStatus}, RDP={_lastSecurityInfo.RdpStatus}, SMBv1={_lastSecurityInfo.SmbV1Status}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _lastSecurityInfo = null;
                        App.LogMessage($"[SecurityInfo] Erreur: {ex.Message}");
                    }
                    finally
                    {
                        _scanTimingTracker?.EndPhase("SecurityInfo", _lastSecurityInfo != null);
                    }
                }, _scanCts.Token);

                await Task.WhenAll(driverTask, windowsUpdateTask, securityTask).ConfigureAwait(false);
                ReportScanProgressStep(
                    section: GetString("PhaseLabel_Rapport"),
                    message: "Enrichissements du rapport terminés",
                    explicitPercent: 100);
                CompleteScanProgressPhase(ScanProgressPhase.Sensors, "Collecte C# terminée");
                BeginScanProgressPhase(
                    ScanProgressPhase.MergeJson,
                    "Merge JSON",
                    "Assemblage des données de scan");

                // Event logs détaillés + SMART attributs + Minidumps (C#)
                try
                {
                    var eventLogTask = EventLogDetailedCollector.CollectAsync(EventLogDetailedCollector.DefaultMaxEventsPerLog, EventLogDetailedCollector.DefaultHoursBack, _scanCts.Token);
                    var smartTask = SmartAttributesCollector.CollectAsync(_scanCts.Token);
                    var minidumpTask = MinidumpListCollector.CollectAsync(MinidumpListCollector.DefaultMaxDumps, _scanCts.Token);
                    await Task.WhenAll(eventLogTask, smartTask, minidumpTask).ConfigureAwait(false);
                    _lastEventLogsDetailed = await eventLogTask;
                    _lastSmartAttributes = await smartTask;
                    _lastMinidumpsDetailed = await minidumpTask;
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[ExtendedCollectors] Error: {ex.Message}");
                }
                if (_perfTimeseriesTask != null)
                {
                    try
                    {
                        _lastPerformanceTimeseriesSummary = await _perfTimeseriesTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        App.LogMessage($"[PerfTimeseries] Await error: {ex.Message}");
                    }
                    _perfTimeseriesTask = null;
                }

                _resultJsonPath = await ResolveResultJsonPathAsync(outputDir, _scanStartTime, _scanCts.Token);
                _scanTimingTracker?.StartPhase("WriteCombined", "C#");
                await WriteCombinedResultAsync(outputDir, sensorsResult);
                _scanTimingTracker?.EndPhase("WriteCombined", true);
                ReportScanProgressStep(
                    section: "Merge JSON",
                    message: "Résolution JSON terminée",
                    explicitPercent: 100);
                CompleteScanProgressPhase(ScanProgressPhase.MergeJson, "Merge JSON terminé");
                BeginScanProgressPhase(
                    ScanProgressPhase.ReportBuild,
                    GetString("PhaseLabel_Rapport"),
                    "Construction du rapport");

                // Lire le JSON
                if (!string.IsNullOrWhiteSpace(_resultJsonPath) && File.Exists(_resultJsonPath))
                {
                    _scanTimingTracker?.StartPhase("LoadJson", "C#");
                    await LoadJsonResultAsync();
                    _scanTimingTracker?.EndPhase("LoadJson", true);
                }
                else
                {
                    ErrorMessage = "Rapport introuvable";
                    var searchDirs = string.Join(" | ", GetCandidateReportDirectories(outputDir));
                    var patterns = string.Join(", ", GetJsonSearchPatterns());
                    ResultsMessage = $"Rapport introuvable. Dossiers: {searchDirs}. Patterns: {patterns}";
                    StatusMessage = GetString("StatusJsonMissing");
                    App.LogMessage($"Rapport JSON introuvable après le scan. Dossiers: {searchDirs}. Patterns: {patterns}");
                    OnScanPipelineCompleted(null, ResultsMessage, GetString("StatusJsonMissing"), forceCompletedStatus: false);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_cancelHandled)
                {
                    ResetAfterCancel();
                    _cancelHandled = true;
                }
                App.LogMessage("Scan annulé");
            }
            catch (Exception ex)
            {
                _scanStopwatch.Stop();
                _liveFeedTimer.Stop();
                StopScanProgressTimer();
                ErrorMessage = ex.Message;
                StatusMessage = GetString("StatusScanError");
                ScanState = "Error";
                App.LogMessage($"Erreur scan: {ex.Message}");

                // Persist failed status to disk history
                if (!string.IsNullOrWhiteSpace(_activeRunId))
                {
                    Services.ScanStorageService.DeleteCombinedJsonIfExists(_activeRunId);
                    Services.ScanStorageService.CleanupRunTempFiles(_activeRunId);

                    var failedMeta = new Models.ScanMeta
                    {
                        RunId           = _activeRunId,
                        StartTime       = _scanStartTime.UtcDateTime,
                        EndTime         = DateTime.UtcNow,
                        MachineName     = Environment.MachineName,
                        Status          = Models.ScanStatus.Failed,
                        DurationSeconds = _scanStopwatch.Elapsed.TotalSeconds,
                        ErrorSummary    = ex.Message,
                        AppVersion      = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "unknown"
                    };

                    PersistScanMetaSafe(failedMeta, "scan_exception");
                    UpsertHistoryItemFromMeta(failedMeta);
                }
            }
            finally
            {
                var totalEncodingFixes = _stdoutEncodingFixCount + _stderrEncodingFixCount;
                if (totalEncodingFixes > 0)
                {
                    App.LogMessage($"[ENCODING] source=powershell.summary stdout_fixes={_stdoutEncodingFixCount} stderr_fixes={_stderrEncodingFixCount}");
                }

                _scanTimingTracker?.FlushToLog();
                _scanTimingTracker = null;
                _scanOutputBuilder = null;
                _scanErrorBuilder = null;
                lock (_scanLock)
                {
                    _scanProcess?.Dispose();
                    _scanProcess = null;
                    _scanCts?.Dispose();
                    _scanCts = null;
                }
            }
        }

        /// <summary>Runs on UI thread; drains pending output/error lines by chunks to keep UI responsive.</summary>
        private void FlushPendingScanOutput()
        {
            if (Application.Current?.Dispatcher.CheckAccess() == false)
            {
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPendingScanOutput));
                return;
            }

            List<string> outputCopy;
            List<string> errorCopy;
            lock (_pendingLinesLock)
            {
                var outputPending = _pendingOutputLines.Count;
                var errorPending = _pendingErrorLines.Count;

                var outputChunk = outputPending > 400 ? MaxOutputLinesPerFlush * 3 :
                                  outputPending > 200 ? MaxOutputLinesPerFlush * 2 :
                                  MaxOutputLinesPerFlush;
                var errorChunk = errorPending > 200 ? MaxErrorLinesPerFlush * 2 : MaxErrorLinesPerFlush;

                var outputTake = Math.Min(outputChunk, outputPending);
                var errorTake = Math.Min(errorChunk, errorPending);

                outputCopy = _pendingOutputLines.Take(outputTake).ToList();
                errorCopy = _pendingErrorLines.Take(errorTake).ToList();

                if (outputTake > 0)
                    _pendingOutputLines.RemoveRange(0, outputTake);
                if (errorTake > 0)
                    _pendingErrorLines.RemoveRange(0, errorTake);

                _pendingFlushScheduled = false;
            }

            if (outputCopy.Count > 0)
                AddLiveFeedItems(outputCopy);

            foreach (var line in outputCopy)
            {
                _scanOutputBuilder?.AppendLine(line);
                ProcessOutputMetadata(line);
            }
            foreach (var line in errorCopy)
            {
                _scanErrorBuilder?.AppendLine(line);
                App.LogMessage($"ERREUR PS: {line}");
            }
            // If more lines arrived during the flush, schedule another flush.
            lock (_pendingLinesLock)
            {
                if (_pendingOutputLines.Count > 0 || _pendingErrorLines.Count > 0)
                {
                    _pendingFlushScheduled = true;
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPendingScanOutput));
                }
            }
        }

        private void ProcessOutputMetadata(string line)
        {
            EncodingCorruptionWatcher.CheckAndLog(line, "powershell.stdout");

            if (TryParseLiveMarker(line, out var liveMessage))
            {
                var section = string.IsNullOrWhiteSpace(CurrentSection) ? GetString("PhaseLabel_PowerShell") : CurrentSection;
                PublishScanStep(
                    MapPowerShellSectionToStep(section),
                    liveMessage,
                    null,
                    ScanStepSeverity.Info,
                    liveMessage);
                AddLiveFeedItem($"[STATUS] {section} | {liveMessage}");
                return;
            }

            if (TryParseMachineProgressMarker(line, out var marker))
            {
                var markerSection = string.IsNullOrWhiteSpace(marker.Section)
                    ? (string.IsNullOrWhiteSpace(marker.Phase)
                        ? GetString("PhaseLabel_PowerShell")
                        : marker.Phase)
                    : marker.Section;

                if (marker.Total.HasValue && marker.Total.Value > 0)
                {
                    ApplyStructuredPowerShellProgress(
                        markerSection,
                        marker.Done ?? ProgressCount,
                        marker.Total.Value,
                        marker.Percent);
                }
                else if (marker.Percent.HasValue)
                {
                    ApplyStructuredPowerShellProgress(
                        markerSection,
                        marker.Done ?? ProgressCount,
                        _totalSteps,
                        marker.Percent);
                }
                else
                {
                    ReportScanProgressStep(
                        section: markerSection,
                        message: marker.Message,
                        indeterminate: true);
                }

                if (!string.IsNullOrWhiteSpace(marker.Message))
                {
                    PublishScanStep(
                        MapPowerShellSectionToStep(markerSection),
                        marker.Message,
                        marker.Total.HasValue && marker.Done.HasValue ? $"{marker.Done}/{marker.Total}" : null,
                        ScanStepSeverity.Info,
                        marker.Message);
                }

                return;
            }

            var structuredMatch = _structuredPattern.Match(line);
            if (structuredMatch.Success)
            {
                var type = structuredMatch.Groups["type"].Value.Trim().ToUpperInvariant();
                var rawSection = structuredMatch.Groups["section"].Value.Trim();
                var rest = structuredMatch.Groups["rest"].Success ? structuredMatch.Groups["rest"].Value.Trim() : string.Empty;
                var stepName = MapPowerShellSectionToStep(rawSection);
                var progressHint = ExtractProgressHint(rest);
                var scriptLine = ShouldShowScriptLine(type, rest) ? SanitizeScriptLine(rest) : null;
                var severity = type switch
                {
                    "ERROR" => ScanStepSeverity.Error,
                    "WARN" => ScanStepSeverity.Warning,
                    _ => ScanStepSeverity.Info
                };

                PublishScanStep(stepName, rest, progressHint, severity, scriptLine);

                if (type == "PROGRESS")
                {
                    if (TryParseStructuredProgress(rest, out var current, out var total, out var percent))
                    {
                        ApplyStructuredPowerShellProgress(rawSection, current, total, percent);
                    }
                }
                else if (type == "DONE")
                {
                    if (IsDynamicSignalsSection(rawSection))
                    {
                        ApplyStructuredPowerShellProgress(rawSection, ProgressCount, _totalSteps, _powerShellCollectorPercent);
                    }
                    else if (IsAdvancedAnalysisSection(rawSection))
                    {
                        ApplyStructuredPowerShellProgress(rawSection, ProgressCount, _totalSteps, _powerShellCollectorPercent);
                    }
                }
            }

            if (line.StartsWith("[OK]", StringComparison.OrdinalIgnoreCase))
            {
                var jsonMatch = Regex.Match(line, @"^\[OK\]\s+JSON:\s+(?<path>.+)$", RegexOptions.IgnoreCase);
                if (jsonMatch.Success)
                {
                    _jsonPathFromOutput = jsonMatch.Groups["path"].Value.Trim();
                    App.LogMessage($"Chemin JSON stdout: {_jsonPathFromOutput}");
                }

                var reportMatch = Regex.Match(line, @"^\[OK\]\s+Rapport:\s+(?<path>.+)$", RegexOptions.IgnoreCase);
                if (reportMatch.Success)
                {
                    var reportPath = reportMatch.Groups["path"].Value.Trim();
                    App.LogMessage($"Rapport créé: {reportPath}");
                }
            }

            // New structured script output format: [DONE] JSON | <path>
            if (structuredMatch.Success &&
                string.Equals(structuredMatch.Groups["type"].Value, "DONE", StringComparison.OrdinalIgnoreCase))
            {
                var doneSection = structuredMatch.Groups["section"].Value.Trim();
                var doneRest = structuredMatch.Groups["rest"].Success ? structuredMatch.Groups["rest"].Value.Trim() : string.Empty;

                if (string.Equals(doneSection, "JSON", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(doneRest))
                {
                    _jsonPathFromOutput = doneRest;
                    App.LogMessage($"Chemin JSON stdout (DONE): {_jsonPathFromOutput}");
                }

                if (string.Equals(doneSection, "Marker", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(doneRest))
                {
                    _jsonCompletionMarkerPath = doneRest;
                    App.LogMessage($"Marker path stdout (DONE): {_jsonCompletionMarkerPath}");
                }
            }

            if (line.StartsWith("[SECTION]", StringComparison.OrdinalIgnoreCase))
            {
                if (structuredMatch.Success)
                {
                    var rawSection = structuredMatch.Groups["section"].Value.Trim();
                    var count = ProgressCount > 0 ? ProgressCount : 0;
                    PublishPowerShellSectionTransition(rawSection, count);
                }
            }
        }

        private void ApplyStructuredPowerShellProgress(string rawSection, int current, int total, int? explicitPercent)
        {
            if (total > 0)
                _totalSteps = total;

            ProgressCount = Math.Max(0, current);
            CurrentSection = TextEncodingNormalizer.NormalizeIfCorrupted(rawSection);
            CurrentStep = CurrentSection;

            var computedPercent = explicitPercent ?? (total > 0
                ? (int)Math.Round((current / (double)total) * 100.0)
                : _powerShellCollectorPercent);
            _powerShellCollectorPercent = Math.Max(_powerShellCollectorPercent, Math.Max(0, Math.Min(100, computedPercent)));
            IsScanProgressIndeterminate = total <= 0 && !explicitPercent.HasValue;

            PublishPowerShellSectionTransition(rawSection, ProgressCount);
            ReportScanProgressStep(
                section: CurrentSection,
                message: $"Progression stdout: {CurrentSection}",
                done: current,
                total: total > 0 ? total : null,
                explicitPercent: _powerShellCollectorPercent,
                indeterminate: IsScanProgressIndeterminate);
        }

        private static bool TryParseStructuredProgress(string rest, out int current, out int total, out int? percent)
        {
            current = 0;
            total = 0;
            percent = null;

            if (string.IsNullOrWhiteSpace(rest))
                return false;

            // Expected format from PS: "14/35 | 40%"
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

            var percentMatch = Regex.Match(rest, @"(?<pct>\d{1,3})\s*%");
            if (percentMatch.Success &&
                int.TryParse(percentMatch.Groups["pct"].Value, out var parsedPct))
            {
                percent = Math.Max(0, Math.Min(100, parsedPct));
            }

            return total > 0;
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

        private readonly struct MachineProgressMarker
        {
            public string Phase { get; init; }
            public string Section { get; init; }
            public string Message { get; init; }
            public int? Done { get; init; }
            public int? Total { get; init; }
            public int? Percent { get; init; }
        }

        private static bool IsDynamicSignalsSection(string section)
        {
            var normalized = TextEncodingNormalizer.NormalizeIfCorrupted(section).ToLowerInvariant();
            return normalized.Contains("signaux dynamiques", StringComparison.Ordinal)
                || normalized.Contains("dynamicsignals", StringComparison.Ordinal);
        }

        private static bool IsAdvancedAnalysisSection(string section)
        {
            var normalized = TextEncodingNormalizer.NormalizeIfCorrupted(section).ToLowerInvariant();
            return normalized.Contains("analyse avancee", StringComparison.Ordinal)
                || normalized.Contains("advancedanalysis", StringComparison.Ordinal);
        }

        private void PublishPowerShellSectionTransition(string rawSection, int count)
        {
            if (string.IsNullOrWhiteSpace(rawSection))
                return;

            if (string.Equals(rawSection, _lastPowerShellSection, StringComparison.OrdinalIgnoreCase))
                return;

            if (!string.IsNullOrWhiteSpace(_lastPowerShellSection))
            {
                AddLiveFeedItem($"[DONE] {_lastPowerShellSection} | Section terminee");
                PublishScanStep(MapPowerShellSectionToStep(_lastPowerShellSection), "Section terminee", null, ScanStepSeverity.Info, null);
            }

            _lastPowerShellSection = rawSection;
            var total = _totalSteps > 0 ? _totalSteps : 0;
            var progressText = total > 0 ? $"{Math.Min(count, total)}/{total}" : count.ToString();
            AddLiveFeedItem($"[SECTION] {rawSection} | Section en cours ({progressText})");
            PublishScanStep(MapPowerShellSectionToStep(rawSection), "Section en cours", progressText, ScanStepSeverity.Info, null);
        }

        private void PublishScanStep(string stepName, string? subStep, string? progressHint, ScanStepSeverity severity, string? scriptLine)
        {
            var normalizedStep = TextEncodingNormalizer.NormalizeIfCorrupted(stepName);
            var normalizedSubStep = string.IsNullOrWhiteSpace(subStep) ? null : TextEncodingNormalizer.NormalizeIfCorrupted(subStep);
            var normalizedHint = string.IsNullOrWhiteSpace(progressHint) ? null : TextEncodingNormalizer.NormalizeIfCorrupted(progressHint);
            var normalizedScript = string.IsNullOrWhiteSpace(scriptLine) ? null : TextEncodingNormalizer.NormalizeIfCorrupted(scriptLine);

            var signature = $"{normalizedStep}|{normalizedSubStep}|{normalizedHint}|{severity}";
            var nowUtc = DateTime.UtcNow;
            if (signature == _lastScanStepSignature && (nowUtc - _lastScanStepEventUtc) < TimeSpan.FromMilliseconds(350))
                return;

            _lastScanStepSignature = signature;
            _lastScanStepEventUtc = nowUtc;

            var step = new ScanStepTrace
            {
                StepName = normalizedStep,
                SubStep = normalizedSubStep,
                ProgressHint = normalizedHint,
                Timestamp = DateTime.Now,
                Severity = severity,
                ScriptLine = normalizedScript
            };

            lock (_scanStepLock)
            {
                _scanSteps.Insert(0, step);
                while (_scanSteps.Count > 400)
                    _scanSteps.RemoveAt(_scanSteps.Count - 1);
            }

            var runPrefix = string.IsNullOrWhiteSpace(_activeRunId) ? "[RunId:N/A]" : $"[RunId:{_activeRunId}]";
            App.LogMessage($"{runPrefix} [ScanStep] {step.StepName} | {step.SubStep ?? "-"} | {step.ProgressHint ?? "-"} | {step.Severity}");

            if (!string.IsNullOrWhiteSpace(step.ScriptLine) && (nowUtc - _lastScanStepUiLogUtc) > TimeSpan.FromSeconds(2.2))
            {
                _lastScanStepUiLogUtc = nowUtc;
                AddLiveFeedItem($"[STATUS] {step.StepName} | {step.ScriptLine}");
            }
        }

        private static string MapPowerShellSectionToStep(string rawSection)
        {
            var normalized = TextEncodingNormalizer.NormalizeIfCorrupted(rawSection);
            var key = normalized.ToLowerInvariant();

            if (key.Contains("reseau") || key.Contains("network") || key.Contains("latence") || key.Contains("dns"))
                return "Réseau";
            if (key.Contains("application") || key.Contains("startup") || key.Contains("task"))
                return "Applications";
            if (key.Contains("driver") || key.Contains("pilote") || key.Contains("device"))
                return "Pilotes";
            if (key.Contains("secur") || key.Contains("security") || key.Contains("defender") || key.Contains("uac"))
                return "Sécurité";
            if (key.Contains("stock") || key.Contains("disk") || key.Contains("smart") || key.Contains("storage"))
                return "Stockage";
            if (key.Contains("memoire") || key.Contains("ram"))
                return "Mémoire";
            if (key.Contains("gpu") || key.Contains("graph"))
                return "GPU";
            if (key.Contains("cpu") || key.Contains("processeur"))
                return "CPU";
            if (key.Contains("event") || key.Contains("stabil") || key.Contains("whea") || key.Contains("kernel"))
                return "Stabilité";
            if (key.Contains("update") || key.Contains("windowsupdate") || key.Contains("rapport") || key.Contains("report"))
                return "Rapport";

            return normalized;
        }

        private static string? ExtractProgressHint(string rest)
        {
            if (string.IsNullOrWhiteSpace(rest))
                return null;

            var countMatch = Regex.Match(rest, @"(?<current>\\d+)\\s*/\\s*(?<total>\\d+)");
            if (countMatch.Success)
                return $"{countMatch.Groups["current"].Value}/{countMatch.Groups["total"].Value}";

            var percentMatch = Regex.Match(rest, @"(?<percent>\\d{1,3})\\s*%");
            if (percentMatch.Success)
                return $"{percentMatch.Groups["percent"].Value}%";

            return null;
        }

        private static bool ShouldShowScriptLine(string type, string rest)
        {
            if (string.IsNullOrWhiteSpace(rest))
                return false;

            if (type != "STATUS" && type != "INFO")
                return false;

            if (rest.Length < 12)
                return false;

            if (rest.Contains("JSON:", StringComparison.OrdinalIgnoreCase) ||
                rest.Contains("Rapport:", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static string? SanitizeScriptLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            var sanitized = line.Trim();
            sanitized = Regex.Replace(sanitized, @"[A-Za-z]:\\\\[^\\s|\""]+", "<path>");
            sanitized = Regex.Replace(sanitized, @"(password|secret|token|apikey)\\s*[:=]\\s*\\S+", "$1=<masqué>", RegexOptions.IgnoreCase);
            sanitized = MultiLineWhitespaceToSingleSpace(sanitized);

            return sanitized.Length > 110 ? sanitized.Substring(0, 107) + "..." : sanitized;
        }

        private static string MultiLineWhitespaceToSingleSpace(string input)
        {
            var compact = input.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            while (compact.Contains("  ", StringComparison.Ordinal))
                compact = compact.Replace("  ", " ", StringComparison.Ordinal);
            return compact.Trim();
        }

        private static string DecorateSectionLabel(string section)
        {
            var emoji = GetSectionEmoji(section);
            return string.IsNullOrWhiteSpace(emoji) ? section : $"{emoji} {section}";
        }

        private static string GetSectionEmoji(string section)
        {
            var s = (section ?? string.Empty).ToLowerInvariant();
            if (s.Contains("ident") || s.Contains("machine")) return "\uD83E\uDDFE";
            if (s.Contains("systeme") || s.Contains("os")) return "\uD83D\uDDA5\uFE0F";
            if (s.Contains("processeur") || s.Contains("cpu")) return "\uD83E\uDDE0";
            if (s.Contains("memoire") || s.Contains("ram")) return "\uD83E\uDDEE";
            if (s.Contains("stockage") || s.Contains("smart")) return "\uD83D\uDCBD";
            if (s.Contains("graphique") || s.Contains("gpu")) return "\uD83C\uDFAE";
            if (s.Contains("reseau") || s.Contains("latence")) return "\uD83C\uDF10";
            if (s.Contains("secur")) return "\uD83D\uDEE1\uFE0F";
            if (s.Contains("service")) return "\uD83E\uDDE9";
            if (s.Contains("demarrage")) return "\uD83D\uDE80";
            if (s.Contains("event") || s.Contains("journal")) return "\uD83D\uDCDC";
            if (s.Contains("update")) return "\uD83D\uDEE0";
            if (s.Contains("audio")) return "\uD83D\uDD0A";
            if (s.Contains("peripher")) return "\uD83D\uDD0C";
            if (s.Contains("application")) return "\uD83D\uDCE6";
            if (s.Contains("process")) return "\u2699\uFE0F";
            if (s.Contains("batterie") || s.Contains("alim")) return "\uD83D\uDD0B";
            if (s.Contains("imprim")) return "\uD83D\uDDA8\uFE0F";
            if (s.Contains("profil")) return "\uD83D\uDC64";
            if (s.Contains("virtual")) return "\uD83E\uDDF1";
            if (s.Contains("restauration")) return "\uD83E\uDDEF";
            if (s.Contains("temp")) return "\uD83C\uDF21\uFE0F";
            if (s.Contains("registre")) return "\uD83D\uDCC2";
            if (s.Contains("integr")) return "\u2705";
            if (s.Contains("dynamique")) return "\uD83D\uDCC8";
            if (s.Contains("analyse")) return "\uD83D\uDD0E";
            if (s.Contains("resume")) return "\uD83D\uDCC4";
            if (s.Contains("ecriture") || s.Contains("json") || s.Contains("rapport")) return "\uD83D\uDCBE";
            return "\u25B6";
        }

        private string? ResolveScriptPath()
        {
            var candidates = new List<string>
            {
                Path.Combine(_baseDir, "Scripts", "Total_PS_PC_Scan_v7.0.ps1"),
                Path.Combine(_baseDir, "Total_PS_PC_Scan_v7.0.ps1"),
                Path.Combine(AppContext.BaseDirectory, "Scripts", "Total_PS_PC_Scan_v7.0.ps1"),
                Path.Combine(AppContext.BaseDirectory, "Total_PS_PC_Scan_v7.0.ps1"),
                // Chemin relatif au répertoire de travail actuel
                Path.Combine(Environment.CurrentDirectory, "Scripts", "Total_PS_PC_Scan_v7.0.ps1"),
                Path.Combine(Environment.CurrentDirectory, "Total_PS_PC_Scan_v7.0.ps1"),
                // Chemin relatif au dossier source (développement)
                Path.Combine(Directory.GetParent(_baseDir)?.Parent?.Parent?.Parent?.FullName ?? _baseDir, "Scripts", "Total_PS_PC_Scan_v7.0.ps1")
            };

            App.LogMessage($"Recherche script dans {candidates.Count} chemins candidats:");
            foreach (var candidate in candidates)
            {
                var exists = File.Exists(candidate);
                App.LogMessage($"  [{(exists ? "OK" : "KO")}] {candidate}");
                if (exists)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string? ResolvePowerShellExecutable()
        {
            var candidates = new List<string>();
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrWhiteSpace(systemDir))
            {
                candidates.Add(Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe"));
            }

            candidates.Add("powershell.exe");

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                candidates.Add(Path.Combine(programFilesX86, "PowerShell", "7", "pwsh.exe"));
            }

            foreach (var candidate in candidates)
            {
                if (Path.IsPathRooted(candidate))
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                else
                {
                    var resolved = FindOnPath(candidate);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }
                }
            }

            return null;
        }

        private static string? FindOnPath(string exeName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv))
            {
                return null;
            }

            foreach (var path in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var candidate = Path.Combine(path.Trim(), exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private void ExtractExplicitNodes(JsonElement psRoot, CombinedScanResult combined, string outputDir)
        {
            try
            {
                // 1. Extract missingData (ROBUST: Array OR Object)
                if (psRoot.TryGetProperty("missingData", out var missingDataEl))
                {
                    ExtractMissingData(missingDataEl, combined);
                }
                
                // 2. Extract metadata (ROBUST: Object check)
                if (psRoot.TryGetProperty("metadata", out var metaEl) && 
                    metaEl.ValueKind == JsonValueKind.Object)
                {
                    combined.Metadata.Version = metaEl.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                    combined.Metadata.RunId = metaEl.TryGetProperty("runId", out var r) ? r.GetString() ?? "" : "";
                    combined.Metadata.Timestamp = metaEl.TryGetProperty("timestamp", out var t) ? t.GetString() ?? "" : "";
                    combined.Metadata.IsAdmin = metaEl.TryGetProperty("isAdmin", out var a) && a.GetBoolean();
                    combined.Metadata.PartialFailure = metaEl.TryGetProperty("partialFailure", out var pf) && pf.GetBoolean();
                    combined.Metadata.DurationSeconds = metaEl.TryGetProperty("durationSeconds", out var d) ? d.GetDouble() : 0;
                }
                
                // 3. Extract findings (ROBUST: Array OR Object)
                if (psRoot.TryGetProperty("findings", out var findingsEl))
                {
                    ExtractFindings(findingsEl, combined);
                }
                
                // 4. Extract errors (ROBUST: Array OR Object)
                if (psRoot.TryGetProperty("errors", out var errorsEl))
                {
                    ExtractErrors(errorsEl, combined);
                }
                
                // 5. Extract sections (ROBUST: Object OR Array)
                if (psRoot.TryGetProperty("sections", out var sectionsEl))
                {
                    ExtractSections(sectionsEl, combined);
                }
                
                // 6. Set paths
                combined.Paths.JsonOutput = _resultJsonPath;
                combined.Paths.CombinedJson = Path.Combine(outputDir, "scan_result_combined.json");
                combined.Paths.UnifiedTxt = Path.Combine(outputDir, $"Rapport_Unifie_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                // 7. Extract NVMe / StorageReliabilityCounter data from SmartDetails section
                ExtractStorageReliability(psRoot, combined);

                // Log to file for debugging
                LogExtractedNodes(combined, outputDir);

                App.LogMessage($"[ExtractNodes] missingData={combined.MissingData.Count}, findings={combined.Findings.Count}, errors={combined.Errors.Count}, sections={combined.Sections.Count} | nvmeDisks={combined.StorageReliability?.DiskCount ?? 0}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ExtractNodes] Erreur extraction: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Extract NVMe/StorageReliabilityCounter data from sections.SmartDetails.nvmeReliability.
        /// Populates CombinedScanResult.StorageReliability with typed entries.
        /// </summary>
        private static void ExtractStorageReliability(JsonElement psRoot, CombinedScanResult combined)
        {
            try
            {
                // Navigate: sections â†’ SmartDetails â†’ nvmeReliability (array)
                if (!psRoot.TryGetProperty("sections", out var sections) ||
                    sections.ValueKind != JsonValueKind.Object) return;

                if (!sections.TryGetProperty("SmartDetails", out var smartSec) &&
                    !sections.TryGetProperty("smartDetails", out smartSec)) return;

                // The section has a 'data' wrapper
                var dataEl = smartSec;
                if (smartSec.ValueKind == JsonValueKind.Object &&
                    smartSec.TryGetProperty("data", out var d))
                    dataEl = d;

                if (!dataEl.TryGetProperty("nvmeReliability", out var nvmeArr) ||
                    nvmeArr.ValueKind != JsonValueKind.Array) return;

                var sourceStr = dataEl.TryGetProperty("nvmeReliabilitySource", out var srcEl)
                    ? srcEl.GetString() ?? "StorageReliabilityCounter"
                    : "StorageReliabilityCounter";

                var result = new PCDiagnosticPro.Models.StorageReliabilityResult { Source = sourceStr };

                foreach (var item in nvmeArr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var entry = new PCDiagnosticPro.Models.NvmeReliabilityEntry
                    {
                        Source = sourceStr
                    };

                    if (item.TryGetProperty("friendlyName",        out var v)) entry.FriendlyName        = v.GetString();
                    if (item.TryGetProperty("serialNumber",        out v)) entry.SerialNumber        = v.GetString();
                    if (item.TryGetProperty("busType",             out v)) entry.BusType             = v.GetString();
                    if (item.TryGetProperty("mediaType",           out v)) entry.MediaType           = v.GetString();
                    if (item.TryGetProperty("healthStatus",        out v)) entry.HealthStatus        = v.GetString();
                    if (item.TryGetProperty("operationalStatus",   out v)) entry.OperationalStatus   = v.GetString();
                    if (item.TryGetProperty("sizeBytes",           out v) && v.ValueKind == JsonValueKind.Number)
                        entry.SizeBytes = v.GetInt64();
                    if (item.TryGetProperty("temperature",         out v) && v.ValueKind == JsonValueKind.Number)
                        entry.TemperatureC = v.GetInt32();
                    if (item.TryGetProperty("wear",                out v) && v.ValueKind == JsonValueKind.Number)
                        entry.WearPercent = v.GetInt32();
                    if (item.TryGetProperty("mediaWearoutIndicator", out v) && v.ValueKind == JsonValueKind.Number)
                        entry.MediaWearoutIndicator = v.GetInt32();
                    if (item.TryGetProperty("readErrorsTotal",     out v) && v.ValueKind == JsonValueKind.Number)
                        entry.ReadErrorsTotal = v.GetInt64();
                    if (item.TryGetProperty("writeErrorsTotal",    out v) && v.ValueKind == JsonValueKind.Number)
                        entry.WriteErrorsTotal = v.GetInt64();
                    if (item.TryGetProperty("powerOnHours",        out v) && v.ValueKind == JsonValueKind.Number)
                        entry.PowerOnHours = v.GetInt64();
                    if (item.TryGetProperty("readLatencyMaxMs",    out v) && v.ValueKind == JsonValueKind.Number)
                        entry.ReadLatencyMaxMs = v.GetInt64();
                    if (item.TryGetProperty("writeLatencyMaxMs",   out v) && v.ValueKind == JsonValueKind.Number)
                        entry.WriteLatencyMaxMs = v.GetInt64();

                    result.Disks.Add(entry);
                }

                if (result.Disks.Count > 0)
                {
                    combined.StorageReliability = result;
                    App.LogMessage($"[NVMe] StorageReliability extracted: {result.Disks.Count} disk(s) from source={sourceStr}");
                }
                else
                {
                    App.LogMessage($"[NVMe] StorageReliabilityCounter source={sourceStr} - 0 disks extracted (unavailable or no non-USB disks)");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[NVMe] ExtractStorageReliability error: {ex.Message}");
            }
        }

        /// <summary>
        /// Extract missingData - handles both Array and Object formats.
        /// Populates legacy List&lt;string&gt; MissingData AND structured List&lt;MissingDataEntry&gt; MissingDataStructured.
        /// </summary>
        private void ExtractMissingData(JsonElement element, CombinedScanResult combined)
        {
            try
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var s = item.GetString() ?? "";
                            combined.MissingData.Add(s);
                            combined.MissingDataStructured.Add(new PCDiagnosticPro.Models.MissingDataEntry
                            {
                                Item = s, Source = "legacy", Confidence = "low",
                                Timestamp = DateTime.UtcNow.ToString("o")
                            });
                        }
                        else if (item.ValueKind == JsonValueKind.Object)
                        {
                            // Full structured object from PS (v2): { section, item, reason, source, confidence, timestamp }
                            var entry = new PCDiagnosticPro.Models.MissingDataEntry();
                            if (item.TryGetProperty("section",    out var sec))  entry.Section    = sec.GetString()  ?? "";
                            if (item.TryGetProperty("item",       out var itm))  entry.Item       = itm.GetString()  ?? "";
                            if (item.TryGetProperty("reason",     out var rsn))  entry.Reason     = rsn.GetString()  ?? "";
                            if (item.TryGetProperty("source",     out var src))  entry.Source     = src.GetString()  ?? "PowerShell";
                            if (item.TryGetProperty("confidence", out var cnf))  entry.Confidence = cnf.GetString()  ?? "low";
                            if (item.TryGetProperty("timestamp",  out var ts))   entry.Timestamp  = ts.GetString()   ?? "";
                            // Legacy: also add a string summary
                            var legacyStr = string.IsNullOrEmpty(entry.Section)
                                ? entry.Item
                                : $"{entry.Section}.{entry.Item}";
                            if (!string.IsNullOrEmpty(entry.Reason))
                                legacyStr += $": {entry.Reason}";
                            combined.MissingData.Add(legacyStr);
                            combined.MissingDataStructured.Add(entry);
                        }
                    }
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in element.EnumerateObject())
                    {
                        var val = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? prop.Name
                            : prop.Name;
                        combined.MissingData.Add(val);
                        combined.MissingDataStructured.Add(new PCDiagnosticPro.Models.MissingDataEntry
                        {
                            Item = prop.Name, Reason = val, Source = "legacy", Confidence = "low",
                            Timestamp = DateTime.UtcNow.ToString("o")
                        });
                    }
                    App.LogMessage($"[ExtractMissingData] Converted Object to Array: {combined.MissingData.Count} items");
                }
                else if (element.ValueKind == JsonValueKind.String)
                {
                    var s = element.GetString() ?? "";
                    combined.MissingData.Add(s);
                    combined.MissingDataStructured.Add(new PCDiagnosticPro.Models.MissingDataEntry
                    {
                        Item = s, Source = "legacy", Confidence = "low",
                        Timestamp = DateTime.UtcNow.ToString("o")
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ExtractMissingData] Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Extract findings - handles both Array and Object formats
        /// </summary>
        private void ExtractFindings(JsonElement element, CombinedScanResult combined)
        {
            try
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in element.EnumerateArray())
                    {
                        var finding = ExtractSingleFinding(f);
                        if (finding != null) combined.Findings.Add(finding);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    // Object format: each property is a finding
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            var finding = ExtractSingleFinding(prop.Value);
                            if (finding != null)
                            {
                                if (string.IsNullOrEmpty(finding.Source))
                                    finding.Source = prop.Name;
                                combined.Findings.Add(finding);
                            }
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            // Nested array of findings under a key
                            foreach (var f in prop.Value.EnumerateArray())
                            {
                                var finding = ExtractSingleFinding(f);
                                if (finding != null)
                                {
                                    if (string.IsNullOrEmpty(finding.Source))
                                        finding.Source = prop.Name;
                                    combined.Findings.Add(finding);
                                }
                            }
                        }
                    }
                    App.LogMessage($"[ExtractFindings] Converted Object to Array: {combined.Findings.Count} findings");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ExtractFindings] Error: {ex.Message}");
            }
        }
        
        private FindingExtract? ExtractSingleFinding(JsonElement f)
        {
            if (f.ValueKind != JsonValueKind.Object) return null;
            
            return new FindingExtract
            {
                Type = f.TryGetProperty("type", out var ft) ? ft.GetString() ?? "" : "",
                Severity = f.TryGetProperty("severity", out var fs) ? fs.GetString() ?? "" : "",
                Message = f.TryGetProperty("message", out var fm) ? fm.GetString() ?? "" :
                         f.TryGetProperty("msg", out var fmsg) ? fmsg.GetString() ?? "" : "",
                Source = f.TryGetProperty("source", out var src) ? src.GetString() ?? "" : ""
            };
        }
        
        /// <summary>
        /// Extract errors - handles both Array and Object formats
        /// </summary>
        private void ExtractErrors(JsonElement element, CombinedScanResult combined)
        {
            try
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in element.EnumerateArray())
                    {
                        var error = ExtractSingleError(e);
                        if (error != null) combined.Errors.Add(error);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    // Object format: each property is an error or category
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            var error = ExtractSingleError(prop.Value);
                            if (error != null)
                            {
                                if (string.IsNullOrEmpty(error.Section))
                                    error.Section = prop.Name;
                                combined.Errors.Add(error);
                            }
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            // Nested array of errors under a key
                            foreach (var e in prop.Value.EnumerateArray())
                            {
                                var error = ExtractSingleError(e);
                                if (error != null)
                                {
                                    if (string.IsNullOrEmpty(error.Section))
                                        error.Section = prop.Name;
                                    combined.Errors.Add(error);
                                }
                            }
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            // Simple key-value error
                            combined.Errors.Add(new ErrorExtract
                            {
                                Code = prop.Name,
                                Message = prop.Value.GetString() ?? "",
                                Section = ""
                            });
                        }
                    }
                    App.LogMessage($"[ExtractErrors] Converted Object to Array: {combined.Errors.Count} errors");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ExtractErrors] Error: {ex.Message}");
            }
        }
        
        private ErrorExtract? ExtractSingleError(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            
            return new ErrorExtract
            {
                Code = e.TryGetProperty("code", out var ec) ? ec.GetString() ?? "" : "",
                Message = e.TryGetProperty("message", out var em) ? em.GetString() ?? "" :
                         e.TryGetProperty("msg", out var emsg) ? emsg.GetString() ?? "" : "",
                Section = e.TryGetProperty("section", out var es) ? es.GetString() ?? "" : ""
            };
        }
        
        /// <summary>
        /// Extract sections - handles both Object and Array formats
        /// </summary>
        private void ExtractSections(JsonElement element, CombinedScanResult combined)
        {
            try
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    // Standard object format: extract keys
                    foreach (var prop in element.EnumerateObject())
                    {
                        combined.Sections.Add(prop.Name);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    // Array format: extract string values or object keys
                    foreach (var item in element.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            combined.Sections.Add(item.GetString() ?? "");
                        else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name))
                            combined.Sections.Add(name.GetString() ?? "");
                    }
                    App.LogMessage($"[ExtractSections] Converted Array to section list: {combined.Sections.Count} sections");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ExtractSections] Error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Log extracted nodes to %TEMP% for debugging
        /// </summary>
        private void LogExtractedNodes(CombinedScanResult combined, string outputDir)
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_ExtractNodes.log");
                var logContent = $"=== ExtractExplicitNodes Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n" +
                                 $"Output Dir: {outputDir}\n" +
                                 $"MissingData Count: {combined.MissingData.Count}\n" +
                                 $"Findings Count: {combined.Findings.Count}\n" +
                                 $"Errors Count: {combined.Errors.Count}\n" +
                                 $"Sections Count: {combined.Sections.Count}\n" +
                                 $"Sections: {string.Join(", ", combined.Sections)}\n" +
                                 $"MissingData: {string.Join(", ", combined.MissingData)}\n";
                
                if (combined.Findings.Count > 0)
                    logContent += $"First Finding: Type={combined.Findings[0].Type}, Severity={combined.Findings[0].Severity}\n";
                    
                if (combined.Errors.Count > 0)
                    logContent += $"First Error: Code={combined.Errors[0].Code}, Section={combined.Errors[0].Section}\n";
                
                File.AppendAllText(logPath, logContent + "\n", Encoding.UTF8);
            }
            catch { /* Ignore logging errors */ }
        }

        private void CancelScan()
        {
            try
            {
                lock (_scanLock)
                {
                    // Annuler le CancellationToken
                    _scanCts?.Cancel();

                    // Tuer le processus si encore actif
                    if (_scanProcess != null && !_scanProcess.HasExited)
                    {
                        try
                        {
                            _scanProcess.Kill(true);
                        }
                        catch (Exception ex)
                        {
                            App.LogMessage($"Erreur kill process: {ex.Message}");
                        }
                    }
                }

                if (!_cancelHandled)
                {
                    ResetAfterCancel();
                    _cancelHandled = true;
                }
                App.LogMessage("Scan annulé");
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur annulation: {ex.Message}");
            }
        }

        private void ResetAfterCancel()
        {
            _scanStopwatch.Stop();
            _liveFeedTimer.Stop();
            StopScanProgressTimer();

            // Persist cancelled status to disk history
            if (!string.IsNullOrWhiteSpace(_activeRunId))
            {
                Services.ScanStorageService.DeleteCombinedJsonIfExists(_activeRunId);
                Services.ScanStorageService.CleanupRunTempFiles(_activeRunId);

                var cancelledMeta = new Models.ScanMeta
                {
                    RunId           = _activeRunId,
                    StartTime       = _scanStartTime.UtcDateTime,
                    EndTime         = DateTime.UtcNow,
                    MachineName     = Environment.MachineName,
                    Status          = Models.ScanStatus.Cancelled,
                    DurationSeconds = _scanStopwatch.Elapsed.TotalSeconds,
                    AppVersion      = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "unknown"
                };

                PersistScanMetaSafe(cancelledMeta, "scan_cancel");
                UpsertHistoryItemFromMeta(cancelledMeta);
            }

            // Reset UI
            UpdateProgress(0, "Scan canceled", allowDecrease: true);
            ResetScanProgressEngine();
            ProgressCount = 0;
            CurrentStep = GetString("ReadyToScan");
            CurrentSection = string.Empty;
                _lastPowerShellSection = string.Empty;
            StatusMessage = GetString("StatusCanceled");
            ScanState = "Idle";
            AddLiveFeedItem("⏹️ Analyse annulée");
        }

        private void OnOutputReceived(string output)
        {
            var safeOutput = TextEncodingNormalizer.NormalizeIfCorrupted(output);
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => AddLiveFeedItem(safeOutput)));
        }

        private void OnProgressChanged(int progress)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (progress == Services.PowerShellService.IndeterminateProgress)
                {
                    // No real PROGRESS markers yet - show honest indeterminate spinner
                    IsScanProgressIndeterminate = true;
                }
                else
                {
                    // Real PROGRESS marker received - switch back to determinate mode
                    IsScanProgressIndeterminate = false;
                    UpdateProgress(progress, "PowerShellService progress");
                }
            });
        }

        private void OnStepChanged(string step)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var safeStep = TextEncodingNormalizer.NormalizeIfCorrupted(step);
                CurrentStep = safeStep;
                AddLiveFeedItem($"[STATUS] Etape | {safeStep}");
            });
        }
    }
}

