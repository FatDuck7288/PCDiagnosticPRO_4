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

namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// ViewModel principal de l'application
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        #region Fields

        private readonly PowerShellService _powerShellService;
        private readonly ReportParserService _reportParserService;
        private readonly PowerShellJsonMapper _jsonMapper;
        private readonly HardwareSensorsCollector _hardwareSensorsCollector;
        private readonly DispatcherTimer _liveFeedTimer;
        private readonly DispatcherTimer _scanProgressTimer;
        private readonly Stopwatch _scanStopwatch;

        // Process management pour Cancel
        private Process? _scanProcess;
        private CancellationTokenSource? _scanCts;
        private readonly object _scanLock = new object();
        private bool _cancelHandled;
        
        // Résultat capteurs hardware pour injection dans HealthReport
        private HardwareSensorsResult? _lastSensorsResult;
        
        // Résultat compteurs de performance pour enrichir les métriques
        private PerfCounterCollector.PerfCounterResult? _lastPerfCounterResult;
        
        // Résultat des signaux diagnostiques avancés (10 mesures GOD TIER)
        private DiagnosticsSignals.DiagnosticSignalsResult? _lastDiagnosticSignals;
        
        // Résultat du fallback process telemetry (C#)
        private ProcessTelemetryResult? _lastProcessTelemetry;
        
        // Résultat des diagnostics réseau complets
        private NetworkDiagnosticsResult? _lastNetworkDiagnostics;

        // Résultat inventaire pilotes (C#)
        private DriverInventoryResult? _lastDriverInventory;

        // Résultat Windows Update (C#)
        private WindowsUpdateResult? _lastWindowsUpdateResult;

        // Chemins relatifs
        private readonly string _baseDir = AppContext.BaseDirectory;
        private readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCDiagnosticPro");
        private string _scriptPath = string.Empty;
        private string _reportsDir = string.Empty;
        private string _resultJsonPath = string.Empty;
        private string _configPath = string.Empty;
        private DateTimeOffset _scanStartTime;
        private string? _jsonPathFromOutput;

        // Settings loading flag
        private bool _isLoadingSettings = false;

        // Progress tracking
        private int _totalSteps = 27;
        private int _scanProgressCeiling = 85;

        private readonly Dictionary<string, Dictionary<string, string>> _localizedStrings = new()
        {
            ["fr"] = new Dictionary<string, string>
            {
                ["HomeTitle"] = "PC Diagnostic PRO",
                ["HomeSubtitle"] = "Outil de diagnostic système professionnel",
                ["HomeScanTitle"] = "Scan et Fix",
                ["HomeScanAction"] = "Action : Lancer un diagnostic",
                ["HomeScanDescription"] = "Analysez votre PC et corrigez les problèmes",
                ["HomeChatTitle"] = "Chat et Support",
                ["HomeChatAction"] = "Action : Ouvrir l'assistance",
                ["HomeChatDescription"] = "Discutez avec l'IA pour résoudre vos problèmes",
                ["NavHomeTooltip"] = "Tableau de bord",
                ["NavScanTooltip"] = "Scan Healthcheck",
                ["NavReportsTooltip"] = "Rapports",
                ["NavSettingsTooltip"] = "Paramètres",
                ["HealthProgressTitle"] = "Progression",
                ["ElapsedTimeLabel"] = "Temps écoulé",
                ["ConfigsScannedLabel"] = "Configurations scannées",
                ["CurrentSectionLabel"] = "Section courante",
                ["LiveFeedLabel"] = "Flux en direct",
                ["ReportButtonText"] = "Rapport intégrale",
                ["ExportButtonText"] = "Exporter",
                ["ScanButtonText"] = "ANALYSER",
                ["ScanButtonTextScanning"] = "Analyse… {0}%",
                ["ScanButtonSubtext"] = "Cliquez pour démarrer",
                ["CancelButtonText"] = "Arrêt",
                ["ChatTitle"] = "Chat et Support",
                ["ChatSubtitle"] = "Cette fonctionnalité sera disponible prochainement",
                ["ResultsHistoryTitle"] = "Historique des scans",
                ["ResultsDetailTitle"] = "Résultats du diagnostic",
                ["ResultsCompletedTitle"] = "Scan terminé",
                ["ResultsCompletionFormat"] = "Terminé le {0:dd/MM/yyyy HH:mm}",
                ["NotAvailable"] = "Non disponible",
                ["ResultsBreakdownTitle"] = "Répartition des niveaux",
                ["ResultsBreakdownOk"] = "OK",
                ["ResultsBreakdownWarning"] = "Avert.",
                ["ResultsBreakdownError"] = "Erreurs",
                ["ResultsBreakdownCritical"] = "Critiques",
                ["ResultsScanDateFormat"] = "Scan du {0}",
                ["ResultsDetailsHeader"] = "Résultats détaillés",
                ["ResultsBackButton"] = "← Retour",
                ["ResultsNoDataMessage"] = "Aucune donnée de rapport disponible.",
                ["ResultsCategoryHeader"] = "Catégorie",
                ["ResultsItemHeader"] = "Élément",
                ["ResultsLevelHeader"] = "Niveau",
                ["ResultsDetailHeader"] = "Détail",
                ["ResultsRecommendationHeader"] = "Recommandation",
                ["SettingsTitle"] = "Paramètres",
                ["ReportsDirectoryTitle"] = "Répertoire des rapports",
                ["ReportsDirectoryDescription"] = "Sélectionnez le dossier où les rapports seront recherchés.",
                ["BrowseButtonText"] = "Parcourir...",
                ["AdminRightsTitle"] = "Droits administrateur",
                ["AdminStatusLabel"] = "Statut actuel: ",
                ["AdminNoText"] = "NON ADMIN",
                ["AdminYesText"] = "ADMINISTRATEUR",
                ["RestartAdminButtonText"] = "🔐 Relancer en administrateur",
                ["SaveSettingsButtonText"] = "💾 Enregistrer",
                ["LanguageTitle"] = "Langue de l'application",
                ["LanguageDescription"] = "Choisissez la langue de l'interface.",
                ["LanguageLabel"] = "Langue",
                ["ReadyToScan"] = "Prêt à analyser",
                ["StatusReady"] = "Cliquez sur ANALYSER pour démarrer le diagnostic",
                ["AdminRequiredWarning"] = "⚠️ Droits administrateur requis pour un scan complet",
                ["InitStep"] = "Initialisation...",
                ["StatusScanning"] = "🔄 Analyse en cours...",
                ["StatusScriptMissing"] = "❌ Script PowerShell introuvable",
                ["StatusPowerShellMissing"] = "❌ PowerShell introuvable",
                ["StatusFolderError"] = "❌ Erreur création dossier",
                ["StatusCanceled"] = "⏹️ Analyse annulée",
                ["StatusScanError"] = "❌ Erreur lors de l'analyse",
                ["StatusJsonMissing"] = "⚠️ Scan terminé mais rapport JSON introuvable",
                ["StatusParsingError"] = "⚠️ Analyse terminée avec des erreurs",
                ["StatusLoadReportError"] = "⚠️ Erreur lors du chargement du rapport",
                ["StatusScanDeleted"] = "Scan supprimé",
                ["StatusExportSuccess"] = "Rapport exporté avec succès",
                ["StatusExportError"] = "Erreur d'exportation",
                ["StatusSettingsSaved"] = "Paramètres enregistrés",
                ["StatusSettingsSaveError"] = "Erreur lors de la sauvegarde",
                ["AdminAlreadyElevated"] = "L'application est déjà en mode administrateur.",
                ["AdminRestartError"] = "Impossible de redémarrer en administrateur.",
                ["ArchivesButtonText"] = "Archives",
                ["ArchivesTitle"] = "Archives",
                ["ArchiveMenuText"] = "Archiver",
                ["DeleteMenuText"] = "Supprimer",
                ["ScoreLegendTitle"] = "Légende / Calcul du score",
                ["ScoreRulesTitle"] = "Règles de score (UDIS)",
                ["ScoreGradesTitle"] = "Grades",
                ["ScoreRuleInitial"] = "• Score = moyenne pondérée des 8 domaines",
                ["ScoreRuleCritical"] = "• Domaines : OS, CPU, GPU, RAM, Stockage, Réseau, Stabilité, Pilotes",
                ["ScoreRuleError"] = "• Pénalités appliquées selon les problèmes détectés",
                ["ScoreRuleWarning"] = "• Poids : Stockage (20%), OS/CPU/RAM (15%), GPU/Réseau/Stabilité (10%), Pilotes (5%)",
                ["ScoreRuleMin"] = "• Score min : 0",
                ["ScoreRuleMax"] = "• Score max : 100",
                ["ScoreGradeA"] = "• 💎 ≥ 95 : A+ (Excellent) | ❤️ ≥ 90 : A (Très bien)",
                ["ScoreGradeB"] = "• 👍 ≥ 80 : B+ (Bien) | 👌 ≥ 70 : B (Correct)",
                ["ScoreGradeC"] = "• ⚠️ ≥ 60 : C (Dégradé - Attention)",
                ["ScoreGradeD"] = "• 💀 ≥ 50 : D (Critique - Intervention)",
                ["ScoreGradeF"] = "• 🧨 < 50 : F (Critique - Urgence)",
                ["DeleteScanConfirmTitle"] = "Confirmation",
                ["DeleteScanConfirmMessage"] = "Voulez-vous vraiment supprimer ce scan ?"
            },
            ["en"] = new Dictionary<string, string>
            {
                ["HomeTitle"] = "PC Diagnostic PRO",
                ["HomeSubtitle"] = "Professional system diagnostic tool",
                ["HomeScanTitle"] = "Scan & Fix",
                ["HomeScanAction"] = "Action: Run a diagnostic",
                ["HomeScanDescription"] = "Analyze your PC and fix issues",
                ["HomeChatTitle"] = "Chat & Support",
                ["HomeChatAction"] = "Action: Open support",
                ["HomeChatDescription"] = "Chat with AI to resolve your issues",
                ["NavHomeTooltip"] = "Dashboard",
                ["NavScanTooltip"] = "Healthcheck scan",
                ["NavReportsTooltip"] = "Reports",
                ["NavSettingsTooltip"] = "Settings",
                ["HealthProgressTitle"] = "Progress",
                ["ElapsedTimeLabel"] = "Elapsed time",
                ["ConfigsScannedLabel"] = "Scanned configurations",
                ["CurrentSectionLabel"] = "Current section",
                ["LiveFeedLabel"] = "Live Feed",
                ["ReportButtonText"] = "Report",
                ["ExportButtonText"] = "Export",
                ["ScanButtonText"] = "SCAN",
                ["ScanButtonTextScanning"] = "Scanning… {0}%",
                ["ScanButtonSubtext"] = "Click to start",
                ["CancelButtonText"] = "Stop",
                ["ChatTitle"] = "Chat & Support",
                ["ChatSubtitle"] = "This feature will be available soon",
                ["ResultsHistoryTitle"] = "Scan history",
                ["ResultsDetailTitle"] = "Diagnostic results",
                ["ResultsCompletedTitle"] = "Scan completed",
                ["ResultsCompletionFormat"] = "Completed on {0:MM/dd/yyyy HH:mm}",
                ["NotAvailable"] = "Not available",
                ["ResultsBreakdownTitle"] = "Severity breakdown",
                ["ResultsBreakdownOk"] = "OK",
                ["ResultsBreakdownWarning"] = "Warnings",
                ["ResultsBreakdownError"] = "Errors",
                ["ResultsBreakdownCritical"] = "Critical",
                ["ResultsScanDateFormat"] = "Scan from {0}",
                ["ResultsDetailsHeader"] = "Detailed analyzed items",
                ["ResultsBackButton"] = "← Back",
                ["ResultsNoDataMessage"] = "No report data available.",
                ["ResultsCategoryHeader"] = "Category",
                ["ResultsItemHeader"] = "Item",
                ["ResultsLevelHeader"] = "Level",
                ["ResultsDetailHeader"] = "Detail",
                ["ResultsRecommendationHeader"] = "Recommendation",
                ["SettingsTitle"] = "Settings",
                ["ReportsDirectoryTitle"] = "Reports directory",
                ["ReportsDirectoryDescription"] = "Select the folder where reports will be searched.",
                ["BrowseButtonText"] = "Browse...",
                ["AdminRightsTitle"] = "Administrator rights",
                ["AdminStatusLabel"] = "Current status: ",
                ["AdminNoText"] = "NOT ADMIN",
                ["AdminYesText"] = "ADMINISTRATOR",
                ["RestartAdminButtonText"] = "🔐 Restart as administrator",
                ["SaveSettingsButtonText"] = "💾 Save",
                ["LanguageTitle"] = "Application language",
                ["LanguageDescription"] = "Choose the interface language.",
                ["LanguageLabel"] = "Language",
                ["ReadyToScan"] = "Ready to scan",
                ["StatusReady"] = "Click SCAN to start the diagnostic",
                ["AdminRequiredWarning"] = "⚠️ Administrator rights required for a full scan",
                ["InitStep"] = "Initializing...",
                ["StatusScanning"] = "🔄 Scan in progress...",
                ["StatusScriptMissing"] = "❌ PowerShell script not found",
                ["StatusPowerShellMissing"] = "❌ PowerShell not found",
                ["StatusFolderError"] = "❌ Error creating folder",
                ["StatusCanceled"] = "⏹️ Scan canceled",
                ["StatusScanError"] = "❌ Error during scan",
                ["StatusJsonMissing"] = "⚠️ Scan completed but JSON report not found",
                ["StatusParsingError"] = "⚠️ Scan completed with errors",
                ["StatusLoadReportError"] = "⚠️ Error while loading the report",
                ["StatusScanDeleted"] = "Scan deleted",
                ["StatusExportSuccess"] = "Report exported successfully",
                ["StatusExportError"] = "Export error",
                ["StatusSettingsSaved"] = "Settings saved",
                ["StatusSettingsSaveError"] = "Error while saving settings",
                ["AdminAlreadyElevated"] = "The application is already running as administrator.",
                ["AdminRestartError"] = "Unable to restart as administrator.",
                ["ArchivesButtonText"] = "Archives",
                ["ArchivesTitle"] = "Archives",
                ["ArchiveMenuText"] = "Archive",
                ["DeleteMenuText"] = "Delete",
                ["ScoreLegendTitle"] = "Legend / Score calculation",
                ["ScoreRulesTitle"] = "Score rules (UDIS)",
                ["ScoreGradesTitle"] = "Grades",
                ["ScoreRuleInitial"] = "• Score = weighted average of 8 domains",
                ["ScoreRuleCritical"] = "• Domains: OS, CPU, GPU, RAM, Storage, Network, Stability, Drivers",
                ["ScoreRuleError"] = "• Penalties applied based on detected issues",
                ["ScoreRuleWarning"] = "• Weights: Storage (20%), OS/CPU/RAM (15%), GPU/Network/Stability (10%), Drivers (5%)",
                ["ScoreRuleMin"] = "• Minimum score: 0",
                ["ScoreRuleMax"] = "• Maximum score: 100",
                ["ScoreGradeA"] = "• 💎 ≥ 95 : A+ (Excellent) | ❤️ ≥ 90 : A (Very Good)",
                ["ScoreGradeB"] = "• 👍 ≥ 80 : B+ (Good) | 👌 ≥ 70 : B (Acceptable)",
                ["ScoreGradeC"] = "• ⚠️ ≥ 60 : C (Degraded - Attention)",
                ["ScoreGradeD"] = "• 💀 ≥ 50 : D (Critical - Intervention)",
                ["ScoreGradeF"] = "• 🧨 < 50 : F (Critical - Urgent)",
                ["DeleteScanConfirmTitle"] = "Confirmation",
                ["DeleteScanConfirmMessage"] = "Do you really want to delete this scan?"
            },
            ["es"] = new Dictionary<string, string>
            {
                ["HomeTitle"] = "PC Diagnostic PRO",
                ["HomeSubtitle"] = "Herramienta profesional de diagnóstico del sistema",
                ["HomeScanTitle"] = "Escanear y reparar",
                ["HomeScanAction"] = "Acción: Ejecutar un diagnóstico",
                ["HomeScanDescription"] = "Analice su PC y corrija los problemas",
                ["HomeChatTitle"] = "Chat y soporte",
                ["HomeChatAction"] = "Acción: Abrir soporte",
                ["HomeChatDescription"] = "Chatee con la IA para resolver sus problemas",
                ["NavHomeTooltip"] = "Panel",
                ["NavScanTooltip"] = "Escaneo de salud",
                ["NavReportsTooltip"] = "Informes",
                ["NavSettingsTooltip"] = "Configuración",
                ["HealthProgressTitle"] = "Progreso",
                ["ElapsedTimeLabel"] = "Tiempo transcurrido",
                ["ConfigsScannedLabel"] = "Configuraciones escaneadas",
                ["CurrentSectionLabel"] = "Sección actual",
                ["LiveFeedLabel"] = "Feed en vivo",
                ["ReportButtonText"] = "Informe",
                ["ExportButtonText"] = "Exportar",
                ["ScanButtonText"] = "ESCANEAR",
                ["ScanButtonTextScanning"] = "Analizando… {0}%",
                ["ScanButtonSubtext"] = "Haga clic para iniciar",
                ["CancelButtonText"] = "Detener",
                ["ChatTitle"] = "Chat y soporte",
                ["ChatSubtitle"] = "Esta función estará disponible pronto",
                ["ResultsHistoryTitle"] = "Historial de escaneos",
                ["ResultsDetailTitle"] = "Resultados del diagnóstico",
                ["ResultsCompletedTitle"] = "Escaneo finalizado",
                ["ResultsCompletionFormat"] = "Finalizado el {0:dd/MM/yyyy HH:mm}",
                ["NotAvailable"] = "No disponible",
                ["ResultsBreakdownTitle"] = "Distribución por nivel",
                ["ResultsBreakdownOk"] = "OK",
                ["ResultsBreakdownWarning"] = "Advert.",
                ["ResultsBreakdownError"] = "Errores",
                ["ResultsBreakdownCritical"] = "Críticos",
                ["ResultsScanDateFormat"] = "Escaneo del {0}",
                ["ResultsDetailsHeader"] = "Detalle de elementos analizados",
                ["ResultsBackButton"] = "← Volver",
                ["ResultsNoDataMessage"] = "No hay datos de informe disponibles.",
                ["ResultsCategoryHeader"] = "Categoría",
                ["ResultsItemHeader"] = "Elemento",
                ["ResultsLevelHeader"] = "Nivel",
                ["ResultsDetailHeader"] = "Detalle",
                ["ResultsRecommendationHeader"] = "Recomendación",
                ["SettingsTitle"] = "Configuración",
                ["ReportsDirectoryTitle"] = "Directorio de informes",
                ["ReportsDirectoryDescription"] = "Seleccione la carpeta donde se buscarán los informes.",
                ["BrowseButtonText"] = "Examinar...",
                ["AdminRightsTitle"] = "Permisos de administrador",
                ["AdminStatusLabel"] = "Estado actual: ",
                ["AdminNoText"] = "SIN ADMIN",
                ["AdminYesText"] = "ADMINISTRADOR",
                ["RestartAdminButtonText"] = "🔐 Reiniciar como administrador",
                ["SaveSettingsButtonText"] = "💾 Guardar",
                ["LanguageTitle"] = "Idioma de la aplicación",
                ["LanguageDescription"] = "Elija el idioma de la interfaz.",
                ["LanguageLabel"] = "Idioma",
                ["ReadyToScan"] = "Listo para escanear",
                ["StatusReady"] = "Haga clic en ESCANEAR para iniciar el diagnóstico",
                ["AdminRequiredWarning"] = "⚠️ Se requieren permisos de administrador para un análisis completo",
                ["InitStep"] = "Inicializando...",
                ["StatusScanning"] = "🔄 Análisis en curso...",
                ["StatusScriptMissing"] = "❌ Script de PowerShell no encontrado",
                ["StatusPowerShellMissing"] = "❌ PowerShell no encontrado",
                ["StatusFolderError"] = "❌ Error al crear la carpeta",
                ["StatusCanceled"] = "⏹️ Análisis cancelado",
                ["StatusScanError"] = "❌ Error durante el análisis",
                ["StatusJsonMissing"] = "⚠️ Escaneo completado pero no se encontró el informe JSON",
                ["StatusParsingError"] = "⚠️ Análisis completado con errores",
                ["StatusLoadReportError"] = "⚠️ Error al cargar el informe",
                ["StatusScanDeleted"] = "Escaneo eliminado",
                ["StatusExportSuccess"] = "Informe exportado correctamente",
                ["StatusExportError"] = "Error de exportación",
                ["StatusSettingsSaved"] = "Configuración guardada",
                ["StatusSettingsSaveError"] = "Error al guardar la configuración",
                ["AdminAlreadyElevated"] = "La aplicación ya está en modo administrador.",
                ["AdminRestartError"] = "No se pudo reiniciar como administrador.",
                ["ArchivesButtonText"] = "Archivos",
                ["ArchivesTitle"] = "Archivos",
                ["ArchiveMenuText"] = "Archivar",
                ["DeleteMenuText"] = "Eliminar",
                ["ScoreLegendTitle"] = "Leyenda / Cálculo del puntaje",
                ["ScoreRulesTitle"] = "Reglas de puntaje (UDIS)",
                ["ScoreGradesTitle"] = "Calificaciones",
                ["ScoreRuleInitial"] = "• Puntaje = promedio ponderado de 8 dominios",
                ["ScoreRuleCritical"] = "• Dominios: SO, CPU, GPU, RAM, Almacenamiento, Red, Estabilidad, Controladores",
                ["ScoreRuleError"] = "• Penalizaciones aplicadas según problemas detectados",
                ["ScoreRuleWarning"] = "• Pesos: Almacenamiento (20%), SO/CPU/RAM (15%), GPU/Red/Estabilidad (10%), Controladores (5%)",
                ["ScoreRuleMin"] = "• Puntaje mínimo: 0",
                ["ScoreRuleMax"] = "• Puntaje máximo: 100",
                ["ScoreGradeA"] = "• 💎 ≥ 95 : A+ (Excelente) | ❤️ ≥ 90 : A (Muy bien)",
                ["ScoreGradeB"] = "• 👍 ≥ 80 : B+ (Bien) | 👌 ≥ 70 : B (Aceptable)",
                ["ScoreGradeC"] = "• ⚠️ ≥ 60 : C (Degradado - Atención)",
                ["ScoreGradeD"] = "• 💀 ≥ 40 y < 60 : D",
                ["ScoreGradeF"] = "• 🧨 < 40 : F",
                ["DeleteScanConfirmTitle"] = "Confirmación",
                ["DeleteScanConfirmMessage"] = "¿Desea eliminar este escaneo?"
            }
        };

        private bool _isUpdatingLanguage;

        #endregion

        #region Properties

        // Navigation
        private string _currentView = "Home";
        public string CurrentView
        {
            get => _currentView;
            set
            {
                if (SetProperty(ref _currentView, value))
                {
                    OnPropertyChanged(nameof(IsScannerView));
                    OnPropertyChanged(nameof(IsResultsView));
                    OnPropertyChanged(nameof(IsSettingsView));
                    OnPropertyChanged(nameof(IsHealthcheckView));
                    OnPropertyChanged(nameof(IsChatView));
                    OnPropertyChanged(nameof(IsViewingHistoryDetail));
                    OnPropertyChanged(nameof(IsViewingHistoryList));
                }
            }
        }

        public bool IsScannerView => CurrentView == "Home";
        public bool IsResultsView => CurrentView == "Results";
        public bool IsSettingsView => CurrentView == "Settings";
        public bool IsHealthcheckView => CurrentView == "Healthcheck";
        public bool IsChatView => CurrentView == "Chat";

        private string _scanState = "Idle";
        public string ScanState
        {
            get => _scanState;
            set
            {
                if (SetProperty(ref _scanState, value))
                {
                    OnPropertyChanged(nameof(IsIdle));
                    OnPropertyChanged(nameof(IsScanning));
                    OnPropertyChanged(nameof(IsCompleted));
                    OnPropertyChanged(nameof(IsError));
                    OnPropertyChanged(nameof(CanStartScan));
                    OnPropertyChanged(nameof(ShowScanButtons));
                    OnPropertyChanged(nameof(HasAnyScan));
                    CommandManager.InvalidateRequerySuggested();
                    UpdateScanButtonText();
                }
            }
        }

        public bool IsIdle => ScanState == "Idle";
        public bool IsScanning => ScanState == "Scanning";
        public bool IsCompleted => ScanState == "Completed";
        public bool IsError => ScanState == "Error";
        public bool CanStartScan => !IsScanning;
        public bool ShowScanButtons => IsCompleted || IsError;
        public bool HasAnyScan => ScanHistory.Count > 0 || ArchivedScanHistory.Count > 0;

        private int _progress;
        public int Progress
        {
            get => _progress;
            set
            {
                if (SetProperty(ref _progress, value))
                {
                    if (_progressPercent != value)
                    {
                        _progressPercent = value;
                        OnPropertyChanged(nameof(ProgressPercent));
                    }
                    UpdateScanButtonText();
                }
            }
        }

        private int _progressPercent;
        public int ProgressPercent
        {
            get => _progressPercent;
            set
            {
                if (SetProperty(ref _progressPercent, value))
                {
                    if (_progress != value)
                    {
                        _progress = value;
                        OnPropertyChanged(nameof(Progress));
                    }
                    UpdateScanButtonText();
                }
            }
        }

        private int _progressCount;
        public int ProgressCount
        {
            get => _progressCount;
            set => SetProperty(ref _progressCount, value);
        }

        private string _currentSection = string.Empty;
        public string CurrentSection
        {
            get => _currentSection;
            set => SetProperty(ref _currentSection, value);
        }

        private string _currentStep = "Prêt à analyser";
        public string CurrentStep
        {
            get => _currentStep;
            set => SetProperty(ref _currentStep, value);
        }

        private string _statusMessage = "Cliquez sur ANALYSER pour démarrer le diagnostic";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        private ScanResult? _scanResult;
        public ScanResult? ScanResult
        {
            get => _scanResult;
            set
            {
                if (SetProperty(ref _scanResult, value))
                {
                    OnPropertyChanged(nameof(HasScanResult));
                    OnPropertyChanged(nameof(ScoreDisplay));
                    OnPropertyChanged(nameof(GradeDisplay));
                    OnPropertyChanged(nameof(StatusWithScore));
                    OnPropertyChanged(nameof(ResultsCompletionDisplay));
                    OnPropertyChanged(nameof(ResultsStatusDisplay));
                    OnPropertyChanged(nameof(TotalItemsForChart));
                    OnPropertyChanged(nameof(OkCountDisplay));
                    OnPropertyChanged(nameof(WarningCountDisplay));
                    OnPropertyChanged(nameof(ErrorCountDisplay));
                    OnPropertyChanged(nameof(CriticalCountDisplay));
                }
            }
        }

        public bool HasScanResult => ScanResult != null && ScanResult.IsValid;
        public string ScoreDisplay => ScanResult?.Summary?.Score.ToString() ?? "0";
        public string GradeDisplay => ScanResult?.Summary?.Grade ?? "N/A";
        public string StatusWithScore => HasScanResult 
            ? $"Score: {ScanResult!.Summary.Score}/100 | Grade: {ScanResult.Summary.Grade}" 
            : "Aucun scan effectué";
        public string ResultsCompletionDisplay => ScanResult?.Summary != null
            ? FormatStringSafely(GetString("ResultsCompletionFormat"), ScanResult.Summary.ScanDate)
            : GetString("NotAvailable");
        public string ResultsStatusDisplay => HasScanResult ? StatusWithScore : GetString("NotAvailable");
        public string ResultsBreakdownTitle => GetString("ResultsBreakdownTitle");
        public string ResultsBreakdownOk => GetString("ResultsBreakdownOk");
        public string ResultsBreakdownWarning => GetString("ResultsBreakdownWarning");
        public string ResultsBreakdownError => GetString("ResultsBreakdownError");
        public string ResultsBreakdownCritical => GetString("ResultsBreakdownCritical");
        public int TotalItemsForChart => Math.Max(1, ScanResult?.Summary?.TotalItems ?? 1);
        public int OkCountDisplay => ScanResult?.Summary?.OkCount ?? 0;
        public int WarningCountDisplay => ScanResult?.Summary?.WarningCount ?? 0;
        public int ErrorCountDisplay => ScanResult?.Summary?.ErrorCount ?? 0;
        public int CriticalCountDisplay => ScanResult?.Summary?.CriticalCount ?? 0;

        // ========== HEALTH REPORT (Modèle industriel) ==========
        
        private HealthReport? _healthReport;
        public HealthReport? HealthReport
        {
            get => _healthReport;
            set
            {
                if (SetProperty(ref _healthReport, value))
                {
                    OnPropertyChanged(nameof(HasHealthReport));
                    OnPropertyChanged(nameof(GlobalHealthScore));
                    OnPropertyChanged(nameof(GlobalHealthGrade));
                    OnPropertyChanged(nameof(GlobalHealthMessage));
                    OnPropertyChanged(nameof(GlobalHealthColor));
                    OnPropertyChanged(nameof(GlobalHealthIcon));
                    // Confidence Score
                    OnPropertyChanged(nameof(ConfidenceScore));
                    OnPropertyChanged(nameof(ConfidenceLevel));
                    OnPropertyChanged(nameof(ConfidenceDisplay));
                    OnPropertyChanged(nameof(ConfidenceColor));
                    OnPropertyChanged(nameof(CollectionStatusBadgeText));
                    OnPropertyChanged(nameof(IsCollectionPartialOrFailed));
                    OnPropertyChanged(nameof(CollectorErrorsLogicalDisplay));
                    OnPropertyChanged(nameof(MachineHealthScore));
                    OnPropertyChanged(nameof(DataReliabilityScore));
                    OnPropertyChanged(nameof(DiagnosticClarityScore));
                    OnPropertyChanged(nameof(MachineHealthDisplay));
                    OnPropertyChanged(nameof(DataReliabilityDisplay));
                    OnPropertyChanged(nameof(AutoFixAllowed));
                    // UDIS nouvelles sections
                    OnPropertyChanged(nameof(ThermalScore));
                    OnPropertyChanged(nameof(ThermalStatus));
                    OnPropertyChanged(nameof(BootHealthScore));
                    OnPropertyChanged(nameof(BootHealthTier));
                    OnPropertyChanged(nameof(StorageIoHealthScore));
                    OnPropertyChanged(nameof(StorageIoStatus));
                    OnPropertyChanged(nameof(SystemStabilityIndex));
                    OnPropertyChanged(nameof(CpuPerformanceTier));
                    OnPropertyChanged(nameof(NetworkDownloadMbps));
                    OnPropertyChanged(nameof(NetworkLatencyMs));
                    OnPropertyChanged(nameof(NetworkSpeedTier));
                    OnPropertyChanged(nameof(NetworkRecommendation));
                    UpdateUdisSectionsSummary();
                    UpdateHealthSections();
                }
            }
        }

        public bool HasHealthReport => HealthReport != null;
        public int GlobalHealthScore => HealthReport?.GlobalScore ?? 0;
        public string GlobalHealthGrade => HealthReport?.Grade ?? "N/A";
        public string GlobalHealthMessage => HealthReport?.GlobalMessage ?? "Aucune analyse disponible";
        
        /// <summary>P0.3 / P3: Badge "Partiel / Limité" si collecte FAILED ou PARTIAL ou collectorErrorsLogical > 0</summary>
        public bool IsCollectionPartialOrFailed => HealthReport?.CollectionStatus == "PARTIAL" || HealthReport?.CollectionStatus == "FAILED" || (HealthReport?.CollectorErrorsLogical ?? 0) > 0;
        public string CollectionStatusBadgeText => !IsCollectionPartialOrFailed ? "" : (HealthReport?.CollectionStatus == "FAILED" ? "Collecte échouée" : "Collecte partielle / limitée");
        public string CollectorErrorsLogicalDisplay => (HealthReport?.CollectorErrorsLogical ?? 0) > 0 ? $"Erreurs collecteur: {HealthReport!.CollectorErrorsLogical}" : "";
        public string GlobalHealthColor => HealthReport != null 
            ? Models.HealthReport.SeverityToColor(HealthReport.GlobalSeverity) 
            : "#9E9E9E";
        public string GlobalHealthIcon => HealthReport != null 
            ? Models.HealthReport.SeverityToIcon(HealthReport.GlobalSeverity) 
            : "?";
        
        // === CONFIDENCE SCORE (qualité de collecte) ===
        public int ConfidenceScore => HealthReport?.ConfidenceModel?.ConfidenceScore ?? 0;
        public string ConfidenceLevel => HealthReport?.ConfidenceModel?.ConfidenceLevel ?? "N/A";
        public string ConfidenceDisplay => $"{ConfidenceScore}/100 ({ConfidenceLevel})";
        public string ConfidenceColor => ConfidenceScore >= 80 ? "#4CAF50" : 
                                          ConfidenceScore >= 60 ? "#FFC107" : "#F44336";

        // === UDIS — AFFICHAGE MODE INDUSTRIE (séparé) ===
        public int MachineHealthScore => HealthReport?.MachineHealthScore ?? 0;
        public int DataReliabilityScore => HealthReport?.DataReliabilityScore ?? 0;
        public int DiagnosticClarityScore => HealthReport?.DiagnosticClarityScore ?? 0;
        public string MachineHealthDisplay => $"{MachineHealthScore}/100";
        public string DataReliabilityDisplay => $"{DataReliabilityScore}/100";
        public bool AutoFixAllowed => HealthReport?.AutoFixAllowed ?? false;

        // === UDIS — NOUVELLES SECTIONS ===
        public int ThermalScore => HealthReport?.UdisReport?.ThermalScore ?? 100;
        public string ThermalStatus => HealthReport?.UdisReport?.ThermalStatus ?? "N/A";
        public int BootHealthScore => HealthReport?.UdisReport?.BootHealthScore ?? 100;
        public string BootHealthTier => HealthReport?.UdisReport?.BootHealthTier ?? "N/A";
        public int StorageIoHealthScore => HealthReport?.UdisReport?.StorageIoHealthScore ?? 100;
        public string StorageIoStatus => HealthReport?.UdisReport?.StorageIoStatus ?? "N/A";
        public int SystemStabilityIndex => HealthReport?.UdisReport?.SystemStabilityIndex ?? 100;
        public string CpuPerformanceTier => HealthReport?.UdisReport?.CpuPerformanceTier ?? "N/A";

        // === UDIS — NETWORK SPEED TEST ===
        public double? NetworkDownloadMbps => HealthReport?.UdisReport?.DownloadMbps;
        public double? NetworkLatencyMs => HealthReport?.UdisReport?.LatencyMs;
        public string NetworkSpeedTier => HealthReport?.UdisReport?.NetworkSpeedTier ?? "Non mesuré";
        public string NetworkRecommendation => HealthReport?.UdisReport?.NetworkRecommendation ?? "";
        
        // === PROCESS TELEMETRY — UI DISPLAY ===
        public bool HasProcessTelemetry => _lastProcessTelemetry?.Available ?? false;
        public int ProcessCount => _lastProcessTelemetry?.TotalProcessCount ?? 0;
        public string TopCpuProcess => _lastProcessTelemetry?.TopByCpu?.FirstOrDefault()?.Name ?? "N/A";
        public double TopCpuPercent => _lastProcessTelemetry?.TopByCpu?.FirstOrDefault()?.CpuPercent ?? 0;
        public string TopMemoryProcess => _lastProcessTelemetry?.TopByMemory?.FirstOrDefault()?.Name ?? "N/A";
        public double TopMemoryMB => _lastProcessTelemetry?.TopByMemory?.FirstOrDefault()?.WorkingSetMB ?? 0;
        public string ProcessTelemetryDisplay => HasProcessTelemetry 
            ? $"{ProcessCount} processus | Top CPU: {TopCpuProcess} ({TopCpuPercent:F1}%) | Top RAM: {TopMemoryProcess} ({TopMemoryMB:F0} MB)"
            : "Données non disponibles";
        
        // === SENSOR BLOCKING STATUS — UI DISPLAY ===
        public bool IsSensorBlocked => _lastSensorsResult?.BlockedBySecurity ?? false;
        public string SensorBlockingMessage => _lastSensorsResult?.BlockingMessage ?? "";
        public bool HasSensorBlockingMessage => !string.IsNullOrEmpty(SensorBlockingMessage);
        
        // === NETWORK DIAGNOSTICS — UI DISPLAY ===
        public bool HasNetworkDiagnostics => _lastNetworkDiagnostics?.Available ?? false;
        public double NetLatencyP50 => _lastNetworkDiagnostics?.OverallLatencyMsP50 ?? 0;
        public double NetLatencyP95 => _lastNetworkDiagnostics?.OverallLatencyMsP95 ?? 0;
        public double NetJitterP95 => _lastNetworkDiagnostics?.OverallJitterMsP95 ?? 0;
        public double NetPacketLoss => _lastNetworkDiagnostics?.OverallLossPercent ?? 0;
        public double NetDnsP95 => _lastNetworkDiagnostics?.DnsP95Ms ?? 0;
        public string NetGateway => _lastNetworkDiagnostics?.Gateway ?? "N/A";
        public double? NetThroughputMbps => _lastNetworkDiagnostics?.Throughput?.DownloadMbpsMedian;
        public string NetworkDiagnosticsDisplay => HasNetworkDiagnostics
            ? $"Latence: {NetLatencyP50:F0}ms | Jitter: {NetJitterP95:F1}ms | Perte: {NetPacketLoss:F1}% | DNS: {NetDnsP95:F0}ms"
            : "Données non disponibles";
        public string NetworkQualityVerdict => GetNetworkQualityVerdict();
        
        private string GetNetworkQualityVerdict()
        {
            if (!HasNetworkDiagnostics) return "Non mesuré";
            if (NetPacketLoss > 5 || NetLatencyP95 > 200) return "⚠️ Dégradé";
            if (NetPacketLoss > 1 || NetLatencyP95 > 100) return "⚡ Acceptable";
            return "✅ Excellent";
        }

        private bool _isSpeedTestRunning;
        public bool IsSpeedTestRunning
        {
            get => _isSpeedTestRunning;
            set => SetProperty(ref _isSpeedTestRunning, value);
        }
        
        // FIX 7: Allow external network tests (Internet speed test opt-in)
        private bool _allowExternalNetworkTests = false;
        public bool AllowExternalNetworkTests
        {
            get => _allowExternalNetworkTests;
            set
            {
                if (SetProperty(ref _allowExternalNetworkTests, value))
                {
                    // Persist setting
                    SaveSettingsAsync().ConfigureAwait(false);
                }
            }
        }

        // === UDIS — SECTIONS SUMMARY POUR UI ===
        public ObservableCollection<UdisSectionSummary> UdisSectionsSummary { get; } = new();

        private ObservableCollection<HealthSection> _healthSections = new();
        public ObservableCollection<HealthSection> HealthSections
        {
            get => _healthSections;
            set => SetProperty(ref _healthSections, value);
        }

        private HealthSection? _selectedHealthSection;
        public HealthSection? SelectedHealthSection
        {
            get => _selectedHealthSection;
            set => SetProperty(ref _selectedHealthSection, value);
        }

        private void UpdateHealthSections()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                HealthSections.Clear();
                if (HealthReport?.Sections != null)
                {
                    foreach (var section in HealthReport.Sections)
                    {
                        HealthSections.Add(section);
                    }
                }
            });
        }
        
        /// <summary>
        /// Notify UI when process telemetry data changes
        /// </summary>
        private void NotifyProcessTelemetryChanged()
        {
            OnPropertyChanged(nameof(HasProcessTelemetry));
            OnPropertyChanged(nameof(ProcessCount));
            OnPropertyChanged(nameof(TopCpuProcess));
            OnPropertyChanged(nameof(TopCpuPercent));
            OnPropertyChanged(nameof(TopMemoryProcess));
            OnPropertyChanged(nameof(TopMemoryMB));
            OnPropertyChanged(nameof(ProcessTelemetryDisplay));
        }
        
        /// <summary>
        /// Notify UI when network diagnostics data changes
        /// </summary>
        private void NotifyNetworkDiagnosticsChanged()
        {
            OnPropertyChanged(nameof(HasNetworkDiagnostics));
            OnPropertyChanged(nameof(NetLatencyP50));
            OnPropertyChanged(nameof(NetLatencyP95));
            OnPropertyChanged(nameof(NetJitterP95));
            OnPropertyChanged(nameof(NetPacketLoss));
            OnPropertyChanged(nameof(NetDnsP95));
            OnPropertyChanged(nameof(NetGateway));
            OnPropertyChanged(nameof(NetThroughputMbps));
            OnPropertyChanged(nameof(NetworkDiagnosticsDisplay));
            OnPropertyChanged(nameof(NetworkQualityVerdict));
        }
        
        /// <summary>
        /// Notify UI when sensor blocking status changes
        /// </summary>
        private void NotifySensorBlockingChanged()
        {
            OnPropertyChanged(nameof(IsSensorBlocked));
            OnPropertyChanged(nameof(SensorBlockingMessage));
            OnPropertyChanged(nameof(HasSensorBlockingMessage));
        }

        private void UpdateUdisSectionsSummary()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                UdisSectionsSummary.Clear();
                if (HealthReport?.UdisReport?.SectionsSummary != null)
                {
                    foreach (var summary in HealthReport.UdisReport.SectionsSummary)
                    {
                        UdisSectionsSummary.Add(summary);
                    }
                }
            });
        }

        /// <summary>
        /// Lancer le SpeedTest réseau (async, non bloquant).
        /// </summary>
        private ICommand? _runSpeedTestCommand;
        public ICommand RunSpeedTestCommand => _runSpeedTestCommand ??= new RelayCommand(async _ =>
        {
            if (IsSpeedTestRunning) return;
            IsSpeedTestRunning = true;
            try
            {
                if (HealthReport?.UdisReport != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var updatedUdis = await UnifiedDiagnosticScoreEngine.AddNetworkSpeedTestAsync(HealthReport.UdisReport, cts.Token);
                    // Notifier la UI
                    OnPropertyChanged(nameof(NetworkDownloadMbps));
                    OnPropertyChanged(nameof(NetworkLatencyMs));
                    OnPropertyChanged(nameof(NetworkSpeedTier));
                    OnPropertyChanged(nameof(NetworkRecommendation));
                    App.LogMessage($"[SpeedTest] Terminé: Download={updatedUdis.DownloadMbps:F1} Mbps, Tier={updatedUdis.NetworkSpeedTier}");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SpeedTest] Erreur: {ex.Message}");
            }
            finally
            {
                IsSpeedTestRunning = false;
            }
        });

        // ========== FIN HEALTH REPORT ==========

        private ScanHistoryItem? _selectedHistoryScan;
        public ScanHistoryItem? SelectedHistoryScan
        {
            get => _selectedHistoryScan;
            set
            {
                if (SetProperty(ref _selectedHistoryScan, value))
                {
                    OnPropertyChanged(nameof(IsViewingHistoryDetail));
                    OnPropertyChanged(nameof(IsViewingHistoryList));
                    OnPropertyChanged(nameof(SelectedScanDateDisplay));
                    if (value != null && value.Result != null)
                    {
                        ResultsMessage = string.Empty;
                        ScanResult = value.Result;
                        UpdateScanItemsFromResult(value.Result);
                        UpdateResultSectionsFromResult(value.Result);
                    }
                }
            }
        }

        public bool IsViewingHistoryDetail => SelectedHistoryScan != null && IsResultsView;

        public ObservableCollection<ResultSection> ResultSections { get; } = new ObservableCollection<ResultSection>();

        public bool HasResultSections => ResultSections.Count > 0;

        private string _resultsMessage = string.Empty;
        public string ResultsMessage
        {
            get => _resultsMessage;
            set
            {
                if (SetProperty(ref _resultsMessage, value))
                {
                    OnPropertyChanged(nameof(HasResultsMessage));
                }
            }
        }

        public bool HasResultsMessage => !string.IsNullOrWhiteSpace(ResultsMessage);

        private bool _isViewingArchives;
        public bool IsViewingArchives
        {
            get => _isViewingArchives;
            set
            {
                if (SetProperty(ref _isViewingArchives, value))
                {
                    OnPropertyChanged(nameof(IsViewingHistoryList));
                }
            }
        }

        public bool IsViewingHistoryList => !IsViewingHistoryDetail && !IsViewingArchives && IsResultsView;

        private bool _isAdmin;
        public bool IsAdmin
        {
            get => _isAdmin;
            set
            {
                if (SetProperty(ref _isAdmin, value))
                {
                    OnPropertyChanged(nameof(AdminStatusText));
                    OnPropertyChanged(nameof(AdminStatusForeground));
                }
            }
        }

        private string _elapsedTime = "00:00";
        public string ElapsedTime
        {
            get => _elapsedTime;
            set => SetProperty(ref _elapsedTime, value);
        }

        // Paramètres
        private string _reportDirectory = string.Empty;
        public string ReportDirectory
        {
            get => _reportDirectory;
            set
            {
                if (SetProperty(ref _reportDirectory, value) && !_isLoadingSettings)
                {
                    IsSettingsDirty = true;
                }
            }
        }

        private bool _isSettingsDirty = false;
        public bool IsSettingsDirty
        {
            get => _isSettingsDirty;
            set => SetProperty(ref _isSettingsDirty, value);
        }

        private string _currentLanguage = "fr";
        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (SetProperty(ref _currentLanguage, value))
                {
                    UpdateLocalizedStrings();
                    if (!_isUpdatingLanguage)
                    {
                        _isUpdatingLanguage = true;
                        SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Code == value)
                                           ?? AvailableLanguages.First();
                        _isUpdatingLanguage = false;
                    }

                    if (!_isLoadingSettings)
                    {
                        IsSettingsDirty = true;
                    }
                }
            }
        }

        public ObservableCollection<LanguageOption> AvailableLanguages { get; } =
            new ObservableCollection<LanguageOption>
            {
                new LanguageOption { Code = "fr", DisplayName = "Français" },
                new LanguageOption { Code = "en", DisplayName = "English" },
                new LanguageOption { Code = "es", DisplayName = "Español" }
            };

        private LanguageOption? _selectedLanguage;
        public LanguageOption? SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (SetProperty(ref _selectedLanguage, value) && value != null)
                {
                    if (!_isUpdatingLanguage)
                    {
                        _isUpdatingLanguage = true;
                        CurrentLanguage = value.Code;
                        _isUpdatingLanguage = false;
                    }

                    if (!_isLoadingSettings)
                    {
                        IsSettingsDirty = true;
                    }
                }
            }
        }

        public string HomeTitle => GetString("HomeTitle");
        public string HomeSubtitle => GetString("HomeSubtitle");
        public string HomeScanTitle => GetString("HomeScanTitle");
        public string HomeScanAction => GetString("HomeScanAction");
        public string HomeScanDescription => GetString("HomeScanDescription");
        public string HomeChatTitle => GetString("HomeChatTitle");
        public string HomeChatAction => GetString("HomeChatAction");
        public string HomeChatDescription => GetString("HomeChatDescription");
        public string NavHomeTooltip => GetString("NavHomeTooltip");
        public string NavScanTooltip => GetString("NavScanTooltip");
        public string NavReportsTooltip => GetString("NavReportsTooltip");
        public string NavSettingsTooltip => GetString("NavSettingsTooltip");
        public string HealthProgressTitle => GetString("HealthProgressTitle");
        public string ElapsedTimeLabel => GetString("ElapsedTimeLabel");
        public string ConfigsScannedLabel => GetString("ConfigsScannedLabel");
        public string CurrentSectionLabel => GetString("CurrentSectionLabel");
        public string LiveFeedLabel => GetString("LiveFeedLabel");
        public string ReportButtonText => GetString("ReportButtonText");
        public string ExportButtonText => GetString("ExportButtonText");
        private string _scanButtonText = string.Empty;
        public string ScanButtonText
        {
            get => _scanButtonText;
            set => SetProperty(ref _scanButtonText, value);
        }
        public string ScanButtonSubtext => GetString("ScanButtonSubtext");
        public string CancelButtonText => GetString("CancelButtonText");
        public string ChatTitle => GetString("ChatTitle");
        public string ChatSubtitle => GetString("ChatSubtitle");
        public string ResultsHistoryTitle => GetString("ResultsHistoryTitle");
        public string ResultsDetailTitle => GetString("ResultsDetailTitle");
        public string ResultsDetailsHeader => GetString("ResultsDetailsHeader");
        public string ResultsBackButton => GetString("ResultsBackButton");
        public string ResultsNoDataMessage => GetString("ResultsNoDataMessage");
        public string ResultsCategoryHeader => GetString("ResultsCategoryHeader");
        public string ResultsItemHeader => GetString("ResultsItemHeader");
        public string ResultsLevelHeader => GetString("ResultsLevelHeader");
        public string ResultsDetailHeader => GetString("ResultsDetailHeader");
        public string ResultsRecommendationHeader => GetString("ResultsRecommendationHeader");
        public string SettingsTitle => GetString("SettingsTitle");
        public string ReportsDirectoryTitle => GetString("ReportsDirectoryTitle");
        public string ReportsDirectoryDescription => GetString("ReportsDirectoryDescription");
        public string BrowseButtonText => GetString("BrowseButtonText");
        public string AdminRightsTitle => GetString("AdminRightsTitle");
        public string AdminStatusLabel => GetString("AdminStatusLabel");
        public string AdminStatusText => IsAdmin ? GetString("AdminYesText") : GetString("AdminNoText");
        public Brush AdminStatusForeground => IsAdmin
            ? new SolidColorBrush(Color.FromRgb(46, 213, 115))
            : new SolidColorBrush(Color.FromRgb(255, 71, 87));
        public string RestartAdminButtonText => GetString("RestartAdminButtonText");
        public string SaveSettingsButtonText => GetString("SaveSettingsButtonText");
        public string LanguageTitle => GetString("LanguageTitle");
        public string LanguageDescription => GetString("LanguageDescription");
        public string LanguageLabel => GetString("LanguageLabel");
        public string ArchivesButtonText => GetString("ArchivesButtonText");
        public string ArchivesTitle => GetString("ArchivesTitle");
        public string ArchiveMenuText => GetString("ArchiveMenuText");
        public string DeleteMenuText => GetString("DeleteMenuText");
        public string ScoreLegendTitle => GetString("ScoreLegendTitle");
        public string ScoreRulesTitle => GetString("ScoreRulesTitle");
        public string ScoreGradesTitle => GetString("ScoreGradesTitle");
        public string ScoreRuleInitial => GetString("ScoreRuleInitial");
        public string ScoreRuleCritical => GetString("ScoreRuleCritical");
        public string ScoreRuleError => GetString("ScoreRuleError");
        public string ScoreRuleWarning => GetString("ScoreRuleWarning");
        public string ScoreRuleMin => GetString("ScoreRuleMin");
        public string ScoreRuleMax => GetString("ScoreRuleMax");
        public string ScoreGradeA => GetString("ScoreGradeA");
        public string ScoreGradeB => GetString("ScoreGradeB");
        public string ScoreGradeC => GetString("ScoreGradeC");
        public string ScoreGradeD => GetString("ScoreGradeD");
        public string ScoreGradeF => GetString("ScoreGradeF");
        public string SelectedScanDateDisplay => SelectedHistoryScan != null
            ? string.Format(GetString("ResultsScanDateFormat"), SelectedHistoryScan.DateDisplay)
            : string.Empty;
        public string ResultsCompletedTitle => GetString("ResultsCompletedTitle");

        // Collections
        public ObservableCollection<string> LiveFeedItems { get; } = new ObservableCollection<string>();
        public ObservableCollection<ScanItem> ScanItems { get; } = new ObservableCollection<ScanItem>();
        public ObservableCollection<ScanHistoryItem> ScanHistory { get; } = new ObservableCollection<ScanHistoryItem>();
        public ObservableCollection<ScanHistoryItem> ArchivedScanHistory { get; } = new ObservableCollection<ScanHistoryItem>();
        public ICollectionView ArchivedScanHistoryView { get; }

        #endregion

        #region Commands

        public ICommand StartScanCommand { get; }
        public ICommand CancelScanCommand { get; }
        public ICommand OpenReportCommand { get; }
        public ICommand OpenReportTxtCommand { get; }
        public ICommand RestartAsAdminCommand { get; }
        public ICommand ExportResultsCommand { get; }
        public ICommand NavigateToScannerCommand { get; }
        public ICommand NavigateToResultsCommand { get; }
        public ICommand NavigateToSettingsCommand { get; }
        public ICommand NavigateToHealthcheckCommand { get; }
        public ICommand NavigateToChatCommand { get; }
        public ICommand BrowseReportDirectoryCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand SelectHistoryScanCommand { get; }
        public ICommand BackToHistoryCommand { get; }
        public ICommand NavigateToArchivesCommand { get; }
        public ICommand ArchiveScanCommand { get; }
        public ICommand DeleteScanCommand { get; }

        #endregion

        #region Constructor

        public MainViewModel()
        {
            _powerShellService = new PowerShellService();
            _reportParserService = new ReportParserService();
            _jsonMapper = new PowerShellJsonMapper();
            _hardwareSensorsCollector = new HardwareSensorsCollector();
            _scanStopwatch = new Stopwatch();

            ArchivedScanHistoryView = CollectionViewSource.GetDefaultView(ArchivedScanHistory);
            ArchivedScanHistoryView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ScanHistoryItem.MonthYearDisplay)));
            ArchivedScanHistoryView.SortDescriptions.Add(new SortDescription(nameof(ScanHistoryItem.ScanDate), ListSortDirection.Descending));

            _liveFeedTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _liveFeedTimer.Tick += (s, e) => UpdateElapsedTime();

            _scanProgressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _scanProgressTimer.Tick += (s, e) => TickScanProgress();

            // Initialiser les chemins relatifs
            _scriptPath = ResolveScriptPath()
                          ?? Path.Combine(_baseDir, "Scripts", "Total_PS_PC_Scan_v7.0.ps1");
            _reportsDir = Path.Combine(_appDataDir, "Rapports");
            _resultJsonPath = Path.Combine(_reportsDir, "scan_result.json");
            _configPath = Path.Combine(_appDataDir, "config.json");

            // Créer le dossier Rapports s'il n'existe pas
            if (!Directory.Exists(_reportsDir))
            {
                try
                {
                    Directory.CreateDirectory(_appDataDir);
                    Directory.CreateDirectory(_reportsDir);
                }
                catch { }
            }

            IsAdmin = AdminService.IsRunningAsAdmin();

            // Charger les paramètres
            LoadSettings();
            _isUpdatingLanguage = true;
            SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Code == CurrentLanguage)
                               ?? AvailableLanguages.First();
            _isUpdatingLanguage = false;
            UpdateLocalizedStrings();
            UpdateScanButtonText();

            // Initialiser les commandes
            StartScanCommand = new AsyncRelayCommand(StartScanAsync, () => CanStartScan);
            CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
            OpenReportCommand = new RelayCommand(OpenReport, () => HasScanResult);
            OpenReportTxtCommand = new RelayCommand(OpenReportTxt, () => HasScanResult);
            RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
            ExportResultsCommand = new RelayCommand(ExportResults, () => HasScanResult);
            NavigateToScannerCommand = new RelayCommand(() => { CurrentView = "Home"; SelectedHistoryScan = null; IsViewingArchives = false; });
            NavigateToResultsCommand = new RelayCommand(() => { CurrentView = "Results"; SelectedHistoryScan = null; IsViewingArchives = false; }, () => HasAnyScan);
            NavigateToSettingsCommand = new RelayCommand(() => { CurrentView = "Settings"; SelectedHistoryScan = null; IsViewingArchives = false; });
            NavigateToHealthcheckCommand = new RelayCommand(() => { CurrentView = "Healthcheck"; SelectedHistoryScan = null; IsViewingArchives = false; });
            NavigateToChatCommand = new RelayCommand(() => { CurrentView = "Chat"; SelectedHistoryScan = null; IsViewingArchives = false; });
            BrowseReportDirectoryCommand = new RelayCommand(BrowseReportDirectory);
            SaveSettingsCommand = new RelayCommand(SaveSettings, () => IsSettingsDirty);
            SelectHistoryScanCommand = new RelayCommand<ScanHistoryItem>(SelectHistoryScan);
            BackToHistoryCommand = new RelayCommand(BackToHistory);
            NavigateToArchivesCommand = new RelayCommand(NavigateToArchives, () => ScanHistory.Count > 0 || ArchivedScanHistory.Count > 0);
            ArchiveScanCommand = new RelayCommand<ScanHistoryItem>(ArchiveScan, item => item != null);
            DeleteScanCommand = new RelayCommand<ScanHistoryItem>(DeleteScan, item => item != null);

            ScanHistory.CollectionChanged += OnHistoryCollectionChanged;
            ArchivedScanHistory.CollectionChanged += OnHistoryCollectionChanged;
            ResultSections.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasResultSections));

            // S'abonner aux événements
            _powerShellService.OutputReceived += OnOutputReceived;
            _powerShellService.ProgressChanged += OnProgressChanged;
            _powerShellService.StepChanged += OnStepChanged;

            if (!IsAdmin)
            {
                StatusMessage = GetString("AdminRequiredWarning");
            }

            App.LogMessage("MainViewModel initialisé");
        }

        #endregion

        #region Methods

        private async Task StartScanAsync()
        {
            lock (_scanLock)
            {
                if (_scanProcess != null && !_scanProcess.HasExited)
                {
                    App.LogMessage("Scan déjà en cours");
                    return;
                }
            }

            // VÉRIFICATION MODE ADMIN - Proposer relance si non-admin
            if (!Services.AdminHelper.IsRunningAsAdmin())
            {
                App.LogMessage("[Admin] Application non en mode administrateur");
                var adminMessage = "Pour un diagnostic complet, le mode administrateur est recommandé.\n\n" +
                    Services.AdminHelper.GetAdminExplanation() + "\n\n" +
                    "Sans droits admin, certaines données peuvent être incomplètes.";
                
                var result = System.Windows.MessageBox.Show(
                    adminMessage,
                    "Mode administrateur recommandé",
                    System.Windows.MessageBoxButton.YesNoCancel,
                    System.Windows.MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    // Relancer en admin
                    Services.AdminHelper.RestartAsAdmin();
                    return;
                }
                else if (result == System.Windows.MessageBoxResult.Cancel)
                {
                    // Annuler le scan
                    return;
                }
                // No = continuer sans admin
                App.LogMessage("[Admin] Utilisateur continue sans droits admin");
            }

            try
            {
                var resolvedScriptPath = ResolveScriptPath();
                if (!string.IsNullOrWhiteSpace(resolvedScriptPath))
                {
                    _scriptPath = resolvedScriptPath;
                }

                // Vérifier que le script existe
                if (!File.Exists(_scriptPath))
                {
                    ErrorMessage = $"Script introuvable";
                    StatusMessage = GetString("StatusScriptMissing");
                    ScanState = "Error";
                    App.LogMessage($"Script non trouvé: {_scriptPath}");
                    App.LogMessage($"BaseDir: {_baseDir}");
                    App.LogMessage($"CurrentDirectory: {Environment.CurrentDirectory}");
                    return;
                }

                var outputDir = string.IsNullOrWhiteSpace(ReportDirectory) ? _reportsDir : ReportDirectory;
                _resultJsonPath = Path.Combine(outputDir, "scan_result.json");
                _reportParserService.ReportDirectory = outputDir;

                // Vérifier/Créer le dossier Rapports
                if (!Directory.Exists(outputDir))
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

                // Réinitialiser
                ScanState = "Scanning";
                App.LogMessage($"=== DÉMARRAGE SCAN ===");
                App.LogMessage($"IsScanning={IsScanning}, ScanState={ScanState}");
                
                // P0.2: Clear WMI errors from previous scan
                WmiQueryRunner.ClearErrors();
                
                UpdateProgress(0, "Scan reset", allowDecrease: true);
                ProgressCount = 0;
                CurrentStep = GetString("InitStep");
                CurrentSection = string.Empty;
                StatusMessage = GetString("StatusScanning");
                ErrorMessage = string.Empty;
                ResultsMessage = string.Empty;
                LiveFeedItems.Clear();
                ScanItems.Clear();
                ResultSections.Clear();
                OnPropertyChanged(nameof(HasResultSections));
                ScanResult = null;
                _cancelHandled = false;

                _scanStopwatch.Restart();
                _liveFeedTimer.Start();
                _scanStartTime = DateTimeOffset.Now;
                _jsonPathFromOutput = null;

                AddLiveFeedItem("▶ Démarrage du scan...");

                App.LogMessage("Démarrage du scan");
                App.LogMessage($"Start scan timestamp: {_scanStartTime:O}");
                App.LogMessage($"Scan output directory: {outputDir}");
                UpdateProgress(5, "Scan started");

                // Créer CancellationTokenSource
                _scanCts = new CancellationTokenSource();

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                // Lancer le processus PowerShell
                var powerShellExe = ResolvePowerShellExecutable();
                if (string.IsNullOrWhiteSpace(powerShellExe))
                {
                    ErrorMessage = "PowerShell introuvable";
                    StatusMessage = GetString("StatusPowerShellMissing");
                    ScanState = "Error";
                    App.LogMessage("PowerShell introuvable (powershell.exe/pwsh.exe).");
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = powerShellExe,
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{_scriptPath}\" -OutputDir \"{outputDir}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _scanProcess = new Process { StartInfo = startInfo };
                _scanProcess.EnableRaisingEvents = true;
                UpdateProgress(10, "Process configured");

                // CORRECTION: Utiliser les événements DataReceived au lieu de ReadLineAsync
                _scanProcess.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        outputBuilder.AppendLine(e.Data);
                        ProcessOutputLine(e.Data);
                    });
                };

                _scanProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            errorBuilder.AppendLine(e.Data);
                            App.LogMessage($"ERREUR PS: {e.Data}");
                        });
                    }
                };

                _scanProcess.Start();
                _scanProcess.BeginOutputReadLine();
                _scanProcess.BeginErrorReadLine();
                StartScanProgressTimer(85);
                UpdateProgress(15, "Process launched");

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

                _scanStopwatch.Stop();
                _liveFeedTimer.Stop();
                StopScanProgressTimer();

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

                if (exitCode != 0 && errorBuilder.Length > 0)
                {
                    App.LogMessage($"Script terminé avec erreur: {errorBuilder}");
                }

                AddLiveFeedItem("✅ Scan terminé");
                App.LogMessage($"Scan terminé. ExitCode={exitCode}");
                UpdateProgress(85, "PowerShell scan completed");

                HardwareSensorsResult sensorsResult;
                try
                {
                    sensorsResult = await _hardwareSensorsCollector.CollectAsync(_scanCts.Token);
                    _lastSensorsResult = sensorsResult; // Stocker pour injection dans HealthReport
                    var (avail, total) = sensorsResult.GetAvailabilitySummary();
                    App.LogMessage($"[Sensors] Collectés: {avail}/{total} métriques disponibles");
                    
                    // Check for security blocking
                    if (sensorsResult.BlockedBySecurity)
                    {
                        App.LogMessage($"[Sensors] ⚠️ BLOCKED BY SECURITY: {sensorsResult.BlockingMessage}");
                    }
                    
                    // Notify UI of sensor blocking status
                    NotifySensorBlockingChanged();
                }
                catch (Exception ex)
                {
                    sensorsResult = new HardwareSensorsResult();
                    _lastSensorsResult = null;
                    App.LogMessage($"Erreur collecte capteurs: {ex.Message}");
                }

                UpdateProgress(88, "Hardware sensors collected");

                // === PHASE 2B: Collecte PerfCounters robustes ===
                try
                {
                    _lastPerfCounterResult = await PerfCounterCollector.CollectAsync(_scanCts.Token);
                    App.LogMessage($"[PerfCounters] CPU={_lastPerfCounterResult.CpuPercent:F1}%, Mem={_lastPerfCounterResult.MemoryAvailableMB:F0}MB, DiskTime={_lastPerfCounterResult.DiskTimePercent:F1}%");
                }
                catch (Exception ex)
                {
                    _lastPerfCounterResult = null;
                    App.LogMessage($"[PerfCounters] Erreur: {ex.Message}");
                }
                UpdateProgress(90, "Performance counters collected");

                // === PHASE 2C: Collecte des signaux diagnostiques avancés (11 mesures GOD TIER + Internet speed test) ===
                try
                {
                    UpdateProgress(91, "Collecting diagnostic signals...");
                    var signalsOrchestrator = new DiagnosticsSignals.SignalsOrchestrator();
                    // FIX 7: Enable internet speed test only if user opted in
                    signalsOrchestrator.SetAllowExternalNetworkTests(_allowExternalNetworkTests);
                    _lastDiagnosticSignals = await signalsOrchestrator.CollectAllAsync(_scanCts.Token);
                    App.LogMessage($"[DiagnosticSignals] Collected: {_lastDiagnosticSignals.SuccessCount} success, {_lastDiagnosticSignals.FailCount} fail, {_lastDiagnosticSignals.TotalDurationMs}ms");
                }
                catch (Exception ex)
                {
                    _lastDiagnosticSignals = null;
                    App.LogMessage($"[DiagnosticSignals] Erreur: {ex.Message}");
                }
                UpdateProgress(93, "Diagnostic signals collected");

                // === PHASE 2D: Process Telemetry C# Fallback (si PS a échoué) ===
                try
                {
                    UpdateProgress(94, "Collecting process telemetry...");
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

                // === PHASE 2E: Network Diagnostics Complets (internet autorisé) ===
                try
                {
                    UpdateProgress(95, "Running network diagnostics...");
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
                UpdateProgress(96, "Network diagnostics completed");

                // === PHASE 2F: Inventaire pilotes (C#) ===
                try
                {
                    UpdateProgress(96, "Collecting driver inventory...");
                    var driverCollector = new DriverInventoryCollector();
                    _lastDriverInventory = await driverCollector.CollectAsync(
                        _scanCts.Token,
                        includeUpdateLookup: true,
                        onlineUpdateSearch: _allowExternalNetworkTests);
                    App.LogMessage($"[DriverInventory] Completed: total={_lastDriverInventory.TotalCount}, available={_lastDriverInventory.Available}");
                }
                catch (Exception ex)
                {
                    _lastDriverInventory = null;
                    App.LogMessage($"[DriverInventory] Erreur: {ex.Message}");
                }

                // === PHASE 2G: Windows Update (C#) ===
                try
                {
                    UpdateProgress(97, "Collecting Windows Update status...");
                    var updateCollector = new WindowsUpdateCollector();
                    _lastWindowsUpdateResult = await updateCollector.CollectAsync(_scanCts.Token, _allowExternalNetworkTests);
                    App.LogMessage($"[WindowsUpdate] Completed: pending={_lastWindowsUpdateResult.PendingCount}, available={_lastWindowsUpdateResult.Available}");
                }
                catch (Exception ex)
                {
                    _lastWindowsUpdateResult = null;
                    App.LogMessage($"[WindowsUpdate] Erreur: {ex.Message}");
                }

                _resultJsonPath = await ResolveResultJsonPathAsync(outputDir, _scanStartTime, _scanCts.Token);
                await WriteCombinedResultAsync(outputDir, sensorsResult);
                UpdateProgress(98, "JSON resolved");

                // Lire le JSON
                if (!string.IsNullOrWhiteSpace(_resultJsonPath) && File.Exists(_resultJsonPath))
                {
                    await LoadJsonResultAsync();
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
            }
            finally
            {
                lock (_scanLock)
                {
                    _scanProcess?.Dispose();
                    _scanProcess = null;
                    _scanCts?.Dispose();
                    _scanCts = null;
                }
            }
        }

        private void ProcessOutputLine(string line)
        {
            AddLiveFeedItem(line);

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

            // Parser PROGRESS|<count>|<section>
            if (line.StartsWith("PROGRESS|"))
            {
                var parts = line.Split('|');
                if (parts.Length >= 3)
                {
                    if (int.TryParse(parts[1], out int count))
                    {
                        ProgressCount = count;
                        CurrentSection = parts[2];
                        CurrentStep = CurrentSection;
                        
                        // Calculer le pourcentage
                        var percent = (int)Math.Round((count / (double)_totalSteps) * 85);
                        UpdateProgress(percent, $"Progression stdout: {CurrentSection}");
                    }
                }
            }
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

        private void OnScanPipelineCompleted(ScanResult? result, string resultsMessage, string statusMessage, bool forceCompletedStatus)
        {
            App.LogMessage("Attempt build chart: démarrage");
            ResultsMessage = resultsMessage;
            StatusMessage = statusMessage;

            if (result != null)
            {
                try
                {
                    result.Summary.TotalItems = result.Items.Count;
                    ScanResult = result;
                    UpdateScanItemsFromResult(result);
                    UpdateResultSectionsFromResult(result);
                    AddToHistory(result);

                    var chartReady = TryBuildChartData(result, out var chartFailureReason);
                    if (!chartReady)
                    {
                        ResultsMessage = $"Graphique indisponible: {chartFailureReason}";
                        App.LogMessage($"Chart build KO: {chartFailureReason}");
                    }
                    else
                    {
                        App.LogMessage("Chart build OK");
                    }
                }
                catch (Exception ex)
                {
                    ResultsMessage = $"Graphique indisponible: {ex.Message}";
                    App.LogMessage($"Chart build exception: {ex.Message}");
                }
            }
            else
            {
                ScanResult = null;
                ScanItems.Clear();
                ResultSections.Clear();
                OnPropertyChanged(nameof(HasResultSections));
                ResultsMessage = resultsMessage;
                App.LogMessage($"Chart build skipped: {resultsMessage}");
            }

            ScanState = "Completed";
            App.LogMessage($"=== FIN SCAN ===");
            App.LogMessage($"IsScanning={IsScanning}, ScanState={ScanState}");
            if (forceCompletedStatus)
            {
                CurrentStep = GetString("ResultsCompletedTitle");
                StatusMessage = GetString("ResultsCompletedTitle");
            }
            else
            {
                CurrentStep = statusMessage;
            }
            NavigateToResults();
            UpdateProgress(100, "Fin de scan confirmée");
            App.LogMessage("Progress=100 / IsScanning=false");
        }

        private bool TryBuildChartData(ScanResult result, out string reason)
        {
            reason = string.Empty;
            try
            {
                var summary = result.Summary;
                App.LogMessage($"Attempt build chart: total={summary.TotalItems} ok={summary.OkCount} warn={summary.WarningCount} err={summary.ErrorCount} crit={summary.CriticalCount}");

                if (summary.TotalItems <= 0)
                {
                    reason = "Aucune donnée disponible";
                    return false;
                }

                if (summary.OkCount + summary.WarningCount + summary.ErrorCount + summary.CriticalCount <= 0)
                {
                    reason = "Métriques de sévérité vides";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private async Task LoadJsonResultAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_resultJsonPath))
                {
                    throw new FileNotFoundException("Chemin JSON introuvable.");
                }

                App.LogMessage($"Fichier JSON final choisi: {_resultJsonPath}");
                var jsonContent = await File.ReadAllTextAsync(_resultJsonPath, Encoding.UTF8);
                App.LogMessage($"Taille du fichier JSON: {jsonContent.Length} caractères");
                
                // Parse legacy pour compatibilité
                var result = _jsonMapper.Parse(jsonContent, _resultJsonPath, _scanStopwatch.Elapsed);
                result.Summary.TotalItems = result.Items.Count;
                
                // ===== CONSTRUCTION HEALTH REPORT INDUSTRIEL AVEC CAPTEURS =====
                try
                {
                    // Passer les capteurs hardware pour injection dans EvidenceData
                    var healthReport = HealthReportBuilder.Build(
                        jsonContent,
                        _lastSensorsResult,
                        _lastDriverInventory,
                        _lastWindowsUpdateResult);
                    HealthReport = healthReport;
                    App.LogMessage($"[HealthReport] Construit: Score={healthReport.GlobalScore}, Grade={healthReport.Grade}, " +
                        $"Sections={healthReport.Sections.Count}, Confiance={healthReport.ConfidenceModel.ConfidenceLevel}");
                    App.LogMessage($"CollectionStatus={healthReport.CollectionStatus}; errors={healthReport.Errors?.Count ?? 0}; collectorErrorsLogical={healthReport.CollectorErrorsLogical}; missingDataCount={healthReport.MissingData?.Count ?? 0}");
                    App.LogMessage($"ScoreV2_PS={healthReport.ScoreV2?.Score ?? 0}; ScoreCSharp={healthReport.Divergence?.GradeEngineScore ?? 0}; FinalScore={healthReport.GlobalScore}; FinalGrade={healthReport.Grade}; ConfidenceScore={healthReport.ConfidenceModel?.ConfidenceScore ?? 0}");
                    
                    // SYNCHRONISER LE SCORE UNIFIÉ (FinalScore = source de vérité)
                    // On synchronise Summary.Score pour que TOUTE l'UI affiche le même score
                    var unifiedScore = healthReport.GlobalScore;
                    var unifiedGrade = healthReport.Grade;
                    
                    if (result.Summary.Score != unifiedScore)
                    {
                        App.LogMessage($"[ScoreUnifié] Synchronisation: Legacy={result.Summary.Score} -> GradeEngine={unifiedScore} ({unifiedGrade})");
                        App.LogMessage($"[ScoreUnifié] Divergence PS({healthReport.ScoreV2.Score}) vs App({unifiedScore}) = delta {healthReport.Divergence.Delta}");
                        result.Summary.Score = unifiedScore;
                        result.Summary.Grade = unifiedGrade;
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HealthReport] ERREUR construction: {ex.Message}");
                    HealthReport = null;
                }
                // ===== FIN HEALTH REPORT =====
                
                // ===== GÉNÉRATION TXT UNIFIÉ (PS + SENSORS + SCORE) =====
                var outputDir = Path.GetDirectoryName(_resultJsonPath) ?? _reportsDir;
                await GenerateUnifiedTxtReportAsync(outputDir);
                // ===== FIN TXT UNIFIÉ =====

                App.LogMessage($"Scan terminé: Score={result.Summary.Score} | JSON={_resultJsonPath}");
                App.LogMessage("Parse OK");
                if (result.IsValid)
                {
                    ResultsMessage = string.Empty;
                    OnScanPipelineCompleted(result, ResultsMessage, GetString("ResultsCompletedTitle"), forceCompletedStatus: true);
                }
                else
                {
                    ErrorMessage = "Erreur lors du parsing JSON";
                    ResultsMessage = GetString("StatusParsingError");
                    OnScanPipelineCompleted(result, ResultsMessage, GetString("StatusParsingError"), forceCompletedStatus: false);
                }
            }
            catch (JsonException ex)
            {
                var tempDump = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_LastBadJson.json");
                try
                {
                    if (!string.IsNullOrWhiteSpace(_resultJsonPath) && File.Exists(_resultJsonPath))
                    {
                        var raw = await File.ReadAllTextAsync(_resultJsonPath, Encoding.UTF8);
                        await File.WriteAllTextAsync(tempDump, raw, Encoding.UTF8);
                    }
                }
                catch
                {
                    // Ignorer
                }

                ErrorMessage = "Rapport corrompu";
                ResultsMessage = $"Rapport corrompu. Dump: {tempDump}";
                StatusMessage = GetString("StatusParsingError");
                App.LogMessage($"Parse FAIL: {ex.Message} | Dump={tempDump}");
                OnScanPipelineCompleted(null, ResultsMessage, StatusMessage, forceCompletedStatus: false);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Erreur lecture JSON: {ex.Message}";
                ResultsMessage = GetString("StatusLoadReportError");
                StatusMessage = GetString("StatusLoadReportError");
                App.LogMessage($"Parse FAIL: {ex.Message}");
                OnScanPipelineCompleted(null, ResultsMessage, StatusMessage, forceCompletedStatus: false);
            }
        }

        private async Task<string> ResolveResultJsonPathAsync(string outputDir, DateTimeOffset scanStartTime, CancellationToken token)
        {
            var patterns = GetJsonSearchPatterns();
            var candidateDirs = GetCandidateReportDirectories(outputDir);

            App.LogMessage($"Dossier JSON détecté: {outputDir}");
            App.LogMessage($"Pattern JSON détecté: {string.Join(", ", patterns)}");

            // PRIORITÉ 1: JSON annoncé via stdout [OK] JSON: path
            if (!string.IsNullOrWhiteSpace(_jsonPathFromOutput))
            {
                App.LogMessage($"[JSON SOURCE] Priorité 1 - stdout: {_jsonPathFromOutput}");
                if (await WaitForJsonReadyAsync(_jsonPathFromOutput, token))
                {
                    App.LogMessage($"[JSON RÉSOLU] Via stdout: {_jsonPathFromOutput}");
                    LogJsonFileDetails(_jsonPathFromOutput);
                    return _jsonPathFromOutput;
                }
                App.LogMessage($"[JSON] stdout path non accessible, fallback suivant...");
            }

            // PRIORITÉ 2: Path attendu par défaut
            if (File.Exists(_resultJsonPath))
            {
                App.LogMessage($"[JSON SOURCE] Priorité 2 - path attendu: {_resultJsonPath}");
                if (await WaitForJsonReadyAsync(_resultJsonPath, token))
                {
                    App.LogMessage($"[JSON RÉSOLU] Via path attendu: {_resultJsonPath}");
                    LogJsonFileDetails(_resultJsonPath);
                    return _resultJsonPath;
                }
            }

            // PRIORITÉ 3: Scan récursif des dossiers candidats
            App.LogMessage($"[JSON SOURCE] Priorité 3 - scan récursif dans {candidateDirs.Count} dossiers");
            foreach (var dir in candidateDirs)
            {
                var latestJson = FindLatestJsonAfter(dir, patterns, scanStartTime);
                if (!string.IsNullOrWhiteSpace(latestJson))
                {
                    App.LogMessage($"[JSON] Candidat trouvé: {latestJson}");
                    if (await WaitForJsonReadyAsync(latestJson, token))
                    {
                        App.LogMessage($"[JSON RÉSOLU] Via scan récursif: {latestJson}");
                        LogJsonFileDetails(latestJson);
                        return latestJson;
                    }
                }
            }
            
            App.LogMessage("[JSON] Aucune source n'a retourné de fichier valide");
            return string.Empty;
        }

        private static IReadOnlyList<string> GetJsonSearchPatterns()
        {
            return new[] { "Scan_*.json", "scan_result.json", "*.json" };
        }

        private static IReadOnlyList<string> GetCandidateReportDirectories(string outputDir)
        {
            var fallbackDir = Path.Combine(Path.GetTempPath(), "VirtualITPro", "Rapport");
            if (string.Equals(outputDir, fallbackDir, StringComparison.OrdinalIgnoreCase))
            {
                return new[] { outputDir };
            }

            return new[] { outputDir, fallbackDir };
        }

        private static string? FindLatestJsonAfter(string directory, IReadOnlyList<string> patterns, DateTimeOffset scanStartTime)
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var threshold = scanStartTime.AddMinutes(-1);
            var matches = new List<FileInfo>();

            foreach (var pattern in patterns)
            {
                try
                {
                    var files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
                    foreach (var file in files)
                    {
                        matches.Add(new FileInfo(file));
                    }
                }
                catch
                {
                    // Ignorer
                }
            }

            var latest = matches
                .Where(f => f.LastWriteTime >= threshold.LocalDateTime)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            return latest?.FullName;
        }

        private static async Task<bool> WaitForJsonReadyAsync(string filePath, CancellationToken token)
        {
            // Timeout augmenté à 15+ secondes (30 tentatives × 500ms)
            const int maxAttempts = 30;
            const int delayMs = 500;
            var lastSize = -1L;
            var stableCount = 0;

            App.LogMessage($"[JSON] Attente fichier prêt: {filePath} (max {maxAttempts * delayMs / 1000}s)");

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    if (!File.Exists(filePath))
                    {
                        lastSize = -1L;
                        stableCount = 0;
                        await Task.Delay(delayMs, token);
                        continue;
                    }

                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var currentSize = stream.Length;

                    if (currentSize > 0 && currentSize == lastSize)
                    {
                        stableCount++;
                        // Fichier stable pendant 2 checks consécutifs
                        if (stableCount >= 2)
                        {
                            App.LogMessage($"[JSON] Fichier prêt (taille stable: {currentSize} octets, tentative {attempt})");
                            return true;
                        }
                    }
                    else
                    {
                        stableCount = 0;
                    }

                    lastSize = currentSize;
                }
                catch (IOException)
                {
                    // Fichier verrouillé - normal pendant écriture
                    stableCount = 0;
                }
                catch (UnauthorizedAccessException)
                {
                    // Fichier verrouillé
                    stableCount = 0;
                }

                await Task.Delay(delayMs, token);
            }

            App.LogMessage($"[JSON] TIMEOUT: Fichier non prêt après {maxAttempts * delayMs / 1000}s: {filePath}");
            return File.Exists(filePath);
        }

        private static void LogJsonFileDetails(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                App.LogMessage($"Fichier JSON final choisi: {info.FullName}");
                App.LogMessage($"Taille du fichier JSON: {info.Length} octets");
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur lecture taille JSON: {ex.Message}");
            }
        }

        private void NavigateToResults()
        {
            CurrentView = "Results";
            IsViewingArchives = false;
            if (ScanHistory.Count > 0)
            {
                SelectedHistoryScan = ScanHistory[0];
            }
            App.LogMessage("Switch tab to Stats/Results.");
        }

        private async Task WriteCombinedResultAsync(string outputDir, HardwareSensorsResult sensorsResult)
        {
            if (!File.Exists(_resultJsonPath))
            {
                App.LogMessage("JSON PowerShell introuvable pour l'enveloppe combinée.");
                return;
            }

            try
            {
                // P2.1 Normaliser sentinelles AVANT écriture JSON combiné (alignement TXT↔JSON)
                var sanitizeActions = DataSanitizer.SanitizeSensors(sensorsResult);
                if (sanitizeActions.Count > 0)
                {
                    App.LogMessage($"[SANITIZE] Avant écriture JSON combiné: {sanitizeActions.Count} métrique(s) invalidée(s)");
                    foreach (var a in sanitizeActions)
                        App.LogMessage($"  SANITIZE: {a}");
                }

                var jsonContent = await File.ReadAllTextAsync(_resultJsonPath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(jsonContent);

                // PHASE 1+6: Build DiagnosticSnapshot with schemaVersion 2.0.0
                var snapshotBuilder = new DiagnosticSnapshotBuilder()
                    .AddCpuMetrics(sensorsResult)
                    .AddGpuMetrics(sensorsResult)
                    .AddStorageMetrics(sensorsResult)
                    .AddPowerShellData(doc.RootElement)
                    .AddDiagnosticSignals(_lastDiagnosticSignals?.Signals);
                
                var diagnosticSnapshot = snapshotBuilder.Build();

                // P0.2: Collect WMI errors for detailed diagnostics
                var wmiErrors = WmiQueryRunner.GetErrors();
                CollectorDiagnostics? collectorDiagnostics = null;
                if (wmiErrors.Count > 0)
                {
                    collectorDiagnostics = new CollectorDiagnostics { WmiErrors = wmiErrors };
                    App.LogMessage($"[WmiErrors] {wmiErrors.Count} erreurs WMI capturées pour le rapport");
                }

                var combined = new CombinedScanResult
                {
                    ScanPowershell = doc.RootElement.Clone(),
                    SensorsCsharp = sensorsResult,
                    DiagnosticSnapshot = diagnosticSnapshot,
                    DiagnosticSignals = _lastDiagnosticSignals?.Signals,
                    ProcessTelemetry = _lastProcessTelemetry,
                    NetworkDiagnostics = _lastNetworkDiagnostics,
                    CollectorDiagnostics = collectorDiagnostics,
                    DriverInventory = _lastDriverInventory,
                    UpdatesCsharp = _lastWindowsUpdateResult
                };
                
                // === EXTRACTION DES NŒUDS EXPLICITES (missingData, metadata, findings, errors, sections, paths) ===
                ExtractExplicitNodes(doc.RootElement, combined, outputDir);

                var combinedPath = Path.Combine(outputDir, "scan_result_combined.json");
                var combinedJson = JsonSerializer.Serialize(combined, HardwareSensorsResult.JsonOptions);
                await File.WriteAllTextAsync(combinedPath, combinedJson, Encoding.UTF8);
                App.LogMessage($"Rapport combiné généré: {combinedPath} (schemaVersion={diagnosticSnapshot.SchemaVersion})");
                
                _combinedJsonPath = combinedPath;
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur création rapport combiné: {ex.Message}");
            }
        }
        
        // Chemin du JSON combiné pour TXT unifié
        private string _combinedJsonPath = string.Empty;
        
        /// <summary>
        /// Extrait les nœuds explicites du JSON PS vers le CombinedScanResult
        /// pour garantir que missingData, metadata, findings, errors, sections, paths
        /// sont TOUJOURS présents dans scan_result_combined.json
        /// ROBUST: Handles both Array and Object ValueKind for all nodes
        /// </summary>
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
                
                // Log to file for debugging
                LogExtractedNodes(combined, outputDir);
                
                App.LogMessage($"[ExtractNodes] missingData={combined.MissingData.Count}, findings={combined.Findings.Count}, errors={combined.Errors.Count}, sections={combined.Sections.Count}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ExtractNodes] Erreur extraction: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Extract missingData - handles both Array and Object formats
        /// </summary>
        private void ExtractMissingData(JsonElement element, CombinedScanResult combined)
        {
            try
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    // Standard array format
                    foreach (var item in element.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            combined.MissingData.Add(item.GetString() ?? "");
                        else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name))
                            combined.MissingData.Add(name.GetString() ?? "");
                    }
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    // Object format: extract keys or values
                    foreach (var prop in element.EnumerateObject())
                    {
                        // If value is a string, use it; otherwise use the key name
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            combined.MissingData.Add(prop.Value.GetString() ?? prop.Name);
                        else
                            combined.MissingData.Add(prop.Name);
                    }
                    App.LogMessage($"[ExtractMissingData] Converted Object to Array: {combined.MissingData.Count} items");
                }
                else if (element.ValueKind == JsonValueKind.String)
                {
                    // Single string value
                    combined.MissingData.Add(element.GetString() ?? "");
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
                
                File.AppendAllText(logPath, logContent + "\n");
            }
            catch { /* Ignore logging errors */ }
        }

        /// <summary>
        /// Génère le rapport TXT UNIFIÉ = PowerShell + Hardware Sensors + Score + Metadata.
        /// Appelé après que le HealthReport soit construit.
        /// </summary>
        private async Task GenerateUnifiedTxtReportAsync(string outputDir)
        {
            try
            {
                if (string.IsNullOrEmpty(_combinedJsonPath) || !File.Exists(_combinedJsonPath))
                {
                    App.LogMessage("[UnifiedTXT] JSON combiné introuvable, génération TXT annulée");
                    return;
                }

                // Trouver le TXT PowerShell original
                var originalTxtPath = Services.UnifiedReportBuilder.FindLatestPsTxtReport(outputDir);
                if (originalTxtPath == null)
                {
                    // Chercher aussi dans le dossier parent
                    var parentDir = Path.GetDirectoryName(outputDir);
                    if (parentDir != null)
                    {
                        originalTxtPath = Services.UnifiedReportBuilder.FindLatestPsTxtReport(parentDir);
                    }
                }
                
                App.LogMessage($"[UnifiedTXT] TXT PowerShell trouvé: {originalTxtPath ?? "AUCUN"}");

                // Chemin du TXT unifié final
                var unifiedTxtPath = Path.Combine(outputDir, $"Rapport_Unifie_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                // Générer le rapport unifié
                var success = await Services.UnifiedReportBuilder.BuildUnifiedReportAsync(
                    _combinedJsonPath,
                    originalTxtPath,
                    unifiedTxtPath,
                    HealthReport);

                if (success)
                {
                    App.LogMessage($"[UnifiedTXT] ✅ Rapport unifié généré: {unifiedTxtPath}");
                    _lastUnifiedTxtPath = unifiedTxtPath;
                }
                else
                {
                    App.LogMessage("[UnifiedTXT] ❌ Échec génération rapport unifié");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UnifiedTXT] ERREUR: {ex.Message}");
            }
        }
        
        // Chemin du dernier TXT unifié généré
        private string _lastUnifiedTxtPath = string.Empty;

        private void UpdateScanItemsFromResult(ScanResult result)
        {
            ScanItems.Clear();
            foreach (var item in result.Items)
            {
                ScanItems.Add(item);
            }
        }

        private void UpdateResultSectionsFromResult(ScanResult result)
        {
            ResultSections.Clear();
            foreach (var section in result.Sections)
            {
                ResultSections.Add(section);
            }
            OnPropertyChanged(nameof(HasResultSections));
        }

        private void AddToHistory(ScanResult result)
        {
            var historyItem = new ScanHistoryItem
            {
                ScanDate = result.Summary.ScanDate,
                Score = result.Summary.Score,
                Grade = result.Summary.Grade,
                Result = result
            };

            ScanHistory.Insert(0, historyItem);

            // Limiter à 10 scans
            while (ScanHistory.Count > 10)
            {
                ScanHistory.RemoveAt(ScanHistory.Count - 1);
            }

            OnPropertyChanged(nameof(HasAnyScan));
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

            // Reset UI
            UpdateProgress(0, "Scan canceled", allowDecrease: true);
            ProgressCount = 0;
            CurrentStep = GetString("ReadyToScan");
            CurrentSection = string.Empty;
            StatusMessage = GetString("StatusCanceled");
            ScanState = "Idle";
            AddLiveFeedItem("⏹️ Analyse annulée");
        }

        private void OpenReport()
        {
            if (HasAnyScan)
            {
                CurrentView = "Results";
                if (ScanHistory.Count > 0)
                {
                    IsViewingArchives = false;
                    SelectedHistoryScan = ScanHistory[0];
                }
            }
        }

        /// <summary>
        /// Ouvre le rapport TXT dans Bloc-notes
        /// </summary>
        private void OpenReportTxt()
        {
            try
            {
                // Chercher le fichier Rapport.txt dans le dossier des rapports
                var reportTxtPath = FindReportTxtPath();
                
                if (string.IsNullOrEmpty(reportTxtPath) || !File.Exists(reportTxtPath))
                {
                    System.Windows.MessageBox.Show(
                        "Le fichier Rapport.txt n'a pas été trouvé.\n\n" +
                        "Lancez d'abord un scan pour générer le rapport.",
                        "Rapport introuvable",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }

                // Ouvrir dans Notepad
                var startInfo = new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{reportTxtPath}\"",
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                
                App.LogMessage($"[Rapport] Ouverture: {reportTxtPath}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[Rapport] Erreur ouverture: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Impossible d'ouvrir le rapport.\n\n{ex.Message}",
                    "Erreur",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Recherche le fichier Rapport.txt le plus récent (priorité au TXT unifié)
        /// </summary>
        private string? FindReportTxtPath()
        {
            // PRIORITÉ 1: TXT unifié le plus récent (contient PS + Sensors)
            if (!string.IsNullOrEmpty(_lastUnifiedTxtPath) && File.Exists(_lastUnifiedTxtPath))
            {
                return _lastUnifiedTxtPath;
            }

            var searchDirs = new[]
            {
                _reportsDir,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCDiagnosticPro", "Rapports"),
                Path.GetDirectoryName(_resultJsonPath) ?? ""
            };

            // PRIORITÉ 2: Rapport_Unifie (pattern TXT unifié)
            foreach (var dir in searchDirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
            {
                var unifiedFiles = Directory.GetFiles(dir, "Rapport_Unifie*.txt", SearchOption.TopDirectoryOnly);
                if (unifiedFiles.Length > 0)
                {
                    return unifiedFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                }
            }

            // PRIORITÉ 3: Autres patterns TXT
            var patterns = new[] { "Scan_*.txt", "Rapport*.txt", "*_report.txt" };

            foreach (var dir in searchDirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
            {
                foreach (var pattern in patterns)
                {
                    var files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                    {
                        // Retourner le plus récent
                        return files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                    }
                }
            }

            // Fallback: chercher à côté du JSON
            if (!string.IsNullOrEmpty(_resultJsonPath))
            {
                var dir = Path.GetDirectoryName(_resultJsonPath);
                if (dir != null)
                {
                    var txtPath = Path.Combine(dir, "Rapport.txt");
                    if (File.Exists(txtPath)) return txtPath;

                    // Essayer avec le même nom que le JSON mais en .txt
                    txtPath = Path.ChangeExtension(_resultJsonPath, ".txt");
                    if (File.Exists(txtPath)) return txtPath;
                }
            }

            return null;
        }

        private void SelectHistoryScan(ScanHistoryItem? item)
        {
            if (item != null)
            {
                IsViewingArchives = false;
                SelectedHistoryScan = item;
            }
        }

        private void BackToHistory()
        {
            SelectedHistoryScan = null;
            IsViewingArchives = false;
        }

        private void NavigateToArchives()
        {
            SelectedHistoryScan = null;
            IsViewingArchives = true;
        }

        private void ArchiveScan(ScanHistoryItem? item)
        {
            if (item == null) return;

            if (ScanHistory.Remove(item))
            {
                ArchivedScanHistory.Insert(0, item);
                SelectedHistoryScan = null;
                IsViewingArchives = true;
                OnPropertyChanged(nameof(HasAnyScan));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void DeleteScan(ScanHistoryItem? item)
        {
            if (item == null) return;

            if (SelectedHistoryScan == item)
            {
                SelectedHistoryScan = null;
            }

            if (ScanHistory.Remove(item))
            {
                OnPropertyChanged(nameof(HasAnyScan));
            }
            else if (ArchivedScanHistory.Remove(item))
            {
                OnPropertyChanged(nameof(HasAnyScan));
            }

            CommandManager.InvalidateRequerySuggested();
            StatusMessage = GetString("StatusScanDeleted");
        }

        private void RestartAsAdmin()
        {
            try
            {
                var result = AdminService.RestartAsAdmin();
                
                switch (result)
                {
                    case ElevationResult.UserCancelled:
                        // L'utilisateur a annulé UAC, ne pas afficher d'erreur
                        App.LogMessage("Élévation annulée par l'utilisateur");
                        break;
                    case ElevationResult.AlreadyElevated:
                        StatusMessage = GetString("AdminAlreadyElevated");
                        break;
                    case ElevationResult.Error:
                        StatusMessage = GetString("AdminRestartError");
                        break;
                    // Success: l'application va se fermer, pas besoin de message
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Impossible de redémarrer en administrateur: {ex.Message}");
                StatusMessage = GetString("AdminRestartError");
            }
        }

        private void ExportResults()
        {
            try
            {
                if (ScanResult == null) return;

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = $"Diagnostic_{DateTime.Now:yyyyMMdd_HHmmss}",
                    DefaultExt = ".txt",
                    Filter = "Fichiers texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
                };

                if (dialog.ShowDialog() == true)
                {
                    File.WriteAllText(dialog.FileName, ScanResult.RawReport, Encoding.UTF8);
                    StatusMessage = GetString("StatusExportSuccess");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur d'exportation: {ex.Message}");
                StatusMessage = $"{GetString("StatusExportError")}: {ex.Message}";
            }
        }

        private void BrowseReportDirectory()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Sélectionner le dossier des rapports",
                SelectedPath = ReportDirectory,
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ReportDirectory = dialog.SelectedPath;
                IsSettingsDirty = true;
            }
        }

        private void SaveSettings()
        {
            try
            {
                var config = new
                {
                    ReportDirectory = ReportDirectory,
                    Language = CurrentLanguage,
                    AllowExternalNetworkTests = AllowExternalNetworkTests // FIX 7
                };

                var jsonContent = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, jsonContent, Encoding.UTF8);
                
                IsSettingsDirty = false;
                App.LogMessage("Paramètres sauvegardés");
                StatusMessage = GetString("StatusSettingsSaved");
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur sauvegarde paramètres: {ex.Message}");
                StatusMessage = $"{GetString("StatusSettingsSaveError")}: {ex.Message}";
            }
        }
        
        // FIX 7: Async version for property setters
        private Task SaveSettingsAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var config = new
                    {
                        ReportDirectory = ReportDirectory,
                        Language = CurrentLanguage,
                        AllowExternalNetworkTests = AllowExternalNetworkTests
                    };

                    var jsonContent = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_configPath, jsonContent, Encoding.UTF8);
                    App.LogMessage("Paramètres sauvegardés (async)");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"Erreur sauvegarde paramètres (async): {ex.Message}");
                }
            });
        }

        private void LoadSettings()
        {
            try
            {
                _isLoadingSettings = true;

                if (File.Exists(_configPath))
                {
                    var jsonContent = File.ReadAllText(_configPath, Encoding.UTF8);
                    var jsonDoc = JsonDocument.Parse(jsonContent);
                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("ReportDirectory", out var reportDirEl))
                    {
                        _reportDirectory = reportDirEl.GetString() ?? _reportsDir;
                    }
                    else
                    {
                        _reportDirectory = _reportsDir;
                    }

                    if (root.TryGetProperty("Language", out var languageEl))
                    {
                        CurrentLanguage = languageEl.GetString() ?? "fr";
                    }
                    
                    // FIX 7: Load AllowExternalNetworkTests setting
                    if (root.TryGetProperty("AllowExternalNetworkTests", out var extNetEl))
                    {
                        _allowExternalNetworkTests = extNetEl.GetBoolean();
                    }
                }
                else
                {
                    // Valeur par défaut
                    _reportDirectory = _reportsDir;
                }

                OnPropertyChanged(nameof(ReportDirectory));
                OnPropertyChanged(nameof(AllowExternalNetworkTests));
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur chargement paramètres: {ex.Message}");
                _reportDirectory = _reportsDir;
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private string GetString(string key)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(CurrentLanguage) &&
                    _localizedStrings.TryGetValue(CurrentLanguage, out var languageSet) &&
                    languageSet.TryGetValue(key, out var value))
                {
                    return value;
                }

                if (_localizedStrings.TryGetValue("fr", out var fallback) &&
                    fallback.TryGetValue(key, out var fallbackValue))
                {
                    return fallbackValue;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur GetString pour '{key}': {ex.Message}");
            }

            return key;
        }

        private void UpdateLocalizedStrings()
        {
            var properties = new[]
            {
                nameof(HomeTitle),
                nameof(HomeSubtitle),
                nameof(HomeScanTitle),
                nameof(HomeScanAction),
                nameof(HomeScanDescription),
                nameof(HomeChatTitle),
                nameof(HomeChatAction),
                nameof(HomeChatDescription),
                nameof(NavHomeTooltip),
                nameof(NavScanTooltip),
                nameof(NavReportsTooltip),
                nameof(NavSettingsTooltip),
                nameof(HealthProgressTitle),
                nameof(ElapsedTimeLabel),
                nameof(ConfigsScannedLabel),
                nameof(CurrentSectionLabel),
                nameof(LiveFeedLabel),
                nameof(ReportButtonText),
                nameof(ExportButtonText),
                nameof(ScanButtonText),
                nameof(ScanButtonSubtext),
                nameof(CancelButtonText),
                nameof(ChatTitle),
                nameof(ChatSubtitle),
                nameof(ResultsHistoryTitle),
                nameof(ResultsDetailTitle),
                nameof(ResultsCompletedTitle),
                nameof(ResultsCompletionDisplay),
                nameof(ResultsStatusDisplay),
                nameof(ResultsBreakdownTitle),
                nameof(ResultsBreakdownOk),
                nameof(ResultsBreakdownWarning),
                nameof(ResultsBreakdownError),
                nameof(ResultsBreakdownCritical),
                nameof(ResultsDetailsHeader),
                nameof(ResultsBackButton),
                nameof(ResultsNoDataMessage),
                nameof(ResultsCategoryHeader),
                nameof(ResultsItemHeader),
                nameof(ResultsLevelHeader),
                nameof(ResultsDetailHeader),
                nameof(ResultsRecommendationHeader),
                nameof(SettingsTitle),
                nameof(ReportsDirectoryTitle),
                nameof(ReportsDirectoryDescription),
                nameof(BrowseButtonText),
                nameof(AdminRightsTitle),
                nameof(AdminStatusLabel),
                nameof(AdminStatusText),
                nameof(AdminStatusForeground),
                nameof(RestartAdminButtonText),
                nameof(SaveSettingsButtonText),
                nameof(LanguageTitle),
                nameof(LanguageDescription),
                nameof(LanguageLabel),
                nameof(ArchivesButtonText),
                nameof(ArchivesTitle),
                nameof(ArchiveMenuText),
                nameof(DeleteMenuText),
                nameof(ScoreLegendTitle),
                nameof(ScoreRulesTitle),
                nameof(ScoreGradesTitle),
                nameof(ScoreRuleInitial),
                nameof(ScoreRuleCritical),
                nameof(ScoreRuleError),
                nameof(ScoreRuleWarning),
                nameof(ScoreRuleMin),
                nameof(ScoreRuleMax),
                nameof(ScoreGradeA),
                nameof(ScoreGradeB),
                nameof(ScoreGradeC),
                nameof(ScoreGradeD),
                nameof(ScoreGradeF),
                nameof(SelectedScanDateDisplay)
            };

            foreach (var prop in properties)
            {
                OnPropertyChanged(prop);
            }

            if (IsIdle)
            {
                CurrentStep = GetString("ReadyToScan");
                StatusMessage = IsAdmin ? GetString("StatusReady") : GetString("AdminRequiredWarning");
            }

            UpdateScanButtonText();
        }


        private void OnOutputReceived(string output)
        {
            Application.Current?.Dispatcher.Invoke(() => AddLiveFeedItem(output));
        }

        private void OnProgressChanged(int progress)
        {
            Application.Current?.Dispatcher.Invoke(() => UpdateProgress(progress, "PowerShellService progress"));
        }

        private void OnStepChanged(string step)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                CurrentStep = step;
                AddLiveFeedItem($"📍 {step}");
            });
        }

        private void AddLiveFeedItem(string item)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                LiveFeedItems.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {item}");
                while (LiveFeedItems.Count > 100)
                {
                    LiveFeedItems.RemoveAt(LiveFeedItems.Count - 1);
                }
            });
        }

        private void UpdateElapsedTime()
        {
            ElapsedTime = _scanStopwatch.Elapsed.ToString(@"mm\:ss");
        }

        private void UpdateProgress(int percent, string reason, bool allowDecrease = false)
        {
            var normalized = Math.Max(0, Math.Min(100, percent));
            if (!allowDecrease && normalized < ProgressPercent)
            {
                App.LogMessage($"Progress update ignored ({normalized}% < {ProgressPercent}%): {reason}");
                return;
            }

            Progress = normalized;
            ProgressPercent = normalized;
            App.LogMessage($"Progress update: {ProgressPercent}% - {reason}");
        }

        private void StartScanProgressTimer(int ceiling)
        {
            _scanProgressCeiling = Math.Max(0, Math.Min(99, ceiling));
            _scanProgressTimer.Start();
        }

        private void StopScanProgressTimer()
        {
            _scanProgressTimer.Stop();
        }

        private void TickScanProgress()
        {
            if (!IsScanning)
            {
                return;
            }

            if (ProgressPercent >= _scanProgressCeiling)
            {
                return;
            }

            var increment = ProgressPercent < 30 ? 2 : 1;
            UpdateProgress(Math.Min(_scanProgressCeiling, ProgressPercent + increment), "Progression timer");
        }

        private void UpdateScanButtonText()
        {
            if (IsScanning)
            {
                var template = GetString("ScanButtonTextScanning");
                ScanButtonText = FormatStringSafely(template, ProgressPercent);
            }
            else
            {
                ScanButtonText = GetString("ScanButtonText");
            }
        }

        private string FormatStringSafely(string template, params object[] args)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            try
            {
                return string.Format(template, args);
            }
            catch (FormatException ex)
            {
                App.LogMessage($"Erreur formatage string: {ex.Message}");
                return template;
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur inattendue formatage string: {ex.Message}");
                return template;
            }
        }

        private void OnHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasAnyScan));
            ArchivedScanHistoryView.Refresh();
            CommandManager.InvalidateRequerySuggested();
        }

        #endregion
    }

    /// <summary>
    /// Élément d'historique de scan
    /// </summary>
    public class ScanHistoryItem
    {
        public DateTime ScanDate { get; set; }
        public int Score { get; set; }
        public string Grade { get; set; } = "N/A";
        public ScanResult? Result { get; set; }
        public string DateDisplay => ScanDate.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);
        public string DayDisplay => ScanDate.ToString("dd", CultureInfo.CurrentCulture);
        public string MonthYearDisplay => ScanDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        public string ScoreDisplay => $"{Score}/100 ({Grade})";
    }
}
