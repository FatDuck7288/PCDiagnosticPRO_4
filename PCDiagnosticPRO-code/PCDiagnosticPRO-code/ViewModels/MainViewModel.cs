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
    /// <summary>
    /// ViewModel principal de l'application
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        #region Fields

        private readonly PowerShellService _powerShellService;
        private readonly ReportParserService _reportParserService;
        private readonly PowerShellJsonMapper _jsonMapper;
        private readonly HardwareSensorsCollector _hardwareSensorsCollector;
        private readonly DispatcherTimer _liveFeedTimer;
        private readonly DispatcherTimer _scanProgressTimer;
        private readonly DispatcherTimer _rainBitsTimer;
        private readonly DispatcherTimer _ambientFeedTimer;
        private readonly Random _rainBitsRandom = new Random();
        private readonly Queue<string> _ambientRecentDetails = new Queue<string>();
        private readonly Dictionary<string, int> _ambientCursorBySection = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastNonAmbientFeedAtUtc = DateTime.MinValue;
        private string _lastPowerShellSection = string.Empty;
        private readonly Stopwatch _scanStopwatch;
        private string _activeRunId = string.Empty;

        // Structured scan-step trace (for section-aware progress and diagnostics).
        private readonly List<ScanStepTrace> _scanSteps = new List<ScanStepTrace>();
        private readonly object _scanStepLock = new object();
        private DateTime _lastScanStepUiLogUtc = DateTime.MinValue;
        private DateTime _lastScanStepEventUtc = DateTime.MinValue;
        private string _lastScanStepSignature = string.Empty;

        // Process management pour Cancel
        private Process? _scanProcess;
        private CancellationTokenSource? _scanCts;
        private readonly object _scanLock = new object();
        private bool _cancelHandled;

        // Batched live feed: reduce UI callbacks during scan (one flush per batch instead of one per line).
        private readonly List<string> _pendingOutputLines = new List<string>();
        private readonly List<string> _pendingErrorLines = new List<string>();
        private readonly object _pendingLinesLock = new object();
        private bool _pendingFlushScheduled;
        private const int MaxOutputLinesPerFlush = 60;
        private const int MaxErrorLinesPerFlush = 40;
        private StringBuilder? _scanOutputBuilder;
        private StringBuilder? _scanErrorBuilder;
        
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
        
        // Last DiagnosticSnapshot for coverage tracking
        private DiagnosticSnapshot? _lastDiagnosticSnapshot;

        // Résultat Windows Update (C#)
        private WindowsUpdateResult? _lastWindowsUpdateResult;

        // Résultat Security Info (C# - BitLocker, RDP, SMBv1)
        private SecurityInfoCollector.SecurityInfoResult? _lastSecurityInfo;

        // Performance timeseries (min/max/avg over 10-30s)
        private PerformanceTimeseriesSummary? _lastPerformanceTimeseriesSummary;
        // Event log détaillé (derniers N événements Critical/Error)
        private List<EventLogDetailedEntry>? _lastEventLogsDetailed;
        // SMART attributs par disque (WMI)
        private List<SmartDiskEntry>? _lastSmartAttributes;
        // Minidumps détaillés (liste + date, optionnel BugCheck)
        private List<MinidumpEntry>? _lastMinidumpsDetailed;
        // Task pour timeseries (lancée en parallèle, await avant WriteCombined)
        private Task<PerformanceTimeseriesSummary?>? _perfTimeseriesTask;

        // Service LibreSpeed pour tests de vitesse fiables
        private readonly LibreSpeedTestService _libreSpeedService = new();

        // Profiling: timing par phase → %TEMP%\PCDiagnosticPro_timing.log
        private ScanTimingTracker? _scanTimingTracker;

        // Combined JSON data for applications window (from scan_result_combined.json)
        private string? _lastCombinedJsonContent;
        private CombinedScanResult? _lastCombinedResult;
        private RunStatusEnvelope? _lastRunStatus;
        private JsonDocument? _combinedJsonDocumentCache;
        private string? _combinedJsonDocumentCacheContent;
        private readonly object _combinedJsonCacheLock = new();
        private readonly ContractGateOptions _contractGateOptions = new();

        // Chemins relatifs
        private readonly string _baseDir = AppContext.BaseDirectory;
        private readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            App.AppDataFolderName);
        private readonly string _legacyAppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            App.LegacyAppDataFolderName);
        private string _scriptPath = string.Empty;
        private string _reportsDir = string.Empty;
        private string _legacyReportsDir = string.Empty;
        private string _legacyReportsDirAlt = string.Empty;
        private string _resultJsonPath = string.Empty;
        private string _configPath = string.Empty;
        private string _legacyConfigPath = string.Empty;
        private string _reportDisplayNamesPath = string.Empty;
        private string _legacyReportDisplayNamesPath = string.Empty;
        private Dictionary<string, string> _reportDisplayNames = new();
        private DateTimeOffset _scanStartTime;
        private string? _jsonPathFromOutput;
        private string? _jsonCompletionMarkerPath;
        private bool _isHistoryLoading;
        private bool _hasHistoryLoadError;
        private string _historyLoadErrorMessage = string.Empty;
        private readonly string _uiLogPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_ui.log");
        private readonly string _bootLogPath = Path.Combine(Path.GetTempPath(), "PCDiag_boot.log");

        // Settings loading flag
        private bool _isLoadingSettings = false;

        // Progress tracking
        private int _totalSteps = 27;
        private int _scanProgressCeiling = 85;
        private int _powerShellCollectorPercent;
        private readonly ScanProgressEngine _scanProgressEngine = new();
        private int _stdoutEncodingFixCount;
        private int _stderrEncodingFixCount;

        private readonly Dictionary<string, Dictionary<string, string>> _localizedStrings = new()
        {
            ["fr"] = new Dictionary<string, string>
            {
                ["HomeTitle"] = App.BrandDisplayName,
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
                ["LiveFeedPauseLabel"] = "Pause défilement",
                ["ReportButtonText"] = "Rapport intégral",
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
                ["ScoreLegendText"] = "Score = niveau de risque et performance (0-100). A+ = excellent, F = critique.",
                ["ResultsBreakdownTitle"] = "Répartition des niveaux",
                ["ResultsBreakdownOk"] = "OK",
                ["ResultsBreakdownWarning"] = "Avert.",
                ["ResultsBreakdownError"] = "Erreurs",
                ["ResultsBreakdownCritical"] = "Critiques",
                ["ResultsScanDateFormat"] = "Scan du {0}",
                ["ResultsDetailsHeader"] = "Résultats détaillés",
                ["ResultsBackButton"] = "\u2190 Retour",
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
                ["StatusScriptMissing"] = "âŒ Script PowerShell introuvable",
                ["StatusPowerShellMissing"] = "âŒ PowerShell introuvable",
                ["StatusFolderError"] = "❌ Erreur création dossier",
                ["StatusCanceled"] = "⏹️ Analyse annulée",
                ["StatusScanError"] = "âŒ Erreur lors de l'analyse",
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
                ["RenameMenuText"] = "Renommer",
                ["DeleteMenuText"] = "Supprimer",
                ["DeleteScanConfirmTitle"] = "Confirmation",
                ["DeleteScanConfirmMessage"] = "Voulez-vous vraiment supprimer ce scan ?",
                ["CollectFailed"] = "Collecte échouée",
                ["CollectPartialLimited"] = "Collecte partielle / limitée",
                ["PhaseLabel_PowerShell"] = "Inventaire système",
                ["PhaseLabel_Capteurs"] = "Capteurs & températures",
                ["PhaseLabel_Compteurs"] = "Performances temps réel",
                ["PhaseLabel_Signaux"] = "Stabilité & intégrité",
                ["PhaseLabel_Telemetrie"] = "Analyse processus",
                ["PhaseLabel_Reseau"] = "Connectivité réseau",
                ["PhaseLabel_Rapport"] = "Génération rapport",
                ["LiveFeed_PhaseStart_PowerShell"] = "▶ Démarrage du scan PowerShell...",
                ["LiveFeed_PhaseEnd_PowerShell"] = "✅ Scan PowerShell terminé",
                ["LiveFeed_PhaseStart_Capteurs"] = "🔧 Collecte des capteurs matériels...",
                ["LiveFeed_PhaseEnd_Capteurs"] = "✅ Capteurs collectés",
                ["LiveFeed_PhaseStart_Compteurs"] = "📊 Collecte des compteurs de performance...",
                ["LiveFeed_PhaseEnd_Compteurs"] = "✅ Compteurs collectés",
                ["LiveFeed_PhaseStart_Signaux"] = "📡 Collecte des signaux de diagnostic...",
                ["LiveFeed_PhaseEnd_Signaux"] = "✅ Signaux collectés",
                ["LiveFeed_PhaseStart_Telemetrie"] = "📈 Collecte de la télémétrie processus...",
                ["LiveFeed_PhaseEnd_Telemetrie"] = "✅ Télémétrie collectée",
                ["LiveFeed_PhaseStart_Reseau"] = "🌐 Diagnostic réseau en cours...",
                ["LiveFeed_PhaseEnd_Reseau"] = "✅ Diagnostic réseau terminé",
                ["LiveFeed_PhaseStart_Rapport"] = "📄 Génération du rapport...",
                ["LiveFeed_PhaseEnd_Rapport"] = "✅ Rapport généré",
                ["ScanStatus_Preparation"] = "Préparation...",
                ["ScanStatus_Finalization"] = "Finalisation..."
            },
            ["en"] = new Dictionary<string, string>
            {
                ["HomeTitle"] = App.BrandDisplayName,
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
                ["LiveFeedPauseLabel"] = "Pause scroll",
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
                ["ScoreLegendText"] = "Score = risk and performance level (0-100). A+ = excellent, F = critical.",
                ["ResultsBreakdownTitle"] = "Severity breakdown",
                ["ResultsBreakdownOk"] = "OK",
                ["ResultsBreakdownWarning"] = "Warnings",
                ["ResultsBreakdownError"] = "Errors",
                ["ResultsBreakdownCritical"] = "Critical",
                ["ResultsScanDateFormat"] = "Scan from {0}",
                ["ResultsDetailsHeader"] = "Detailed analyzed items",
                ["ResultsBackButton"] = "\u2190 Back",
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
                ["StatusScriptMissing"] = "âŒ PowerShell script not found",
                ["StatusPowerShellMissing"] = "âŒ PowerShell not found",
                ["StatusFolderError"] = "âŒ Error creating folder",
                ["StatusCanceled"] = "⏹️ Scan canceled",
                ["StatusScanError"] = "âŒ Error during scan",
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
                ["RenameMenuText"] = "Rename",
                ["DeleteMenuText"] = "Delete",
                ["DeleteScanConfirmTitle"] = "Confirmation",
                ["DeleteScanConfirmMessage"] = "Do you really want to delete this scan?",
                ["CollectFailed"] = "Collection failed",
                ["CollectPartialLimited"] = "Partial / limited collection",
                ["PhaseLabel_PowerShell"] = "System Inventory",
                ["PhaseLabel_Capteurs"] = "Sensors & Temperatures",
                ["PhaseLabel_Compteurs"] = "Real-time Performance",
                ["PhaseLabel_Signaux"] = "Stability & Integrity",
                ["PhaseLabel_Telemetrie"] = "Process Analysis",
                ["PhaseLabel_Reseau"] = "Network Connectivity",
                ["PhaseLabel_Rapport"] = "Report Generation",
                ["LiveFeed_PhaseStart_PowerShell"] = "▶ Starting PowerShell scan...",
                ["LiveFeed_PhaseEnd_PowerShell"] = "âœ… PowerShell scan completed",
                ["LiveFeed_PhaseStart_Capteurs"] = "🔧 Collecting hardware sensors...",
                ["LiveFeed_PhaseEnd_Capteurs"] = "âœ… Sensors collected",
                ["LiveFeed_PhaseStart_Compteurs"] = "📊 Collecting performance counters...",
                ["LiveFeed_PhaseEnd_Compteurs"] = "âœ… Counters collected",
                ["LiveFeed_PhaseStart_Signaux"] = "📡 Collecting diagnostic signals...",
                ["LiveFeed_PhaseEnd_Signaux"] = "âœ… Signals collected",
                ["LiveFeed_PhaseStart_Telemetrie"] = "📈 Collecting process telemetry...",
                ["LiveFeed_PhaseEnd_Telemetrie"] = "âœ… Telemetry collected",
                ["LiveFeed_PhaseStart_Reseau"] = "🌐 Network diagnostics in progress...",
                ["LiveFeed_PhaseEnd_Reseau"] = "âœ… Network diagnostics completed",
                ["LiveFeed_PhaseStart_Rapport"] = "📄 Generating report...",
                ["LiveFeed_PhaseEnd_Rapport"] = "âœ… Report generated",
                ["ScanStatus_Preparation"] = "Preparing...",
                ["ScanStatus_Finalization"] = "Finalizing..."
            },
            ["es"] = new Dictionary<string, string>
            {
                ["HomeTitle"] = App.BrandDisplayName,
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
                ["LiveFeedPauseLabel"] = "Pausar desplazamiento",
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
                ["ScoreLegendText"] = "Puntuación = nivel de riesgo y rendimiento (0-100). A+ = excelente, F = crítico.",
                ["ResultsBreakdownTitle"] = "Distribución por nivel",
                ["ResultsBreakdownOk"] = "OK",
                ["ResultsBreakdownWarning"] = "Advert.",
                ["ResultsBreakdownError"] = "Errores",
                ["ResultsBreakdownCritical"] = "Críticos",
                ["ResultsScanDateFormat"] = "Escaneo del {0}",
                ["ResultsDetailsHeader"] = "Detalle de elementos analizados",
                ["ResultsBackButton"] = "\u2190 Volver",
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
                ["StatusScriptMissing"] = "âŒ Script de PowerShell no encontrado",
                ["StatusPowerShellMissing"] = "âŒ PowerShell no encontrado",
                ["StatusFolderError"] = "âŒ Error al crear la carpeta",
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
                ["RenameMenuText"] = "Renombrar",
                ["DeleteMenuText"] = "Eliminar",
                ["DeleteScanConfirmTitle"] = "Confirmación",
                ["DeleteScanConfirmMessage"] = "¿Desea eliminar este escaneo?",
                ["CollectFailed"] = "Recolección fallida",
                ["CollectPartialLimited"] = "Recolección parcial / limitada",
                ["PhaseLabel_PowerShell"] = "Inventario del sistema",
                ["PhaseLabel_Capteurs"] = "Sensores y temperaturas",
                ["PhaseLabel_Compteurs"] = "Rendimiento en tiempo real",
                ["PhaseLabel_Signaux"] = "Estabilidad e integridad",
                ["PhaseLabel_Telemetrie"] = "Análisis de procesos",
                ["PhaseLabel_Reseau"] = "Conectividad de red",
                ["PhaseLabel_Rapport"] = "Generación de informe",
                ["LiveFeed_PhaseStart_PowerShell"] = "▶ Iniciando escaneo PowerShell...",
                ["LiveFeed_PhaseEnd_PowerShell"] = "âœ… Escaneo PowerShell completado",
                ["LiveFeed_PhaseStart_Capteurs"] = "🔧 Recopilando sensores de hardware...",
                ["LiveFeed_PhaseEnd_Capteurs"] = "âœ… Sensores recopilados",
                ["LiveFeed_PhaseStart_Compteurs"] = "📊 Recopilando contadores de rendimiento...",
                ["LiveFeed_PhaseEnd_Compteurs"] = "âœ… Contadores recopilados",
                ["LiveFeed_PhaseStart_Signaux"] = "📡 Recopilando señales de diagnóstico...",
                ["LiveFeed_PhaseEnd_Signaux"] = "✅ Señales recopiladas",
                ["LiveFeed_PhaseStart_Telemetrie"] = "📈 Recopilando telemetría de procesos...",
                ["LiveFeed_PhaseEnd_Telemetrie"] = "✅ Telemetría recopilada",
                ["LiveFeed_PhaseStart_Reseau"] = "🌐 Diagnóstico de red en progreso...",
                ["LiveFeed_PhaseEnd_Reseau"] = "✅ Diagnóstico de red completado",
                ["LiveFeed_PhaseStart_Rapport"] = "📄 Generando informe...",
                ["LiveFeed_PhaseEnd_Rapport"] = "âœ… Informe generado",
                ["ScanStatus_Preparation"] = "Preparando...",
                ["ScanStatus_Finalization"] = "Finalizando..."
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
                    if (value == "Scanning")
                    {
                        _rainBitsTimer.Start();
                        StartAmbientFeed();
                    }
                    else
                    {
                        _rainBitsTimer.Stop();
                        StopAmbientFeed();
                    }
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
        public bool IsHistoryLoading
        {
            get => _isHistoryLoading;
            private set
            {
                if (SetProperty(ref _isHistoryLoading, value))
                {
                    OnPropertyChanged(nameof(HasNoScanHistory));
                    OnPropertyChanged(nameof(IsHistoryErrorStateVisible));
                    OnPropertyChanged(nameof(IsHistoryEmptyStateVisible));
                    OnPropertyChanged(nameof(IsHistoryListStateVisible));
                }
            }
        }

        public bool HasHistoryLoadError
        {
            get => _hasHistoryLoadError;
            private set
            {
                if (SetProperty(ref _hasHistoryLoadError, value))
                {
                    OnPropertyChanged(nameof(HasNoScanHistory));
                    OnPropertyChanged(nameof(IsHistoryErrorStateVisible));
                    OnPropertyChanged(nameof(IsHistoryEmptyStateVisible));
                    OnPropertyChanged(nameof(IsHistoryListStateVisible));
                }
            }
        }

        public string HistoryLoadErrorMessage
        {
            get => _historyLoadErrorMessage;
            private set => SetProperty(ref _historyLoadErrorMessage, value);
        }

        public bool IsHistoryErrorStateVisible => HasHistoryLoadError;
        public bool IsHistoryListStateVisible => ScanHistory.Count > 0;
        public bool IsHistoryEmptyStateVisible => !IsHistoryLoading && !HasHistoryLoadError && !HasAnyScan;

        /// <summary>True when no scan exists and no explicit history-load error is active.</summary>
        public bool HasNoScanHistory => !IsHistoryLoading && !HasHistoryLoadError && !HasAnyScan;

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

        private double _smoothProgressPercent;
        /// <summary>
        /// Progression lissée (double) pour les bindings XAML.
        /// S'interpole de façon fluide vers ProgressPercent / le plafond du timer.
        /// </summary>
        public double SmoothProgressPercent
        {
            get => _smoothProgressPercent;
            private set
            {
                if (Math.Abs(_smoothProgressPercent - value) > 0.001)
                {
                    _smoothProgressPercent = value;
                    OnPropertyChanged(nameof(SmoothProgressPercent));
                }
            }
        }

        private bool _isScanProgressIndeterminate;
        /// <summary>
        /// True when no real PROGRESS markers have been received from the PS script.
        /// Bound to ProgressBar.IsIndeterminate - shows an honest spinner instead of a fake %.
        /// </summary>
        public bool IsScanProgressIndeterminate
        {
            get => _isScanProgressIndeterminate;
            private set => SetProperty(ref _isScanProgressIndeterminate, value);
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

        private string _contractGateBannerText = string.Empty;
        public string ContractGateBannerText
        {
            get => _contractGateBannerText;
            set
            {
                if (SetProperty(ref _contractGateBannerText, value))
                    OnPropertyChanged(nameof(ShowContractGateBanner));
            }
        }

        private string _contractGateBannerDetails = string.Empty;
        public string ContractGateBannerDetails
        {
            get => _contractGateBannerDetails;
            set => SetProperty(ref _contractGateBannerDetails, value);
        }

        public bool ShowContractGateBanner => !string.IsNullOrWhiteSpace(ContractGateBannerText);

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
                    OnPropertyChanged(nameof(DisplayGrade));
                    OnPropertyChanged(nameof(StatusWithScore));
                    OnPropertyChanged(nameof(ResultsCompletionDisplay));
                    OnPropertyChanged(nameof(ResultsStatusDisplay));
                    OnPropertyChanged(nameof(TotalItemsForChart));
                    OnPropertyChanged(nameof(OkCountDisplay));
                    OnPropertyChanged(nameof(WarningCountDisplay));
                    OnPropertyChanged(nameof(ErrorCountDisplay));
                    OnPropertyChanged(nameof(CriticalCountDisplay));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool HasScanResult => ScanResult != null && ScanResult.IsValid;
        public string ScoreDisplay => ScanResult?.Summary?.Score.ToString() ?? "0";
        public string GradeDisplay => ScanResult?.Summary?.Grade ?? "N/A";
        /// <summary>Sous-score fiabilité = moyenne DRS + ConfidenceScore.</summary>
        public int ReliabilitySubScore => HasScanResult
            ? (int)Math.Round(((HealthReport?.DataReliabilityScore ?? 0) + (HealthReport?.ConfidenceModel?.ConfidenceScore ?? 0)) / 2.0)
            : 0;
        public string ReliabilitySubScoreDisplay => ReliabilitySubScore > 0 ? $"Fiabilité: {ReliabilitySubScore}/100" : "";

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

        // === HEALTH REPORT (Modèle industriel) ===
        
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
                    OnPropertyChanged(nameof(IsLowConfidence));
                    OnPropertyChanged(nameof(ScoreCircleOpacity));
                    OnPropertyChanged(nameof(LowConfidenceWarning));
                    // Coverage Score
                    OnPropertyChanged(nameof(CoveragePercent));
                    OnPropertyChanged(nameof(CoverageQualityLabel));
                    OnPropertyChanged(nameof(CoverageDisplay));
                    OnPropertyChanged(nameof(IsCoverageLow));
                    OnPropertyChanged(nameof(CoverageLowWarning));
                    OnPropertyChanged(nameof(CollectionStatusBadgeText));
                    OnPropertyChanged(nameof(IsCollectionPartialOrFailed));
                    OnPropertyChanged(nameof(CollectorErrorsLogicalDisplay));
                    OnPropertyChanged(nameof(MachineHealthScore));
                    OnPropertyChanged(nameof(DataReliabilityScore));
                    OnPropertyChanged(nameof(UnifiedReliabilityScore));
                    OnPropertyChanged(nameof(UnifiedReliabilityLabel));
                    OnPropertyChanged(nameof(UnifiedReliabilityDisplay));
                    OnPropertyChanged(nameof(HealthScore));
                    OnPropertyChanged(nameof(ReliabilityScore));
                    OnPropertyChanged(nameof(ShowPartialCollectionBadge));
                    OnPropertyChanged(nameof(DisplayGrade));
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
                    OnPropertyChanged(nameof(NetworkUploadMbps));
                    OnPropertyChanged(nameof(NetworkLatencyMs));
                    OnPropertyChanged(nameof(NetworkSpeedTier));
                    OnPropertyChanged(nameof(NetworkRecommendation));
                    OnPropertyChanged(nameof(NetworkDownloadColor));
                    OnPropertyChanged(nameof(NetworkUploadColor));
                    OnPropertyChanged(nameof(NetworkLatencyColor));
                    UpdateUdisSectionsSummary();
                    UpdateHealthSections();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool HasHealthReport => HealthReport != null;
        public int GlobalHealthScore => HealthReport?.GlobalScore ?? 0;
        public string GlobalHealthGrade => HealthReport?.Grade ?? "N/A";
        public string GlobalHealthMessage => HealthReport?.GlobalMessage ?? "Aucune analyse disponible";
        
        /// <summary>P0.3 / P3: Badge "Partiel / Limité" si collecte FAILED/PARTIAL/INCOMPLETE ou collectorErrorsLogical > 0</summary>
        public bool IsCollectionPartialOrFailed =>
            HealthReport?.CollectionStatus == RunState.Partial ||
            HealthReport?.CollectionStatus == RunState.Incomplete ||
            HealthReport?.CollectionStatus == RunState.Failed ||
            (HealthReport?.CollectorErrorsLogical ?? 0) > 0;
        public string CollectionStatusBadgeText =>
            HealthReport == null
                ? string.Empty
                : HealthReport.CollectionStatus == RunState.Failed
                    ? GetString("CollectFailed")
                    : HealthReport.CollectionStatus == RunState.Incomplete
                        ? "Collecte incomplète"
                        : HealthReport.CollectionStatus == RunState.Partial
                        ? GetString("CollectPartialLimited")
                        : "Collecte complète";
        public string CollectorErrorsLogicalDisplay
        {
            get
            {
                if (HealthReport == null)
                    return string.Empty;

                var errors = HealthReport.Errors?.Count ?? 0;
                var missing = HealthReport.MissingData?.Count ?? 0;
                return $"Erreurs: {errors} | Manquants: {missing}";
            }
        }
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
        /// <summary>True quand la confiance est trop basse pour se fier au score santé.</summary>
        public bool IsLowConfidence => ConfidenceScore > 0 && ConfidenceScore < 70;
        /// <summary>Opacité du cercle score : atténuée si confiance faible.</summary>
        public double ScoreCircleOpacity => IsLowConfidence ? 0.5 : 1.0;
        /// <summary>Message d'avertissement affiché sous le score quand confiance faible.</summary>
        public string LowConfidenceWarning => IsLowConfidence 
            ? $"⚠ Collecte insuffisante ({ConfidenceScore}/100) - score non fiable"
            : "";

        // === COVERAGE SCORE (couverture de collecte) ===
        public double CoveragePercent => _lastDiagnosticSnapshot?.CollectionQuality?.CoveragePercent ?? 0;
        public string CoverageQualityLabel => CoveragePercent >= 90 ? "FULL" : CoveragePercent >= 50 ? "PARTIAL" : "LOW";
        public string CoverageDisplay => _lastDiagnosticSnapshot != null
            ? $"Collecte: {CoveragePercent:F0}% / qualité: {CoverageQualityLabel}"
            : "";
        public bool IsCoverageLow => _lastDiagnosticSnapshot != null && CoveragePercent < 70;
        public string CoverageLowWarning => IsCoverageLow
            ? $"⚠ Couverture collecte faible ({CoveragePercent:F0}%) - données incomplètes"
            : "";

        // === UDIS - AFFICHAGE MODE INDUSTRIE (séparé) ===
        public int MachineHealthScore => HealthReport?.MachineHealthScore ?? 0;
        public int DataReliabilityScore => HealthReport?.DataReliabilityScore ?? 0;
        public int DiagnosticClarityScore => HealthReport?.DiagnosticClarityScore ?? 0;
        public string MachineHealthDisplay => $"{MachineHealthScore}/100";
        public string DataReliabilityDisplay => $"{DataReliabilityScore}/100";

        /// <summary>Indicateur unique de fiabilité affiché une seule fois : XX/100 (OK/Partiel/Faible).</summary>
        public int UnifiedReliabilityScore => HealthReport?.DataReliabilityScore ?? 0;
        public string UnifiedReliabilityLabel => UnifiedReliabilityScore >= 80 ? "OK" : UnifiedReliabilityScore >= 60 ? "Partiel" : "Faible";
        public string UnifiedReliabilityDisplay => $"Fiabilité : {UnifiedReliabilityScore}/100 ({UnifiedReliabilityLabel})";

        public bool AutoFixAllowed => HealthReport?.AutoFixAllowed ?? false;
        /// <summary>HealthScore 0-100 (santé technique) - alias GlobalScore pour affichage.</summary>
        public int HealthScore => HealthReport?.GlobalScore ?? 0;
        /// <summary>ReliabilityScore 0-100 (fiabilité de la collecte) - alias DataReliabilityScore.</summary>
        public int ReliabilityScore => HealthReport?.DataReliabilityScore ?? 0;
        /// <summary>Seuil sous lequel on affiche le badge "Collecte partielle" et on plafonne le grade.</summary>
        private const int ReliabilityBadgeThreshold = 70;
        /// <summary>True si ReliabilityScore &lt; seuil → afficher badge "Collecte partielle".</summary>
        public bool ShowPartialCollectionBadge => ReliabilityScore > 0 && ReliabilityScore < ReliabilityBadgeThreshold;
        /// <summary>Grade affiché : plafonné à C max quand collecte partielle (ReliabilityScore &lt; 70).</summary>
        public string DisplayGrade
        {
            get
            {
                var grade = ScanResult?.Summary?.Grade ?? HealthReport?.Grade ?? "N/A";
                if (ShowPartialCollectionBadge && !string.IsNullOrEmpty(grade))
                {
                    if (grade.StartsWith("A", StringComparison.OrdinalIgnoreCase) || grade.StartsWith("B", StringComparison.OrdinalIgnoreCase))
                        return "C";
                }
                return grade;
            }
        }

        // === UDIS - NOUVELLES SECTIONS ===
        public int ThermalScore => HealthReport?.UdisReport?.ThermalScore ?? 100;
        public string ThermalStatus => HealthReport?.UdisReport?.ThermalStatus ?? "N/A";
        public int BootHealthScore => HealthReport?.UdisReport?.BootHealthScore ?? 100;
        public string BootHealthTier => HealthReport?.UdisReport?.BootHealthTier ?? "N/A";
        public int StorageIoHealthScore => HealthReport?.UdisReport?.StorageIoHealthScore ?? 100;
        public string StorageIoStatus => HealthReport?.UdisReport?.StorageIoStatus ?? "N/A";
        public int SystemStabilityIndex => HealthReport?.UdisReport?.SystemStabilityIndex ?? 100;
        public string CpuPerformanceTier => HealthReport?.UdisReport?.CpuPerformanceTier ?? "N/A";

        // === UDIS - NETWORK SPEED TEST ===
        // Standalone backing fields pour permettre le SpeedTest avant/sans scan
        private double? _standaloneDownloadMbps;
        private double? _standaloneUploadMbps;
        private double? _standaloneLatencyMs;
        private string _standaloneSpeedTier = "Non mesuré";
        private string _standaloneRecommendation = "";
        private DateTime? _lastSpeedTestTime;
        
        // Propriétés combinées: UdisReport si disponible, sinon standalone
        public double? NetworkDownloadMbps => HealthReport?.UdisReport?.DownloadMbps ?? _standaloneDownloadMbps;
        public double? NetworkUploadMbps => HealthReport?.UdisReport?.UploadMbps ?? _standaloneUploadMbps;
        public double? NetworkLatencyMs => HealthReport?.UdisReport?.LatencyMs ?? _standaloneLatencyMs;
        public string NetworkSpeedTier => HealthReport?.UdisReport?.NetworkSpeedTier ?? _standaloneSpeedTier;
        public string NetworkRecommendation => HealthReport?.UdisReport?.NetworkRecommendation ?? _standaloneRecommendation;
        public string LastSpeedTestDisplay => _lastSpeedTestTime.HasValue 
            ? $"Dernier test: {_lastSpeedTestTime.Value:HH:mm:ss}" 
            : "";
        
        // Seuils de qualité selon spécification (modifiables en constantes)
        private const double DOWNLOAD_GOOD_THRESHOLD = 50.0;    // Mbps
        private const double DOWNLOAD_MEDIUM_THRESHOLD = 15.0;  // Mbps
        private const double UPLOAD_GOOD_THRESHOLD = 10.0;      // Mbps
        private const double UPLOAD_MEDIUM_THRESHOLD = 5.0;     // Mbps
        
        // Qualité de connexion globale basée sur Download ET Upload
        public string ConnectionQuality
        {
            get
            {
                var dl = NetworkDownloadMbps;
                var ul = NetworkUploadMbps;
                if (!dl.HasValue || !ul.HasValue) return "NonMesuré";
                if (dl >= DOWNLOAD_GOOD_THRESHOLD && ul >= UPLOAD_GOOD_THRESHOLD) return "Bonne";
                if (dl >= DOWNLOAD_MEDIUM_THRESHOLD && ul >= UPLOAD_MEDIUM_THRESHOLD) return "Moyenne";
                return "Mauvaise";
            }
        }
        
        // Couleur pour le débit Download (seuils ajustés selon spec)
        // Bonne: >= 50 Mbps, Moyenne: >= 15 Mbps, Mauvaise: < 15 Mbps
        public string NetworkDownloadColor => NetworkDownloadMbps switch
        {
            >= DOWNLOAD_GOOD_THRESHOLD => "#22C55E",    // Vert - Bonne connexion
            >= DOWNLOAD_MEDIUM_THRESHOLD => "#F59E0B", // Orange - Connexion moyenne
            > 0 => "#EF4444",                          // Rouge - Mauvaise connexion
            _ => "#6B7280"                             // Gris si non mesuré
        };
        
        // Couleur pour le débit Upload (seuils ajustés selon spec)
        // Bonne: >= 10 Mbps, Moyenne: >= 5 Mbps, Mauvaise: < 5 Mbps
        public string NetworkUploadColor => NetworkUploadMbps switch
        {
            >= UPLOAD_GOOD_THRESHOLD => "#22C55E",    // Vert - Bonne connexion
            >= UPLOAD_MEDIUM_THRESHOLD => "#F59E0B", // Orange - Connexion moyenne
            > 0 => "#EF4444",                        // Rouge - Mauvaise connexion
            _ => "#6B7280"                           // Gris si non mesuré
        };
        
        // Couleur pour la latence (vert < 30, jaune 30-100, rouge > 100)
        public string NetworkLatencyColor => NetworkLatencyMs switch
        {
            <= 30 => "#22C55E",   // Vert
            <= 100 => "#F59E0B",  // Orange
            > 100 => "#EF4444",   // Rouge
            _ => "#6B7280"        // Gris si non mesuré
        };
        
        // === PROCESS TELEMETRY - UI DISPLAY ===
        public bool HasProcessTelemetry => _lastProcessTelemetry?.Available ?? false;
        public int ProcessCount => _lastProcessTelemetry?.TotalProcessCount ?? 0;
        public string TopCpuProcess => _lastProcessTelemetry?.TopByCpu?.FirstOrDefault()?.Name ?? "N/A";
        public double TopCpuPercent => _lastProcessTelemetry?.TopByCpu?.FirstOrDefault()?.CpuPercent ?? 0;
        public string TopMemoryProcess => _lastProcessTelemetry?.TopByMemory?.FirstOrDefault()?.Name ?? "N/A";
        public double TopMemoryMB => _lastProcessTelemetry?.TopByMemory?.FirstOrDefault()?.WorkingSetMB ?? 0;
        public string ProcessTelemetryDisplay => HasProcessTelemetry 
            ? $"{ProcessCount} processus | Top CPU: {TopCpuProcess} ({TopCpuPercent:F1}%) | Top RAM: {TopMemoryProcess} ({TopMemoryMB:F0} MB)"
            : "Données non disponibles";
        
        /// <summary>
        /// T?CHE 6: Top 5 processus RAM comme collection pour tableau visuel
        /// </summary>
        public IEnumerable<ProcessDisplayItem> Top5RamProcesses => 
            _lastProcessTelemetry?.TopByMemory?.Take(5).Select((p, i) => new ProcessDisplayItem
            {
                Rank = i + 1,
                ProcessName = p.Name,
                RamUsedMB = p.WorkingSetMB,
                RamUsedDisplay = p.WorkingSetMB >= 1024 
                    ? $"{p.WorkingSetMB / 1024:F1} GB" 
                    : $"{p.WorkingSetMB:F0} MB",
                RamPercent = 0
            }) ?? Enumerable.Empty<ProcessDisplayItem>();

        /// <summary>True when RAM process data is available (non-empty).</summary>
        public bool HasTop5RamProcesses => _lastProcessTelemetry?.TopByMemory?.Any() == true;

        /// <summary>
        /// T?CHE 6: Top 5 processus CPU comme collection
        /// </summary>
        public IEnumerable<ProcessDisplayItem> Top5CpuProcesses =>
            _lastProcessTelemetry?.TopByCpu?.Take(5).Select((p, i) => new ProcessDisplayItem
            {
                Rank = i + 1,
                ProcessName = p.Name,
                CpuPercent = p.CpuPercent,
                CpuDisplay = $"{p.CpuPercent:F1}%"
            }) ?? Enumerable.Empty<ProcessDisplayItem>();

        /// <summary>True when CPU process data is available (non-empty).</summary>
        public bool HasTop5CpuProcesses => _lastProcessTelemetry?.TopByCpu?.Any() == true;
        
        // === SENSOR BLOCKING STATUS - UI DISPLAY ===
        public bool IsSensorBlocked => _lastSensorsResult?.BlockedBySecurity ?? false;
        public string SensorBlockingMessage => _lastSensorsResult?.BlockingMessage ?? "";
        public bool HasSensorBlockingMessage => !string.IsNullOrEmpty(SensorBlockingMessage);
        
        // === NETWORK DIAGNOSTICS - UI DISPLAY ===
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
            if (NetPacketLoss > 5 || NetLatencyP95 > 200) return "?? D?grad?";
            if (NetPacketLoss > 1 || NetLatencyP95 > 100) return "⚡ Acceptable";
            return "âœ… Excellent";
        }

        private bool _isSpeedTestRunning;
        public bool IsSpeedTestRunning
        {
            get => _isSpeedTestRunning;
            set => SetProperty(ref _isSpeedTestRunning, value);
        }

        // CancellationTokenSource pour annuler le speed test en cours
        private CancellationTokenSource? _speedTestCts;
        
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

        /// <summary>
        /// Activer la surveillance matérielle (LibreHardwareMonitor) pour températures CPU/GPU.
        /// Quand true, le collecteur full est utilisé ; sinon safe (WMI only). Défaut: false.
        /// </summary>
        private bool _enableHardwareMonitoring = false;
        /// <summary>When true, skip full hardware sensors (user chose "limited mode" without admin).</summary>
        private bool _skipHardwareSensors = false;
        public bool EnableHardwareMonitoring
        {
            get => _enableHardwareMonitoring;
            set
            {
                if (SetProperty(ref _enableHardwareMonitoring, value))
                {
                    SaveSettingsAsync().ConfigureAwait(false);
                }
            }
        }

        // === UDIS - SECTIONS SUMMARY POUR UI ===
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
        /// Injecte les résultats du speed test (download/upload/ping/jitter) dans la section Réseau du rapport.
        /// Si aucun speed test n'a été fait, affiche "Speed test" = "Non effectué".
        /// </summary>
        private void InjectSpeedTestIntoNetworkSection(HealthReport? report)
        {
            if (report?.Sections == null) return;
            var networkSection = report.Sections.FirstOrDefault(s => s.Domain == HealthDomain.Network);
            if (networkSection == null) return;

            var dl = NetworkDownloadMbps;
            var ul = NetworkUploadMbps;
            var ping = NetworkLatencyMs;
            var jitter = _lastNetworkDiagnostics?.OverallJitterMsP95;

            if (dl.HasValue || ul.HasValue || ping.HasValue)
            {
                if (dl.HasValue)
                    networkSection.EvidenceData["Débit descendant (speed test)"] = $"{dl.Value:F0} Mbps";
                if (ul.HasValue)
                    networkSection.EvidenceData["Débit montant (speed test)"] = $"{ul.Value:F0} Mbps";
                if (ping.HasValue)
                    networkSection.EvidenceData["Ping (speed test)"] = $"{ping.Value:F0} ms";
                if (jitter.HasValue)
                    networkSection.EvidenceData["Jitter (speed test)"] = $"{jitter.Value:F0} ms";
            }
            else
            {
                networkSection.EvidenceData["Speed test"] = "Non effectué";
            }
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
            OnPropertyChanged(nameof(Top5RamProcesses));
            OnPropertyChanged(nameof(Top5CpuProcesses));
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
        /// Toggle SpeedTest réseau: démarre ou arrête le test.
        /// </summary>
        private ICommand? _toggleSpeedTestCommand;
        public ICommand ToggleSpeedTestCommand => _toggleSpeedTestCommand ??= new RelayCommand(async _ =>
        {
            if (IsSpeedTestRunning)
            {
                // Arrêter le test en cours
                StopSpeedTest();
                return;
            }

            // Démarrer un nouveau test
            await StartSpeedTestAsync();
        });

        /// <summary>
        /// Démarre le SpeedTest réseau (async, non bloquant).
        /// </summary>
        private async Task StartSpeedTestAsync()
        {
            _speedTestCts?.Cancel();
            _speedTestCts?.Dispose();
            _speedTestCts = new CancellationTokenSource();
            var ct = _speedTestCts.Token;

            IsSpeedTestRunning = true;
            
            try
            {
                App.LogMessage("[SpeedTest] Démarrage du test LibreSpeed...");
                AddLiveFeedItem("▶ Test de vitesse réseau en cours...");
                
                // Utiliser LibreSpeed CLI en priorité
                var libreResult = await _libreSpeedService.RunTestAsync(ct);
                
                // Vérifier si annulé
                if (ct.IsCancellationRequested)
                {
                    App.LogMessage("[SpeedTest] Test annulé par l'utilisateur");
                    AddLiveFeedItem("⏹ Test de vitesse annulé");
                    return;
                }
                
                if (libreResult.Success)
                {
                    // TOUJOURS stocker dans les propriétés standalone (fonctionnent sans scan)
                    _standaloneDownloadMbps = libreResult.DownloadMbps;
                    _standaloneUploadMbps = libreResult.UploadMbps;
                    _standaloneLatencyMs = libreResult.PingMs;
                    _standaloneSpeedTier = libreResult.SpeedTier;
                    _standaloneRecommendation = GetSpeedRecommendation(libreResult);
                    _lastSpeedTestTime = DateTime.Now;
                    
                    App.LogMessage($"[SpeedTest] LibreSpeed OK: Down={libreResult.DownloadMbps:F1} Mbps, Up={libreResult.UploadMbps:F1} Mbps, Ping={libreResult.PingMs:F1} ms");
                    AddLiveFeedItem($"âœ… Speed Test: {libreResult.DownloadMbps:F1} Mbps ↓ / {libreResult.UploadMbps:F1} Mbps ↑");
                    
                    // CROSS-CHECK UPLOAD: LibreSpeed CLI may under-report upload speed
                    // (single server, distant location, limited concurrency).
                    // Always run our own parallel multi-stream upload test and take the MAX.
                    try
                    {
                        AddLiveFeedItem("↑ Vérification upload (multi-stream)...");
                        App.LogMessage("[SpeedTest] Cross-check upload: running parallel upload test...");
                        var netCollector = new Services.NetworkDiagnostics.NetworkDiagnosticsCollector();
                        var netResult = await netCollector.CollectUploadOnlyAsync(ct);
                        if (netResult.HasValue && netResult.Value > 0)
                        {
                            App.LogMessage($"[SpeedTest] Cross-check upload result: {netResult.Value:F1} Mbps (LibreSpeed was {libreResult.UploadMbps:F1} Mbps)");
                            double libreUp = libreResult.UploadMbps ?? 0;
                            double bestUpload = Math.Max(libreUp, netResult.Value);
                            if (netResult.Value > libreUp * 1.2) // Our test is >20% higher
                            {
                                App.LogMessage($"[SpeedTest] Using cross-check upload ({bestUpload:F1} Mbps) over LibreSpeed ({libreUp:F1} Mbps)");
                                _standaloneUploadMbps = bestUpload;
                                AddLiveFeedItem($"✅ Upload corrigé: {bestUpload:F1} Mbps ↑ (multi-stream)");
                            }
                        }
                    }
                    catch (Exception crossEx)
                    {
                        App.LogMessage($"[SpeedTest] Cross-check upload failed (using LibreSpeed value): {crossEx.Message}");
                    }
                    
                    // Mettre à jour UdisReport si disponible
                    if (HealthReport?.UdisReport != null)
                    {
                        HealthReport.UdisReport.DownloadMbps = _standaloneDownloadMbps;
                        HealthReport.UdisReport.UploadMbps = _standaloneUploadMbps;
                        HealthReport.UdisReport.LatencyMs = libreResult.PingMs;
                        HealthReport.UdisReport.NetworkSpeedTier = libreResult.SpeedTier;
                        HealthReport.UdisReport.NetworkRecommendation = _standaloneRecommendation;
                    }
                    InjectSpeedTestIntoNetworkSection(HealthReport);
                    UpdateHealthSections();

                    // Sauvegarder le résultat en JSON pour inspection LLM
                    var jsonPath = await _libreSpeedService.SaveResultToJsonAsync(libreResult);
                    if (!string.IsNullOrEmpty(jsonPath))
                        App.LogMessage($"[SpeedTest] JSON sauvegardé: {jsonPath}");
                }
                else if (!ct.IsCancellationRequested)
                {
                    // LibreSpeed échoué - essayer fallback HTTP
                    App.LogMessage($"[SpeedTest] LibreSpeed échoué ({libreResult.Error}), fallback HTTP...");
                    AddLiveFeedItem($"⚠️ LibreSpeed échoué, essai fallback...");
                    
                    try
                    {
                        var fallbackResult = await _libreSpeedService.RunFallbackTestAsync(ct);
                        
                        if (ct.IsCancellationRequested) return;
                        
                        if (fallbackResult.Success && fallbackResult.DownloadMbps.HasValue)
                        {
                            _standaloneDownloadMbps = fallbackResult.DownloadMbps;
                            _standaloneLatencyMs = fallbackResult.PingMs;
                            _standaloneSpeedTier = fallbackResult.SpeedTier;
                            _lastSpeedTestTime = DateTime.Now;
                            
                            App.LogMessage($"[SpeedTest] Fallback OK: Download={fallbackResult.DownloadMbps:F1} Mbps");
                            AddLiveFeedItem($"âœ… Speed Test (fallback): {fallbackResult.DownloadMbps:F1} Mbps ↓");
                            InjectSpeedTestIntoNetworkSection(HealthReport);
                            UpdateHealthSections();

                            // FIX #4: Fallback upload - use NetworkDiagnosticsCollector's upload test
                            // instead of leaving upload as null
                            try
                            {
                                AddLiveFeedItem("↑ Mesure upload en cours...");
                                var netCollector = new Services.NetworkDiagnostics.NetworkDiagnosticsCollector();
                                var netResult = await netCollector.CollectAsync(ct);
                                if (netResult.Throughput?.UploadMbpsMedian.HasValue == true)
                                {
                                    _standaloneUploadMbps = netResult.Throughput.UploadMbpsMedian;
                                    _standaloneRecommendation = $"Download: fallback HTTP, Upload: test direct ({_standaloneUploadMbps:F1} Mbps)";
                                    App.LogMessage($"[SpeedTest] Fallback upload OK: {_standaloneUploadMbps:F1} Mbps");
                                    AddLiveFeedItem($"âœ… Upload (fallback): {_standaloneUploadMbps:F1} Mbps ↑");
                                }
                                else
                                {
                                    _standaloneUploadMbps = null;
                                    _standaloneRecommendation = "Test partiel (fallback HTTP) - upload non mesuré";
                                    App.LogMessage("[SpeedTest] Fallback upload failed");
                                }
                            }
                            catch (Exception uploadEx)
                            {
                                _standaloneUploadMbps = null;
                                _standaloneRecommendation = "Test partiel (fallback HTTP) - upload non mesuré";
                                App.LogMessage($"[SpeedTest] Fallback upload error: {uploadEx.Message}");
                            }
                        }
                        else
                        {
                            App.LogMessage($"[SpeedTest] Fallback échoué: {fallbackResult.Error}");
                            AddLiveFeedItem($"❌ Test de vitesse échoué: {fallbackResult.Error}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        App.LogMessage("[SpeedTest] Test annulé pendant fallback");
                        AddLiveFeedItem("⏹ Test de vitesse annulé");
                        return;
                    }
                    catch (Exception fallbackEx)
                    {
                        App.LogMessage($"[SpeedTest] Erreur fallback: {fallbackEx.Message}");
                        AddLiveFeedItem($"❌ Test de vitesse échoué");
                    }
                }
                
                // Notifier la UI de tous les changements
                OnPropertyChanged(nameof(NetworkDownloadMbps));
                OnPropertyChanged(nameof(NetworkUploadMbps));
                OnPropertyChanged(nameof(NetworkLatencyMs));
                OnPropertyChanged(nameof(NetworkSpeedTier));
                OnPropertyChanged(nameof(NetworkRecommendation));
                OnPropertyChanged(nameof(NetworkDownloadColor));
                OnPropertyChanged(nameof(NetworkUploadColor));
                OnPropertyChanged(nameof(NetworkLatencyColor));
                OnPropertyChanged(nameof(ConnectionQuality));
                OnPropertyChanged(nameof(LastSpeedTestDisplay));
            }
            catch (OperationCanceledException)
            {
                App.LogMessage("[SpeedTest] Test annulé par l'utilisateur");
                AddLiveFeedItem("⏹ Test de vitesse annulé");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SpeedTest] Erreur: {ex.Message}");
                AddLiveFeedItem($"âŒ Erreur Speed Test: {ex.Message}");
            }
            finally
            {
                IsSpeedTestRunning = false;
            }
        }

        /// <summary>
        /// Arrête le SpeedTest en cours.
        /// </summary>
        private void StopSpeedTest()
        {
            if (_speedTestCts != null && !_speedTestCts.IsCancellationRequested)
            {
                App.LogMessage("[SpeedTest] Demande d'arrêt du test...");
                _speedTestCts.Cancel();
            }
            IsSpeedTestRunning = false;
        }

        // Garder RunSpeedTestCommand pour compatibilité (redirige vers toggle)
        private ICommand? _runSpeedTestCommand;
        public ICommand RunSpeedTestCommand => _runSpeedTestCommand ??= ToggleSpeedTestCommand;
        
        private static string GetSpeedRecommendation(LibreSpeedTestService.SpeedTestResult result)
        {
            if (!result.DownloadMbps.HasValue) return "";
            
            return result.DownloadMbps.Value switch
            {
                >= 500 => "Connexion excellente, idéale pour tout usage (streaming 4K, gaming, télétravail).",
                >= 100 => "Très bonne connexion, adaptée à tous usages intensifs.",
                >= 50 => "Bonne connexion, suffisante pour la plupart des usages.",
                >= 25 => "Connexion correcte, peut être limitante pour plusieurs appareils simultanés.",
                >= 10 => "Connexion lente, recommandé de vérifier votre forfait ou équipement.",
                _ => "Connexion très lente, contactez votre fournisseur d'accès."
            };
        }

        // === FIN HEALTH REPORT ===

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
                        ResultsMessage = HealthReport == null
                            ? "Rapport detail charge partiellement. Le resume sante de ce run est indisponible."
                            : string.Empty;
                        ScanResult = value.Result;
                        UpdateScanItemsFromResult(value.Result);
                        UpdateResultSectionsFromResult(value.Result);
                    }
                    else if (value != null)
                    {
                        ScanResult = null;
                        ScanItems.Clear();
                        ResultSections.Clear();
                        OnPropertyChanged(nameof(HasResultSections));
                        ResultsMessage = string.IsNullOrWhiteSpace(value.ErrorSummary)
                            ? "Rapport detail non charge. Utilisez le bouton Rapport integral."
                            : $"Scan avec anomalies: {value.ErrorSummary}";
                    }
                    else if (ScanHistory.Count == 0)
                    {
                        ResultsMessage = GetString("ResultsNoDataMessage");
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

        // === THÈME UI ===
        private string _currentTheme = ThemeDefinitions.DarkFuturisteCode;
        public string CurrentTheme
        {
            get => _currentTheme;
            set
            {
                if (SetProperty(ref _currentTheme, value) && !_isLoadingSettings)
                {
                    IsSettingsDirty = true;
                    OnPropertyChanged(nameof(SelectedTheme));
                    OnPropertyChanged(nameof(SelectedThemeDescription));
                }
            }
        }

        public List<ThemeOption> AvailableThemes { get; } = new()
        {
            new ThemeOption
            {
                Name = ThemeDefinitions.DarkFuturiste.DisplayName,
                Code = ThemeDefinitions.DarkFuturiste.Code,
                Description = ThemeDefinitions.DarkFuturiste.Description
            },
            new ThemeOption
            {
                Name = ThemeDefinitions.PCXRay.DisplayName,
                Code = ThemeDefinitions.PCXRay.Code,
                Description = ThemeDefinitions.PCXRay.Description
            }
        };

        private ThemeOption? _selectedTheme;
        public ThemeOption? SelectedTheme
        {
            get => _selectedTheme ?? AvailableThemes.FirstOrDefault(t => t.Code == CurrentTheme);
            set
            {
                if (SetProperty(ref _selectedTheme, value) && value != null)
                {
                    CurrentTheme = value.Code;
                    App.ApplyTheme(value.Code);
                    OnPropertyChanged(nameof(SelectedThemeDescription));
                }
            }
        }

        public string SelectedThemeDescription => SelectedTheme?.Description ?? string.Empty;

        private string _currentLanguage = "fr";
        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (SetProperty(ref _currentLanguage, value))
                {
                    App.CurrentLanguage = value;
                    UpdateLocalizedStrings();
                    ChatSupportVm.NotifyLocaleChanged();
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
        public string RenameMenuText => GetString("RenameMenuText");
        public string DeleteMenuText => GetString("DeleteMenuText");
        public string SelectedScanDateDisplay => SelectedHistoryScan != null
            ? string.Format(GetString("ResultsScanDateFormat"), SelectedHistoryScan.DateDisplay)
            : string.Empty;
        public string ResultsCompletedTitle => GetString("ResultsCompletedTitle");
        public string ScoreLegendText => GetString("ScoreLegendText");

        // Kernel Power event data for detail window
        private KernelPowerData? _kernelPowerData;
        public KernelPowerData? KernelPowerEvents => _kernelPowerData;

        // Collections
        public ObservableCollection<string> LiveFeedItems { get; } = new ObservableCollection<string>();
        public ObservableCollection<LiveFeedEntry> LiveFeedEntries { get; } = new ObservableCollection<LiveFeedEntry>();
        
        /// <summary>Pluie de 0 et 1 pour le fond du live feed (style matrix), animée en temps réel.</summary>
        public ObservableCollection<string> LiveFeedBackgroundBits { get; } = new ObservableCollection<string>();

        private void InitializeRainBits()
        {
            LiveFeedBackgroundBits.Clear();
            for (int i = 0; i < 240; i++)
                LiveFeedBackgroundBits.Add(_rainBitsRandom.Next(2) == 0 ? "0" : "1");
        }

        private void StartAmbientFeed()
        {
            _ambientRecentDetails.Clear();
            _ambientCursorBySection.Clear();
            _lastNonAmbientFeedAtUtc = DateTime.UtcNow;
            if (!_ambientFeedTimer.IsEnabled)
                _ambientFeedTimer.Start();
        }

        private void StopAmbientFeed()
        {
            if (_ambientFeedTimer.IsEnabled)
                _ambientFeedTimer.Stop();
            _ambientRecentDetails.Clear();
            _ambientCursorBySection.Clear();
        }


        private string ResolveAmbientSection()
        {
            if (!string.IsNullOrWhiteSpace(CurrentSection))
                return CurrentSection;

            var running = SectionPhases.FirstOrDefault(p => string.Equals(p.Status, "Running", StringComparison.OrdinalIgnoreCase));
            if (running != null && !string.IsNullOrWhiteSpace(running.Label))
                return running.Label;

            return CurrentSectionDisplay;
        }

        private string PickAmbientDetail(string ambientSection)
        {
            var candidates = GetAmbientFactsForCurrentPhase();
            if (candidates.Count == 0)
                return string.Empty;

            var eligible = candidates.Where(c => !_ambientRecentDetails.Contains(c)).ToList();
            if (eligible.Count == 0)
                return string.Empty;

            if (!_ambientCursorBySection.TryGetValue(ambientSection, out var cursor))
                cursor = 0;

            var selected = eligible[cursor % eligible.Count];
            _ambientCursorBySection[ambientSection] = cursor + 1;
            TrackAmbientDetail(selected);
            return selected;
        }

        private void TrackAmbientDetail(string detail)
        {
            _ambientRecentDetails.Enqueue(detail);
            while (_ambientRecentDetails.Count > 10)
                _ambientRecentDetails.Dequeue();
        }

        private IReadOnlyList<string> GetAmbientFactsForCurrentPhase()
        {
            var facts = new List<string>(10);

            if (!string.IsNullOrWhiteSpace(CurrentSection))
                facts.Add($"Section active: {CurrentSection}");

            if (IsPhaseRunning(0))
                facts.Add("Inventaire système: collecte PowerShell en cours");
            if (IsPhaseRunning(1))
                facts.Add($"Capteurs matériels: collecte en cours ({(_hardwareSensorsCollector.ForceUnsafeMode ? "mode LHM" : "mode SAFE")})");
            if (IsPhaseRunning(2))
                facts.Add("Compteurs performances: échantillonnage CPU/RAM/IO en cours");
            if (IsPhaseRunning(3))
                facts.Add("Signaux diagnostic: corrélation des collecteurs en cours");
            if (IsPhaseRunning(4))
                facts.Add("Télémétrie processus: inventaire des processus actifs en cours");
            if (IsPhaseRunning(5))
                facts.Add("Connectivité réseau: mesures latence/perte en cours");
            if (IsPhaseRunning(6))
                facts.Add("Génération rapport: assemblage des sections en cours");

            return facts;
        }

        private bool IsPhaseRunning(int index)
        {
            return index >= 0
                   && index < SectionPhases.Count
                   && string.Equals(SectionPhases[index].Status, "Running", StringComparison.OrdinalIgnoreCase);
        }

        private ICollectionView? _filteredLiveFeedView;
        public ICollectionView FilteredLiveFeedItems => _filteredLiveFeedView ??= CreateFilteredLiveFeedView();
        
        private ICollectionView CreateFilteredLiveFeedView()
        {
            var view = CollectionViewSource.GetDefaultView(LiveFeedEntries);
            view.Filter = o => o is LiveFeedEntry e && MatchesLiveFeedFilter(e);
            return view;
        }
        
        private bool MatchesLiveFeedFilter(LiveFeedEntry e)
        {
            var f = LiveFeedFilterSelected ?? "Tout";
            return f switch
            {
                "Tout" => true,
                "Erreurs" => e.IsError,
                "Avertissements" => e.IsWarning,
                "Important" => (e.IsError || e.IsWarning) && !e.IsAmbient,
                "Progression" => e.IsProgress || e.IsStatus || e.IsDone,
                _ => true
            };
        }
        
        public IEnumerable<string> LiveFeedFilterOptions { get; } = new[] { "Tout", "Erreurs", "Avertissements", "Important" };
        private string _liveFeedFilterSelected = "Tout";
        public string LiveFeedFilterSelected
        {
            get => _liveFeedFilterSelected;
            set { _liveFeedFilterSelected = value; OnPropertyChanged(); _filteredLiveFeedView?.Refresh(); }
        }
        public bool LiveFeedFilterVisible => true;
        private bool _liveFeedPaused;
        public bool LiveFeedPaused
        {
            get => _liveFeedPaused;
            set { _liveFeedPaused = value; OnPropertyChanged(); }
        }
        public string LiveFeedPauseLabel => GetString("LiveFeedPauseLabel");
        
        public string CurrentSectionDisplay => !string.IsNullOrWhiteSpace(CurrentSection) 
            ? CurrentSection 
            : (IsScanning ? (ProgressPercent >= 90 ? GetString("ScanStatus_Finalization") : GetString("ScanStatus_Preparation")) : "-");
        
        public ObservableCollection<SectionPhaseItem> SectionPhases { get; } = new ObservableCollection<SectionPhaseItem>();
        
        public ObservableCollection<ScanItem> ScanItems { get; } = new ObservableCollection<ScanItem>();
        public ObservableCollection<ScanHistoryItem> ScanHistory { get; } = new ObservableCollection<ScanHistoryItem>();
        public ObservableCollection<ScanHistoryItem> ArchivedScanHistory { get; } = new ObservableCollection<ScanHistoryItem>();
        public ICollectionView ArchivedScanHistoryView { get; }

        /// <summary>Child ViewModel for the Chat &amp; Support AI panel.</summary>
        public ChatSupportViewModel ChatSupportVm { get; } = new ChatSupportViewModel();

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
        public ICommand ApplyDefenderExclusionCommand { get; }
        public ICommand SelectHistoryScanCommand { get; }
        public ICommand BackToHistoryCommand { get; }
        public ICommand NavigateToArchivesCommand { get; }
        public ICommand ArchiveScanCommand { get; }
        public ICommand RenameScanCommand { get; }
        public ICommand DeleteScanCommand { get; }
        
        // Commands for detail windows (Drivers and Applications)
        public ICommand OpenDriversDetailsCommand { get; }
        public ICommand OpenAppsDetailsCommand { get; }
        /// <summary>Ouvre une fenêtre de liste (Périph. audio, Imprimantes, Obsolètes) selon le paramètre (Key).</summary>
        public ICommand OpenListDetailCommand { get; }
        
        // Command for collector errors details
        public ICommand ShowCollectorErrorsCommand { get; }
        // History utility commands
        public ICommand OpenScansFolderCommand { get; }
        public ICommand OpenHistoryReportCommand { get; }
        public ICommand OpenHistoryFolderCommand { get; }
        public ICommand ShowHistoryErrorDetailsCommand { get; }
        public ICommand OpenHistoryLogsCommand { get; }
        public ICommand GoToScannerCommand { get; }

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
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _scanProgressTimer.Tick += (s, e) => TickScanProgress();

            _rainBitsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(220)
            };
            _rainBitsTimer.Tick += (s, e) => TickRainBits();
            InitializeRainBits();

            _ambientFeedTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(650)
            };
            _ambientFeedTimer.Tick += (s, e) => EmitAmbientFeedLine();

            // Initialiser les chemins relatifs
            _scriptPath = ResolveScriptPath()
                          ?? Path.Combine(_baseDir, "Scripts", "Total_PS_PC_Scan_v7.0.ps1");
            _reportsDir = Path.Combine(_appDataDir, "Reports");
            _legacyReportsDir = Path.Combine(_legacyAppDataDir, "Rapports");
            _legacyReportsDirAlt = Path.Combine(_legacyAppDataDir, "Reports");
            _resultJsonPath = Path.Combine(_reportsDir, "scan_result.json");
            _configPath = Path.Combine(_appDataDir, "config.json");
            _legacyConfigPath = Path.Combine(_legacyAppDataDir, "config.json");
            _reportDisplayNamesPath = Path.Combine(_appDataDir, "report_display_names.json");
            _legacyReportDisplayNamesPath = Path.Combine(_legacyAppDataDir, "report_display_names.json");

            // Créer le dossier Rapports s'il n'existe pas
            EnsureReportsDirectories();

            IsAdmin = AdminService.IsRunningAsAdmin();

            // Charger les paramètres
            LoadSettings();
            LoadReportDisplayNames();
            _currentTheme = NormalizeThemeCode(App.GetCurrentTheme());
            _selectedTheme = AvailableThemes.FirstOrDefault(t => t.Code == _currentTheme);
            
            // Après élévation UAC (one-click) : activer les tests réseau et persister
            var elevationFlagPath = GetExistingPath(
                Path.Combine(_appDataDir, "enable_network_after_elevation.flag"),
                Path.Combine(_legacyAppDataDir, "enable_network_after_elevation.flag"));
            if (IsAdmin && !string.IsNullOrWhiteSpace(elevationFlagPath) && File.Exists(elevationFlagPath))
            {
                try
                {
                    AllowExternalNetworkTests = true;
                    SaveSettings();
                    OnPropertyChanged(nameof(AllowExternalNetworkTests));
                    File.Delete(elevationFlagPath);
                    App.LogMessage("[Admin] Tests réseau externes activés après élévation (one-click).");
                }
                catch (Exception ex) { App.LogMessage($"[Admin] Flag elevation: {ex.Message}"); }
            }
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
            // No HasScanResult guard: OpenReportTxt already handles missing data gracefully
            // (FindLatestCombinedJsonPath fallback + MessageBox when nothing is available).
            // With the guard the button was grayed-out/non-functional whenever no in-session
            // scan had run, even though a combined JSON file was present on disk.
            OpenReportTxtCommand = new RelayCommand(OpenReportTxt);
            RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
            ExportResultsCommand = new RelayCommand(ExportResults, () => HasScanResult);
            NavigateToScannerCommand = new RelayCommand(() => { CurrentView = "Home"; SelectedHistoryScan = null; IsViewingArchives = false; });
            NavigateToResultsCommand = new RelayCommand(() => NavigateToResults());
            NavigateToSettingsCommand = new RelayCommand(() => { CurrentView = "Settings"; SelectedHistoryScan = null; IsViewingArchives = false; });
            NavigateToHealthcheckCommand = new RelayCommand(() => { CurrentView = "Healthcheck"; SelectedHistoryScan = null; IsViewingArchives = false; });
            NavigateToChatCommand = new RelayCommand(() => { CurrentView = "Chat"; SelectedHistoryScan = null; IsViewingArchives = false; });
            BrowseReportDirectoryCommand = new RelayCommand(BrowseReportDirectory);
            SaveSettingsCommand = new RelayCommand(SaveSettings, () => IsSettingsDirty);
            ApplyDefenderExclusionCommand = new RelayCommand(ApplyDefenderExclusion);
            SelectHistoryScanCommand = new AsyncRelayCommand(
                async parameter => await SelectHistoryScanAsync(parameter as ScanHistoryItem),
                parameter => parameter is ScanHistoryItem);
            BackToHistoryCommand = new RelayCommand(BackToHistory);
            NavigateToArchivesCommand = new RelayCommand(NavigateToArchives, () => ScanHistory.Count > 0 || ArchivedScanHistory.Count > 0);
            ArchiveScanCommand = new RelayCommand<ScanHistoryItem>(ArchiveScan, item => item != null);
            RenameScanCommand = new RelayCommand<ScanHistoryItem>(RenameScan, item => item != null);
            DeleteScanCommand = new RelayCommand<ScanHistoryItem>(DeleteScan, item => item != null);
            
            // Commands for detail windows
            OpenDriversDetailsCommand = new RelayCommand(OpenDriversDetails, () => _lastDriverInventory?.Available == true);
            OpenAppsDetailsCommand = new RelayCommand(OpenAppsDetails, () => !string.IsNullOrEmpty(_lastCombinedJsonContent));
            OpenListDetailCommand = new RelayCommand(OpenListDetail, _ => true);
            
            // Command for collector errors details
            ShowCollectorErrorsCommand = new RelayCommand(ShowCollectorErrorsDetails, () => HasHealthReport);

            // History utility commands
            OpenScansFolderCommand = new RelayCommand(() =>
            {
                try
                {
                    var dir = Services.ScanStorageService.BaseDir;
                    Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
                catch (Exception ex) { App.LogMessage($"[History] OpenScansFolder: {ex.Message}"); }
            });
            OpenHistoryReportCommand = new RelayCommand<ScanHistoryItem>(OpenHistoryReport, item => item != null);
            OpenHistoryFolderCommand = new RelayCommand<ScanHistoryItem>(OpenHistoryFolder, item => item != null);
            ShowHistoryErrorDetailsCommand = new RelayCommand<ScanHistoryItem>(ShowHistoryErrorDetails, item => item != null);
            OpenHistoryLogsCommand = new RelayCommand(OpenHistoryLogs);
            GoToScannerCommand = new RelayCommand(() =>
            {
                CurrentView = "Home";
                SelectedHistoryScan = null;
                IsViewingArchives = false;
            });

            ScanHistory.CollectionChanged += OnHistoryCollectionChanged;
            ArchivedScanHistory.CollectionChanged += OnHistoryCollectionChanged;
            ResultSections.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasResultSections));

            // S'abonner aux événements
            _powerShellService.OutputReceived += OnOutputReceived;
            _powerShellService.ProgressChanged += OnProgressChanged;
            _powerShellService.StepChanged += OnStepChanged;
            AttachScanProgressEngine();

            if (!IsAdmin)
            {
                StatusMessage = GetString("AdminRequiredWarning");
            }

            // Afficher les 7 étapes dès l'ouverture (Pending) même sans scan lancé
            InitializeSectionPhases();

            App.LogMessage("MainViewModel initialisé");

            // Load persisted scan history from disk (fire-and-forget, non-blocking)
            _ = LoadHistoryFromDiskAsync();
        }

        #endregion


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

        /// <summary>
        /// Loads the persisted scan history from disk into ScanHistory.
        /// Reads only lightweight scan_meta.json files (fast, &lt; 200 ms typical).
        /// Called once at startup via fire-and-forget. Skips duplicates already in memory.
        /// </summary>
        private async Task LoadHistoryFromDiskAsync()
        {
            IsHistoryLoading = true;
            HasHistoryLoadError = false;
            HistoryLoadErrorMessage = string.Empty;
            try
            {
                App.LogMessage($"[History] Load start baseDir={Services.ScanStorageService.BaseDir}");
                var metas = await Task.Run(() => Services.ScanStorageService.EnumerateScans());
                App.LogMessage($"[History] Enumerated metas={metas.Count}");

                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    int added = 0;
                    foreach (var meta in metas.Take(100))
                    {
                        if (ScanHistory.Any(h => h.RunId == meta.RunId)) continue;
                        if (ArchivedScanHistory.Any(h => h.RunId == meta.RunId)) continue;

                        var item = new ScanHistoryItem
                        {
                            RunId            = meta.RunId,
                            ScanDate         = meta.StartTime.ToLocalTime(),
                            Score            = meta.Score,
                            Grade            = meta.Grade,
                            Status           = meta.Status,
                            DurationSeconds  = meta.DurationSeconds,
                            CombinedJsonPath = meta.CombinedJsonPath,
                            ErrorSummary     = string.IsNullOrWhiteSpace(meta.ErrorSummary) ? meta.StatusReason : meta.ErrorSummary,
                            Result           = null
                        };

                        if (_reportDisplayNames.TryGetValue(ReportDisplayNameKey(item), out var savedName)
                            && !string.IsNullOrWhiteSpace(savedName))
                        {
                            item.CustomDisplayName = savedName;
                        }

                        if (ScanHistory.Count < 10)
                            ScanHistory.Add(item);
                        else
                            ArchivedScanHistory.Insert(0, item);

                        added++;
                    }

                    if (added > 0 || HasAnyScan)
                    {
                        OnPropertyChanged(nameof(HasAnyScan));
                        OnPropertyChanged(nameof(HasNoScanHistory));
                        OnPropertyChanged(nameof(IsHistoryListStateVisible));
                        OnPropertyChanged(nameof(IsHistoryEmptyStateVisible));
                        CommandManager.InvalidateRequerySuggested();
                        App.LogMessage($"[History] Load done added={added} totalMain={ScanHistory.Count} totalArchive={ArchivedScanHistory.Count}");
                    }
                    else
                    {
                        App.LogMessage("[History] No persisted scans found.");
                    }
                });
            }
            catch (Exception ex)
            {
                HasHistoryLoadError = true;
                HistoryLoadErrorMessage = ex.Message;
                App.LogMessage($"[History] Load error baseDir={Services.ScanStorageService.BaseDir} error={ex.Message}");
            }
            finally
            {
                IsHistoryLoading = false;
                OnPropertyChanged(nameof(HasNoScanHistory));
                OnPropertyChanged(nameof(IsHistoryListStateVisible));
                OnPropertyChanged(nameof(IsHistoryEmptyStateVisible));
                OnPropertyChanged(nameof(IsHistoryErrorStateVisible));
            }
        }

        private void EnsureReportsDirectories()
        {
            try
            {
                Directory.CreateDirectory(_appDataDir);
                Directory.CreateDirectory(_reportsDir);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[Storage] Unable to initialize report directories: {ex.Message}");
            }
        }

        private string NormalizeReportDirectory(string? candidatePath)
        {
            var normalized = candidatePath?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return _reportsDir;
            }

            try
            {
                normalized = Path.GetFullPath(normalized);
            }
            catch
            {
                return _reportsDir;
            }

            if (!Directory.Exists(normalized))
            {
                try
                {
                    Directory.CreateDirectory(normalized);
                }
                catch
                {
                    return _reportsDir;
                }
            }

            return normalized;
        }

        private static string? GetExistingPath(params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        }

        private IEnumerable<string> GetReportSearchDirectories()
        {
            var directories = new[]
            {
                ReportDirectory,
                _reportsDir,
                _legacyReportsDir,
                _legacyReportsDirAlt,
                Path.GetDirectoryName(_resultJsonPath)
            };

            return directories
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists);
        }

        private string? FindLatestCombinedJsonPath()
        {
            var candidates = new List<string>();
            foreach (var directory in GetReportSearchDirectories())
            {
                try
                {
                    candidates.AddRange(Directory.GetFiles(directory, "scan_result_combined.json", SearchOption.TopDirectoryOnly));
                    candidates.AddRange(Directory.GetFiles(directory, "scan_result*.json", SearchOption.TopDirectoryOnly));
                }
                catch
                {
                    // Ignore invalid folder access.
                }
            }

            try
            {
                if (Directory.Exists(Services.ScanStorageService.BaseDir))
                {
                    foreach (var runFolder in Directory.GetDirectories(Services.ScanStorageService.BaseDir))
                    {
                        var canonicalCombined = Path.Combine(runFolder, Services.ScanStorageService.CombinedFileName);
                        if (File.Exists(canonicalCombined))
                            candidates.Add(canonicalCombined);
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[History] FindLatestCombinedJsonPath canonical scan failed: {ex.Message}");
            }

            return candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(path =>
                {
                    try { return File.GetLastWriteTimeUtc(path); }
                    catch { return DateTime.MinValue; }
                })
                .FirstOrDefault();
        }

        private static string GetPrimaryExecutableName()
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var fileName = Path.GetFileName(executable);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return fileName;
                }
            }

            return "PCXRay.exe";
        }

        private static IReadOnlyList<string> GetDefenderProcessCandidates()
        {
            return new[]
            {
                GetPrimaryExecutableName(),
                "PCDiagnosticPro.exe",
                "PCXRay.exe"
            }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            // Timeout augment? ? 15+ secondes (30 tentatives ? 500ms)
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

        private void NavigateToResults(ScanHistoryItem? preferredItem = null)
        {
            IsViewingArchives = false;
            CurrentView = "Results";

            if (preferredItem != null)
            {
                _ = SelectHistoryScanAsync(preferredItem);
            }
            else if (ScanHistory.Count > 0)
            {
                _ = SelectHistoryScanAsync(ScanHistory[0]);
            }
            else
            {
                SelectedHistoryScan = null;
                ResultsMessage = GetString("ResultsNoDataMessage");
            }

            OnPropertyChanged(nameof(IsViewingHistoryDetail));
            OnPropertyChanged(nameof(IsViewingHistoryList));
            App.LogMessage("Switch tab to Stats/Results.");
        }


        private ScanTimingEnvelope BuildTimingEnvelope(JsonElement psRoot)
        {
            var envelope = new ScanTimingEnvelope
            {
                RunId = !string.IsNullOrWhiteSpace(_activeRunId) ? _activeRunId : string.Empty
            };

            var csCollectors = new List<CollectorTimingEntry>();
            var trackerSnapshot = _scanTimingTracker?.GetSnapshot() ?? Array.Empty<ScanTimingEntry>();
            foreach (var phase in trackerSnapshot)
            {
                var duration = Math.Max(0, phase.DurationMs);
                var status = phase.IsActive ? "in_progress" : (phase.Success ? "ok" : "failed");
                var entry = new CollectorTimingEntry
                {
                    Name = phase.PhaseName,
                    Source = phase.Source,
                    DurationMs = duration,
                    Status = status
                };

                if (string.Equals(phase.Source, "PS", StringComparison.OrdinalIgnoreCase))
                    envelope.PsCollectors.Add(entry);
                else
                    csCollectors.Add(entry);

                envelope.PhaseTotals[phase.PhaseName] = duration;
            }

            envelope.CsCollectors = csCollectors;

            foreach (var psCollector in ExtractPsCollectorTimings(psRoot))
            {
                envelope.PsCollectors.Add(psCollector);
                var key = $"PS.{psCollector.Name}";
                if (!envelope.PhaseTotals.ContainsKey(key))
                    envelope.PhaseTotals[key] = psCollector.DurationMs;
            }

            envelope.SlowCollectors = envelope.PsCollectors
                .Concat(envelope.CsCollectors)
                .Where(e => e.DurationMs > 0)
                .OrderByDescending(e => e.DurationMs)
                .Take(8)
                .ToList();

            return envelope;
        }

        private static IEnumerable<CollectorTimingEntry> ExtractPsCollectorTimings(JsonElement psRoot)
        {
            if (!psRoot.TryGetProperty("timings", out var timingsEl))
                yield break;

            if (!timingsEl.TryGetProperty("collectors", out var collectorsEl))
                yield break;

            if (collectorsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var collector in collectorsEl.EnumerateArray())
                {
                    var parsed = ParseCollectorTiming(collector);
                    if (parsed != null)
                        yield return parsed;
                }
                yield break;
            }

            if (collectorsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in collectorsEl.EnumerateObject())
                {
                    var parsed = ParseCollectorTiming(property.Value, property.Name);
                    if (parsed != null)
                        yield return parsed;
                }
            }
        }

        private static CollectorTimingEntry? ParseCollectorTiming(JsonElement timingEl, string? fallbackName = null)
        {
            if (timingEl.ValueKind != JsonValueKind.Object)
                return null;

            var name = timingEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : fallbackName;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            long durationMs = 0;
            if (timingEl.TryGetProperty("durationMs", out var durationEl))
            {
                if (durationEl.ValueKind == JsonValueKind.Number)
                    durationMs = (long)Math.Round(durationEl.GetDouble());
                else if (durationEl.ValueKind == JsonValueKind.String && long.TryParse(durationEl.GetString(), out var parsedDuration))
                    durationMs = parsedDuration;
            }

            var status = timingEl.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : "ok";
            var source = timingEl.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() : "PS";

            return new CollectorTimingEntry
            {
                Name = name,
                Source = string.IsNullOrWhiteSpace(source) ? "PS" : source!,
                DurationMs = Math.Max(0, durationMs),
                Status = string.IsNullOrWhiteSpace(status) ? "ok" : status!
            };
        }
        
        // Chemin du JSON combiné pour TXT unifié
        private string _combinedJsonPath = string.Empty;
        
        /// <summary>
        /// Extrait les nœuds explicites du JSON PS vers le CombinedScanResult
        /// pour garantir que missingData, metadata, findings, errors, sections, paths
        /// sont TOUJOURS présents dans scan_result_combined.json
        /// ROBUST: Handles both Array and Object ValueKind for all nodes
        /// </summary>

        /// <summary>
        /// Génère le rapport TXT UNIFIÉ = PowerShell + Hardware Sensors + Score + Metadata.
        /// Appelé après que le HealthReport soit construit.
        /// </summary>

        private ScanHistoryItem AddToHistory(ScanResult result)
        {
            var combinedPath = !string.IsNullOrWhiteSpace(_activeRunId)
                ? Services.ScanStorageService.GetCombinedJsonPath(_activeRunId)
                : _combinedJsonPath;

            var historyItem = new ScanHistoryItem
            {
                RunId           = _activeRunId,
                ScanDate        = result.Summary.ScanDate,
                Score           = result.Summary.Score,
                Grade           = result.Summary.Grade,
                Status          = result.IsValid
                                    ? Models.ScanStatus.Success
                                    : Models.ScanStatus.Partial,
                DurationSeconds = _scanStopwatch.Elapsed.TotalSeconds,
                CombinedJsonPath = combinedPath,
                ErrorSummary    = result.IsValid ? null : "Scan partiel.",
                Result          = result
            };
            if (_reportDisplayNames.TryGetValue(ReportDisplayNameKey(historyItem), out var savedName) && !string.IsNullOrWhiteSpace(savedName))
                historyItem.CustomDisplayName = savedName;

            ScanHistory.Insert(0, historyItem);

            // Keep only the 10 most recent in the main list. Older entries stay available in archives.
            while (ScanHistory.Count > 10)
            {
                var overflow = ScanHistory[ScanHistory.Count - 1];
                ScanHistory.RemoveAt(ScanHistory.Count - 1);
                if (!ArchivedScanHistory.Any(h => h.RunId == overflow.RunId))
                    ArchivedScanHistory.Insert(0, overflow);
            }

            OnPropertyChanged(nameof(HasAnyScan));
            OnPropertyChanged(nameof(HasNoScanHistory));
            OnPropertyChanged(nameof(IsHistoryListStateVisible));
            OnPropertyChanged(nameof(IsHistoryEmptyStateVisible));
            return historyItem;
        }

        private static string ReportDisplayNameKey(ScanHistoryItem item) => $"{item.ScanDate.Ticks}_{item.Score}";

        private void LoadReportDisplayNames()
        {
            try
            {
                var displayNamesPath = GetExistingPath(_reportDisplayNamesPath, _legacyReportDisplayNamesPath);
                if (string.IsNullOrWhiteSpace(displayNamesPath) || !File.Exists(displayNamesPath))
                    return;

                var json = File.ReadAllText(displayNamesPath, Encoding.UTF8);
                var doc = JsonDocument.Parse(json);
                _reportDisplayNames.Clear();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    _reportDisplayNames[prop.Name] = prop.Value.GetString() ?? "";
            }
            catch (Exception ex) { App.LogMessage($"[ReportDisplayNames] Load: {ex.Message}"); }
        }

        /// <summary>Persiste les noms personnalisés des rapports (après renommage).</summary>
        public void PersistReportDisplayNames()
        {
            try
            {
                var dict = new Dictionary<string, string>();
                foreach (var item in ScanHistory.Concat(ArchivedScanHistory))
                {
                    if (!string.IsNullOrWhiteSpace(item.CustomDisplayName))
                        dict[ReportDisplayNameKey(item)] = item.CustomDisplayName;
                }
                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_reportDisplayNamesPath, json, new UTF8Encoding(false));
            }
            catch (Exception ex) { App.LogMessage($"[ReportDisplayNames] Save: {ex.Message}"); }
        }


        private void OpenReport()
        {
            if (HasAnyScan)
            {
                CurrentView = "Results";
                if (ScanHistory.Count > 0)
                {
                    IsViewingArchives = false;
                    _ = SelectHistoryScanAsync(ScanHistory[0]);
                }
            }
        }

        /// <summary>
        /// Ouvre l'écran "Rapport intégral" (HUD vitre) avec les données du rapport combiné.
        /// Si aucun JSON disponible, affiche un message. Option de fallback: ouvrir le TXT dans Bloc-notes.
        /// Le travail lourd (lecture fichier + BuildFromJson) est exécuté en arrière-plan pour éviter de geler l'UI.
        /// </summary>
        private void OpenReportTxt()
        {
            OpenReportTxtForItem(SelectedHistoryScan);
        }

        private void OpenReportTxtForItem(ScanHistoryItem? preferredItem)
        {
            string? jsonContent = null;
            string? combinedJsonPath = null;

            if (preferredItem != null)
            {
                combinedJsonPath = preferredItem.CombinedJsonPath;
                if (string.IsNullOrWhiteSpace(combinedJsonPath) && !string.IsNullOrWhiteSpace(preferredItem.RunId))
                    combinedJsonPath = Services.ScanStorageService.GetCombinedJsonPath(preferredItem.RunId);

                if (!string.IsNullOrWhiteSpace(combinedJsonPath) && !File.Exists(combinedJsonPath))
                    combinedJsonPath = null;
            }

            if (string.IsNullOrWhiteSpace(combinedJsonPath))
                combinedJsonPath = _combinedJsonPath;

            if (!string.IsNullOrWhiteSpace(combinedJsonPath) &&
                string.Equals(combinedJsonPath, _combinedJsonPath, StringComparison.OrdinalIgnoreCase))
            {
                jsonContent = _lastCombinedJsonContent;
            }

            if (string.IsNullOrWhiteSpace(jsonContent) && !string.IsNullOrWhiteSpace(combinedJsonPath) && File.Exists(combinedJsonPath))
            {
                jsonContent = File.ReadAllText(combinedJsonPath, Encoding.UTF8);
            }

            if (string.IsNullOrWhiteSpace(jsonContent) && string.IsNullOrWhiteSpace(combinedJsonPath) && preferredItem == null)
            {
                combinedJsonPath = FindLatestCombinedJsonPath();
                if (!string.IsNullOrWhiteSpace(combinedJsonPath))
                {
                    _combinedJsonPath = combinedJsonPath;
                    jsonContent = File.Exists(combinedJsonPath)
                        ? File.ReadAllText(combinedJsonPath, Encoding.UTF8)
                        : null;
                }
            }

            if (string.IsNullOrWhiteSpace(jsonContent) && string.IsNullOrWhiteSpace(combinedJsonPath))
            {
                if (preferredItem != null)
                {
                    var msg = string.IsNullOrWhiteSpace(preferredItem.ErrorSummary)
                        ? "Aucune donnee de rapport combine n'est disponible pour ce scan."
                        : preferredItem.ErrorSummary;
                    App.LogMessage($"[Report][RunId:{preferredItem.RunId}] Open failed: {msg}");
                    System.Windows.MessageBox.Show(
                        msg,
                        "Rapport integral",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }

                ShowNoReportContentOnUi();
                return;
            }

            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            var mainWindow = Application.Current?.MainWindow;

            Task.Run(() =>
            {
                try
                {
                    string? content = jsonContent;
                    if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(combinedJsonPath) && File.Exists(combinedJsonPath))
                    {
                        content = File.ReadAllText(combinedJsonPath, Encoding.UTF8);
                    }

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        dispatcher.Invoke(() => ShowNoReportContentOnUi());
                        return;
                    }

                    TechnicalContractValidator.GateValidationResult uiGate;
                    JsonDocument? combinedDoc = null;
                    try
                    {
                        combinedDoc = JsonDocument.Parse(content);
                        uiGate = TechnicalContractValidator.ValidateCombinedJsonRoot(
                            combinedDoc.RootElement,
                            null,
                            _contractGateOptions);
                    }
                    catch (Exception gateEx)
                    {
                        App.LogMessage($"[ContractGate] UI pre-open parse gate error: {gateEx.Message}");
                        uiGate = new TechnicalContractValidator.GateValidationResult();
                        uiGate.Add(
                            TechnicalContractValidator.ReasonCombinedSchemaInvalid,
                            "ui_open_parse",
                            $"Combined parse failed: {gateEx.Message}");
                    }

                    var viewModel = Services.FullReportBuilder.BuildFromJson(content);
                    if (combinedDoc != null)
                    {
                        try
                        {
                            var uiValidation = UiCompletenessValidator.Validate(combinedDoc.RootElement, HealthReport, _lastSensorsResult);
                            var coverageGate = TechnicalContractValidator.ValidateCombinedJsonRoot(
                                combinedDoc.RootElement,
                                uiValidation,
                                _contractGateOptions);
                            uiGate.Merge(coverageGate);
                        }
                        catch (Exception gateEx)
                        {
                            App.LogMessage($"[ContractGate] UI coverage gate error: {gateEx.Message}");
                        }
                    }

                    var runStatus = ApplyGateResult(uiGate, "ui_open", HealthReport);
                    PersistRunStatusAsync(runStatus).GetAwaiter().GetResult();
                    if (viewModel != null)
                    {
                        viewModel.ContractGateBannerText = ContractGateBannerText;
                        viewModel.ContractGateBannerDetails = ContractGateBannerDetails;
                    }
                    combinedDoc?.Dispose();

                    dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            if (viewModel == null)
                            {
                                App.LogMessage("[Rapport] Impossible de charger le rapport combine (parse null).");
                                System.Windows.MessageBox.Show(
                                    "Impossible de charger le rapport combine.",
                                    "Rapport integral",
                                    System.Windows.MessageBoxButton.OK,
                                    System.Windows.MessageBoxImage.Warning);
                                return;
                            }
                            var window = new Views.FullReportWindow(viewModel)
                            {
                                Owner = mainWindow as Window
                            };
                            window.Show();
                            App.LogMessage($"[Rapport] Fenetre Rapport integral ouverte. {viewModel.UiCoverageDisplay}");
                        }
                        catch (Exception ex)
                        {
                            App.LogMessage($"[Rapport] Erreur ouverture: {ex.Message}");
                            System.Windows.MessageBox.Show(
                                "Erreur lors de l'ouverture du rapport integral : " + ex.Message,
                                "Rapport integral",
                                System.Windows.MessageBoxButton.OK,
                                System.Windows.MessageBoxImage.Error);
                        }
                    });
                }
                catch (Exception ex)
                {
                    dispatcher.InvokeAsync(() =>
                    {
                        App.LogMessage($"[Rapport] Erreur ouverture: {ex.Message}");
                        System.Windows.MessageBox.Show(
                            "Erreur lors de l'ouverture du rapport integral : " + ex.Message,
                            "Rapport integral",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                    });
                }
            });
        }
        private void ShowNoReportContentOnUi()
        {
            var reportTxtPath = FindReportTxtPath();
            if (!string.IsNullOrEmpty(reportTxtPath) && File.Exists(reportTxtPath))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{reportTxtPath}\"",
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                App.LogMessage($"[Rapport] Ouverture TXT fallback: {reportTxtPath}");
            }
            else
            {
                App.LogMessage("[Rapport] Aucune donnée de rapport disponible.");
                System.Windows.MessageBox.Show(
                    "Aucune donnée de rapport disponible.\n\nLancez un scan complet puis rouvrez le rapport intégral.",
                    "Rapport intégral",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Ouvre la fenêtre de détails des pilotes
        /// </summary>
        private void OpenDriversDetails()
        {
            try
            {
                if (_lastDriverInventory == null || !_lastDriverInventory.Available)
                {
                    System.Windows.MessageBox.Show(
                        "Aucune donnée de pilotes disponible.\n\n" +
                        "Lancez d'abord un scan pour collecter les informations.",
                        "Données non disponibles",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }

                var window = new DriversDetailsWindow(_lastDriverInventory)
                {
                    Owner = Application.Current.MainWindow
                };
                window.ShowDialog();
                
                App.LogMessage($"[DriversDetails] Fenêtre ouverte: {_lastDriverInventory.TotalCount} pilotes");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DriversDetails] Erreur: {ex.Message}");
            }
        }

        /// <summary>
        /// Ouvre la fenêtre de détails des applications
        /// </summary>
        private void OpenAppsDetails()
        {
            try
            {
                if (string.IsNullOrEmpty(_lastCombinedJsonContent))
                {
                    System.Windows.MessageBox.Show(
                        "Aucune donnée d'applications disponible.\n\n" +
                        "Lancez d'abord un scan pour collecter les informations.",
                        "Données non disponibles",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }

                if (!TryGetCombinedJsonRoot(out var root))
                {
                    App.LogMessage("[AppsDetails] Combined JSON cache unavailable");
                    return;
                }

                JsonElement? appsData = null;
                JsonElement? startupData = null;

                try
                {
                    // Try scan_powershell.sections first
                    if (root.TryGetProperty("scan_powershell", out var ps) &&
                        ps.TryGetProperty("sections", out var sections))
                    {
                        if (sections.TryGetProperty("InstalledApplications", out var apps))
                        {
                            appsData = apps.TryGetProperty("data", out var appsDataEl) ? appsDataEl : apps;
                        }
                        if (sections.TryGetProperty("StartupPrograms", out var startup))
                        {
                            startupData = startup.TryGetProperty("data", out var startupDataEl) ? startupDataEl : startup;
                        }
                    }

                    // Fallback to direct sections
                    if (!appsData.HasValue && root.TryGetProperty("sections", out var directSections))
                    {
                        if (directSections.TryGetProperty("InstalledApplications", out var apps))
                        {
                            appsData = apps.TryGetProperty("data", out var appsDataEl) ? appsDataEl : apps;
                        }
                        if (directSections.TryGetProperty("StartupPrograms", out var startup))
                        {
                            startupData = startup.TryGetProperty("data", out var startupDataEl) ? startupDataEl : startup;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    App.LogMessage($"[AppsDetails] JSON parse error: {ex.Message}");
                }

                var window = new AppsDetailsWindow(appsData, startupData)
                {
                    Owner = Application.Current.MainWindow
                };
                window.ShowDialog();
                
                App.LogMessage("[AppsDetails] Fenêtre ouverte");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AppsDetails] Erreur: {ex.Message}");
            }
        }

        /// <summary>
        /// Ouvre une fenêtre listant les éléments selon la clé (Périph. audio, Imprimantes, Obsolètes / Pilotes obsolètes).
        /// </summary>
        private void OpenListDetail(object? parameter)
        {
            var key = parameter as string;
            if (string.IsNullOrEmpty(key)) return;

            try
            {
                if (key.Equals("Périph. audio", StringComparison.OrdinalIgnoreCase))
                {
                    var items = GetAudioDevicesFromJson();
                    var window = new ListDetailWindow(
                        "Périphériques audio",
                        "Liste des périphériques audio détectés (source : rapport de scan).",
                        items)
                    {
                        Owner = Application.Current.MainWindow
                    };
                    window.ShowDialog();
                    return;
                }

                if (key.Equals("Imprimantes", StringComparison.OrdinalIgnoreCase))
                {
                    var items = GetPrintersFromJson();
                    var window = new ListDetailWindow(
                        "Imprimantes",
                        "Liste des imprimantes installées (source : rapport de scan).",
                        items)
                    {
                        Owner = Application.Current.MainWindow
                    };
                    window.ShowDialog();
                    return;
                }

                if (key.Equals("Obsolètes", StringComparison.OrdinalIgnoreCase) || key.Equals("Pilotes obsolètes", StringComparison.OrdinalIgnoreCase))
                {
                    var items = GetOutdatedDriversList();
                    var window = new ListDetailWindow(
                        "Pilotes obsolètes",
                        "Pilotes considérés comme obsolètes (>24 mois ou signalés à mettre à jour). Source : inventaire C# ou rapport JSON.",
                        items)
                    {
                        Owner = Application.Current.MainWindow
                    };
                    window.ShowDialog();
                    return;
                }

                if (key.Equals("Barrettes", StringComparison.OrdinalIgnoreCase))
                {
                    var items = GetMemoryModulesFromJson();
                    var window = new ListDetailWindow(
                        "Détails des barrettes RAM",
                        "Marque, modèle, capacité, vitesse et slot des barrettes détectées (source : section Mémoire du scan).",
                        items)
                    {
                        Owner = Application.Current.MainWindow
                    };
                    window.ShowDialog();
                    return;
                }

                if (key.Equals("Points de restauration", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryGetCombinedJsonRoot(out var root))
                    {
                        System.Windows.MessageBox.Show(
                            "Données de restauration indisponibles. Relancez un scan complet.",
                            "Points de restauration",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Information);
                        return;
                    }

                    var restorePointService = new RestorePointService();
                    var details = restorePointService.ReadFromCombinedRoot(root);
                    var window = new RestorePointsWindow(details, restorePointService)
                    {
                        Owner = Application.Current.MainWindow
                    };
                    window.ShowDialog();
                    return;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ListDetail] Erreur: {ex.Message}");
            }
        }

        private List<string> GetMemoryModulesFromJson()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(_lastCombinedJsonContent)) return list;

            static string NormalizeField(string? value)
            {
                var normalized = TextEncodingNormalizer.NormalizeIfCorrupted(value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                    return string.Empty;

                if (normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("na", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("non disponible", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("non detecte", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return normalized;
            }

            static string DisplayOrUnavailable(string? value) =>
                string.IsNullOrWhiteSpace(value) ? "Non disponible" : value;

            static string ResolveManufacturer(string? manufacturer, string? partNumber)
            {
                var normalizedManufacturer = NormalizeField(manufacturer);
                if (!string.IsNullOrWhiteSpace(normalizedManufacturer))
                    return normalizedManufacturer;

                var normalizedPart = NormalizeField(partNumber);
                if (string.IsNullOrWhiteSpace(normalizedPart))
                    return string.Empty;

                if (normalizedPart.StartsWith("F4-", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPart.StartsWith("F5-", StringComparison.OrdinalIgnoreCase))
                    return "G.Skill";

                if (normalizedPart.StartsWith("HMA", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPart.StartsWith("HMC", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPart.StartsWith("H5", StringComparison.OrdinalIgnoreCase))
                    return "Hynix";

                if (normalizedPart.StartsWith("MTA", StringComparison.OrdinalIgnoreCase))
                    return "Micron";

                if (normalizedPart.StartsWith("K", StringComparison.OrdinalIgnoreCase))
                    return "Samsung";

                return string.Empty;
            }

            try
            {
                if (!TryGetCombinedJsonRoot(out var root))
                    return list;

                if (!root.TryGetProperty("scan_powershell", out var ps) ||
                    !ps.TryGetProperty("sections", out var sections) ||
                    !sections.TryGetProperty("Memory", out var memory) ||
                    !memory.TryGetProperty("data", out var data))
                {
                    return list;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (data.TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in modules.EnumerateArray())
                    {
                        var manufacturerRaw = m.TryGetProperty("manufacturer", out var mf) ? mf.GetString() : null;
                        var partNumberRaw = m.TryGetProperty("partNumber", out var pn) ? pn.GetString() : null;
                        var manufacturer = ResolveManufacturer(manufacturerRaw, partNumberRaw);
                        var partNumber = NormalizeField(partNumberRaw);

                        string capacityGb = "Non disponible";
                        if (m.TryGetProperty("capacityGB", out var capGb))
                        {
                            if (capGb.ValueKind == JsonValueKind.Number)
                            {
                                capacityGb = $"{capGb.GetDouble():F0} GB";
                            }
                            else if (capGb.ValueKind == JsonValueKind.String &&
                                     double.TryParse(
                                         capGb.GetString()?.Replace(',', '.'),
                                         System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture,
                                         out var capGbParsed) &&
                                     capGbParsed > 0)
                            {
                                capacityGb = $"{capGbParsed:F0} GB";
                            }
                        }
                        else if (m.TryGetProperty("capacityMB", out var capMb) && capMb.ValueKind == JsonValueKind.Number)
                        {
                            capacityGb = $"{(capMb.GetDouble() / 1024.0):F0} GB";
                        }

                        string speed = "Non disponible";
                        if (m.TryGetProperty("configuredSpeedMHz", out var cfgSpeed) && cfgSpeed.ValueKind == JsonValueKind.Number && cfgSpeed.GetDouble() > 0)
                        {
                            speed = $"{cfgSpeed.GetDouble():F0} MHz";
                        }
                        else if (m.TryGetProperty("speedMHz", out var speedMhz) && speedMhz.ValueKind == JsonValueKind.Number && speedMhz.GetDouble() > 0)
                        {
                            speed = $"{speedMhz.GetDouble():F0} MHz";
                        }
                        else if (m.TryGetProperty("speed", out var speedText) && speedText.ValueKind == JsonValueKind.String)
                        {
                            speed = DisplayOrUnavailable(NormalizeField(speedText.GetString()));
                        }

                        var slot = m.TryGetProperty("deviceLocator", out var dl) ? dl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(slot))
                            slot = m.TryGetProperty("slot", out var slotProp) ? slotProp.GetString() : null;
                        slot = NormalizeField(slot);

                        var dedupeKey = $"{slot}|{partNumber}|{capacityGb}|{speed}";
                        if (!seen.Add(dedupeKey))
                            continue;

                        list.Add(
                            $"Slot: {DisplayOrUnavailable(slot)}{Environment.NewLine}" +
                            $"  Marque: {DisplayOrUnavailable(manufacturer)}{Environment.NewLine}" +
                            $"  Modèle: {DisplayOrUnavailable(partNumber)}{Environment.NewLine}" +
                            $"  Capacité: {capacityGb}{Environment.NewLine}" +
                            $"  Vitesse: {speed}");
                    }
                }
            }
            catch
            {
                // Empty list remains valid for modal rendering.
            }

            return list;
        }

        private List<string> GetAudioDevicesFromJson()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(_lastCombinedJsonContent)) return list;
            try
            {
                if (!TryGetCombinedJsonRoot(out var root))
                    return list;

                if (!root.TryGetProperty("scan_powershell", out var ps) || !ps.TryGetProperty("sections", out var sections) ||
                    !sections.TryGetProperty("Audio", out var audio) || !audio.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                    return list;

                foreach (var dev in devices.EnumerateArray())
                {
                    var name = dev.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var status = dev.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        list.Add("Non disponible");
                    }
                    else
                    {
                        list.Add(string.IsNullOrWhiteSpace(status) ? name : $"{name} - {status}");
                    }
                }
            }
            catch { }
            return list;
        }

        private List<string> GetPrintersFromJson()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(_lastCombinedJsonContent)) return list;
            try
            {
                if (!TryGetCombinedJsonRoot(out var root))
                    return list;

                if (!root.TryGetProperty("scan_powershell", out var ps) || !ps.TryGetProperty("sections", out var sections) ||
                    !sections.TryGetProperty("Printers", out var printers) || !printers.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("printers", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return list;

                foreach (var p in arr.EnumerateArray())
                {
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var isDefault = p.TryGetProperty("default", out var d) &&
                        (d.ValueKind == JsonValueKind.True || (d.ValueKind == JsonValueKind.Number && d.GetInt32() != 0));

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        list.Add("Non disponible");
                    }
                    else
                    {
                        list.Add(isDefault ? $"{name} [Défaut]" : name);
                    }
                }
            }
            catch { }
            return list;
        }

        private List<string> GetOutdatedDriversList()
        {
            var list = new List<string>();
            if (_lastDriverInventory?.Available == true && _lastDriverInventory.Drivers != null)
            {
                foreach (var d in _lastDriverInventory.Drivers)
                {
                    var isOutdated = d.UpdateStatus == "Outdated";
                    if (!isOutdated && !string.IsNullOrEmpty(d.DriverDate) && DateTime.TryParse(d.DriverDate, out var date))
                        isOutdated = (DateTime.Now - date).TotalDays > 730;
                    if (!isOutdated) continue;
                    var cls = d.DeviceClass ?? "";
                    var name = d.DeviceName ?? "Non disponible";
                    var ver = d.DriverVersion ?? "?";
                    list.Add($"{cls}: {name.Trim()} v{ver}");
                }
            }
            if (list.Count == 0 && !string.IsNullOrEmpty(_lastCombinedJsonContent))
            {
                try
                {
                    if (!TryGetCombinedJsonRoot(out var root))
                        return list;

                    if (root.TryGetProperty("driver_inventory", out var inv) && inv.TryGetProperty("drivers", out var drivers) && drivers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in drivers.EnumerateArray())
                        {
                            var cls = d.TryGetProperty("deviceClass", out var c) ? c.GetString() ?? "" : "";
                            var name = d.TryGetProperty("deviceName", out var n) ? n.GetString()?.Trim() ?? "Non disponible" : "Non disponible";
                            var ver = d.TryGetProperty("driverVersion", out var v) ? v.GetString() ?? "?" : "?";
                            var dateStr = d.TryGetProperty("driverDate", out var dt) ? dt.GetString() : null;
                            var outdated = d.TryGetProperty("updateStatus", out var u) && u.GetString() == "Outdated";
                            if (!outdated && !string.IsNullOrEmpty(dateStr) && dateStr.Length >= 8 && DateTime.TryParse(dateStr.Substring(0, Math.Min(10, dateStr.Length)), out var date))
                                outdated = (DateTime.Now - date).TotalDays > 730;
                            if (outdated)
                                list.Add($"{cls}: {name} v{ver}");
                        }
                    }
                }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// Affiche les détails des erreurs collecteur dans une fenêtre modale
        /// avec vue unifiée: erreurs, missingData, diagnostics WMI et exceptions collecteur C#.
        /// </summary>
        private void ShowCollectorErrorsDetails()
        {
            try
            {
                if (!HasHealthReport)
                    return;

                var dialogData = BuildCollectorDiagnosticsDialogData();
                var window = new Views.CollectorErrorsWindow(dialogData)
                {
                    Owner = Application.Current?.MainWindow
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ShowCollectorErrors] Erreur: {ex.Message}");
                System.Windows.MessageBox.Show("Impossible d'afficher les détails de collecte.", "Détails de collecte", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private CollectorDiagnosticsDialogData BuildCollectorDiagnosticsDialogData()
        {
            var dialogData = new CollectorDiagnosticsDialogData
            {
                CollectorErrorsLogical = HealthReport?.CollectorErrorsLogical ?? 0
            };

            var seenErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCsharp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var fallbackTimestamp = string.Empty;
            if (TryGetCombinedJsonRoot(out var root))
            {
                fallbackTimestamp = TryReadMetadataTimestamp(root);
                AppendPowerShellErrors(root, dialogData.Errors, seenErrors, fallbackTimestamp);
                AppendMissingData(root, dialogData.MissingData, seenMissing, fallbackTimestamp);
                AppendCollectorWmiErrors(root, dialogData.Errors, seenErrors, fallbackTimestamp);
                AppendCsharpCollectorExceptions(root, dialogData.CsharpExceptions, seenCsharp, fallbackTimestamp);
            }

            AppendHealthReportFallback(dialogData, seenErrors, seenMissing, fallbackTimestamp);

            if (dialogData.Errors.Count == 0 && dialogData.MissingData.Count == 0 && dialogData.CsharpExceptions.Count == 0)
            {
                dialogData.MissingData.Add(new CollectorDiagnosticDetailItem
                {
                    Section = "Collecte",
                    Reason = "Aucun incident structuré n'a été détecté sur cette exécution.",
                    Source = "PS",
                    Timestamp = fallbackTimestamp,
                    ConfidenceImpact = "Faible"
                });
            }

            return dialogData;
        }

        private void AppendPowerShellErrors(JsonElement root, List<CollectorDiagnosticDetailItem> target, HashSet<string> dedupe, string fallbackTimestamp)
        {
            if (!TryGetNestedElement(root, out var errorsElement, "scan_powershell", "errors") &&
                !TryGetNestedElement(root, out errorsElement, "errors"))
            {
                return;
            }

            if (errorsElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var err in errorsElement.EnumerateArray())
            {
                if (err.ValueKind != JsonValueKind.Object)
                    continue;

                var code = GetElementString(err, "type", "code");
                var section = GetElementString(err, "section", "source");
                var message = GetElementString(err, "message", "msg");
                var exceptionType = GetElementString(err, "exceptionType");
                var timestamp = GetElementString(err, "timestamp") ?? fallbackTimestamp;

                var reason = SanitizeReason(message);
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "Erreur de collecte sans détail explicite.";

                var technical = BuildTechnicalDetails(
                    ("Code", code),
                    ("Section", section),
                    ("Type exception", exceptionType));

                AddDiagnosticItem(
                    target,
                    dedupe,
                    new CollectorDiagnosticDetailItem
                    {
                        Section = string.IsNullOrWhiteSpace(section) ? "Collecte PowerShell" : section!,
                        Reason = reason,
                        Source = "PS",
                        Timestamp = timestamp ?? string.Empty,
                        ConfidenceImpact = DetermineConfidenceImpact(section, reason, code),
                        TechnicalDetails = technical
                    });
            }
        }

        private void AppendCollectorWmiErrors(JsonElement root, List<CollectorDiagnosticDetailItem> target, HashSet<string> dedupe, string fallbackTimestamp)
        {
            if (!TryGetNestedElement(root, out var wmiErrors, "collector_diagnostics", "wmi_errors") || wmiErrors.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in wmiErrors.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var ns = GetElementString(item, "namespace");
                var query = GetElementString(item, "query");
                var method = GetElementString(item, "method");
                var severity = GetElementString(item, "severity");
                var message = GetElementString(item, "message");
                var timestamp = GetElementString(item, "timestamp") ?? fallbackTimestamp;
                var exceptionType = GetElementString(item, "exceptionType");
                var hresult = GetElementString(item, "hresult");
                var topFrame = GetElementString(item, "topStackFrame");

                var reason = SanitizeReason(message);
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "Erreur WMI/CIM détectée pendant la collecte.";

                var section = string.IsNullOrWhiteSpace(method) ? "Collecteur WMI" : $"Collecteur WMI ({method})";
                var confidenceImpact = string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase) ? "Élevé" : "Moyen";
                var technical = BuildTechnicalDetails(
                    ("Namespace", ns),
                    ("Requête", query),
                    ("Méthode", method),
                    ("Exception", exceptionType),
                    ("HRESULT", hresult),
                    ("Top frame", topFrame));

                AddDiagnosticItem(
                    target,
                    dedupe,
                    new CollectorDiagnosticDetailItem
                    {
                        Section = section,
                        Reason = reason,
                        Source = "C#",
                        Timestamp = timestamp ?? string.Empty,
                        ConfidenceImpact = confidenceImpact,
                        TechnicalDetails = technical
                    });
            }
        }

        private void AppendCsharpCollectorExceptions(JsonElement root, List<CollectorDiagnosticDetailItem> target, HashSet<string> dedupe, string fallbackTimestamp)
        {
            if (!TryGetNestedElement(root, out var exceptionsElement, "sensors_csharp", "collectionExceptions") || exceptionsElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var ex in exceptionsElement.EnumerateArray())
            {
                if (ex.ValueKind != JsonValueKind.String)
                    continue;

                var raw = ex.GetString() ?? string.Empty;
                var reason = SanitizeReason(raw);
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "Exception capteurs C#.";

                var technical = BuildTechnicalDetails(("Exception", Truncate(raw, 700)));
                AddDiagnosticItem(
                    target,
                    dedupe,
                    new CollectorDiagnosticDetailItem
                    {
                        Section = "Capteurs matériels",
                        Reason = reason,
                        Source = "C#",
                        Timestamp = fallbackTimestamp,
                        ConfidenceImpact = DetermineConfidenceImpact("Capteurs", reason, "C#"),
                        TechnicalDetails = technical
                    });
            }
        }

        private void AppendMissingData(JsonElement root, List<CollectorDiagnosticDetailItem> target, HashSet<string> dedupe, string fallbackTimestamp)
        {
            if (!TryGetNestedElement(root, out var missingData, "scan_powershell", "missingData") &&
                !TryGetNestedElement(root, out missingData, "missingData"))
            {
                return;
            }

            if (missingData.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in missingData.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var section = GetElementString(item, "section");
                        var missingItem = GetElementString(item, "item");
                        var reason = GetElementString(item, "reason");
                        var timestamp = GetElementString(item, "timestamp") ?? fallbackTimestamp;
                        var technical = BuildTechnicalDetails(("Item", missingItem));

                        AddDiagnosticItem(
                            target,
                            dedupe,
                            new CollectorDiagnosticDetailItem
                            {
                                Section = string.IsNullOrWhiteSpace(section) ? "Donnée manquante" : section!,
                                Reason = SanitizeReason(reason) ?? "Donnée non collectée.",
                                Source = "PS",
                                Timestamp = timestamp ?? string.Empty,
                                ConfidenceImpact = DetermineConfidenceImpact(section, reason, "missingData"),
                                TechnicalDetails = technical
                            });
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        AddParsedMissingText(item.GetString(), target, dedupe, fallbackTimestamp);
                    }
                }
            }
            else if (missingData.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in missingData.EnumerateObject())
                {
                    var reason = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.GetRawText();

                    AddDiagnosticItem(
                        target,
                        dedupe,
                        new CollectorDiagnosticDetailItem
                        {
                            Section = property.Name,
                            Reason = SanitizeReason(reason) ?? "Donnée non collectée.",
                            Source = "PS",
                            Timestamp = fallbackTimestamp,
                            ConfidenceImpact = DetermineConfidenceImpact(property.Name, reason, "missingData")
                        });
                }
            }
            else if (missingData.ValueKind == JsonValueKind.String)
            {
                AddParsedMissingText(missingData.GetString(), target, dedupe, fallbackTimestamp);
            }
        }

        private void AppendHealthReportFallback(CollectorDiagnosticsDialogData dialogData, HashSet<string> seenErrors, HashSet<string> seenMissing, string fallbackTimestamp)
        {
            if (HealthReport == null)
                return;

            foreach (var error in HealthReport.Errors ?? new List<ScanErrorInfo>())
            {
                var reason = SanitizeReason(error.Message);
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "Erreur de collecte non détaillée.";

                AddDiagnosticItem(
                    dialogData.Errors,
                    seenErrors,
                    new CollectorDiagnosticDetailItem
                    {
                        Section = string.IsNullOrWhiteSpace(error.Section) ? "Collecte PowerShell" : error.Section,
                        Reason = reason,
                        Source = "PS",
                        Timestamp = fallbackTimestamp,
                        ConfidenceImpact = DetermineConfidenceImpact(error.Section, reason, error.Code),
                        TechnicalDetails = BuildTechnicalDetails(("Code", error.Code), ("Type exception", error.ExceptionType))
                    });
            }

            foreach (var missing in HealthReport.MissingData ?? new List<string>())
            {
                AddParsedMissingText(missing, dialogData.MissingData, seenMissing, fallbackTimestamp);
            }
        }

        private void AddParsedMissingText(string? raw, List<CollectorDiagnosticDetailItem> target, HashSet<string> dedupe, string fallbackTimestamp)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;

            var section = "Donnée manquante";
            var reason = raw.Trim();
            var timestamp = fallbackTimestamp;

            if (raw.Contains(';'))
            {
                var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                if (parts.Count > 0)
                    section = parts[0];

                var nonTimestampParts = new List<string>();
                foreach (var part in parts.Skip(1))
                {
                    if (DateTimeOffset.TryParse(part, out _))
                        timestamp = part;
                    else
                        nonTimestampParts.Add(part);
                }

                if (nonTimestampParts.Count > 0)
                    reason = string.Join(" | ", nonTimestampParts);
            }
            else if (raw.Contains(':'))
            {
                var idx = raw.IndexOf(':');
                if (idx > 0)
                {
                    section = raw.Substring(0, idx).Trim();
                    reason = raw[(idx + 1)..].Trim();
                }
            }

            AddDiagnosticItem(
                target,
                dedupe,
                new CollectorDiagnosticDetailItem
                {
                    Section = section,
                    Reason = SanitizeReason(reason) ?? "Donnée non collectée.",
                    Source = "PS",
                    Timestamp = timestamp,
                    ConfidenceImpact = DetermineConfidenceImpact(section, reason, "missingData")
                });
        }

        private static void AddDiagnosticItem(List<CollectorDiagnosticDetailItem> target, HashSet<string> dedupe, CollectorDiagnosticDetailItem item)
        {
            item.Section = string.IsNullOrWhiteSpace(item.Section) ? "Collecte" : item.Section.Trim();
            item.Source = string.IsNullOrWhiteSpace(item.Source) ? "PS" : item.Source.Trim();
            item.Reason = string.IsNullOrWhiteSpace(item.Reason) ? "Information indisponible." : item.Reason.Trim();
            item.ConfidenceImpact = string.IsNullOrWhiteSpace(item.ConfidenceImpact) ? "Moyen" : item.ConfidenceImpact.Trim();

            var key = $"{item.Source}|{item.Section}|{item.Reason}";
            if (!dedupe.Add(key))
                return;

            target.Add(item);
        }

        private static bool TryGetNestedElement(JsonElement root, out JsonElement value, params string[] path)
        {
            value = root;
            foreach (var segment in path)
            {
                if (value.ValueKind != JsonValueKind.Object)
                    return false;

                if (value.TryGetProperty(segment, out var direct))
                {
                    value = direct;
                    continue;
                }

                var found = false;
                foreach (var property in value.EnumerateObject())
                {
                    if (!string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                        continue;

                    value = property.Value;
                    found = true;
                    break;
                }

                if (!found)
                    return false;
            }

            return true;
        }

        private static string? GetElementString(JsonElement element, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    return null;

                if (element.TryGetProperty(propertyName, out var property))
                    return property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();

                foreach (var candidate in element.EnumerateObject())
                {
                    if (!string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return candidate.Value.ValueKind == JsonValueKind.String ? candidate.Value.GetString() : candidate.Value.GetRawText();
                }
            }

            return null;
        }

        private static string TryReadMetadataTimestamp(JsonElement root)
        {
            if (TryGetNestedElement(root, out var metadataTimestamp, "scan_powershell", "metadata", "timestamp") ||
                TryGetNestedElement(root, out metadataTimestamp, "metadata", "timestamp"))
            {
                return metadataTimestamp.ValueKind == JsonValueKind.String ? metadataTimestamp.GetString() ?? string.Empty : string.Empty;
            }

            return string.Empty;
        }

        private static string? SanitizeReason(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var normalized = text.Replace("\r", string.Empty).Trim();
            var firstLine = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? normalized;
            if (firstLine.Length == 0)
                return null;

            return Truncate(firstLine, 280);
        }

        private static string BuildTechnicalDetails(params (string Label, string? Value)[] rows)
        {
            var lines = new List<string>();
            foreach (var (label, value) in rows)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                lines.Add($"{label}: {Truncate(value.Trim(), 320)}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string DetermineConfidenceImpact(string? section, string? reason, string? code)
        {
            var text = $"{section} {reason} {code}".ToLowerInvariant();
            if (text.Contains("cpu", StringComparison.Ordinal) ||
                text.Contains("memory", StringComparison.Ordinal) ||
                text.Contains("ram", StringComparison.Ordinal) ||
                text.Contains("disk", StringComparison.Ordinal) ||
                text.Contains("storage", StringComparison.Ordinal) ||
                text.Contains("smart", StringComparison.Ordinal) ||
                text.Contains("wmi", StringComparison.Ordinal))
            {
                return "Élevé";
            }

            if (text.Contains("network", StringComparison.Ordinal) ||
                text.Contains("security", StringComparison.Ordinal) ||
                text.Contains("access", StringComparison.Ordinal) ||
                text.Contains("permission", StringComparison.Ordinal))
            {
                return "Moyen";
            }

            return "Faible";
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength) + "...";
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

            var searchDirs = GetReportSearchDirectories().ToArray();

            // PRIORITÉ 2: Rapport_Unifie (pattern TXT unifié)
            foreach (var dir in searchDirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
            {
                var canonicalUnifiedPath = Path.Combine(dir, Services.ScanStorageService.UnifiedReportFileName);
                if (File.Exists(canonicalUnifiedPath))
                {
                    return canonicalUnifiedPath;
                }

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

        private void OpenHistoryReport(ScanHistoryItem? item)
        {
            if (item == null)
                return;

            OpenReportTxtForItem(item);
        }

        private void OpenHistoryFolder(ScanHistoryItem? item)
        {
            if (item == null)
                return;

            try
            {
                var runFolder = !string.IsNullOrWhiteSpace(item.RunId)
                    ? Services.ScanStorageService.GetRunFolder(item.RunId)
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(runFolder) && Directory.Exists(runFolder))
                {
                    Process.Start("explorer.exe", runFolder);
                    return;
                }

                var combinedDir = !string.IsNullOrWhiteSpace(item.CombinedJsonPath)
                    ? Path.GetDirectoryName(item.CombinedJsonPath)
                    : null;
                if (!string.IsNullOrWhiteSpace(combinedDir) && Directory.Exists(combinedDir))
                {
                    Process.Start("explorer.exe", combinedDir);
                    return;
                }

                Directory.CreateDirectory(Services.ScanStorageService.BaseDir);
                Process.Start("explorer.exe", Services.ScanStorageService.BaseDir);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[History][RunId:{item.RunId}] Open folder failed: {ex.Message}");
            }
        }

        private void ShowHistoryErrorDetails(ScanHistoryItem? item)
        {
            if (item == null)
                return;

            var details = string.IsNullOrWhiteSpace(item.ErrorSummary)
                ? "Aucune erreur detaillee pour ce scan."
                : item.ErrorSummary;

            System.Windows.MessageBox.Show(
                details,
                $"Scan {item.RunId}",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        private void OpenHistoryLogs()
        {
            try
            {
                var logFiles = new[] { _uiLogPath, _bootLogPath }
                    .Where(File.Exists)
                    .ToList();

                if (logFiles.Count == 0)
                {
                    System.Windows.MessageBox.Show(
                        "Aucun fichier log trouve.",
                        "Historique",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }

                foreach (var log in logFiles)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "notepad.exe",
                        Arguments = $"\"{log}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[History] Open logs failed: {ex.Message}");
            }
        }

        private sealed class HistoryLoadPreparation
        {
            public bool Success { get; init; }
            public string? CombinedPath { get; init; }
            public string? CombinedContent { get; init; }
            public ScanResult? ParsedResult { get; init; }
            public CombinedScanResult? Combined { get; init; }
            public HealthReport? HealthReport { get; init; }
            public string? ErrorSummary { get; init; }
        }

        private async Task<bool> TryLoadHistoryItemResultAsync(ScanHistoryItem item)
        {
            var prepared = await Task.Run(() => PrepareHistoryItemLoad(item));
            return ApplyHistoryItemLoad(item, prepared);
        }

        private HistoryLoadPreparation PrepareHistoryItemLoad(ScanHistoryItem item)
        {
            var combinedPath = ResolveHistoryCombinedPath(item);

            if (string.IsNullOrWhiteSpace(combinedPath) || !File.Exists(combinedPath))
            {
                App.LogMessage($"[History][RunId:{item.RunId}] Combined file missing path={combinedPath}");
                return new HistoryLoadPreparation
                {
                    Success = false,
                    ErrorSummary = "Fichier scan_result_combined.json introuvable pour ce RunId."
                };
            }

            try
            {
                var content = File.ReadAllText(combinedPath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                ScanResult? parsedResult = item.Result;
                if (parsedResult == null)
                {
                    var psJson = root.TryGetProperty("scan_powershell", out var psNode)
                        ? psNode.GetRawText()
                        : content;

                    parsedResult = new PowerShellJsonMapper().Parse(psJson, combinedPath, TimeSpan.Zero);
                }

                CombinedScanResult? combined = null;
                try
                {
                    combined = JsonSerializer.Deserialize<CombinedScanResult>(content, HardwareSensorsResult.JsonOptions);
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[History][RunId:{item.RunId}] Combined deserialize warning: {ex.Message}");
                }

                if (!TryBuildHistoryHealthReport(content, combined, out var historyReport))
                {
                    App.LogMessage($"[History][RunId:{item.RunId}] UI health report reconstruction failed.");
                    return new HistoryLoadPreparation
                    {
                        Success = true,
                        CombinedPath = combinedPath,
                        CombinedContent = content,
                        ParsedResult = parsedResult,
                        Combined = combined,
                        HealthReport = null,
                        ErrorSummary = string.IsNullOrWhiteSpace(item.ErrorSummary)
                            ? "Le rapport sante de ce run n'a pas pu etre reconstruit."
                            : item.ErrorSummary
                    };
                }

                return new HistoryLoadPreparation
                {
                    Success = true,
                    CombinedPath = combinedPath,
                    CombinedContent = content,
                    ParsedResult = parsedResult,
                    Combined = combined,
                    HealthReport = historyReport
                };
            }
            catch (Exception ex)
            {
                App.LogMessage($"[History][RunId:{item.RunId}] Hydrate failed: {ex.Message}");
                return new HistoryLoadPreparation
                {
                    Success = false,
                    ErrorSummary = $"Impossible de charger ce scan: {ex.Message}"
                };
            }
        }

        private bool ApplyHistoryItemLoad(ScanHistoryItem item, HistoryLoadPreparation prepared)
        {
            if (prepared.ParsedResult != null)
            {
                prepared.ParsedResult.Summary.ScanDate = item.ScanDate;
                if (item.Score > 0) prepared.ParsedResult.Summary.Score = item.Score;
                if (!string.IsNullOrWhiteSpace(item.Grade)) prepared.ParsedResult.Summary.Grade = item.Grade;
                item.Result = prepared.ParsedResult;
            }

            if (!string.IsNullOrWhiteSpace(prepared.CombinedPath))
            {
                item.CombinedJsonPath = prepared.CombinedPath;
                _combinedJsonPath = prepared.CombinedPath;
            }

            if (!string.IsNullOrWhiteSpace(prepared.CombinedContent))
            {
                SetCombinedJsonContent(prepared.CombinedContent, prepared.Combined);
                HydrateHistoryRuntimeState(prepared.Combined);
            }

            if (!prepared.Success)
            {
                item.ErrorSummary = prepared.ErrorSummary;
                HealthReport = null;
                return false;
            }

            if (prepared.HealthReport != null)
            {
                InjectSpeedTestIntoNetworkSection(prepared.HealthReport);
            }
            else if (!string.IsNullOrWhiteSpace(prepared.ErrorSummary))
            {
                item.ErrorSummary = prepared.ErrorSummary;
            }

            HealthReport = prepared.HealthReport;
            App.LogMessage($"[History][RunId:{item.RunId}] Item hydrated from {prepared.CombinedPath}");
            return true;
        }

        private string? ResolveHistoryCombinedPath(ScanHistoryItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.CombinedJsonPath) && File.Exists(item.CombinedJsonPath))
                return item.CombinedJsonPath;

            if (!string.IsNullOrWhiteSpace(item.RunId))
            {
                var canonicalPath = Services.ScanStorageService.GetCombinedJsonPath(item.RunId);
                if (File.Exists(canonicalPath))
                    return canonicalPath;
            }

            return item.CombinedJsonPath;
        }

        private void HydrateHistoryRuntimeState(CombinedScanResult? combined)
        {
            _lastSensorsResult = combined?.SensorsCsharp;
            _lastProcessTelemetry = combined?.ProcessTelemetry;
            _lastNetworkDiagnostics = combined?.NetworkDiagnostics;
            _lastDriverInventory = combined?.DriverInventory;
            _lastWindowsUpdateResult = combined?.UpdatesCsharp;
            _lastSecurityInfo = combined?.SecurityInfoCsharp;
            _lastPerformanceTimeseriesSummary = combined?.PerformanceTimeseriesSummary;
            _lastEventLogsDetailed = combined?.EventLogsDetailed;
            _lastSmartAttributes = combined?.SmartAttributes;
            _lastMinidumpsDetailed = combined?.MinidumpsDetailed;
            _lastDiagnosticSnapshot = combined?.DiagnosticSnapshot;

            NotifySensorBlockingChanged();
            NotifyProcessTelemetryChanged();
            NotifyNetworkDiagnosticsChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        private bool TryBuildHistoryHealthReport(
            string combinedJsonContent,
            CombinedScanResult? combined,
            out HealthReport? report)
        {
            report = null;
            try
            {
                report = HealthReportBuilder.Build(
                    combinedJsonContent,
                    combined?.SensorsCsharp,
                    combined?.DriverInventory,
                    combined?.UpdatesCsharp);
                return report != null;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[History] HealthReport rebuild failed: {ex.Message}");
                return false;
            }
        }

        private void UpsertHistoryItemFromMeta(ScanMeta meta, string? combinedPathOverride = null)
        {
            if (string.IsNullOrWhiteSpace(meta.RunId))
                return;

            if (Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                Application.Current.Dispatcher.Invoke(() => UpsertHistoryItemFromMeta(meta, combinedPathOverride));
                return;
            }

            HasHistoryLoadError = false;
            HistoryLoadErrorMessage = string.Empty;

            var target = ScanHistory.FirstOrDefault(h => h.RunId == meta.RunId) ??
                         ArchivedScanHistory.FirstOrDefault(h => h.RunId == meta.RunId);
            var combinedPath = combinedPathOverride;
            if (string.IsNullOrWhiteSpace(combinedPath))
                combinedPath = Services.ScanStorageService.GetCombinedJsonPath(meta.RunId);

            if (target == null)
            {
                target = new ScanHistoryItem
                {
                    RunId = meta.RunId,
                    ScanDate = meta.StartTime.ToLocalTime(),
                    Score = meta.Score,
                    Grade = meta.Grade,
                    Status = meta.Status,
                    DurationSeconds = meta.DurationSeconds,
                    ErrorSummary = string.IsNullOrWhiteSpace(meta.ErrorSummary) ? meta.StatusReason : meta.ErrorSummary,
                    CombinedJsonPath = File.Exists(combinedPath) ? combinedPath : null,
                    Result = null
                };

                if (ScanHistory.Count < 10)
                {
                    ScanHistory.Insert(0, target);
                }
                else
                {
                    ArchivedScanHistory.Insert(0, target);
                }
            }
            else
            {
                target.ScanDate = meta.StartTime.ToLocalTime();
                target.Score = meta.Score;
                target.Grade = meta.Grade;
                target.Status = meta.Status;
                target.DurationSeconds = meta.DurationSeconds;
                target.ErrorSummary = string.IsNullOrWhiteSpace(meta.ErrorSummary) ? meta.StatusReason : meta.ErrorSummary;
                target.CombinedJsonPath = File.Exists(combinedPath) ? combinedPath : target.CombinedJsonPath;
            }

            OnPropertyChanged(nameof(HasAnyScan));
            OnPropertyChanged(nameof(HasNoScanHistory));
            OnPropertyChanged(nameof(IsHistoryListStateVisible));
            OnPropertyChanged(nameof(IsHistoryEmptyStateVisible));
            CommandManager.InvalidateRequerySuggested();
        }

        private void PersistScanMetaSafe(ScanMeta meta, string context)
        {
            try
            {
                Services.ScanStorageService.SaveMeta(meta);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[History][RunId:{meta.RunId}] SaveMeta failed context={context} error={ex.Message}");
            }
        }

        private void PersistFinalScanArtifacts(ScanResult? result, ScanHistoryItem? historyItem, string resultsMessage)
        {
            if (string.IsNullOrWhiteSpace(_activeRunId))
            {
                App.LogMessage("[History] PersistFinalScanArtifacts skipped: RunId missing.");
                return;
            }

            string? canonicalCombinedPath = null;
            var expectedCanonicalCombinedPath = Services.ScanStorageService.GetCombinedJsonPath(_activeRunId);
            try
            {
                if (!string.IsNullOrWhiteSpace(_combinedJsonPath) &&
                    string.Equals(_combinedJsonPath, expectedCanonicalCombinedPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(_combinedJsonPath))
                {
                    canonicalCombinedPath = _combinedJsonPath;
                    if (string.IsNullOrWhiteSpace(_lastCombinedJsonContent))
                    {
                        var diskJson = File.ReadAllText(_combinedJsonPath, Encoding.UTF8);
                        SetCombinedJsonContent(diskJson, _lastCombinedResult);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(_lastCombinedJsonContent))
                {
                    canonicalCombinedPath = Services.ScanStorageService.SaveCombinedJson(_activeRunId, _lastCombinedJsonContent);
                }
                else if (!string.IsNullOrWhiteSpace(_combinedJsonPath) && File.Exists(_combinedJsonPath))
                {
                    var diskJson = File.ReadAllText(_combinedJsonPath, Encoding.UTF8);
                    canonicalCombinedPath = Services.ScanStorageService.SaveCombinedJson(_activeRunId, diskJson);
                    SetCombinedJsonContent(diskJson, _lastCombinedResult);
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[History][RunId:{_activeRunId}] Canonical combined persist failed: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(canonicalCombinedPath))
            {
                _combinedJsonPath = canonicalCombinedPath;
                if (historyItem != null)
                    historyItem.CombinedJsonPath = canonicalCombinedPath;
            }

            string? canonicalUnifiedTxtPath = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(_lastUnifiedTxtPath) && File.Exists(_lastUnifiedTxtPath))
                {
                    canonicalUnifiedTxtPath = Services.ScanStorageService.SaveUnifiedReportCopy(_activeRunId, _lastUnifiedTxtPath);
                    _lastUnifiedTxtPath = canonicalUnifiedTxtPath;
                }
            }
            catch (Exception txtEx)
            {
                App.LogMessage($"[History][RunId:{_activeRunId}] Unified TXT copy warning: {txtEx.Message}");
            }

            string? canonicalSnapshotPath = null;
            try
            {
                if (_lastDiagnosticSnapshot != null)
                {
                    var snapshotJson = JsonSerializer.Serialize(_lastDiagnosticSnapshot, HardwareSensorsResult.JsonOptions);
                    canonicalSnapshotPath = Services.ScanStorageService.SaveSnapshotJson(_activeRunId, snapshotJson);
                }
            }
            catch (Exception snapshotEx)
            {
                App.LogMessage($"[History][RunId:{_activeRunId}] Snapshot save warning: {snapshotEx.Message}");
            }

            var status = result == null
                ? Models.ScanStatus.Failed
                : (result.IsValid ? Models.ScanStatus.Success : Models.ScanStatus.Partial);

            var score = result?.Summary.Score ?? historyItem?.Score ?? 0;
            var grade = result?.Summary.Grade ?? historyItem?.Grade ?? "N/A";
            var errorSummary = status switch
            {
                Models.ScanStatus.Success => null,
                Models.ScanStatus.Partial => string.IsNullOrWhiteSpace(resultsMessage) ? "Scan partiel." : resultsMessage,
                _ => string.IsNullOrWhiteSpace(resultsMessage) ? "Scan echoue." : resultsMessage
            };

            var meta = new ScanMeta
            {
                RunId = _activeRunId,
                StartTime = _scanStartTime.UtcDateTime == default ? DateTime.UtcNow : _scanStartTime.UtcDateTime,
                EndTime = DateTime.UtcNow,
                MachineName = Environment.MachineName,
                Score = score,
                Grade = string.IsNullOrWhiteSpace(grade) ? "N/A" : grade,
                Status = status,
                DurationSeconds = _scanStopwatch.Elapsed.TotalSeconds,
                AppVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "unknown",
                ErrorSummary = errorSummary,
                TotalItems = result?.Summary.TotalItems ?? 0,
                OkCount = result?.Summary.OkCount ?? 0,
                WarnCount = result?.Summary.WarningCount ?? 0,
                ErrorCount = result?.Summary.ErrorCount ?? 0,
                CriticalCount = result?.Summary.CriticalCount ?? 0,
                SnapshotPath = canonicalSnapshotPath,
                UnifiedTxtPath = canonicalUnifiedTxtPath,
                CombinedSizeBytes = !string.IsNullOrWhiteSpace(canonicalCombinedPath) && File.Exists(canonicalCombinedPath)
                    ? new FileInfo(canonicalCombinedPath).Length
                    : 0,
                StatusReason = _lastRunStatus?.ReasonCodes?.Count > 0
                    ? string.Join("|", _lastRunStatus.ReasonCodes)
                    : errorSummary,
                TimingsDigest = _lastCombinedResult?.Timings?.PhaseTotals != null
                    ? _lastCombinedResult.Timings.PhaseTotals
                        .OrderByDescending(kv => kv.Value)
                        .Take(12)
                        .ToDictionary(kv => kv.Key, kv => kv.Value)
                    : null
            };

            PersistScanMetaSafe(meta, "finalize");
            Services.ScanStorageService.CleanupRunTempFiles(_activeRunId);

            if (historyItem != null)
            {
                historyItem.Status = meta.Status;
                historyItem.DurationSeconds = meta.DurationSeconds;
                historyItem.ErrorSummary = meta.ErrorSummary;
                historyItem.Score = meta.Score;
                historyItem.Grade = meta.Grade;
            }
            else
            {
                UpsertHistoryItemFromMeta(meta, canonicalCombinedPath);
            }
        }

        private async Task SelectHistoryScanAsync(ScanHistoryItem? item)
        {
            if (item == null)
                return;

            IsViewingArchives = false;
            ResultsMessage = "Chargement du scan historique...";

            if (item.Result != null && !ReferenceEquals(SelectedHistoryScan, item))
            {
                SelectedHistoryScan = item;
            }

            var loaded = await TryLoadHistoryItemResultAsync(item);
            if (!ReferenceEquals(SelectedHistoryScan, item))
            {
                SelectedHistoryScan = item;
            }
            if (!loaded)
            {
                ResultsMessage = string.IsNullOrWhiteSpace(item.ErrorSummary)
                    ? "Chargement partiel du scan historique. Le rapport detaille est indisponible."
                    : item.ErrorSummary;
            }
            else if (HealthReport == null && !string.IsNullOrWhiteSpace(item.ErrorSummary))
            {
                ResultsMessage = item.ErrorSummary;
            }
            else
            {
                ResultsMessage = string.Empty;
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
                OnPropertyChanged(nameof(HasNoScanHistory));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void RenameScan(ScanHistoryItem? item)
        {
            if (item == null) return;

            // Désactiver le mode renommage sur tous les autres items
            foreach (var h in ScanHistory.Concat(ArchivedScanHistory))
            {
                if (h != item) h.IsRenaming = false;
            }

            // Toggle le mode renommage inline sur cet item
            item.IsRenaming = true;
        }

        /// <summary>
        /// Validates and sanitizes a display name. Returns null if empty/whitespace.
        /// Trims, caps at 80 chars, strips invalid filename characters.
        /// </summary>
        private static string? ValidateDisplayName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();
            // Strip invalid filename chars: < > : " / \ | ? *
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                trimmed = trimmed.Replace(c, ' ');
            trimmed = trimmed.Trim();
            if (trimmed.Length > 80) trimmed = trimmed.Substring(0, 80).TrimEnd();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        /// <summary>Termine le renommage inline d'un item, valide le nom, et persiste.</summary>
        public void CommitRename(ScanHistoryItem? item)
        {
            if (item == null) return;
            var oldName = item.DisplayName;
            var validated = ValidateDisplayName(item.CustomDisplayName);
            item.CustomDisplayName = validated;
            item.IsRenaming = false;
            App.LogMessage($"[Rename] '{oldName}' -> '{item.DisplayName}'");
            PersistReportDisplayNames();
        }

        /// <summary>Annule le renommage inline et restaure l'état précédent.</summary>
        public void CancelRename(ScanHistoryItem? item)
        {
            if (item == null) return;
            item.IsRenaming = false;
            // No change to CustomDisplayName - keeps whatever was there before editing started
            App.LogMessage($"[Rename] Cancelled for '{item.DisplayName}'");
        }

        private bool _isRenamingReport;
        public bool IsRenamingReport
        {
            get => _isRenamingReport;
            set
            {
                if (SetProperty(ref _isRenamingReport, value))
                {
                    OnPropertyChanged(nameof(IsRenamingReport));
                }
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
                OnPropertyChanged(nameof(HasNoScanHistory));
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
                // Ouvrir le dialog bundle (UAC + Defender + réseau) ; à la fermeture avec succès, activer les tests réseau et persister.
                var dialog = new AdminPermissionsDialog
                {
                    Owner = Application.Current.MainWindow
                };
                if (dialog.ShowDialog() == true)
                {
                    AllowExternalNetworkTests = true;
                    SaveSettings();
                    OnPropertyChanged(nameof(AllowExternalNetworkTests));
                    StatusMessage = "Autorisations enregistrées. Tests réseau externes activés.";
                }
                return;
            }
            catch (Exception ex)
            {
                App.LogMessage($"Dialog autorisations: {ex.Message}");
                StatusMessage = GetString("AdminRestartError");
            }
        }

        /// <summary>Known protected folder paths that may trigger Controlled Folder Access.</summary>
        private static readonly string[] ProtectedFolderRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.Favorites),
        };

        private static bool IsProtectedFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var full = Path.GetFullPath(path);
            // Also check OneDrive
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive") ?? "";
            if (!string.IsNullOrEmpty(oneDrive) && full.StartsWith(oneDrive, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var root in ProtectedFolderRoots)
            {
                if (!string.IsNullOrEmpty(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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
                    Filter = "Fichiers texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*",
                    InitialDirectory = NormalizeReportDirectory(ReportDirectory)
                };

                if (dialog.ShowDialog() == true)
                {
                    var targetPath = dialog.FileName;

                    // Warn if exporting to a protected folder (CFA may block)
                    if (IsProtectedFolder(targetPath))
                    {
                        App.LogMessage($"[Export] Target is a protected folder: {targetPath}");
                        var warnResult = System.Windows.MessageBox.Show(
                            "Le dossier sélectionné est protégé par Windows (Accès contrôlé aux dossiers).\n\n" +
                            "L'écriture peut être bloquée par Windows Defender.\n" +
                            "Il est recommandé d'exporter vers un dossier non protégé.\n\n" +
                            "Continuer quand même ?",
                            "Dossier protégé",
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Warning);
                        if (warnResult != System.Windows.MessageBoxResult.Yes)
                            return;
                    }

                    File.WriteAllText(targetPath, ScanResult.RawReport, Encoding.UTF8);
                    App.LogMessage($"[Export] Success: {targetPath}");
                    StatusMessage = GetString("StatusExportSuccess");
                }
            }
            catch (UnauthorizedAccessException uaEx)
            {
                App.LogMessage($"[Export] UnauthorizedAccessException: {uaEx.Message}");
                StatusMessage = "Export bloqué : accès refusé (Accès contrôlé aux dossiers Windows Defender ?)";
                System.Windows.MessageBox.Show(
                    "L'écriture a été bloquée.\n\n" +
                    "Cause probable : l'Accès contrôlé aux dossiers de Windows Defender empêche l'application d'écrire dans ce dossier.\n\n" +
                    "Solutions :\n" +
                    "- Exporter vers un dossier non protégé\n" +
                    $"- Ajouter {GetPrimaryExecutableName()} aux applications autorisées dans Windows Security",
                    "Accès bloqué par Windows Defender",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
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

        /// <summary>
        /// Applique l'exception Windows Defender au niveau machine (tous les usagers) puis les exclusions de processus (binaire courant + alias legacy).
        /// Comportement : en cas d'échec du chemin, les exclusions de processus ne sont pas tentées (échec global).
        /// Si le chemin est ajouté avec succès, on tente les exclusions de processus ; un échec processus seul = succès partiel avec message explicite.
        /// En cas d'échec du chemin, un conseil Tamper Protection peut être ajouté au message si le texte d'erreur le suggère.
        /// </summary>
        private async void ApplyDefenderExclusion()
        {
            StatusMessage = "Ajout de l'exception Defender en cours…";
            var path = WindowsDefenderExclusionService.GetDefaultExclusionPath();
            App.LogMessage($"[DefenderExclusion] Path à exclure (machine): {path}");
            try
            {
                var (pathSuccess, pathMessage) = await WindowsDefenderExclusionService.AddMachineExclusionAsync(path).ConfigureAwait(false);

                if (!pathSuccess)
                {
                    var failureMessage = $"Échec : {pathMessage}";
                    if (ContainsPolicyOrTamperHint(pathMessage))
                        failureMessage += " Si la Protection contre les modifications est activée, ajoutez l'exclusion manuellement dans Sécurité Windows.";
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        StatusMessage = failureMessage;
                    });
                    return;
                }

                // Chemin ajouté : on tente les exclusions de processus (réduit les alertes Defender sur LibreHardwareMonitor/WinRing0).
                var (processSuccess, processMessage) = await WindowsDefenderExclusionService.AddProcessExclusionsAsync().ConfigureAwait(false);

                var processFullyOk = processSuccess && (processMessage?.Contains("ajoutées avec succès", StringComparison.OrdinalIgnoreCase) == true);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (processFullyOk)
                        StatusMessage = "Exception Windows Defender ajoutée. Redémarrez l'application pour que la prise en compte soit complète.";
                    else
                        StatusMessage = $"Exception du dossier ajoutée. Exclusions de processus partiellement échouées : {processMessage}";
                });
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DefenderExclusion] Erreur: {ex.Message}");
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"Erreur : {ex.Message}";
                });
            }
        }

        /// <summary>
        /// Indique si le message d'échec Defender suggère un blocage par stratégie ou Protection contre les modifications (Tamper Protection).
        /// Utilisé pour ajouter un conseil invitant à ajouter l'exclusion manuellement dans Sécurité Windows.
        /// </summary>
        private static bool ContainsPolicyOrTamperHint(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            var m = message.Trim();
            return m.IndexOf("Tamper", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("stratégies", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("stratégie", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("permissions", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("policy", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("Protection contre les modifications", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<bool> IsUnsafeHardwareMonitoringAllowedAsync()
        {
            if (!_enableHardwareMonitoring)
                return false;

            try
            {
                var exclusionPath = WindowsDefenderExclusionService.GetDefaultExclusionPath();
                var pathExcluded = await WindowsDefenderExclusionService.VerifyExclusionAsync(exclusionPath).ConfigureAwait(false);
                var processChecks = GetDefenderProcessCandidates()
                    .Select(WindowsDefenderExclusionService.VerifyProcessExclusionAsync)
                    .ToArray();
                var processExcluded = (await Task.WhenAll(processChecks).ConfigureAwait(false)).Any(v => v);

                if (pathExcluded || processExcluded)
                    return true;

                App.LogMessage("[Sensors] Hardware monitoring unsafe désactivé: exclusion Defender manquante. Fallback SAFE forcé.");
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    AddLiveFeedItem("⚠ Mode capteurs avancé désactivé (exclusion Defender manquante) - fallback SAFE.");
                }));

                // Évite de retomber sur ce mode à chaque scan si l'utilisateur n'a pas configuré Defender.
                _enableHardwareMonitoring = false;
                OnPropertyChanged(nameof(EnableHardwareMonitoring));
                _ = SaveSettingsAsync();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[Sensors] Vérification exclusion Defender impossible: {ex.Message}. Fallback SAFE.");
                return false;
            }

            return false;
        }

        private void SaveSettings()
        {
            try
            {
                var normalizedTheme = NormalizeThemeCode(CurrentTheme);
                var normalizedReportDirectory = NormalizeReportDirectory(ReportDirectory);
                Directory.CreateDirectory(_appDataDir);

                var config = new
                {
                    ReportDirectory = normalizedReportDirectory,
                    Language = CurrentLanguage,
                    AllowExternalNetworkTests = AllowExternalNetworkTests, // FIX 7
                    EnableHardwareMonitoring = EnableHardwareMonitoring
                };

                var jsonContent = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, jsonContent, new UTF8Encoding(true)); // UTF-8 with BOM for French accents
                
                IsSettingsDirty = false;
                App.LogMessage("Paramètres sauvegardés");
                StatusMessage = GetString("StatusSettingsSaved");
                var activeTheme = NormalizeThemeCode(App.GetCurrentTheme());
                if (!string.Equals(activeTheme, normalizedTheme, StringComparison.OrdinalIgnoreCase))
                {
                    App.ApplyTheme(normalizedTheme);
                }
                CurrentTheme = normalizedTheme;
                _reportDirectory = normalizedReportDirectory;
                OnPropertyChanged(nameof(ReportDirectory));
                _selectedTheme = AvailableThemes.FirstOrDefault(t => t.Code == normalizedTheme);
                OnPropertyChanged(nameof(SelectedTheme));
                OnPropertyChanged(nameof(SelectedThemeDescription));
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
                    var normalizedReportDirectory = NormalizeReportDirectory(ReportDirectory);
                    Directory.CreateDirectory(_appDataDir);

                    var config = new
                    {
                        ReportDirectory = normalizedReportDirectory,
                        Language = CurrentLanguage,
                        AllowExternalNetworkTests = AllowExternalNetworkTests,
                        EnableHardwareMonitoring = EnableHardwareMonitoring
                    };

                    var jsonContent = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_configPath, jsonContent, new UTF8Encoding(true));
                    App.LogMessage("Paramètres sauvegardés (async)");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"Erreur sauvegarde paramètres (async): {ex.Message}");
                }
            });
        }

        private static string NormalizeThemeCode(string? themeCode)
        {
            return ThemeDefinitions.Resolve(themeCode).Code;
        }

        private void LoadSettings()
        {
            try
            {
                _isLoadingSettings = true;

                var configPath = GetExistingPath(_configPath, _legacyConfigPath);
                if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
                {
                    var jsonContent = File.ReadAllText(configPath, Encoding.UTF8);
                    var jsonDoc = JsonDocument.Parse(jsonContent);
                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("ReportDirectory", out var reportDirEl))
                    {
                        _reportDirectory = NormalizeReportDirectory(reportDirEl.GetString());
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

                    if (root.TryGetProperty("EnableHardwareMonitoring", out var hwMonEl))
                    {
                        _enableHardwareMonitoring = hwMonEl.GetBoolean();
                    }
                }
                else
                {
                    // Valeur par défaut
                    _reportDirectory = _reportsDir;
                }

                _reportDirectory = NormalizeReportDirectory(_reportDirectory);

                OnPropertyChanged(nameof(ReportDirectory));
                OnPropertyChanged(nameof(AllowExternalNetworkTests));
                OnPropertyChanged(nameof(EnableHardwareMonitoring));
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
                    var normalized = TextEncodingNormalizer.NormalizeIfCorrupted(value);
                    EncodingCorruptionWatcher.CheckAndLog(normalized, $"localized.{CurrentLanguage}.{key}");
                    return normalized;
                }

                if (_localizedStrings.TryGetValue("fr", out var fallback) &&
                    fallback.TryGetValue(key, out var fallbackValue))
                {
                    var normalized = TextEncodingNormalizer.NormalizeIfCorrupted(fallbackValue);
                    EncodingCorruptionWatcher.CheckAndLog(normalized, $"localized.fr.{key}");
                    return normalized;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur GetString pour '{key}': {ex.Message}");
            }

            return TextEncodingNormalizer.NormalizeIfCorrupted(key);
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
                nameof(LiveFeedPauseLabel),
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
                nameof(ScoreLegendText),
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
                nameof(RenameMenuText),
                nameof(DeleteMenuText),
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



        private static readonly Regex _progressBarPattern = new Regex(@"\[#+\s+\d+%\]", RegexOptions.Compiled);

        /// <summary>
        /// Regex pour parser les messages structurés : [TYPE] Section | Detail
        /// Exemples :
        ///   [PROGRESS] Registre | 14/35 | 40%
        ///   [STATUS] Registre | Collecte en cours...
        ///   [DONE] Registre | OK
        ///   [ERROR] Registre | Accès refusé
        /// </summary>
        private static readonly Regex _structuredPattern = new Regex(
            @"^\[(?<type>PROGRESS|STATUS|DONE|ERROR|WARN|INFO|SECTION)\]\s*(?<section>[^|]+?)(?:\s*\|\s*(?<rest>.+))?$",
            RegexOptions.Compiled);


        private static KernelPowerData ExtractKernelPowerData(DiagnosticsSignals.DiagnosticSignalsResult? signalsResult)
        {
            var data = new KernelPowerData();
            if (signalsResult == null) return data;

            if (!signalsResult.Signals.TryGetValue("driverStability", out var driverStabSignal))
                return data;
            if (driverStabSignal?.Value is not DiagnosticsSignals.Collectors.DriverStabilityResult stab)
                return data;

            data.Kp1Count30d = stab.KernelPower1Count30d;
            data.Kp41Count30d = stab.KernelPower41Count30d;

            foreach (var ev in stab.LastEvents
                .Where(e => e.Type.StartsWith("KernelPower", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Time))
            {
                var isKp41 = ev.Type.Contains("41", StringComparison.OrdinalIgnoreCase);
                data.Events.Add(new KernelPowerEventItem
                {
                    Date = FormatEventDate(ev.Time),
                    EventId = isKp41 ? "41" : "1",
                    Type = ev.Type,
                    Details = ev.Details ?? string.Empty,
                    Severity = isKp41 ? "Critique" : "Information"
                });
            }

            // Action suggestion based on criticality
            if (stab.KernelPower41Count30d > 0)
                data.ActionSuggestion = "Des arrêts brutaux ont été détectés (ID 41). Vérifiez l'alimentation (câble, onduleur), la RAM (memtest86) et les températures CPU/GPU sous charge.";
            else if (stab.KernelPower1Count30d > 0)
                data.ActionSuggestion = "Événements de type 'changement d'alimentation' (ID 1) : informatifs, liés aux cycles veille/réveil. Aucune action requise sauf si répétés avec erreurs associées.";

            return data;
        }

        private static string FormatEventDate(string isoDate)
        {
            if (DateTime.TryParse(isoDate, out var dt))
                return dt.ToString("dd/MM/yyyy HH:mm");
            return isoDate;
        }

        /// <summary>
        /// Facteur d'approche exponentielle pour le lissage.
        /// À chaque tick (50ms), on parcourt ~8% de la distance restante.
        /// ~20 FPS visuels → progression fluide, décélérante, sans bond visuel.
        /// SmoothMinIncrement assure qu'on ne stagne pas (progression lente continue).
        /// </summary>
        private const double SmoothEasingFactor = 0.08;
        private const double SmoothMinIncrement = 0.05;


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
            OnPropertyChanged(nameof(HasNoScanHistory));
            OnPropertyChanged(nameof(IsHistoryListStateVisible));
            OnPropertyChanged(nameof(IsHistoryEmptyStateVisible));
            OnPropertyChanged(nameof(IsHistoryErrorStateVisible));
            ArchivedScanHistoryView.Refresh();
            CommandManager.InvalidateRequerySuggested();
        }

    }

    /// <summary>
    /// Élément d'historique de scan
    /// </summary>
    public class ScanHistoryItem : System.ComponentModel.INotifyPropertyChanged
    {
        public DateTime ScanDate { get; set; }
        public int Score { get; set; }
        public string Grade { get; set; } = "N/A";
        public ScanResult? Result { get; set; }

        // --- Disk-persistence fields (populated from ScanMeta on startup load) ---
        public string RunId { get; set; } = string.Empty;
        public PCDiagnosticPro.Models.ScanStatus Status { get; set; } = PCDiagnosticPro.Models.ScanStatus.Success;
        public string? CombinedJsonPath { get; set; }
        public string? ErrorSummary { get; set; }
        public double DurationSeconds { get; set; }
        public bool HasErrorSummary => !string.IsNullOrWhiteSpace(ErrorSummary);

        // Status display helpers for XAML
        public string StatusDisplay => Status switch
        {
            PCDiagnosticPro.Models.ScanStatus.Success   => string.Empty,
            PCDiagnosticPro.Models.ScanStatus.Partial   => "⚠ Partiel",
            PCDiagnosticPro.Models.ScanStatus.Failed    => "✕ Échoué",
            PCDiagnosticPro.Models.ScanStatus.Cancelled => "⊘ Annulé",
            PCDiagnosticPro.Models.ScanStatus.Running   => "⟳ En cours",
            _ => string.Empty
        };
        public Visibility StatusBadgeVisibility =>
            Status != PCDiagnosticPro.Models.ScanStatus.Success ? Visibility.Visible : Visibility.Collapsed;

        public string DurationDisplay => DurationSeconds > 0
            ? $"{(int)DurationSeconds / 60:D1}m{(int)DurationSeconds % 60:D2}s"
            : string.Empty;
        
        // Nom d'affichage personnalisable (sans modifier le chemin de fichier)
        private string? _customDisplayName;
        public string? CustomDisplayName 
        { 
            get => _customDisplayName;
            set 
            {
                if (_customDisplayName != value)
                {
                    _customDisplayName = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CustomDisplayName)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasCustomName)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SubtitleDisplay)));
                }
            }
        }
        
        // Affiche le nom custom s'il existe, sinon la date par défaut
        public string DisplayName => string.IsNullOrWhiteSpace(CustomDisplayName) 
            ? DateDisplay 
            : CustomDisplayName;
        
        // Sous-titre : si renommé, afficher la date originale + score ; sinon juste le score
        public bool HasCustomName => !string.IsNullOrWhiteSpace(CustomDisplayName);
        public string SubtitleDisplay => HasCustomName
            ? $"{DateDisplay} - {ScoreDisplay}"
            : ScoreDisplay;
        
        // Mode édition inline (toggle entre TextBlock et TextBox)
        private bool _isRenaming;
        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                if (_isRenaming != value)
                {
                    _isRenaming = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsRenaming)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(RenameBoxVisibility)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayNameVisibility)));
                }
            }
        }
        public Visibility RenameBoxVisibility => _isRenaming ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DisplayNameVisibility => _isRenaming ? Visibility.Collapsed : Visibility.Visible;

        public string DateDisplay => ScanDate.ToString("dd-MM-yyyy HH:mm", CultureInfo.CurrentCulture);
        public string DayDisplay => ScanDate.ToString("dd", CultureInfo.CurrentCulture);
        public string MonthYearDisplay => ScanDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        public string ScoreDisplay => $"{Score}/100 ({Grade})";
        
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    
    /// <summary>
    /// T?CHE 6: Item pour affichage tableau des processus (Top RAM / Top CPU)
    /// </summary>
    public class ProcessDisplayItem
    {
        public int Rank { get; set; }
        public string ProcessName { get; set; } = "";
        public double RamUsedMB { get; set; }
        public string RamUsedDisplay { get; set; } = "";
        public double RamPercent { get; set; }
        public double CpuPercent { get; set; }
        public string CpuDisplay { get; set; } = "";
        
        /// <summary>Affichage formaté pour le tableau</summary>
        public string RamPercentDisplay => RamPercent > 0 ? $"{RamPercent:F1}%" : "-";
    }
    
    /// <summary>Kernel Power event details used by KernelPowerInfoWindow.</summary>
    public class KernelPowerEventItem
    {
        public string Date { get; set; } = "";
        public string EventId { get; set; } = "";
        public string Type { get; set; } = "";
        public string Details { get; set; } = "";
        public string Severity { get; set; } = "";
    }

    /// <summary>Aggregated Kernel Power data exposed to the UI for the detail window.</summary>
    public class KernelPowerData
    {
        public int Kp1Count30d { get; set; }
        public int Kp41Count30d { get; set; }
        public List<KernelPowerEventItem> Events { get; set; } = new();
        public string ActionSuggestion { get; set; } = "";
    }

    /// <summary>
    /// Entrée du live feed avec niveau structuré, badge coloré, section, et détail.
    /// Parsée depuis les messages [TYPE] Section | Detail émis par PowerShell et les phases C#.
    /// </summary>
    public class LiveFeedEntry
    {
        public string DisplayText { get; set; } = "";
        public string RawMessage { get; set; } = "";

        // Type structuré (parsé depuis [PROGRESS], [STATUS], [DONE], etc.)
        public string EntryType { get; set; } = "INFO";
        public string Section { get; set; } = "";
        public string Detail { get; set; } = "";
        public bool IsAmbient { get; set; }

        // Pour les entrées PROGRESS
        public int Current { get; set; }
        public int Total { get; set; }
        public int Percent { get; set; }

        // Propriétés calculées pour le XAML
        public bool IsError => EntryType == "ERROR";
        public bool IsWarning => EntryType == "WARN";
        public bool IsProgress => EntryType == "PROGRESS";
        public bool IsDone => EntryType == "DONE";
        public bool IsStatus => EntryType == "STATUS";

        // Timestamp séparé pour affichage en colonne
        public string Timestamp { get; set; } = "";

        // Badge texte pour affichage
        public string Badge => EntryType switch
        {
            "PROGRESS" => "RUN",
            "STATUS" => "RUN",
            "DONE" => "OK",
            "ERROR" => "ERR",
            "WARN" => "WARN",
            "SECTION" => "SEC",
            _ => "INFO"
        };

        // Couleur du badge (bindable)
        public Brush BadgeForeground => EntryType switch
        {
            "DONE" => new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)),
            "ERROR" => new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            "WARN" => new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)),
            _ => new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17))
        };

        public Brush BadgeBackground => EntryType switch
        {
            "DONE" => new SolidColorBrush(Color.FromRgb(0x2E, 0xD5, 0x73)),
            "ERROR" => new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49)),
            "WARN" => new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x02)),
            "PROGRESS" => new SolidColorBrush(Color.FromRgb(0xFF, 0x47, 0x57)),
            "STATUS" => new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF)),
            "SECTION" => new SolidColorBrush(Color.FromRgb(0xBC, 0x8C, 0xFF)),
            _ => new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58))
        };

        // Couleur du texte Detail selon type
        public Brush DetailForeground
        {
            get
            {
                if (IsAmbient)
                    return new SolidColorBrush(Color.FromRgb(0x6B, 0x88, 0xA8));

                return EntryType switch
                {
                    "DONE" => new SolidColorBrush(Color.FromRgb(0x2E, 0xD5, 0x73)),
                    "ERROR" => new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49)),
                    "WARN" => new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22)),
                    _ => new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E))
                };
            }
        }

        // Visibilité du pourcentage
        public Visibility PercentVisibility => IsProgress ? Visibility.Visible : Visibility.Collapsed;
        public string PercentDisplay => $"{Percent}%";
    }

    /// <summary>
    /// Niveau de sévérité d'une étape de scan structurée.
    /// </summary>
    public enum ScanStepSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Trace d'une étape de scan (section/sous-étape/progression) pour diagnostic interne.
    /// </summary>
    public sealed class ScanStepTrace
    {
        public string StepName { get; set; } = string.Empty;
        public string? SubStep { get; set; }
        public string? ProgressHint { get; set; }
        public DateTime Timestamp { get; set; }
        public ScanStepSeverity Severity { get; set; } = ScanStepSeverity.Info;
        public string? ScriptLine { get; set; }
    }
    
    /// <summary>
    /// Étape de progression du scan (PowerShell, Capteurs, etc.) avec état
    /// </summary>
    public class SectionPhaseItem : INotifyPropertyChanged
    {
        private string _status = "Pending";
        public string Status
        {
            get => _status;
            set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); 
                  PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusIcon)));
                  PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush))); }
        }
        public string Label { get; set; } = "";
        public string StatusIcon => _status switch { "Done" => "●", "Running" => "◐", "Warning" => "⚠", _ => "○" };
        public Brush StatusBrush => _status switch
        {
            "Done" => new SolidColorBrush(Color.FromRgb(0x2E, 0xD5, 0x73)),
            "Running" => new SolidColorBrush(Color.FromRgb(0xFF, 0x47, 0x57)),
            "Warning" => new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x02)),
            _ => new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E))
        };
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}



