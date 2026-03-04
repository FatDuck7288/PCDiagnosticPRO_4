using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Views
{
    public partial class RestorePointsWindow : Window
    {
        private readonly RestorePointService _restorePointService;

        public RestorePointsWindow(RestorePointDialogData details, RestorePointService restorePointService)
        {
            InitializeComponent();
            _restorePointService = restorePointService;
            DataContext = new RestorePointsWindowViewModel(details ?? new RestorePointDialogData());
            
            // FIX #4: Charger les données temps réel au démarrage de la fenêtre
            // au lieu de dépendre uniquement du cache JSON
            Loaded += async (s, e) => await RefreshRestorePointDetailsOnLoadAsync();
        }
        
        /// <summary>
        /// Rafraîchit les données de points de restauration depuis le système au chargement.
        /// Corrige le problème de détection initiale tardive.
        /// </summary>
        private async Task RefreshRestorePointDetailsOnLoadAsync()
        {
            if (DataContext is not RestorePointsWindowViewModel vm)
                return;

            var scanDetails = vm.Details;
            vm.ActionMessage = "Rafraîchissement des données système...";

            try
            {
                var freshDetails = await _restorePointService.ReadCurrentRestorePointsAsync().ConfigureAwait(true);

                if (freshDetails.Count.HasValue)
                {
                    // Requête live réussie : utiliser les données fraîches
                    vm.UpdateDetails(freshDetails);
                    vm.ActionMessage = freshDetails.Points.Count > 0
                        ? $"✓ {freshDetails.Points.Count} point(s) de restauration détecté(s)"
                        : "✓ Aucun point de restauration détecté";
                }
                else if (scanDetails.Count.HasValue)
                {
                    // Requête live échouée (ex: droits insuffisants) mais données du scan disponibles → les conserver
                    vm.ActionMessage = $"Données du scan ({freshDetails.Reason})";
                }
                else
                {
                    // Les deux sources échouent
                    vm.UpdateDetails(freshDetails);
                    vm.ActionMessage = $"État indisponible ({freshDetails.Reason})";
                }
            }
            catch (Exception ex)
            {
                vm.ActionMessage = $"Erreur lors du rafraîchissement: {ex.Message}";
            }
        }

        private async void CreateRestorePoint_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not RestorePointsWindowViewModel vm)
                return;

            vm.CanCreate = false;
            vm.ActionMessage = "Création du point de restauration en cours...";

            try
            {
                var result = await _restorePointService.CreateRestorePointAsync("PCDiagnostic_Manual_Checkpoint").ConfigureAwait(true);
                vm.ActionMessage = result.Message;

                if (result.RequiresElevation)
                {
                    var relaunch = MessageBox.Show(
                        "La création nécessite des droits administrateur. Voulez-vous relancer l'application en mode administrateur ?",
                        "Droits administrateur requis",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (relaunch == MessageBoxResult.Yes)
                        AdminHelper.RestartAsAdmin();
                    else
                        TryOpenSystemProtection(vm);
                }
                else if (result.Success)
                {
                    await RefreshRestorePointDetailsAsync(vm).ConfigureAwait(true);
                    MessageBox.Show(result.Message, "Point de restauration", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var open = MessageBox.Show(
                        $"{result.Message}\n\nVoulez-vous ouvrir la page Windows 'Protection du système' ?",
                        "Échec création",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (open == MessageBoxResult.Yes)
                        TryOpenSystemProtection(vm);
                }
            }
            catch (Exception ex)
            {
                vm.ActionMessage = ex.Message;
                MessageBox.Show(ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                vm.CanCreate = true;
            }
        }

        private void TryOpenSystemProtection(RestorePointsWindowViewModel vm)
        {
            if (_restorePointService.OpenSystemProtectionSettings(out var openError))
            {
                vm.ActionMessage = "Page Windows de protection du système ouverte.";
                return;
            }

            var message = string.IsNullOrWhiteSpace(openError)
                ? "Impossible d'ouvrir la page Windows de protection du système."
                : openError;
            vm.ActionMessage = message;
            MessageBox.Show(message, "Ouverture impossible", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async Task RefreshRestorePointDetailsAsync(RestorePointsWindowViewModel vm)
        {
            var details = await _restorePointService.ReadCurrentRestorePointsAsync().ConfigureAwait(true);
            vm.UpdateDetails(details);

            if (!details.Count.HasValue)
            {
                vm.ActionMessage = $"Etat actuel indisponible ({details.Reason}).";
                return;
            }

            if (details.Points.Count > 0)
            {
                vm.ActionMessage = $"Dernier point detecte: {details.Points[0].DisplayDate}.";
                return;
            }

            vm.ActionMessage = "Aucun point de restauration detecte.";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public sealed class RestorePointsWindowViewModel : INotifyPropertyChanged
    {
        private string _actionMessage = string.Empty;
        private bool _canCreate = true;

        public RestorePointsWindowViewModel(RestorePointDialogData details)
        {
            Details = details;
        }

        public RestorePointDialogData Details { get; private set; }

        public string CountLine => $"Nombre actuel : {Details.CountDisplay}";
        public string StatusLine => $"État : {Details.StatusDisplay}";
        public string ConfidenceLine => $"Confiance : {Details.Confidence}";
        public string LatestPointLine => Details.Points.Count > 0
            ? $"Dernier point : {Details.Points[0].DisplayDate}"
            : "Dernier point : Indisponible";

        public string ActionMessage
        {
            get => _actionMessage;
            set
            {
                if (_actionMessage == value)
                    return;
                _actionMessage = value;
                OnPropertyChanged();
            }
        }

        public bool CanCreate
        {
            get => _canCreate;
            set
            {
                if (_canCreate == value)
                    return;
                _canCreate = value;
                OnPropertyChanged();
            }
        }

        public void UpdateDetails(RestorePointDialogData details)
        {
            Details = details ?? new RestorePointDialogData();
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(CountLine));
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(ConfidenceLine));
            OnPropertyChanged(nameof(LatestPointLine));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
