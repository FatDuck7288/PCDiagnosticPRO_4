using System;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// ViewModel pour l'écran "Rapport intégral" : HUD vitre, sections, synthèse/détails.
    /// </summary>
    public class FullReportViewModel : ViewModelBase
    {
        private string _title = "RAPPORT INTÉGRAL";
        private string _runId = "";
        private DateTime _scanDate;
        private string _status = "OK"; // OK, Partiel, Échouée
        private double _coveragePercent;
        private double _reliabilityPercent;
        private double _actionabilityPercent;
        private ReportSectionViewModel? _selectedSection;
        private string _searchText = "";
        private bool _isSummaryMode = false; // Détails par défaut (user sees everything)
        private int _uiMappedFields;
        private int _uiDetectedFields;
        private string _contractGateBannerText = string.Empty;
        private string _contractGateBannerDetails = string.Empty;

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value ?? "");
        }

        public string RunId
        {
            get => _runId;
            set => SetProperty(ref _runId, value ?? "");
        }

        public DateTime ScanDate
        {
            get => _scanDate;
            set => SetProperty(ref _scanDate, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value ?? "");
        }

        public double CoveragePercent
        {
            get => _coveragePercent;
            set => SetProperty(ref _coveragePercent, value);
        }

        public double ReliabilityPercent
        {
            get => _reliabilityPercent;
            set
            {
                if (SetProperty(ref _reliabilityPercent, value))
                {
                    OnPropertyChanged(nameof(ReliabilityLabel));
                    OnPropertyChanged(nameof(ReliabilityDisplay));
                }
            }
        }

        public string ReliabilityLabel =>
            ReliabilityPercent >= 80 ? "OK" :
            ReliabilityPercent >= 60 ? "Partiel" : "Faible";

        public string ReliabilityDisplay => $"Fiabilit\u00E9 : {ReliabilityPercent:F0}/100 ({ReliabilityLabel})";

        public double ActionabilityPercent
        {
            get => _actionabilityPercent;
            set => SetProperty(ref _actionabilityPercent, value);
        }

        public ObservableCollection<ReportSectionViewModel> Sections { get; } = new();

        public ReportSectionViewModel? SelectedSection
        {
            get => _selectedSection;
            set => SetProperty(ref _selectedSection, value);
        }

        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value ?? "");
        }

        /// <summary>Synthèse = résumé + top issues uniquement ; Détails = DataGrid + tout.</summary>
        public bool IsSummaryMode
        {
            get => _isSummaryMode;
            set => SetProperty(ref _isSummaryMode, value);
        }

        /// <summary>Compteur Coverage UI : champs mappés dans l'interface.</summary>
        public int UiMappedFields
        {
            get => _uiMappedFields;
            set { if (SetProperty(ref _uiMappedFields, value)) OnPropertyChanged(nameof(UiCoverageDisplay)); }
        }

        /// <summary>Compteur Coverage UI : champs détectés dans le JSON.</summary>
        public int UiDetectedFields
        {
            get => _uiDetectedFields;
            set { if (SetProperty(ref _uiDetectedFields, value)) OnPropertyChanged(nameof(UiCoverageDisplay)); }
        }

        /// <summary>Affichage du taux de couverture UI (mapped/detected).</summary>
        public string UiCoverageDisplay =>
            _uiDetectedFields > 0
                ? $"Couverture UI: {_uiMappedFields}/{_uiDetectedFields} ({100.0 * _uiMappedFields / _uiDetectedFields:F0}%)"
                : "Couverture UI: N/A";

        public string ContractGateBannerText
        {
            get => _contractGateBannerText;
            set
            {
                if (SetProperty(ref _contractGateBannerText, value))
                    OnPropertyChanged(nameof(ShowContractGateBanner));
            }
        }

        public string ContractGateBannerDetails
        {
            get => _contractGateBannerDetails;
            set => SetProperty(ref _contractGateBannerDetails, value);
        }

        public bool ShowContractGateBanner => !string.IsNullOrWhiteSpace(ContractGateBannerText);

        /// <summary>Commande pour basculer Synthèse / Détails (optionnel, peut être liée au ToggleButton).</summary>
        public ICommand? ToggleSummaryModeCommand { get; set; }

        /// <summary>
        /// Auto-sélectionne la première section après chargement.
        /// Appelé par FullReportBuilder après peuplement.
        /// </summary>
        public void SelectFirstSection()
        {
            if (Sections.Count > 0 && SelectedSection == null)
                SelectedSection = Sections[0];
        }
    }
}

