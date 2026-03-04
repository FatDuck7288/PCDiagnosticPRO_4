using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Window showing installed applications and update status.
    /// </summary>
    public partial class AppsDetailsWindow : Window
    {
        public AppsDetailsWindow(JsonElement? appsData, JsonElement? startupData)
        {
            InitializeComponent();
            DataContext = new AppsDetailsViewModel(appsData, startupData);
            Loaded += AppsDetailsWindow_Loaded;
        }

        private async void AppsDetailsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is not AppsDetailsViewModel vm)
                return;

            await vm.RefreshInventoryAndDetectUpdatesAsync().ConfigureAwait(true);
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not AppsDetailsViewModel vm)
                return;

            var preparation = await vm.PrepareUpdateApplyAsync().ConfigureAwait(true);
            if (!preparation.CanRun)
            {
                var icon = preparation.IsError ? MessageBoxImage.Warning : MessageBoxImage.Information;
                MessageBox.Show(preparation.Message, "Mise A Jour", MessageBoxButton.OK, icon);
                return;
            }

            var applyVm = new UpdateApplyViewModel(
                preparation.Items,
                vm.ExecuteUpdateApplyAsync,
                "Application des mises a jour");

            var applyWindow = new UpdateApplyWindow
            {
                Owner = this,
                DataContext = applyVm
            };

            applyWindow.ShowDialog();
        }

        private void UninstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not AppsDetailsViewModel vm)
                return;

            var selected = vm.GetSelectedForUninstall();
            if (selected.Count == 0)
                return;

            var names = string.Join("\n", selected.Select(a => $"• {a.Name}"));
            var confirm = MessageBox.Show(
                $"Désinstaller les applications suivantes ?\n\n{names}\n\nLa fenêtre de désinstallation de chaque application s'ouvrira.",
                "Confirmer la désinstallation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            var errors = new System.Text.StringBuilder();
            foreach (var app in selected)
            {
                var err = TryLaunchUninstall(app);
                if (err != null)
                    errors.AppendLine($"• {app.Name}: {err}");
                app.IsSelectedForUninstall = false;
            }

            var errorText = errors.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(errorText))
                MessageBox.Show(
                    $"Erreur lors du lancement de la désinstallation :\n\n{errorText}",
                    "Erreur",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
        }

        private static string? TryLaunchUninstall(AppDisplayItem app)
        {
            var raw = app.UninstallString;
            if (string.IsNullOrWhiteSpace(raw))
                return "Commande de désinstallation indisponible.";

            try
            {
                var cmd = raw.Trim();
                // MSI: replace /I{ with /X{ to uninstall instead of repair
                if (cmd.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
                    cmd = System.Text.RegularExpressions.Regex.Replace(cmd, @"(?i)/I\{", "/X{");

                string filename;
                string arguments = string.Empty;

                if (cmd.StartsWith("\"", StringComparison.Ordinal))
                {
                    var closing = cmd.IndexOf('"', 1);
                    if (closing < 0) return "Format de commande invalide.";
                    filename = cmd.Substring(1, closing - 1);
                    arguments = closing + 1 < cmd.Length ? cmd.Substring(closing + 1).Trim() : string.Empty;
                }
                else
                {
                    var spaceIdx = cmd.IndexOf(' ');
                    if (spaceIdx < 0)
                    {
                        filename = cmd;
                    }
                    else
                    {
                        filename = cmd.Substring(0, spaceIdx);
                        arguments = cmd.Substring(spaceIdx + 1).Trim();
                    }
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filename,
                    Arguments = arguments,
                    UseShellExecute = true
                });
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    /// <summary>
    /// ViewModel for the application details window.
    /// </summary>
    public class AppsDetailsViewModel : INotifyPropertyChanged
    {
        private static readonly Regex NameCleanupRegex = new(@"\b\d+([._-]\d+){1,4}\b", RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly AppUpdateCheckService _appUpdateCheckService = new();
        private readonly List<AppDisplayItem> _allApps = new();
        private readonly int _startupCount;

        public ObservableCollection<AppDisplayItem> Applications { get; } = new();
        public ObservableCollection<AppDisplayItem> FilteredApplications { get; } = new();

        private string _searchText = string.Empty;
        private bool _isCheckingUpdates;
        private string _updateCheckStatusText = "Verification winget en attente...";

        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                OnPropertyChanged(nameof(SearchText));
                ApplyFilter();
            }
        }

        public int TotalCount => _allApps.Count;
        public int RecentCount => _allApps.Count(a => a.IsRecent);
        public int StartupCount => _startupCount;
        public int UpdatableCount => _allApps.Count(a => a.HasUpdateAvailable);

        public bool HasRecent => RecentCount > 0;
        public bool HasStartupApps => StartupCount > 0;
        public bool HasUpdatableApps => UpdatableCount > 0;
        public bool HasSelectedForUninstall => _allApps.Any(a => a.IsSelectedForUninstall);
        public int SelectedForUninstallCount => _allApps.Count(a => a.IsSelectedForUninstall);
        public string UninstallButtonLabel => SelectedForUninstallCount > 0
            ? $"Désinstaller ({SelectedForUninstallCount})"
            : "Désinstaller";
        public bool IsCheckingUpdates => _isCheckingUpdates;
        public bool CanRunUpdateCheck => !_isCheckingUpdates;
        public string CheckUpdatesButtonLabel => _isCheckingUpdates ? "Mise A Jour..." : "Mise A Jour";
        public string SummaryText { get; }

        public string UpdateCheckStatusText
        {
            get => _updateCheckStatusText;
            private set
            {
                if (string.Equals(_updateCheckStatusText, value, StringComparison.Ordinal))
                    return;

                _updateCheckStatusText = value;
                OnPropertyChanged(nameof(UpdateCheckStatusText));
            }
        }

        public AppsDetailsViewModel(JsonElement? appsData, JsonElement? startupData)
        {
            if (startupData.HasValue)
            {
                JsonElement? programs = null;
                if (startupData.Value.TryGetProperty("programs", out var progs))
                    programs = progs;
                else if (startupData.Value.TryGetProperty("startupItems", out var items))
                    programs = items;

                if (programs.HasValue && programs.Value.ValueKind == JsonValueKind.Array)
                {
                    _startupCount = programs.Value.GetArrayLength();
                }
            }

            foreach (var app in ReadAppsFromJson(appsData)
                         .OrderByDescending(a => a.InstallDate ?? DateTime.MinValue)
                         .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                app.PropertyChanged += AppItem_PropertyChanged;
                _allApps.Add(app);
                Applications.Add(app);
            }

            EnrichFromRegistry();
            ApplyFilter();
            SummaryText = "Applications detectees depuis le registre Windows et les sources de packages";
        }

        public async Task RefreshInventoryAndDetectUpdatesAsync()
        {
            if (_isCheckingUpdates)
                return;

            await ExecuteBusyAsync(async () =>
            {
                UpdateCheckStatusText = "Actualisation de l'inventaire local...";
                EnrichFromRegistry();

                UpdateCheckStatusText = "Detection des mises a jour via winget...";
                var detection = await DetectWingetStatesAsync().ConfigureAwait(true);
                UpdateCheckStatusText = detection.StatusMessage;
            }).ConfigureAwait(true);
        }

        public async Task<UpdateApplyPreparationResult> PrepareUpdateApplyAsync()
        {
            if (_isCheckingUpdates)
            {
                return new UpdateApplyPreparationResult
                {
                    CanRun = false,
                    IsError = false,
                    Message = "Une operation de mise a jour est deja en cours."
                };
            }

            WingetDetectionSnapshot? before = null;
            await ExecuteBusyAsync(async () =>
            {
                UpdateCheckStatusText = "Actualisation de l'inventaire local...";
                EnrichFromRegistry();

                UpdateCheckStatusText = "Detection des mises a jour via winget...";
                before = await DetectWingetStatesAsync().ConfigureAwait(true);
                UpdateCheckStatusText = before.StatusMessage;
            }).ConfigureAwait(true);

            if (before == null)
            {
                return new UpdateApplyPreparationResult
                {
                    CanRun = false,
                    IsError = true,
                    Message = "Impossible de preparer la mise a jour applicative."
                };
            }

            if (!before.WingetAvailable)
            {
                return new UpdateApplyPreparationResult
                {
                    CanRun = false,
                    IsError = true,
                    Message =
                        "winget est introuvable ou indisponible.\n\n" +
                        "Installez ou reparez 'App Installer' depuis Microsoft Store, puis relancez la mise a jour."
                };
            }

            var updatableApps = _allApps
                .Where(a => a.HasUpdateAvailable)
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (updatableApps.Count == 0)
            {
                return new UpdateApplyPreparationResult
                {
                    CanRun = false,
                    IsError = false,
                    Message = "Aucune mise a jour winget a appliquer."
                };
            }

            var items = updatableApps
                .Select(app =>
                    new UpdateItemViewModel(
                        BuildAppItemId(app),
                        app.Name,
                        UpdateItemKind.App,
                        "En attente"))
                .ToList();

            return new UpdateApplyPreparationResult
            {
                CanRun = true,
                IsError = false,
                Message = $"{items.Count} mise(s) a jour prete(s) a etre appliquee(s).",
                Items = items
            };
        }

        public async Task ExecuteUpdateApplyAsync(UpdateApplyViewModel applyVm, CancellationToken cancellationToken)
        {
            if (applyVm == null)
                return;

            SetBusyState(true);
            try
            {
                UpdateCheckStatusText = $"Application des mises a jour ({applyVm.TotalCount})...";
                applyVm.SetHeaderStatus(UpdateCheckStatusText);
                applyVm.AppendLog("Demarrage de winget upgrade --all.");

                // Marquer tous les items en "En cours" dès le départ pour que la progression soit visible
                foreach (var item in applyVm.Items)
                    applyVm.MarkItemRunning(item.Id, item.DisplayName, "En cours d'installation...", 10, UpdateItemKind.App);

                var progress = new Progress<WingetRealtimeEvent>(evt => HandleWingetRealtimeEvent(applyVm, evt));
                var upgrade = await _appUpdateCheckService.UpgradeAllAsync(progress, cancellationToken).ConfigureAwait(true);

                if (!upgrade.WingetAvailable)
                {
                    applyVm.MarkUnfinishedAs(UpdateItemState.Failed, "winget indisponible.");
                    applyVm.AppendLog(upgrade.StatusMessage);
                    UpdateCheckStatusText = "Impossible d'executer winget upgrade.";
                    applyVm.SetHeaderStatus(UpdateCheckStatusText);
                    return;
                }

                if (upgrade.Cancelled)
                {
                    applyVm.MarkUnfinishedAs(UpdateItemState.Skipped, "Operation annulee par l'utilisateur.");
                    UpdateCheckStatusText = "Mise a jour applicative annulee.";
                    applyVm.SetHeaderStatus(UpdateCheckStatusText);
                    return;
                }

                UpdateCheckStatusText = "Relecture de l'inventaire apres mise a jour...";
                applyVm.SetHeaderStatus(UpdateCheckStatusText);
                EnrichFromRegistry();

                UpdateCheckStatusText = "Verification finale des mises a jour...";
                applyVm.SetHeaderStatus(UpdateCheckStatusText);
                await DetectWingetStatesAsync().ConfigureAwait(true);

                FinalizeUnresolvedItems(applyVm, upgrade.Success);

                var updatedCount = applyVm.Items.Count(i => i.State == UpdateItemState.Success);
                var failedCount = applyVm.Items.Count(i => i.State == UpdateItemState.Failed);
                var skippedCount = applyVm.Items.Count(i => i.State == UpdateItemState.Skipped);

                UpdateCheckStatusText = $"Mise a jour terminee: {updatedCount} reussie(s), {failedCount} echec(s), {skippedCount} ignoree(s).";
                applyVm.SetHeaderStatus(UpdateCheckStatusText);
                applyVm.AppendLog(upgrade.StatusMessage);
            }
            catch (OperationCanceledException)
            {
                applyVm.MarkUnfinishedAs(UpdateItemState.Skipped, "Operation annulee par l'utilisateur.");
                UpdateCheckStatusText = "Mise a jour applicative annulee.";
                applyVm.SetHeaderStatus(UpdateCheckStatusText);
            }
            catch (Exception ex)
            {
                applyVm.MarkUnfinishedAs(UpdateItemState.Failed, "Erreur pendant l'application.");
                applyVm.AppendLog($"Erreur: {ex.Message}");
                UpdateCheckStatusText = $"Erreur pendant la mise a jour: {ex.Message}";
                applyVm.SetHeaderStatus("Execution terminee avec erreurs.");
            }
            finally
            {
                SetBusyState(false);
                RaiseInventoryCountersChanged();
            }
        }

        private void SetBusyState(bool isBusy)
        {
            _isCheckingUpdates = isBusy;
            OnPropertyChanged(nameof(IsCheckingUpdates));
            OnPropertyChanged(nameof(CanRunUpdateCheck));
            OnPropertyChanged(nameof(CheckUpdatesButtonLabel));
        }

        private void HandleWingetRealtimeEvent(UpdateApplyViewModel applyVm, WingetRealtimeEvent evt)
        {
            if (!string.IsNullOrWhiteSpace(evt.RawLine))
                applyVm.AppendLog(evt.RawLine);

            var resolved = ResolveAppUpdateItem(applyVm, evt.PackageId, evt.DisplayName);
            if (resolved == null && (evt.EventType == WingetRealtimeEventType.ItemStarted || evt.EventType == WingetRealtimeEventType.Progress))
            {
                var generatedId = !string.IsNullOrWhiteSpace(evt.PackageId)
                    ? evt.PackageId!
                    : string.IsNullOrWhiteSpace(evt.DisplayName)
                        ? Guid.NewGuid().ToString("N")
                        : evt.DisplayName!;
                resolved = applyVm.EnsureItem(generatedId, evt.DisplayName ?? generatedId, UpdateItemKind.App);
            }

            if (resolved == null)
                return;

            switch (evt.EventType)
            {
                case WingetRealtimeEventType.ItemStarted:
                    applyVm.MarkItemRunning(resolved.Id, resolved.DisplayName, "En cours", 50, UpdateItemKind.App);
                    break;

                case WingetRealtimeEventType.Progress:
                    applyVm.UpdateItemProgress(
                        resolved.Id,
                        evt.Percent ?? 50,
                        evt.Message,
                        resolved.DisplayName,
                        UpdateItemKind.App);
                    break;

                case WingetRealtimeEventType.ItemSucceeded:
                    applyVm.MarkItemSuccess(resolved.Id, resolved.DisplayName, "Mise a jour appliquee", UpdateItemKind.App);
                    break;

                case WingetRealtimeEventType.ItemFailed:
                    applyVm.MarkItemFailed(resolved.Id, resolved.DisplayName, evt.Message, UpdateItemKind.App);
                    break;
            }
        }

        private UpdateItemViewModel? ResolveAppUpdateItem(UpdateApplyViewModel applyVm, string? packageId, string? displayName)
        {
            if (!string.IsNullOrWhiteSpace(packageId))
            {
                var byId = applyVm.FindById(packageId);
                if (byId != null)
                    return byId;
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                var byName = applyVm.FindByDisplayName(displayName);
                if (byName != null)
                    return byName;
            }

            return null;
        }

        private void FinalizeUnresolvedItems(UpdateApplyViewModel applyVm, bool upgradeSuccess)
        {
            foreach (var item in applyVm.Items.Where(i => !i.IsTerminal).ToList())
            {
                var matchedApp = _allApps.FirstOrDefault(a =>
                    string.Equals(BuildAppItemId(a), item.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.Name, item.DisplayName, StringComparison.OrdinalIgnoreCase));

                if (matchedApp != null && matchedApp.HasUpdateAvailable)
                {
                    applyVm.MarkItemFailed(item.Id, item.DisplayName,
                        "Toujours signalee obsolete apres verification (causes possibles : cache winget, format de version non standard, ou mise a jour partielle).",
                        UpdateItemKind.App);
                    continue;
                }

                if (upgradeSuccess)
                    applyVm.MarkItemSuccess(item.Id, item.DisplayName, "Mise a jour appliquee ou deja a jour.", UpdateItemKind.App);
                else
                    applyVm.MarkItemFailed(item.Id, item.DisplayName,
                        "Etat final non confirme (winget a signale une erreur - verifiez manuellement ou relancez la mise a jour).",
                        UpdateItemKind.App);
            }
        }

        private static string BuildAppItemId(AppDisplayItem app)
        {
            if (!string.IsNullOrWhiteSpace(app.UpdatePackageId))
                return app.UpdatePackageId!;

            return app.Name;
        }

        public async Task<AppUpdatePipelineSummary?> RunUpdatePipelineAsync()
        {
            if (_isCheckingUpdates)
                return null;

            AppUpdatePipelineSummary? summary = null;
            await ExecuteBusyAsync(async () =>
            {
                UpdateCheckStatusText = "Actualisation de l'inventaire local...";
                EnrichFromRegistry();

                UpdateCheckStatusText = "Detection des mises a jour via winget...";
                var before = await DetectWingetStatesAsync().ConfigureAwait(true);
                if (!before.WingetAvailable)
                {
                    summary = new AppUpdatePipelineSummary
                    {
                        Success = false,
                        Message =
                            "winget est introuvable ou indisponible.\n\n" +
                            "Installez ou reparez 'App Installer' depuis Microsoft Store, puis relancez la mise a jour."
                    };
                    return;
                }

                var beforeUpdatable = before.UpdatableCount;
                if (beforeUpdatable <= 0)
                {
                    var notManagedNow = _allApps.Count(a => !a.IsManagedByAnySource);
                    summary = new AppUpdatePipelineSummary
                    {
                        Success = true,
                        Message =
                            "Aucune mise a jour winget a appliquer.\n\n" +
                            $"Mis a jour: 0\n" +
                            $"Echecs: 0\n" +
                            $"Non gerees: {notManagedNow}"
                    };
                    return;
                }

                UpdateCheckStatusText = $"Application des mises a jour ({beforeUpdatable})...";
                var upgrade = await _appUpdateCheckService.UpgradeAllAsync().ConfigureAwait(true);
                if (!upgrade.WingetAvailable)
                {
                    summary = new AppUpdatePipelineSummary
                    {
                        Success = false,
                        Message =
                            "Impossible d'executer winget upgrade.\n\n" +
                            upgrade.StatusMessage
                    };
                    return;
                }

                UpdateCheckStatusText = "Relecture de l'inventaire apres mise a jour...";
                EnrichFromRegistry();

                UpdateCheckStatusText = "Verification finale des mises a jour...";
                var after = await DetectWingetStatesAsync().ConfigureAwait(true);

                var updatedCount = Math.Max(0, beforeUpdatable - after.UpdatableCount);
                var failedCount = Math.Max(0, after.UpdatableCount);
                var notManagedCount = _allApps.Count(a => !a.IsManagedByAnySource);

                var details =
                    $"Mis a jour: {updatedCount}\n" +
                    $"Echecs: {failedCount}\n" +
                    $"Non gerees: {notManagedCount}";

                if (!upgrade.Success)
                    details += $"\n\nwinget: {upgrade.StatusMessage}";

                summary = new AppUpdatePipelineSummary
                {
                    Success = upgrade.Success || updatedCount > 0,
                    Message = details
                };

                UpdateCheckStatusText =
                    $"Mise a jour terminee: {updatedCount} appliquee(s), {failedCount} echec(s), {notManagedCount} non geree(s).";
            }).ConfigureAwait(true);

            return summary;
        }

        private async Task ExecuteBusyAsync(Func<Task> action)
        {
            SetBusyState(true);

            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                UpdateCheckStatusText = $"Erreur pendant la mise a jour: {ex.Message}";
            }
            finally
            {
                SetBusyState(false);
                RaiseInventoryCountersChanged();
            }
        }

        private async Task<WingetDetectionSnapshot> DetectWingetStatesAsync()
        {
            var managedResult = await _appUpdateCheckService.ListManagedAppsAsync().ConfigureAwait(true);
            if (!managedResult.WingetAvailable)
            {
                foreach (var app in _allApps)
                    app.BeginWingetCheck(wingetAvailable: false);

                RaiseInventoryCountersChanged();
                return new WingetDetectionSnapshot
                {
                    WingetAvailable = false,
                    StatusMessage = managedResult.StatusMessage,
                    ManagedCount = 0,
                    UpdatableCount = 0
                };
            }

            foreach (var app in _allApps)
                app.BeginWingetCheck(wingetAvailable: true);

            ApplyWingetManagedMatches(managedResult.Packages);

            var updatesResult = await _appUpdateCheckService.CheckAvailableUpdatesAsync().ConfigureAwait(true);
            if (!updatesResult.WingetAvailable)
            {
                foreach (var app in _allApps)
                    app.SetUnknownUpdateState();

                RaiseInventoryCountersChanged();
                return new WingetDetectionSnapshot
                {
                    WingetAvailable = false,
                    StatusMessage = updatesResult.StatusMessage,
                    ManagedCount = _allApps.Count(a => a.IsManagedByWinget),
                    UpdatableCount = 0
                };
            }

            ApplyWingetUpdateMatches(updatesResult.Updates);
            RaiseInventoryCountersChanged();

            return new WingetDetectionSnapshot
            {
                WingetAvailable = true,
                ManagedCount = _allApps.Count(a => a.IsManagedByWinget),
                UpdatableCount = _allApps.Count(a => a.HasUpdateAvailable),
                StatusMessage = updatesResult.StatusMessage
            };
        }

        private void AppItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(AppDisplayItem.IsSelectedForUninstall), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(HasSelectedForUninstall));
                OnPropertyChanged(nameof(SelectedForUninstallCount));
                OnPropertyChanged(nameof(UninstallButtonLabel));
                return;
            }

            if (!string.Equals(e.PropertyName, nameof(AppDisplayItem.HasUpdateAvailable), StringComparison.Ordinal) &&
                !string.Equals(e.PropertyName, nameof(AppDisplayItem.IsManagedByWinget), StringComparison.Ordinal) &&
                !string.Equals(e.PropertyName, nameof(AppDisplayItem.IsManagedByAnySource), StringComparison.Ordinal))
            {
                return;
            }

            OnPropertyChanged(nameof(UpdatableCount));
            OnPropertyChanged(nameof(HasUpdatableApps));
        }

        public List<AppDisplayItem> GetSelectedForUninstall()
            => _allApps.Where(a => a.IsSelectedForUninstall).ToList();

        private void EnrichFromRegistry()
        {
            var registry = RegistryInventorySnapshot.Read();
            var missingReason = registry.AccessDeniedDetected ? "AccessDenied" : "NotProvidedByRegistry";

            foreach (var app in _allApps)
            {
                var key = NormalizeName(app.Name);
                registry.ByNormalizedName.TryGetValue(key, out var metadata);
                app.ApplyRegistryMetadata(metadata, missingReason);
            }

            ApplyFilter();
            RaiseInventoryCountersChanged();
        }

        private void ApplyWingetManagedMatches(IReadOnlyList<AppPackageIdentity> managedPackages)
        {
            if (managedPackages == null || managedPackages.Count == 0)
                return;

            var bestByApp = new Dictionary<AppDisplayItem, (AppPackageIdentity pkg, int score)>();

            foreach (var pkg in managedPackages)
            {
                AppDisplayItem? bestApp = null;
                var bestScore = 0;

                foreach (var app in _allApps)
                {
                    var score = ComputeMatchScore(app, pkg.Name, pkg.PackageId, pkg.InstalledVersion);
                    if (score <= bestScore)
                        continue;

                    bestScore = score;
                    bestApp = app;
                }

                if (bestApp == null || bestScore < 35)
                    continue;

                if (!bestByApp.TryGetValue(bestApp, out var existing) || bestScore > existing.score)
                    bestByApp[bestApp] = (pkg, bestScore);
            }

            foreach (var pair in bestByApp)
                pair.Key.MarkWingetManaged(pair.Value.pkg.PackageId, pair.Value.pkg.Source);
        }

        private void ApplyWingetUpdateMatches(IReadOnlyList<AppUpdateCandidate> updates)
        {
            if (updates == null || updates.Count == 0)
                return;

            var bestByApp = new Dictionary<AppDisplayItem, (AppUpdateCandidate update, int score)>();

            foreach (var update in updates)
            {
                AppDisplayItem? bestApp = null;
                var bestScore = 0;

                foreach (var app in _allApps)
                {
                    var score = ComputeMatchScore(app, update.Name, update.PackageId, update.InstalledVersion);
                    if (app.IsManagedByWinget)
                        score += 10;

                    if (score <= bestScore)
                        continue;

                    bestScore = score;
                    bestApp = app;
                }

                if (bestApp == null || bestScore < 35)
                    continue;

                if (!bestByApp.TryGetValue(bestApp, out var existing) || bestScore > existing.score)
                    bestByApp[bestApp] = (update, bestScore);
            }

            foreach (var pair in bestByApp)
            {
                pair.Key.SetUpdateAvailable(
                    pair.Value.update.AvailableVersion,
                    pair.Value.update.Source,
                    pair.Value.update.PackageId);
            }
        }

        private static int ComputeMatchScore(AppDisplayItem app, string? candidateName, string? candidatePackageId, string? candidateInstalledVersion)
        {
            if (string.IsNullOrWhiteSpace(app.Name))
                return 0;

            var appName = NormalizeName(app.Name);
            var updateName = NormalizeName(candidateName);
            var packageId = NormalizeName(candidatePackageId);

            var score = 0;

            if (!string.IsNullOrWhiteSpace(updateName))
            {
                if (string.Equals(appName, updateName, StringComparison.Ordinal))
                    score += 50;
                else if (appName.Contains(updateName, StringComparison.Ordinal) || updateName.Contains(appName, StringComparison.Ordinal))
                    score += 30;
            }

            if (!string.IsNullOrWhiteSpace(packageId) && packageId.Contains(appName, StringComparison.Ordinal))
                score += 20;

            if (!string.IsNullOrWhiteSpace(app.Version) &&
                !string.IsNullOrWhiteSpace(candidateInstalledVersion) &&
                string.Equals(app.Version.Trim(), candidateInstalledVersion.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
            }

            return score;
        }

        private static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var lowered = value.ToLowerInvariant();
            var withoutVersionSuffix = NameCleanupRegex.Replace(lowered, " ");
            var alphaNum = new string(withoutVersionSuffix
                .Select(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ')
                .ToArray());
            return MultiSpaceRegex.Replace(alphaNum, " ").Trim();
        }

        private static List<AppDisplayItem> ReadAppsFromJson(JsonElement? appsData)
        {
            var apps = new List<AppDisplayItem>();
            if (!appsData.HasValue)
                return apps;

            JsonElement? appsList = null;

            if (appsData.Value.TryGetProperty("applications", out var appsProp))
                appsList = appsProp;
            else if (appsData.Value.TryGetProperty("apps", out var appsProp2))
                appsList = appsProp2;
            else if (appsData.Value.TryGetProperty("list", out var listProp))
                appsList = listProp;
            else if (appsData.Value.ValueKind == JsonValueKind.Array)
                appsList = appsData.Value;

            if (!appsList.HasValue || appsList.Value.ValueKind != JsonValueKind.Array)
                return apps;

            foreach (var app in appsList.Value.EnumerateArray())
            {
                var item = AppDisplayItem.FromJson(app);
                if (!string.IsNullOrWhiteSpace(item.Name))
                    apps.Add(item);
            }

            return apps;
        }

        private void ApplyFilter()
        {
            FilteredApplications.Clear();
            var search = SearchText?.ToLowerInvariant() ?? string.Empty;

            foreach (var app in _allApps)
            {
                if (!string.IsNullOrEmpty(search))
                {
                    var matchName = app.Name?.ToLowerInvariant().Contains(search) == true;
                    var matchPublisher = app.PublisherDisplay.ToLowerInvariant().Contains(search);
                    if (!matchName && !matchPublisher)
                        continue;
                }

                FilteredApplications.Add(app);
            }
        }

        private void RaiseInventoryCountersChanged()
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(RecentCount));
            OnPropertyChanged(nameof(HasRecent));
            OnPropertyChanged(nameof(UpdatableCount));
            OnPropertyChanged(nameof(HasUpdatableApps));
        }

        private static string? GetString(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
                return null;

            if (element.Value.TryGetProperty(propName, out var prop))
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();

            return null;
        }

        public sealed class UpdateApplyPreparationResult
        {
            public bool CanRun { get; set; }
            public bool IsError { get; set; }
            public string Message { get; set; } = string.Empty;
            public List<UpdateItemViewModel> Items { get; set; } = new();
        }

        private sealed class WingetDetectionSnapshot
        {
            public bool WingetAvailable { get; set; }
            public int ManagedCount { get; set; }
            public int UpdatableCount { get; set; }
            public string StatusMessage { get; set; } = string.Empty;
        }

        public sealed class RegistryAppMetadata
        {
            public string DisplayName { get; set; } = string.Empty;
            public string? Publisher { get; set; }
            public string? DisplayVersion { get; set; }
            public DateTime? InstallDate { get; set; }
            public string? InstallLocation { get; set; }
            public string? UninstallString { get; set; }
        }

        private sealed class RegistryInventorySnapshot
        {
            public Dictionary<string, RegistryAppMetadata> ByNormalizedName { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool AccessDeniedDetected { get; set; }

            public static RegistryInventorySnapshot Read()
            {
                var snapshot = new RegistryInventorySnapshot();
                var targets = new[]
                {
                    (RegistryHive.LocalMachine, RegistryView.Registry64),
                    (RegistryHive.LocalMachine, RegistryView.Registry32),
                    (RegistryHive.CurrentUser, RegistryView.Registry64),
                    (RegistryHive.CurrentUser, RegistryView.Registry32)
                };

                foreach (var (hive, view) in targets)
                    ReadUninstallKeys(snapshot, hive, view);

                return snapshot;
            }

            private static void ReadUninstallKeys(RegistryInventorySnapshot snapshot, RegistryHive hive, RegistryView view)
            {
                const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallRoot = baseKey.OpenSubKey(uninstallPath, writable: false);
                    if (uninstallRoot == null)
                        return;

                    foreach (var subKeyName in uninstallRoot.GetSubKeyNames())
                    {
                        using var appKey = uninstallRoot.OpenSubKey(subKeyName, writable: false);
                        if (appKey == null)
                            continue;

                        var displayName = appKey.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName))
                            continue;

                        var metadata = new RegistryAppMetadata
                        {
                            DisplayName = displayName.Trim(),
                            Publisher = (appKey.GetValue("Publisher") as string)?.Trim(),
                            DisplayVersion = (appKey.GetValue("DisplayVersion") as string)?.Trim(),
                            InstallDate = ParseRegistryInstallDate(appKey.GetValue("InstallDate") as string),
                            InstallLocation = (appKey.GetValue("InstallLocation") as string)?.Trim(),
                            UninstallString = (appKey.GetValue("UninstallString") as string)?.Trim()
                        };

                        var key = NormalizeName(metadata.DisplayName);
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        if (snapshot.ByNormalizedName.TryGetValue(key, out var existing))
                        {
                            if (ComputeMetadataScore(metadata) > ComputeMetadataScore(existing))
                                snapshot.ByNormalizedName[key] = metadata;
                        }
                        else
                        {
                            snapshot.ByNormalizedName[key] = metadata;
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    snapshot.AccessDeniedDetected = true;
                }
                catch (SecurityException)
                {
                    snapshot.AccessDeniedDetected = true;
                }
                catch
                {
                    // Ignore registry read errors (best effort).
                }
            }

            private static int ComputeMetadataScore(RegistryAppMetadata metadata)
            {
                var score = 0;
                if (!string.IsNullOrWhiteSpace(metadata.Publisher)) score += 2;
                if (!string.IsNullOrWhiteSpace(metadata.DisplayVersion)) score += 2;
                if (metadata.InstallDate.HasValue) score += 1;
                if (!string.IsNullOrWhiteSpace(metadata.InstallLocation)) score += 1;
                return score;
            }

            private static DateTime? ParseRegistryInstallDate(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return null;

                var formats = new[] { "yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "M/d/yyyy", "d/M/yyyy" };
                if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return dt;

                if (DateTime.TryParse(raw.Trim(), out dt))
                    return dt;

                return null;
            }
        }
    }

    /// <summary>
    /// App item used by the applications DataGrid.
    /// </summary>
    public class AppDisplayItem : INotifyPropertyChanged
    {
        private bool? _hasUpdateAvailable;
        private string? _availableVersion;
        private string? _updateSource;
        private string? _updatePackageId;
        private bool _wingetChecked;
        private bool _wingetAvailableLastCheck;
        private bool _isWingetManaged;
        private bool _isSelectedForUninstall;

        private string? _publisher;
        private string? _version;
        private DateTime? _installDate;
        private string _metadataMissingReason = "NotProvidedByRegistry";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string Name { get; }
        public string? Publisher => _publisher;
        public string? Version => _version;
        public DateTime? InstallDate => _installDate;
        public string? UpdatePackageId => _updatePackageId;
        public string? Source { get; private set; }
        public bool IsStoreManaged { get; private set; }
        public string? UninstallString { get; private set; }
        public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallString);

        public bool IsSelectedForUninstall
        {
            get => _isSelectedForUninstall;
            set
            {
                if (_isSelectedForUninstall == value) return;
                _isSelectedForUninstall = value;
                OnPropertyChanged(nameof(IsSelectedForUninstall));
            }
        }

        public bool HasUpdateAvailable => _hasUpdateAvailable == true;
        public bool IsManagedByWinget => _isWingetManaged;
        public bool IsManagedByAnySource => _isWingetManaged || IsStoreManaged;

        public string PublisherDisplay => ToUserFacingValue(string.IsNullOrWhiteSpace(_publisher)
            ? $"Indisponible ({_metadataMissingReason})"
            : _publisher);

        public string VersionDisplay => ToUserFacingValue(string.IsNullOrWhiteSpace(_version)
            ? $"Indisponible ({_metadataMissingReason})"
            : _version);

        public string InstallDateFormatted => ToUserFacingValue(_installDate.HasValue
            ? _installDate.Value.ToString("yyyy-MM-dd")
            : $"Indisponible ({_metadataMissingReason})");

        public bool IsRecent => _installDate.HasValue && (DateTime.Now - _installDate.Value).TotalDays <= 30;

        public string AvailableVersionDisplay
        {
            get
            {
                if (_hasUpdateAvailable == true)
                    return ToUserFacingValue(string.IsNullOrWhiteSpace(_availableVersion) ? "Indisponible" : _availableVersion!);
                if (!_wingetChecked)
                    return ToUserFacingValue("Indisponible (NotChecked)");
                if (!_wingetAvailableLastCheck)
                    return ToUserFacingValue("Indisponible (WingetMissing)");
                if (IsStoreManaged && !_isWingetManaged)
                    return "Gere par Microsoft Store";
                if (_isWingetManaged)
                    return "-";
                return ToUserFacingValue("Indisponible (NotManaged)");
            }
        }

        public string UpdateStatus => ToUserFacingValue(_hasUpdateAvailable switch
        {
            true => "MAJ disponible",
            false => ResolveNoUpdateStatus(),
            _ => _wingetAvailableLastCheck ? "Indisponible (NotChecked)" : "Indisponible (WingetMissing)"
        });

        public static AppDisplayItem FromJson(JsonElement app)
        {
            var name = GetString(app, "name") ?? GetString(app, "displayName") ?? string.Empty;
            var item = new AppDisplayItem(name)
            {
                _publisher = GetString(app, "publisher") ?? GetString(app, "vendor"),
                _version = GetString(app, "version") ?? GetString(app, "displayVersion"),
                Source = DetermineSource(app)
            };

            item.IsStoreManaged = IsSourceStoreManaged(item.Source);

            var dateStr = GetString(app, "installDate") ?? GetString(app, "InstallDate");
            if (!string.IsNullOrWhiteSpace(dateStr) && TryParseDate(dateStr, out var parsed))
                item._installDate = parsed;

            item.SetUnknownUpdateState();
            return item;
        }

        private AppDisplayItem(string name)
        {
            Name = name;
        }

        public void ApplyRegistryMetadata(AppsDetailsViewModel.RegistryAppMetadata? metadata, string missingReason)
        {
            if (metadata == null)
            {
                _metadataMissingReason = string.IsNullOrWhiteSpace(missingReason) ? "NotProvidedByRegistry" : missingReason;
                RaiseMetadataChanged();
                return;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Publisher))
                _publisher = metadata.Publisher;
            if (!string.IsNullOrWhiteSpace(metadata.DisplayVersion))
                _version = metadata.DisplayVersion;
            if (metadata.InstallDate.HasValue)
                _installDate = metadata.InstallDate;

            if (string.IsNullOrWhiteSpace(Source))
            {
                if (!string.IsNullOrWhiteSpace(metadata.UninstallString) && metadata.UninstallString.Contains("MsiExec", StringComparison.OrdinalIgnoreCase))
                    Source = "MSI";
                else if (!string.IsNullOrWhiteSpace(metadata.InstallLocation) && metadata.InstallLocation.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    Source = "Store";
            }

            if (!string.IsNullOrWhiteSpace(metadata.UninstallString))
                UninstallString = metadata.UninstallString;

            IsStoreManaged = IsSourceStoreManaged(Source);
            _metadataMissingReason = "NotProvidedByRegistry";
            RaiseMetadataChanged();
        }

        public void BeginWingetCheck(bool wingetAvailable)
        {
            _wingetChecked = true;
            _wingetAvailableLastCheck = wingetAvailable;
            _isWingetManaged = false;
            _hasUpdateAvailable = wingetAvailable ? false : null;
            _availableVersion = null;
            _updateSource = null;
            _updatePackageId = null;
            RaiseUpdateStateChanged();
        }

        public void MarkWingetManaged(string? packageId, string? source)
        {
            _isWingetManaged = true;
            if (!string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(Source))
                Source = source;
            if (IsSourceStoreManaged(source))
                IsStoreManaged = true;
            _updatePackageId = packageId;
            OnPropertyChanged(nameof(IsManagedByWinget));
            OnPropertyChanged(nameof(IsManagedByAnySource));
            RaiseUpdateStateChanged();
        }

        public void SetUpdateAvailable(string? availableVersion, string? updateSource, string? packageId)
        {
            _hasUpdateAvailable = true;
            _availableVersion = availableVersion;
            _updateSource = updateSource;
            _updatePackageId = packageId;
            _isWingetManaged = true;
            _wingetChecked = true;
            _wingetAvailableLastCheck = true;
            RaiseUpdateStateChanged();
        }

        public void SetUnknownUpdateState()
        {
            _hasUpdateAvailable = null;
            _availableVersion = null;
            _updateSource = null;
            _updatePackageId = null;
            RaiseUpdateStateChanged();
        }

        private string ResolveNoUpdateStatus()
        {
            if (!_wingetAvailableLastCheck)
                return "Indisponible (WingetMissing)";
            if (IsStoreManaged && !_isWingetManaged)
                return "Gere par Microsoft Store";
            return _isWingetManaged ? "A jour" : "Non gere";
        }

        private static string ToUserFacingValue(string? value)
        {
            return TextEncodingNormalizer.ToUserFacingValue(value);
        }

        private void RaiseMetadataChanged()
        {
            OnPropertyChanged(nameof(Publisher));
            OnPropertyChanged(nameof(Version));
            OnPropertyChanged(nameof(InstallDate));
            OnPropertyChanged(nameof(PublisherDisplay));
            OnPropertyChanged(nameof(VersionDisplay));
            OnPropertyChanged(nameof(InstallDateFormatted));
            OnPropertyChanged(nameof(IsRecent));
        }

        private void RaiseUpdateStateChanged()
        {
            OnPropertyChanged(nameof(HasUpdateAvailable));
            OnPropertyChanged(nameof(AvailableVersionDisplay));
            OnPropertyChanged(nameof(UpdateStatus));
            OnPropertyChanged(nameof(IsManagedByWinget));
            OnPropertyChanged(nameof(IsManagedByAnySource));
        }

        private static bool TryParseDate(string input, out DateTime dt)
        {
            if (DateTime.TryParse(input, out dt))
                return true;

            if (input.Length >= 8)
            {
                try
                {
                    var y = int.Parse(input.Substring(0, 4));
                    var m = int.Parse(input.Substring(4, 2));
                    var d = int.Parse(input.Substring(6, 2));
                    dt = new DateTime(y, m, d);
                    return true;
                }
                catch
                {
                    // Ignore malformed date.
                }
            }

            dt = default;
            return false;
        }

        private static string? GetString(JsonElement element, string propName)
        {
            if (element.TryGetProperty(propName, out var prop))
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

            return null;
        }

        private static string? DetermineSource(JsonElement app)
        {
            var source = GetString(app, "source") ?? GetString(app, "installSource");
            if (!string.IsNullOrWhiteSpace(source))
                return source;

            var uninstallString = GetString(app, "uninstallString") ?? string.Empty;
            var installLocation = GetString(app, "installLocation") ?? string.Empty;

            if (uninstallString.Contains("MsiExec", StringComparison.OrdinalIgnoreCase))
                return "MSI";
            if (installLocation.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                return "Store";
            if (uninstallString.Contains("winget", StringComparison.OrdinalIgnoreCase))
                return "Winget";

            return "Unknown";
        }

        private static bool IsSourceStoreManaged(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return false;

            return source.Contains("store", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("msstore", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("appx", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("msix", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class AppUpdatePipelineSummary
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
