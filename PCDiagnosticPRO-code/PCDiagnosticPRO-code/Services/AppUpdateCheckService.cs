using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Checks application updates via winget and returns normalized upgrade candidates.
    /// </summary>
    public sealed class AppUpdateCheckService
    {
        private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex FoundLineRegex = new(@"^(Found|Trouve)\s+(?<name>.+?)\s+\[(?<id>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BracketIdRegex = new(@"\[(?<id>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PercentRegex = new(@"(?<percent>\d{1,3})\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FailedPackageRegex = new(@"^\s*(?<name>.+?)\s+(?<id>\S+)\s+\S+\s+\S+\s+Failed", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Known winget exit codes → readable French message
        private static readonly Dictionary<int, string> WingetExitMessages = new()
        {
            [0]            = "Mise à jour complète.",
            [-1978335189]  = "Aucune mise à jour disponible.",
            [3010]         = "Mise à jour appliquée – redémarrage requis.",
            [-1978335215]  = "Package introuvable.",
            [-1978335212]  = "Version déjà installée.",
            [-1978335141]  = "Accord requis – relancez manuellement.",
            [-1978335174]  = "Droits administrateur requis.",
            [-1978335177]  = "Annulé par l'utilisateur.",
            [-1978335186]  = "Interruption de l'installation.",
        };

        public async Task<AppUpdateCheckResult> CheckAvailableUpdatesAsync(CancellationToken ct = default)
        {
            var result = new AppUpdateCheckResult
            {
                CheckedAt = DateTime.Now
            };

            var wingetPath = ResolveWingetExecutablePath();
            if (string.IsNullOrWhiteSpace(wingetPath))
            {
                result.WingetAvailable = false;
                result.StatusMessage = "winget introuvable sur ce système.";
                return result;
            }

            try
            {
                // Preferred: machine-readable JSON output.
                var jsonRun = await RunWingetAsync(
                    wingetPath,
                    "upgrade --output json --disable-interactivity --accept-source-agreements",
                    ct).ConfigureAwait(false);

                if (jsonRun.StartError != null)
                {
                    result.WingetAvailable = false;
                    result.StatusMessage = $"winget indisponible: {jsonRun.StartError}";
                    return result;
                }

                if (TryParseUpdatesFromJson(jsonRun.StdOut, out var jsonUpdates))
                {
                    result.WingetAvailable = true;
                    result.Updates = jsonUpdates;
                    result.StatusMessage = BuildSummaryMessage(jsonUpdates.Count, "JSON");
                    return result;
                }

                // Fallback for older winget versions that don't support JSON.
                var tableRun = await RunWingetAsync(
                    wingetPath,
                    "upgrade --disable-interactivity --accept-source-agreements",
                    ct).ConfigureAwait(false);

                if (tableRun.StartError != null)
                {
                    result.WingetAvailable = false;
                    result.StatusMessage = $"winget indisponible: {tableRun.StartError}";
                    return result;
                }

                if (TryParseUpdatesFromTable(tableRun.StdOut, out var tableUpdates))
                {
                    result.WingetAvailable = true;
                    result.Updates = tableUpdates;
                    result.StatusMessage = BuildSummaryMessage(tableUpdates.Count, "table");
                    return result;
                }

                // winget executed, but output could not be parsed.
                result.WingetAvailable = true;
                var error = FirstNonEmptyLine(jsonRun.StdErr) ?? FirstNonEmptyLine(tableRun.StdErr);
                result.StatusMessage = string.IsNullOrWhiteSpace(error)
                    ? "winget a répondu, mais le format de sortie n'est pas reconnu."
                    : $"winget: {error}";
                return result;
            }
            catch (OperationCanceledException)
            {
                result.WingetAvailable = true;
                result.StatusMessage = "Vérification des mises à jour annulée.";
                return result;
            }
            catch (Exception ex)
            {
                result.WingetAvailable = false;
                result.StatusMessage = $"Erreur de vérification winget: {ex.Message}";
                return result;
            }
        }

        public Task<AppUpgradeExecutionResult> UpgradeAllAsync(CancellationToken ct = default) =>
            UpgradeAllAsync(progress: null, ct);

        public async Task<AppUpgradeExecutionResult> UpgradeAllAsync(
            IProgress<WingetRealtimeEvent>? progress,
            CancellationToken ct = default)
        {
            var result = new AppUpgradeExecutionResult
            {
                StartedAt = DateTime.Now
            };

            var wingetPath = ResolveWingetExecutablePath();
            if (string.IsNullOrWhiteSpace(wingetPath))
            {
                result.WingetAvailable = false;
                result.StatusMessage = "winget introuvable sur ce systeme.";
                return result;
            }

            try
            {
                // ── PRÉ-VÉRIFICATION (concept Driver Booster #1) ─────────────────────
                // Identifier combien de packages sont réellement en attente avant de lancer.
                var preCheck = await CheckAvailableUpdatesAsync(ct).ConfigureAwait(false);
                if (preCheck.WingetAvailable && preCheck.Updates != null && preCheck.Updates.Count == 0)
                {
                    result.WingetAvailable = true;
                    result.CommandExecuted = false;
                    result.Success = true;
                    result.StatusMessage = "Aucune mise à jour disponible.";
                    return result;
                }

                var pendingBefore = preCheck.Updates?.Count ?? -1;
                if (pendingBefore > 0)
                    ReportInfo(progress, $"Pré-vérification : {pendingBefore} mise(s) à jour détectée(s). Lancement de l'upgrade…");

                // ── EXÉCUTION PRINCIPALE ──────────────────────────────────────────────
                var run = await RunWingetAsync(
                    wingetPath,
                    "upgrade --all --disable-interactivity --accept-source-agreements --accept-package-agreements --silent",
                    ct,
                    progress).ConfigureAwait(false);

                if (run.StartError != null)
                {
                    result.WingetAvailable = false;
                    result.StatusMessage = $"winget indisponible: {run.StartError}";
                    return result;
                }

                result.WingetAvailable = true;
                result.CommandExecuted = true;
                result.ExitCode = run.ExitCode;
                result.StdOut = run.StdOut;
                result.StdErr = run.StdErr;

                // ── MAPPAGE EXIT CODE → MESSAGE LISIBLE ──────────────────────────────
                if (WingetExitMessages.TryGetValue(run.ExitCode, out var mappedMsg))
                {
                    result.Success = run.ExitCode == 0 || run.ExitCode == -1978335189 || run.ExitCode == 3010;
                    result.StatusMessage = mappedMsg;
                }
                else
                {
                    var firstError = FirstNonEmptyLine(run.StdErr);
                    result.Success = run.ExitCode == 0;
                    result.StatusMessage = string.IsNullOrWhiteSpace(firstError)
                        ? $"winget upgrade a retourne le code {run.ExitCode}."
                        : $"winget upgrade: {firstError}";
                }

                // ── FALLBACK PAR PAQUET (concept Driver Booster #2) ──────────────────
                // Si --all a échoué, tenter individuellement chaque package signalé en échec.
                if (!result.Success && !string.IsNullOrEmpty(run.StdOut))
                {
                    var failedIds = ExtractFailedPackageIds(run.StdOut);
                    if (failedIds.Count > 0)
                    {
                        ReportInfo(progress, $"Relance individuelle pour {failedIds.Count} package(s) en échec…");
                        int recovered = 0;
                        foreach (var pkgId in failedIds)
                        {
                            ct.ThrowIfCancellationRequested();
                            var pkgRun = await RunWingetAsync(
                                wingetPath,
                                $"upgrade --id \"{pkgId}\" --disable-interactivity --accept-source-agreements --accept-package-agreements --silent",
                                ct,
                                progress).ConfigureAwait(false);
                            if (pkgRun.StartError == null && pkgRun.ExitCode == 0)
                            {
                                recovered++;
                                ReportInfo(progress, $"  ✓ {pkgId} mis à jour.");
                            }
                            else
                            {
                                var pkgErr = FirstNonEmptyLine(pkgRun.StdErr) ?? $"code {pkgRun.ExitCode}";
                                ReportInfo(progress, $"  ✗ {pkgId} : {pkgErr}");
                            }
                        }
                        if (recovered > 0)
                            result.StatusMessage += $" ({recovered}/{failedIds.Count} package(s) récupéré(s) via fallback individuel)";
                    }
                }

                // ── POST-VÉRIFICATION (concept Driver Booster #3) ────────────────────
                // Confirmer quels packages sont encore en attente après l'upgrade.
                try
                {
                    var postCheck = await CheckAvailableUpdatesAsync(ct).ConfigureAwait(false);
                    if (postCheck.WingetAvailable && postCheck.Updates != null)
                    {
                        var stillPending = postCheck.Updates.Count;
                        if (stillPending == 0)
                            result.StatusMessage += " Tout est à jour.";
                        else
                            result.StatusMessage += $" {stillPending} package(s) encore en attente.";
                    }
                }
                catch
                {
                    // Post-check non bloquant
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new WingetRealtimeEvent
                {
                    EventType = WingetRealtimeEventType.Info,
                    Message = "Execution winget annulee.",
                    RawLine = "Execution winget annulee.",
                    IsError = false
                });
                result.WingetAvailable = true;
                result.Cancelled = true;
                result.StatusMessage = "Mise a jour applicative annulee.";
                return result;
            }
            catch (Exception ex)
            {
                result.WingetAvailable = false;
                result.StatusMessage = $"Erreur pendant winget upgrade: {ex.Message}";
                return result;
            }
        }

        /// <summary>Extracts package IDs reported as Failed in winget upgrade --all output.</summary>
        private static List<string> ExtractFailedPackageIds(string stdOut)
        {
            var ids = new List<string>();
            if (string.IsNullOrWhiteSpace(stdOut))
                return ids;
            foreach (var line in stdOut.Split('\n'))
            {
                var m = FailedPackageRegex.Match(line);
                if (m.Success)
                {
                    var id = m.Groups["id"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                        ids.Add(id);
                }
            }
            return ids;
        }

        private static void ReportInfo(IProgress<WingetRealtimeEvent>? progress, string message)
        {
            progress?.Report(new WingetRealtimeEvent
            {
                EventType = WingetRealtimeEventType.Info,
                Message = message,
                RawLine = message,
                IsError = false
            });
        }

        public async Task<AppWingetInventoryResult> ListManagedAppsAsync(CancellationToken ct = default)
        {
            var result = new AppWingetInventoryResult();

            var wingetPath = ResolveWingetExecutablePath();
            if (string.IsNullOrWhiteSpace(wingetPath))
            {
                result.WingetAvailable = false;
                result.StatusMessage = "winget introuvable sur ce systeme.";
                return result;
            }

            try
            {
                var jsonRun = await RunWingetAsync(
                    wingetPath,
                    "list --output json --disable-interactivity --accept-source-agreements",
                    ct).ConfigureAwait(false);

                if (jsonRun.StartError != null)
                {
                    result.WingetAvailable = false;
                    result.StatusMessage = $"winget indisponible: {jsonRun.StartError}";
                    return result;
                }

                if (TryParseManagedFromJson(jsonRun.StdOut, out var jsonPackages))
                {
                    result.WingetAvailable = true;
                    result.Packages = jsonPackages;
                    result.StatusMessage = $"{jsonPackages.Count} application(s) geree(s) par winget.";
                    return result;
                }

                var tableRun = await RunWingetAsync(
                    wingetPath,
                    "list --disable-interactivity --accept-source-agreements",
                    ct).ConfigureAwait(false);

                if (tableRun.StartError != null)
                {
                    result.WingetAvailable = false;
                    result.StatusMessage = $"winget indisponible: {tableRun.StartError}";
                    return result;
                }

                if (TryParseManagedFromTable(tableRun.StdOut, out var tablePackages))
                {
                    result.WingetAvailable = true;
                    result.Packages = tablePackages;
                    result.StatusMessage = $"{tablePackages.Count} application(s) geree(s) par winget.";
                    return result;
                }

                result.WingetAvailable = true;
                var error = FirstNonEmptyLine(jsonRun.StdErr) ?? FirstNonEmptyLine(tableRun.StdErr);
                result.StatusMessage = string.IsNullOrWhiteSpace(error)
                    ? "winget list a repondu, mais le format de sortie n'est pas reconnu."
                    : $"winget list: {error}";
                return result;
            }
            catch (OperationCanceledException)
            {
                result.WingetAvailable = true;
                result.StatusMessage = "Inventaire winget annule.";
                return result;
            }
            catch (Exception ex)
            {
                result.WingetAvailable = false;
                result.StatusMessage = $"Erreur inventaire winget: {ex.Message}";
                return result;
            }
        }

        private static string? ResolveWingetExecutablePath()
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    var candidate = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch
            {
                // Ignore and fallback.
            }

            return "winget.exe";
        }

        private static async Task<WingetCommandResult> RunWingetAsync(string executable, string arguments, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                if (!process.Start())
                {
                    return new WingetCommandResult
                    {
                        ExitCode = -1,
                        StartError = "Impossible de démarrer le processus winget."
                    };
                }

                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var stdOut = await stdOutTask.ConfigureAwait(false);
                var stdErr = await stdErrTask.ConfigureAwait(false);

                return new WingetCommandResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdOut ?? string.Empty,
                    StdErr = stdErr ?? string.Empty
                };
            }
            catch (Exception ex) when (
                ex is InvalidOperationException ||
                ex is System.ComponentModel.Win32Exception ||
                ex is FileNotFoundException)
            {
                return new WingetCommandResult
                {
                    ExitCode = -1,
                    StartError = ex.Message
                };
            }
        }

        private static async Task<WingetCommandResult> RunWingetAsync(
            string executable,
            string arguments,
            CancellationToken ct,
            IProgress<WingetRealtimeEvent>? progress)
        {
            if (progress == null)
                return await RunWingetAsync(executable, arguments, ct).ConfigureAwait(false);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                if (!process.Start())
                {
                    return new WingetCommandResult
                    {
                        ExitCode = -1,
                        StartError = "Impossible de demarrer le processus winget."
                    };
                }

                var parser = new WingetRealtimeParser(progress);
                var stdOutBuilder = new StringBuilder();
                var stdErrBuilder = new StringBuilder();
                var stdOutClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var stdErrClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                    {
                        stdOutClosed.TrySetResult(true);
                        return;
                    }

                    stdOutBuilder.AppendLine(e.Data);
                    parser.HandleLine(e.Data, isError: false);
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                    {
                        stdErrClosed.TrySetResult(true);
                        return;
                    }

                    stdErrBuilder.AppendLine(e.Data);
                    parser.HandleLine(e.Data, isError: true);
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                try
                {
                    await process.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryTerminateProcess(process);
                    throw;
                }

                await Task.WhenAll(stdOutClosed.Task, stdErrClosed.Task).ConfigureAwait(false);

                return new WingetCommandResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdOutBuilder.ToString(),
                    StdErr = stdErrBuilder.ToString()
                };
            }
            catch (Exception ex) when (
                ex is InvalidOperationException ||
                ex is System.ComponentModel.Win32Exception ||
                ex is FileNotFoundException)
            {
                return new WingetCommandResult
                {
                    ExitCode = -1,
                    StartError = ex.Message
                };
            }
        }

        private static void TryTerminateProcess(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;

                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }
        }

        private static bool TryParseUpdatesFromJson(string rawOutput, out List<AppUpdateCandidate> updates)
        {
            updates = new List<AppUpdateCandidate>();
            if (string.IsNullOrWhiteSpace(rawOutput))
                return false;

            var payload = ExtractJsonPayload(rawOutput);
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(payload);
                ExtractUpdateCandidates(doc.RootElement, updates);
                updates = Deduplicate(updates);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? ExtractJsonPayload(string raw)
        {
            var startObj = raw.IndexOf('{');
            var startArr = raw.IndexOf('[');
            var start = -1;

            if (startObj >= 0 && startArr >= 0)
                start = Math.Min(startObj, startArr);
            else if (startObj >= 0)
                start = startObj;
            else if (startArr >= 0)
                start = startArr;

            if (start < 0)
                return null;

            var endObj = raw.LastIndexOf('}');
            var endArr = raw.LastIndexOf(']');
            var end = Math.Max(endObj, endArr);
            if (end <= start)
                return null;

            return raw.Substring(start, end - start + 1).Trim();
        }

        private static void ExtractUpdateCandidates(JsonElement element, List<AppUpdateCandidate> updates)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        ExtractUpdateCandidates(item, updates);
                    break;

                case JsonValueKind.Object:
                    TryAddUpdateCandidate(element, updates);
                    foreach (var prop in element.EnumerateObject())
                        ExtractUpdateCandidates(prop.Value, updates);
                    break;
            }
        }

        private static void TryAddUpdateCandidate(JsonElement obj, List<AppUpdateCandidate> updates)
        {
            var name = TryGetPropertyString(obj, "Name", "PackageName", "name", "packageName");
            var id = TryGetPropertyString(obj, "Id", "PackageIdentifier", "packageIdentifier", "id");
            var installed = TryGetPropertyString(obj, "Version", "InstalledVersion", "installedVersion");
            var available = TryGetPropertyString(obj, "AvailableVersion", "availableVersion");
            var source = TryGetPropertyString(obj, "Source", "source");

            if (string.IsNullOrWhiteSpace(available))
                return;

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id))
                return;

            updates.Add(new AppUpdateCandidate
            {
                Name = name ?? string.Empty,
                PackageId = id ?? string.Empty,
                InstalledVersion = installed ?? string.Empty,
                AvailableVersion = available ?? string.Empty,
                Source = source ?? string.Empty
            });
        }

        private static bool TryParseUpdatesFromTable(string rawOutput, out List<AppUpdateCandidate> updates)
        {
            updates = new List<AppUpdateCandidate>();
            if (string.IsNullOrWhiteSpace(rawOutput))
                return false;

            var lines = rawOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            foreach (var line in lines)
            {
                if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("-", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("No applicable upgrade", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("upgrades available", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = MultiSpaceRegex.Split(line);
                if (parts.Length < 4)
                    continue;

                var name = parts[0].Trim();
                var id = parts[1].Trim();
                var installed = parts[2].Trim();
                var available = parts[3].Trim();
                var source = parts.Length >= 5 ? parts[4].Trim() : "winget";

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(available))
                    continue;

                updates.Add(new AppUpdateCandidate
                {
                    Name = name,
                    PackageId = id,
                    InstalledVersion = installed,
                    AvailableVersion = available,
                    Source = source
                });
            }

            updates = Deduplicate(updates);
            return true;
        }

        private static bool TryParseManagedFromJson(string rawOutput, out List<AppPackageIdentity> packages)
        {
            packages = new List<AppPackageIdentity>();
            if (string.IsNullOrWhiteSpace(rawOutput))
                return false;

            var payload = ExtractJsonPayload(rawOutput);
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(payload);
                ExtractManagedPackages(doc.RootElement, packages);
                packages = DeduplicateManaged(packages);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ExtractManagedPackages(JsonElement element, List<AppPackageIdentity> packages)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        ExtractManagedPackages(item, packages);
                    break;

                case JsonValueKind.Object:
                    TryAddManagedPackage(element, packages);
                    foreach (var prop in element.EnumerateObject())
                        ExtractManagedPackages(prop.Value, packages);
                    break;
            }
        }

        private static void TryAddManagedPackage(JsonElement obj, List<AppPackageIdentity> packages)
        {
            var name = TryGetPropertyString(obj, "Name", "PackageName", "name", "packageName");
            var id = TryGetPropertyString(obj, "Id", "PackageIdentifier", "packageIdentifier", "id");
            var installed = TryGetPropertyString(obj, "Version", "InstalledVersion", "installedVersion");
            var source = TryGetPropertyString(obj, "Source", "source");

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id))
                return;

            packages.Add(new AppPackageIdentity
            {
                Name = name ?? string.Empty,
                PackageId = id ?? string.Empty,
                InstalledVersion = installed ?? string.Empty,
                Source = source ?? string.Empty
            });
        }

        private static bool TryParseManagedFromTable(string rawOutput, out List<AppPackageIdentity> packages)
        {
            packages = new List<AppPackageIdentity>();
            if (string.IsNullOrWhiteSpace(rawOutput))
                return false;

            var lines = rawOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            foreach (var line in lines)
            {
                if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = MultiSpaceRegex.Split(line);
                if (parts.Length < 2)
                    continue;

                var name = parts[0].Trim();
                var id = parts.Length >= 2 ? parts[1].Trim() : string.Empty;
                var version = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
                var source = parts.Length >= 4 ? parts[3].Trim() : "winget";

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                packages.Add(new AppPackageIdentity
                {
                    Name = name,
                    PackageId = id,
                    InstalledVersion = version,
                    Source = source
                });
            }

            packages = DeduplicateManaged(packages);
            return true;
        }

        private static List<AppUpdateCandidate> Deduplicate(List<AppUpdateCandidate> input)
        {
            return input
                .GroupBy(c => $"{c.PackageId}|{c.Name}|{c.AvailableVersion}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static List<AppPackageIdentity> DeduplicateManaged(List<AppPackageIdentity> input)
        {
            return input
                .GroupBy(c => $"{c.PackageId}|{c.Name}|{c.InstalledVersion}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static string? TryGetPropertyString(JsonElement obj, params string[] names)
        {
            foreach (var name in names)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (!prop.NameEquals(name) && !prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.ToString(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => null
                    };
                }
            }

            return null;
        }

        private static string BuildSummaryMessage(int updateCount, string mode)
        {
            if (updateCount <= 0)
                return $"Aucune mise à jour détectée via winget ({mode}).";

            return $"{updateCount} mise(s) à jour détectée(s) via winget ({mode}).";
        }

        private static string? FirstNonEmptyLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    return line.Trim();
            }

            return null;
        }

        private sealed class WingetRealtimeParser
        {
            private readonly IProgress<WingetRealtimeEvent> _progress;
            private string? _currentPackageId;
            private string? _currentDisplayName;

            public WingetRealtimeParser(IProgress<WingetRealtimeEvent> progress)
            {
                _progress = progress;
            }

            public void HandleLine(string rawLine, bool isError)
            {
                var line = rawLine?.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    return;

                _progress.Report(new WingetRealtimeEvent
                {
                    EventType = WingetRealtimeEventType.Info,
                    RawLine = line,
                    Message = line,
                    IsError = isError,
                    PackageId = _currentPackageId,
                    DisplayName = _currentDisplayName
                });

                var found = FoundLineRegex.Match(line);
                if (found.Success)
                {
                    _currentDisplayName = found.Groups["name"].Value.Trim();
                    _currentPackageId = found.Groups["id"].Value.Trim();
                    _progress.Report(new WingetRealtimeEvent
                    {
                        EventType = WingetRealtimeEventType.ItemStarted,
                        RawLine = line,
                        Message = "Debut de mise a jour",
                        IsError = false,
                        PackageId = _currentPackageId,
                        DisplayName = _currentDisplayName,
                        Percent = 50
                    });
                    return;
                }

                var pct = PercentRegex.Match(line);
                if (pct.Success &&
                    int.TryParse(pct.Groups["percent"].Value, out var parsedPercent))
                {
                    if (parsedPercent >= 0 && parsedPercent <= 100)
                    {
                        _progress.Report(new WingetRealtimeEvent
                        {
                            EventType = WingetRealtimeEventType.Progress,
                            RawLine = line,
                            Message = line,
                            IsError = false,
                            PackageId = ResolvePackageIdFromLine(line),
                            DisplayName = _currentDisplayName,
                            Percent = parsedPercent
                        });
                    }
                }

                if (IsSuccessLine(line))
                {
                    _progress.Report(new WingetRealtimeEvent
                    {
                        EventType = WingetRealtimeEventType.ItemSucceeded,
                        RawLine = line,
                        Message = "Mise a jour appliquee",
                        IsError = false,
                        PackageId = ResolvePackageIdFromLine(line),
                        DisplayName = _currentDisplayName,
                        Percent = 100
                    });
                    _currentDisplayName = null;
                    _currentPackageId = null;
                    return;
                }

                if (IsFailureLine(line))
                {
                    _progress.Report(new WingetRealtimeEvent
                    {
                        EventType = WingetRealtimeEventType.ItemFailed,
                        RawLine = line,
                        Message = line,
                        IsError = true,
                        PackageId = ResolvePackageIdFromLine(line),
                        DisplayName = _currentDisplayName,
                        Percent = 100
                    });
                    _currentDisplayName = null;
                    _currentPackageId = null;
                }
            }

            private string? ResolvePackageIdFromLine(string line)
            {
                var fromBracket = BracketIdRegex.Match(line);
                if (fromBracket.Success)
                    return fromBracket.Groups["id"].Value.Trim();

                return _currentPackageId;
            }

            private static bool IsSuccessLine(string line)
            {
                return line.Contains("successfully installed", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("install succeeded", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("installe avec succes", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("installation reussie", StringComparison.OrdinalIgnoreCase);
            }

            private static bool IsFailureLine(string line)
            {
                if (line.Contains("no applicable upgrade found", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("aucune mise a jour applicable", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("no available upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Patterns spécifiques d'échec winget — ne pas matcher "error"/"erreur" génériques
                // car winget peut afficher des lignes informatives contenant ces mots.
                return line.Contains("installer failed", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("install failed", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("failed with exit code", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("failed to install", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("installation failed", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("echec de l'installation", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("echec d'installation", StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class WingetCommandResult
        {
            public int ExitCode { get; set; }
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;
            public string? StartError { get; set; }
        }

        // ──────────────────────────────────────────────
        // Chocolatey fallback
        // ──────────────────────────────────────────────

        private static string? ResolveChocolateyExecutablePath()
        {
            try
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                var chocoPath = Path.Combine(programData, "chocolatey", "bin", "choco.exe");
                if (File.Exists(chocoPath))
                    return chocoPath;

                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = Path.Combine(dir.Trim(), "choco.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch
            {
                // Ignore and return null
            }

            return null;
        }

        public async Task<ChocoUpdateCheckResult> CheckChocolateyUpdatesAsync(CancellationToken ct = default)
        {
            var result = new ChocoUpdateCheckResult { CheckedAt = DateTime.Now };

            var chocoPath = ResolveChocolateyExecutablePath();
            if (string.IsNullOrWhiteSpace(chocoPath))
            {
                result.ChocoAvailable = false;
                result.StatusMessage = "Chocolatey non installe sur ce systeme.";
                return result;
            }

            try
            {
                var run = await RunWingetAsync(chocoPath, "outdated --limit-output", ct).ConfigureAwait(false);
                if (run.StartError != null)
                {
                    result.ChocoAvailable = false;
                    result.StatusMessage = $"Chocolatey indisponible: {run.StartError}";
                    return result;
                }

                result.ChocoAvailable = true;
                result.Updates = ParseChocoOutdatedOutput(run.StdOut);
                result.StatusMessage = result.Updates.Count > 0
                    ? $"{result.Updates.Count} mise(s) a jour Chocolatey detectee(s)."
                    : "Aucune mise a jour Chocolatey disponible.";
                return result;
            }
            catch (OperationCanceledException)
            {
                result.ChocoAvailable = true;
                result.StatusMessage = "Verification Chocolatey annulee.";
                return result;
            }
            catch (Exception ex)
            {
                result.ChocoAvailable = false;
                result.StatusMessage = $"Erreur Chocolatey: {ex.Message}";
                return result;
            }
        }

        private static List<ChocoUpdateCandidate> ParseChocoOutdatedOutput(string stdOut)
        {
            var updates = new List<ChocoUpdateCandidate>();
            if (string.IsNullOrWhiteSpace(stdOut))
                return updates;

            foreach (var line in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('|');
                if (parts.Length < 3)
                    continue;

                var name = parts[0].Trim();
                var installed = parts[1].Trim();
                var available = parts[2].Trim();
                var pinned = parts.Length >= 4 && parts[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(available) || pinned)
                    continue;

                updates.Add(new ChocoUpdateCandidate
                {
                    PackageName = name,
                    InstalledVersion = installed,
                    AvailableVersion = available
                });
            }

            return updates;
        }

        public async Task<ChocoUpgradeResult> ChocoUpgradeAllAsync(
            IProgress<WingetRealtimeEvent>? progress = null,
            CancellationToken ct = default)
        {
            var result = new ChocoUpgradeResult { StartedAt = DateTime.Now };

            var chocoPath = ResolveChocolateyExecutablePath();
            if (string.IsNullOrWhiteSpace(chocoPath))
            {
                result.ChocoAvailable = false;
                result.StatusMessage = "Chocolatey non installe.";
                return result;
            }

            try
            {
                progress?.Report(new WingetRealtimeEvent
                {
                    EventType = WingetRealtimeEventType.Info,
                    Message = "Lancement de choco upgrade all..."
                });

                var run = await RunWingetAsync(
                    chocoPath,
                    "upgrade all --yes --no-progress --limit-output",
                    ct).ConfigureAwait(false);

                result.ChocoAvailable = true;
                result.ExitCode = run.ExitCode;
                result.Success = run.ExitCode == 0;
                result.StatusMessage = run.ExitCode == 0
                    ? "Chocolatey: toutes les mises a jour appliquees."
                    : $"Chocolatey upgrade termine avec code {run.ExitCode}.";
                return result;
            }
            catch (OperationCanceledException)
            {
                result.ChocoAvailable = true;
                result.StatusMessage = "Chocolatey upgrade annule.";
                return result;
            }
            catch (Exception ex)
            {
                result.ChocoAvailable = false;
                result.StatusMessage = $"Erreur Chocolatey upgrade: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Unified update check: winget (preferred) + Chocolatey (complementary).
        /// </summary>
        public async Task<UnifiedUpdateCheckResult> CheckAllUpdateSourcesAsync(CancellationToken ct = default)
        {
            var unified = new UnifiedUpdateCheckResult();

            var wingetResult = await CheckAvailableUpdatesAsync(ct).ConfigureAwait(false);
            unified.Winget = wingetResult;

            var chocoResult = await CheckChocolateyUpdatesAsync(ct).ConfigureAwait(false);
            unified.Chocolatey = chocoResult;

            var totalUpdates = (wingetResult.Updates?.Count ?? 0) + (chocoResult.Updates?.Count ?? 0);
            unified.TotalUpdatesAvailable = totalUpdates;
            unified.StatusMessage = totalUpdates == 0
                ? "Aucune mise a jour detectee (winget + Chocolatey)."
                : $"{totalUpdates} mise(s) a jour disponible(s) (winget: {wingetResult.Updates?.Count ?? 0}, choco: {chocoResult.Updates?.Count ?? 0}).";

            return unified;
        }
    }

    public sealed class AppUpdateCheckResult
    {
        public bool WingetAvailable { get; set; }
        public DateTime CheckedAt { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public List<AppUpdateCandidate> Updates { get; set; } = new();
    }

    public sealed class AppUpdateCandidate
    {
        public string Name { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string InstalledVersion { get; set; } = string.Empty;
        public string AvailableVersion { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public sealed class AppUpgradeExecutionResult
    {
        public bool WingetAvailable { get; set; }
        public bool CommandExecuted { get; set; }
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public int ExitCode { get; set; }
        public DateTime StartedAt { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public string StdOut { get; set; } = string.Empty;
        public string StdErr { get; set; } = string.Empty;
    }

    public sealed class AppWingetInventoryResult
    {
        public bool WingetAvailable { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public List<AppPackageIdentity> Packages { get; set; } = new();
    }

    public sealed class AppPackageIdentity
    {
        public string Name { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string InstalledVersion { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public enum WingetRealtimeEventType
    {
        Info,
        ItemStarted,
        ItemSucceeded,
        ItemFailed,
        Progress
    }

    public sealed class WingetRealtimeEvent
    {
        public WingetRealtimeEventType EventType { get; set; }
        public string Message { get; set; } = string.Empty;
        public string RawLine { get; set; } = string.Empty;
        public string? PackageId { get; set; }
        public string? DisplayName { get; set; }
        public int? Percent { get; set; }
        public bool IsError { get; set; }
    }

    // ──────────────────────────────────────────────
    // Chocolatey models
    // ──────────────────────────────────────────────

    public sealed class ChocoUpdateCheckResult
    {
        public bool ChocoAvailable { get; set; }
        public DateTime CheckedAt { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public List<ChocoUpdateCandidate> Updates { get; set; } = new();
    }

    public sealed class ChocoUpdateCandidate
    {
        public string PackageName { get; set; } = string.Empty;
        public string InstalledVersion { get; set; } = string.Empty;
        public string AvailableVersion { get; set; } = string.Empty;
    }

    public sealed class ChocoUpgradeResult
    {
        public bool ChocoAvailable { get; set; }
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public DateTime StartedAt { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
    }

    public sealed class UnifiedUpdateCheckResult
    {
        public AppUpdateCheckResult? Winget { get; set; }
        public ChocoUpdateCheckResult? Chocolatey { get; set; }
        public int TotalUpdatesAvailable { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
    }
}
