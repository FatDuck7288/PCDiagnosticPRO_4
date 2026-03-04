using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Fenetre de details des pilotes - affiche un DataGrid avec tous les pilotes installes.
    /// </summary>
    public partial class DriversDetailsWindow : Window
    {
        private readonly DriverUpdateInstallerService _driverUpdateInstallerService = new();

        public DriversDetailsWindow(DriverInventoryResult? driverInventory)
        {
            InitializeComponent();
            DataContext = new DriversDetailsViewModel(driverInventory);
        }

        private void UpdateDriversButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DriversDetailsViewModel vm)
                return;

            var preparation = vm.PrepareUpdateApply();
            if (!preparation.CanRun)
            {
                var icon = preparation.IsError ? MessageBoxImage.Warning : MessageBoxImage.Information;
                MessageBox.Show(
                    preparation.Message,
                    "Mise à jour des pilotes",
                    MessageBoxButton.OK,
                    icon);
                return;
            }

            var confirmation = MessageBox.Show(
                $"Installer les mises à jour Windows pour {preparation.SelectedDrivers.Count} pilote(s) sélectionné(s) ?",
                "Confirmer la mise à jour",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmation != MessageBoxResult.Yes)
                return;

            var applyVm = new UpdateApplyViewModel(
                preparation.Items,
                (windowVm, ct) => ExecuteDriverUpdateApplyAsync(vm, windowVm, preparation.SelectedDrivers, ct),
                "Application des mises a jour");

            var applyWindow = new UpdateApplyWindow
            {
                Owner = this,
                DataContext = applyVm
            };

            applyWindow.ShowDialog();
        }

        private async Task ExecuteDriverUpdateApplyAsync(
            DriversDetailsViewModel vm,
            UpdateApplyViewModel applyVm,
            IReadOnlyList<DriverInventoryItem> selectedDrivers,
            CancellationToken cancellationToken)
        {
            vm.SetUpdatingState(true);
            vm.SetUpdateRunStatus($"Application des mises a jour ({selectedDrivers.Count})...");
            applyVm.SetHeaderStatus(vm.UpdateRunStatusText);
            applyVm.AppendLog("Demarrage du pipeline Windows Update Agent.");

            try
            {
                var progress = new Progress<DriverUpdateProgressEvent>(evt => HandleDriverProgressEvent(applyVm, evt));
                var result = await _driverUpdateInstallerService
                    .InstallSelectedDriverUpdatesAsync(
                        selectedDrivers,
                        onlineSearch: true,
                        cancellationToken: cancellationToken,
                        progress: progress)
                    .ConfigureAwait(true);

                if (result.RequiresElevation)
                {
                    applyVm.MarkUnfinishedAs(UpdateItemState.Failed, "Droits administrateur requis.");
                    applyVm.AppendLog(result.Message);
                    vm.SetUpdateRunStatus("Droits administrateur requis pour la mise a jour.");
                    applyVm.SetHeaderStatus(vm.UpdateRunStatusText);

                    var relaunch = MessageBox.Show(
                        "La mise a jour des pilotes necessite des droits administrateur. Voulez-vous relancer l'application en mode administrateur ?",
                        "Droits administrateur requis",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (relaunch == MessageBoxResult.Yes)
                        AdminHelper.RestartAsAdmin();

                    return;
                }

                ApplyDriverItemResults(applyVm, result.ItemResults);

                if (!result.Success && result.MatchedCount == 0)
                {
                    applyVm.AppendLog(vm.BuildNoUpdatesFoundDialogMessage());
                }

                var unresolved = applyVm.Items.Where(i => !i.IsTerminal).ToList();
                foreach (var item in unresolved)
                {
                    if (result.Success)
                        applyVm.MarkItemSuccess(item.Id, item.DisplayName, "Mise a jour appliquee ou deja a jour.", UpdateItemKind.Driver);
                    else
                        applyVm.MarkItemSkipped(item.Id, item.DisplayName, "Etat final non confirme.", UpdateItemKind.Driver);
                }

                vm.SetUpdateRunStatus(result.Message);
                applyVm.SetHeaderStatus(result.Message);
                applyVm.AppendLog(result.Message);
                applyVm.AppendLog($"Source des mises a jour: {result.SourceDatabase}");

                if (result.Success)
                    vm.ClearUpdateSelection();
            }
            catch (OperationCanceledException)
            {
                applyVm.MarkUnfinishedAs(UpdateItemState.Skipped, "Operation annulee par l'utilisateur.");
                vm.SetUpdateRunStatus("Mise a jour des pilotes annulee.");
                applyVm.SetHeaderStatus(vm.UpdateRunStatusText);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DriversDetails] Update error: {ex.Message}");
                applyVm.MarkUnfinishedAs(UpdateItemState.Failed, "Erreur pendant l'application.");
                applyVm.AppendLog($"Erreur: {ex.Message}");
                vm.SetUpdateRunStatus($"Erreur de mise a jour: {ex.Message}");
                applyVm.SetHeaderStatus("Execution terminee avec erreurs.");
            }
            finally
            {
                vm.SetUpdatingState(false);
                vm.RefreshIdleUpdateStatus();
            }
        }

        private static void HandleDriverProgressEvent(UpdateApplyViewModel applyVm, DriverUpdateProgressEvent evt)
        {
            if (!string.IsNullOrWhiteSpace(evt.Message))
                applyVm.AppendLog(evt.Message);

            if (evt.EventType == DriverUpdateProgressEventType.Info)
                return;

            var resolved = ResolveDriverItem(applyVm, evt.ItemId, evt.DisplayName);
            if (resolved == null)
            {
                var generatedId = !string.IsNullOrWhiteSpace(evt.ItemId)
                    ? evt.ItemId
                    : string.IsNullOrWhiteSpace(evt.DisplayName)
                        ? Guid.NewGuid().ToString("N")
                        : evt.DisplayName;
                resolved = applyVm.EnsureItem(generatedId!, evt.DisplayName ?? generatedId!, UpdateItemKind.Driver);
            }

            switch (evt.EventType)
            {
                case DriverUpdateProgressEventType.ItemStarted:
                    applyVm.MarkItemRunning(resolved.Id, resolved.DisplayName, evt.Message, evt.Percent ?? 50, UpdateItemKind.Driver);
                    break;

                case DriverUpdateProgressEventType.Progress:
                    applyVm.UpdateItemProgress(resolved.Id, evt.Percent ?? 50, evt.Message, resolved.DisplayName, UpdateItemKind.Driver);
                    break;

                case DriverUpdateProgressEventType.ItemSucceeded:
                    applyVm.MarkItemSuccess(resolved.Id, resolved.DisplayName, evt.Message, UpdateItemKind.Driver);
                    break;

                case DriverUpdateProgressEventType.ItemFailed:
                    applyVm.MarkItemFailed(resolved.Id, resolved.DisplayName, evt.Message, UpdateItemKind.Driver);
                    break;

                case DriverUpdateProgressEventType.ItemSkipped:
                    applyVm.MarkItemSkipped(resolved.Id, resolved.DisplayName, evt.Message, UpdateItemKind.Driver);
                    break;
            }
        }

        private static UpdateItemViewModel? ResolveDriverItem(UpdateApplyViewModel applyVm, string? itemId, string? displayName)
        {
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                var byId = applyVm.FindById(itemId);
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

        private static void ApplyDriverItemResults(UpdateApplyViewModel applyVm, IReadOnlyList<DriverUpdateItemResult>? results)
        {
            if (results == null || results.Count == 0)
                return;

            foreach (var result in results)
            {
                var itemId = string.IsNullOrWhiteSpace(result.ItemId) ? Guid.NewGuid().ToString("N") : result.ItemId;
                var displayName = string.IsNullOrWhiteSpace(result.DisplayName) ? itemId : result.DisplayName;
                switch (result.State)
                {
                    case DriverUpdateItemState.Success:
                        applyVm.MarkItemSuccess(itemId, displayName, result.Message, UpdateItemKind.Driver);
                        break;

                    case DriverUpdateItemState.Failed:
                        applyVm.MarkItemFailed(itemId, displayName, result.Message, UpdateItemKind.Driver);
                        break;

                    case DriverUpdateItemState.Skipped:
                        applyVm.MarkItemSkipped(itemId, displayName, result.Message, UpdateItemKind.Driver);
                        break;

                    case DriverUpdateItemState.Running:
                        applyVm.MarkItemRunning(itemId, displayName, result.Message, 50, UpdateItemKind.Driver);
                        break;

                    default:
                        applyVm.MarkItemQueued(itemId, displayName, result.Message, UpdateItemKind.Driver);
                        break;
                }
            }
        }

        private void SelectAllOutdatedButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DriversDetailsViewModel vm)
                return;

            vm.SelectAllOutdatedDrivers();
        }

        private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not DriversDetailsViewModel vm)
                return;

            vm.ClearUpdateSelection();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    /// <summary>
    /// ViewModel pour la fenetre de details des pilotes.
    /// </summary>
    public class DriversDetailsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly List<DriverDisplayItem> _allDrivers = new();
        private bool _isUpdatingDrivers;
        private string _updateRunStatusText = "Selectionnez des pilotes puis lancez la mise a jour.";

        public ObservableCollection<DriverDisplayItem> Drivers { get; } = new();
        public ObservableCollection<DriverDisplayItem> FilteredDrivers { get; } = new();
        public ObservableCollection<string> DeviceClasses { get; } = new();

        private string _searchText = "";
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

        private string _selectedClass = "Toutes";
        public string SelectedClass
        {
            get => _selectedClass;
            set
            {
                _selectedClass = value;
                OnPropertyChanged(nameof(SelectedClass));
                ApplyFilter();
            }
        }

        public int TotalCount { get; }
        public int SignedCount { get; }
        public int UnsignedCount { get; }
        public int ProblemCount { get; }
        public int OldAgeCount { get; }
        public int UpdatesFoundCount { get; }
        public int NonVerifiableCount { get; }
        public int SelectedForUpdateCount => _allDrivers.Count(d => d.IsSelectedForUpdate);

        public bool IsUpdatingDrivers => _isUpdatingDrivers;
        public bool HasUnsigned => UnsignedCount > 0;
        public bool HasProblems => ProblemCount > 0;
        public bool HasOldAge => OldAgeCount > 0;
        public bool HasUpdatesFound => UpdatesFoundCount > 0;
        public bool HasNonVerifiable => NonVerifiableCount > 0;
        public bool CanExecuteUpdate => !_isUpdatingDrivers && SelectedForUpdateCount > 0;
        public string UpdateButtonLabel => _isUpdatingDrivers ? "Mise à jour..." : $"Mise à jour ({SelectedForUpdateCount})";
        public string UpdateRunStatusText
        {
            get => _updateRunStatusText;
            private set
            {
                if (string.Equals(_updateRunStatusText, value, StringComparison.Ordinal))
                    return;

                _updateRunStatusText = value;
                OnPropertyChanged(nameof(UpdateRunStatusText));
            }
        }

        public string VerificationRuleNote =>
            $"Regle: Ancien = age pilote > {DriverStatusEvaluator.AgeThresholdMonths} mois. " +
            "Mise a jour trouvee = correspondance Windows Update par Hardware ID. " +
            "Non verifiable = identifiant local insuffisant ou Windows Update indisponible.";

        public string SummaryText { get; }

        public DriversDetailsViewModel(DriverInventoryResult? inventory)
        {
            DeviceClasses.Add("Toutes");

            if (inventory?.Drivers == null || !inventory.Available)
            {
                SummaryText = "Aucune donnee de pilotes disponible";
                return;
            }

            TotalCount = inventory.TotalCount;
            SignedCount = inventory.SignedCount;
            UnsignedCount = inventory.UnsignedCount;
            ProblemCount = inventory.ProblemCount;
            OldAgeCount = inventory.Drivers.Count(IsDriverOldByAge);
            UpdatesFoundCount = inventory.Drivers.Count(HasWindowsUpdateFound);
            NonVerifiableCount = inventory.Drivers.Count(d => string.Equals(d.UpdateAvailability, "NotVerifiable", StringComparison.OrdinalIgnoreCase));

            var mode = string.IsNullOrWhiteSpace(inventory.UpdateSearchMode) ? "Non precise" : inventory.UpdateSearchMode;
            SummaryText = $"Source: {inventory.Source} | Collecte: {inventory.Timestamp} | Verification WU: {mode}";

            var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var driver in inventory.Drivers)
            {
                var item = new DriverDisplayItem(driver);
                item.PropertyChanged += DriverItem_PropertyChanged;
                _allDrivers.Add(item);
                Drivers.Add(item);

                if (!string.IsNullOrWhiteSpace(driver.DeviceClass))
                    classes.Add(driver.DeviceClass);
            }

            foreach (var cls in classes.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
                DeviceClasses.Add(cls);

            ApplyFilter();
            RefreshIdleUpdateStatus();
        }

        public IReadOnlyList<DriverInventoryItem> GetSelectedDriversForUpdate()
        {
            return _allDrivers
                .Where(d => d.IsSelectedForUpdate)
                .Select(d => d.SourceDriver)
                .ToList();
        }

        public DriverUpdateApplyPreparationResult PrepareUpdateApply()
        {
            var selectedDrivers = GetSelectedDriversForUpdate().ToList();
            if (selectedDrivers.Count == 0)
            {
                return new DriverUpdateApplyPreparationResult
                {
                    CanRun = false,
                    IsError = false,
                    Message = "Cochez au moins un pilote obsolete ou avec \"Mise a jour trouvee\" avant de lancer l'installation."
                };
            }

            var items = selectedDrivers
                .Select(driver => new UpdateItemViewModel(
                    BuildDriverItemId(driver),
                    string.IsNullOrWhiteSpace(driver.DeviceName) ? "Pilote" : driver.DeviceName,
                    UpdateItemKind.Driver,
                    "En attente"))
                .ToList();

            return new DriverUpdateApplyPreparationResult
            {
                CanRun = true,
                IsError = false,
                Message = $"{items.Count} mise(s) a jour pilote prete(s) a etre appliquee(s).",
                SelectedDrivers = selectedDrivers,
                Items = items
            };
        }

        public void SelectAllOutdatedDrivers()
        {
            foreach (var driver in _allDrivers.Where(d => d.CanSelectForUpdate && !d.IsSelectedForUpdate))
                driver.IsSelectedForUpdate = true;

            OnPropertyChanged(nameof(SelectedForUpdateCount));
            OnPropertyChanged(nameof(CanExecuteUpdate));
            OnPropertyChanged(nameof(UpdateButtonLabel));
            RefreshIdleUpdateStatus();
        }

        public string BuildNoUpdatesFoundDialogMessage()
        {
            var lines = new List<string>
            {
                "Aucune mise a jour Windows Update n'a ete trouvee pour les pilotes coches.",
                string.Empty,
                $"Les pilotes marques 'Ancien' sont bases sur l'age (> {DriverStatusEvaluator.AgeThresholdMonths} mois). Cela ne garantit pas qu'une mise a jour existe."
            };

            if (HasNonVerifiable)
            {
                lines.Add(string.Empty);
                lines.Add($"Pilotes non verifiables: {NonVerifiableCount}.");

                var reasons = _allDrivers
                    .Where(d => string.Equals(d.SourceDriver.UpdateAvailability, "NotVerifiable", StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.SourceDriver.UpdateAvailabilityReason)
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();

                foreach (var reason in reasons)
                    lines.Add($"- {reason}");
            }

            lines.Add(string.Empty);
            lines.Add("Action recommandee: verifier le site du fabricant pour les pilotes non verifiables.");
            return string.Join(Environment.NewLine, lines);
        }

        public void ClearUpdateSelection()
        {
            foreach (var driver in _allDrivers.Where(d => d.IsSelectedForUpdate))
                driver.IsSelectedForUpdate = false;

            OnPropertyChanged(nameof(SelectedForUpdateCount));
            OnPropertyChanged(nameof(CanExecuteUpdate));
            OnPropertyChanged(nameof(UpdateButtonLabel));
            RefreshIdleUpdateStatus();
        }

        public void SetUpdateRunStatus(string text)
        {
            UpdateRunStatusText = string.IsNullOrWhiteSpace(text)
                ? "Selectionnez des pilotes puis lancez la mise a jour."
                : text;
        }

        public void RefreshIdleUpdateStatus()
        {
            if (_isUpdatingDrivers)
                return;

            if (SelectedForUpdateCount > 0)
            {
                UpdateRunStatusText = $"{SelectedForUpdateCount} pilote(s) selectionne(s) pour la mise a jour.";
                return;
            }

            if (UpdatesFoundCount > 0)
            {
                UpdateRunStatusText = $"Mises a jour detectees: {UpdatesFoundCount}. Selectionnez les pilotes a appliquer.";
                return;
            }

            UpdateRunStatusText = "Aucune mise a jour pilote selectionnee.";
        }

        public void SetUpdatingState(bool isUpdating)
        {
            if (_isUpdatingDrivers == isUpdating)
                return;

            _isUpdatingDrivers = isUpdating;
            OnPropertyChanged(nameof(IsUpdatingDrivers));
            OnPropertyChanged(nameof(CanExecuteUpdate));
            OnPropertyChanged(nameof(UpdateButtonLabel));
            if (!_isUpdatingDrivers)
                RefreshIdleUpdateStatus();
        }

        private void DriverItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(DriverDisplayItem.IsSelectedForUpdate), StringComparison.Ordinal))
                return;

            OnPropertyChanged(nameof(SelectedForUpdateCount));
            OnPropertyChanged(nameof(CanExecuteUpdate));
            OnPropertyChanged(nameof(UpdateButtonLabel));
            RefreshIdleUpdateStatus();
        }

        private void ApplyFilter()
        {
            FilteredDrivers.Clear();
            var search = SearchText?.ToLowerInvariant() ?? string.Empty;

            var query = _allDrivers.Where(driver =>
            {
                if (SelectedClass != "Toutes" &&
                    !string.Equals(driver.DeviceClass, SelectedClass, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.IsNullOrEmpty(search))
                    return true;

                var matchName = driver.DeviceName?.ToLowerInvariant().Contains(search) == true;
                var matchClass = driver.DeviceClass?.ToLowerInvariant().Contains(search) == true;
                var matchProvider = driver.Provider?.ToLowerInvariant().Contains(search) == true;
                return matchName || matchClass || matchProvider;
            });

            foreach (var driver in query
                         .OrderByDescending(d => d.CanInstallFromWindowsUpdate)
                         .ThenByDescending(d => d.IsOldByAge)
                         .ThenBy(d => d.DeviceClass, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase))
            {
                FilteredDrivers.Add(driver);
            }
        }

        private static bool IsDriverOldByAge(DriverInventoryItem driver)
        {
            if (driver.IsOldByAge.HasValue)
                return driver.IsOldByAge.Value;

            return !string.IsNullOrEmpty(driver.DriverDate) &&
                   DateTime.TryParse(driver.DriverDate, out var date) &&
                   (DateTime.Now - date).TotalDays > DriverStatusEvaluator.AgeThresholdMonths * 30.0;
        }

        private static bool HasWindowsUpdateFound(DriverInventoryItem driver)
        {
            return string.Equals(driver.UpdateAvailability, "Found", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDriverItemId(DriverInventoryItem driver)
        {
            if (!string.IsNullOrWhiteSpace(driver.PnpDeviceId))
                return driver.PnpDeviceId.Trim();
            if (!string.IsNullOrWhiteSpace(driver.DeviceName))
                return driver.DeviceName.Trim();
            if (!string.IsNullOrWhiteSpace(driver.InfName))
                return driver.InfName.Trim();
            if (!string.IsNullOrWhiteSpace(driver.UpdateMatch?.Title))
                return driver.UpdateMatch.Title.Trim();

            return $"driver-{Guid.NewGuid():N}";
        }

        public sealed class DriverUpdateApplyPreparationResult
        {
            public bool CanRun { get; set; }
            public bool IsError { get; set; }
            public string Message { get; set; } = string.Empty;
            public List<DriverInventoryItem> SelectedDrivers { get; set; } = new();
            public List<UpdateItemViewModel> Items { get; set; } = new();
        }
    }

    /// <summary>
    /// Item de pilote pour affichage dans le DataGrid.
    /// </summary>
    public class DriverDisplayItem : INotifyPropertyChanged
    {
        private readonly DriverInventoryItem _driver;
        private bool _isSelectedForUpdate;

        public DriverDisplayItem(DriverInventoryItem driver)
        {
            _driver = driver;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public DriverInventoryItem SourceDriver => _driver;
        public string DeviceClass => _driver.DeviceClass;
        public string DeviceName => _driver.DeviceName;
        public string? Provider => TextEncodingNormalizer.ToUserFacingValue(_driver.Provider ?? _driver.Manufacturer);
        public string? DriverVersion => TextEncodingNormalizer.ToUserFacingValue(_driver.DriverVersion ?? "-");
        public string? InfName => _driver.InfName;
        public string? PnpDeviceId => _driver.PnpDeviceId;
        public bool IsOldByAge => IsDriverOldByAge(_driver);
        public bool HasUpdateFound => string.Equals(_driver.UpdateAvailability, "Found", StringComparison.OrdinalIgnoreCase);
        public bool CanInstallFromWindowsUpdate => HasUpdateFound;
        public bool CanSelectForUpdate => IsOldByAge || CanInstallFromWindowsUpdate;
        public bool NeedsUpdate => CanSelectForUpdate;

        public bool IsSelectedForUpdate
        {
            get => _isSelectedForUpdate;
            set
            {
                if (value && !CanSelectForUpdate)
                    return;

                if (_isSelectedForUpdate == value)
                    return;

                _isSelectedForUpdate = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedForUpdate)));
            }
        }

        public string DriverDateFormatted
        {
            get
            {
                if (string.IsNullOrEmpty(_driver.DriverDate))
                    return TextEncodingNormalizer.ToUserFacingValue("-");

                if (DateTime.TryParse(_driver.DriverDate, out var date))
                    return TextEncodingNormalizer.ToUserFacingValue(date.ToString("yyyy-MM-dd"));

                if (_driver.DriverDate.Length >= 8)
                {
                    try
                    {
                        var compactDate = $"{_driver.DriverDate.Substring(0, 4)}-{_driver.DriverDate.Substring(4, 2)}-{_driver.DriverDate.Substring(6, 2)}";
                        return TextEncodingNormalizer.ToUserFacingValue(compactDate);
                    }
                    catch
                    {
                        // Ignore malformed date, fallback below.
                    }
                }

                return TextEncodingNormalizer.ToUserFacingValue(_driver.DriverDate);
            }
        }

        public string SignedIcon => _driver.IsSigned switch
        {
            true => "OK",
            false => "!",
            _ => "?"
        };

        public string StatusDisplay
        {
            get
            {
                if (_driver.Status?.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    _driver.Status?.IndexOf("problem", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "Erreur";
                }

                if (IsOldByAge)
                    return "Ancien";

                return "OK";
            }
        }

        public string UpdateAvailabilityDisplay
        {
            get
            {
                if (string.Equals(_driver.UpdateAvailability, "Found", StringComparison.OrdinalIgnoreCase))
                    return "Trouvee";

                if (string.Equals(_driver.UpdateAvailability, "NotVerifiable", StringComparison.OrdinalIgnoreCase))
                    return "Non verifiable";

                if (string.Equals(_driver.UpdateAvailability, "NotFound", StringComparison.OrdinalIgnoreCase))
                    return "Non trouvee";

                return "Non verifiable";
            }
        }

        public string UpdateAvailabilityTooltip
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_driver.UpdateAvailabilityReason))
                    return _driver.UpdateAvailabilityReason!;

                return UpdateAvailabilityDisplay switch
                {
                    "Trouvee" => "Mise a jour candidate trouvee via Windows Update.",
                    "Non trouvee" => "Aucune mise a jour Windows Update detectee pour ce pilote.",
                    _ => "Verification impossible (identifiants pilotes insuffisants ou Windows Update indisponible)."
                };
            }
        }

        public Brush StatusColor
        {
            get
            {
                var status = StatusDisplay;
                if (status.Contains("Erreur", StringComparison.OrdinalIgnoreCase))
                    return new SolidColorBrush(Color.FromRgb(255, 71, 87));

                if (status.Contains("Ancien", StringComparison.OrdinalIgnoreCase))
                    return new SolidColorBrush(Color.FromRgb(155, 89, 182));

                return new SolidColorBrush(Color.FromRgb(46, 213, 115));
            }
        }

        private static bool IsDriverOldByAge(DriverInventoryItem driver)
        {
            if (driver.IsOldByAge.HasValue)
                return driver.IsOldByAge.Value;

            return !string.IsNullOrEmpty(driver.DriverDate) &&
                   DateTime.TryParse(driver.DriverDate, out var date) &&
                   (DateTime.Now - date).TotalDays > DriverStatusEvaluator.AgeThresholdMonths * 30.0;
        }
    }
}
