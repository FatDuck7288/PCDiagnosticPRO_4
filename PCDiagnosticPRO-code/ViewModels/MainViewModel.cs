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
using PCDiagnosticPro.Views;

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
        private readonly DispatcherTimer _rainBitsTimer;
        private readonly Random _rainBitsRandom = new Random();
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

        // Résultat Security Info (C# - BitLocker, RDP, SMBv1)
        private SecurityInfoCollector.SecurityInfoResult? _lastSecurityInfo;

        // Service LibreSpeed pour tests de vitesse fiables
        private readonly LibreSpeedTestService _libreSpeedService = new();

        // Combined JSON data for applications window (from scan_result_combined.json)
        private string? _lastCombinedJsonContent;

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
                ["LiveFeedPauseLabel"] = "Pause défilement",
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
                ["ScoreRuleInitial"] = "• Score global = moyenne pondérée des 8 domaines",
                ["ScoreRuleCritical"] = "• Domaines : OS, CPU, GPU, RAM, Stockage, Réseau, Stabilité, Pilotes",
                ["ScoreRuleError"] = "• Pénalités : erreurs critiques (-30), erreurs (-10), avertissements (-5)",
                ["ScoreRuleWarning"] = "• Poids : Stockage (20%), OS/CPU/RAM (15%), GPU/Réseau/Stabilité (10%), Pilotes (5%)",
                ["ScoreRuleMin"] = "• Score collecte : qualité des données récupérées (plafond 70 si erreurs collecteur)",
                ["ScoreRuleMax"] = "• Score final = min(score global, score collecte)",
                ["ScoreGradeA"] = "• 💎 ≥ 95 : A+ (Excellent) | ❤️ ≥ 90 : A (Très bien)",
                ["ScoreGradeB"] = "• 👍 ≥ 80 : B+ (Bien) | 👌 ≥ 70 : B (Correct)",
                ["ScoreGradeC"] = "• ⚠️ ≥ 60 : C (Dégradé - Attention)",
                ["ScoreGradeD"] = "• 💀 ≥ 50 : D (Critique - Intervention)",
                ["ScoreGradeF"] = "• 🧨 < 50 : F (Critique - Urgence)",
                ["DeleteScanConfirmTitle"] = "Confirmation",
                ["DeleteScanConfirmMessage"] = "Voulez-vous vraiment supprimer ce scan ?",
                // Scan phases labels (localized)
                ["PhaseLabel_PowerShell"] = "Pilotes et périphériques",
                ["PhaseLabel_Capteurs"] = "Capteurs matériel",
                ["PhaseLabel_Compteurs"] = "Compteurs performances",
                ["PhaseLabel_Signaux"] = "Signaux diagnostiques",
                ["PhaseLabel_Telemetrie"] = "Télémétrie processus",
                ["PhaseLabel_Reseau"] = "Diagnostic réseau",
                ["PhaseLabel_Rapport"] = "Génération rapport",
                // Live feed messages for phases
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
                // Scan status fallbacks
                ["ScanStatus_Preparation"] = "Préparation...",
                ["ScanStatus_Finalization"] = "Finalisation..."
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
                ["DeleteScanConfirmMessage"] = "Do you really want to delete this scan?",
                // Scan phases labels (localized)
                ["PhaseLabel_PowerShell"] = "PowerShell",
                ["PhaseLabel_Capteurs"] = "Sensors",
                ["PhaseLabel_Compteurs"] = "Counters",
                ["PhaseLabel_Signaux"] = "Signals",
                ["PhaseLabel_Telemetrie"] = "Telemetry",
                ["PhaseLabel_Reseau"] = "Network",
                ["PhaseLabel_Rapport"] = "Report",
                // Live feed messages for phases
                ["LiveFeed_PhaseStart_PowerShell"] = "▶ Starting PowerShell scan...",
                ["LiveFeed_PhaseEnd_PowerShell"] = "✅ PowerShell scan completed",
                ["LiveFeed_PhaseStart_Capteurs"] = "🔧 Collecting hardware sensors...",
                ["LiveFeed_PhaseEnd_Capteurs"] = "✅ Sensors collected",
                ["LiveFeed_PhaseStart_Compteurs"] = "📊 Collecting performance counters...",
                ["LiveFeed_PhaseEnd_Compteurs"] = "✅ Counters collected",
                ["LiveFeed_PhaseStart_Signaux"] = "📡 Collecting diagnostic signals...",
                ["LiveFeed_PhaseEnd_Signaux"] = "✅ Signals collected",
                ["LiveFeed_PhaseStart_Telemetrie"] = "📈 Collecting process telemetry...",
                ["LiveFeed_PhaseEnd_Telemetrie"] = "✅ Telemetry collected",
                ["LiveFeed_PhaseStart_Reseau"] = "🌐 Network diagnostics in progress...",
                ["LiveFeed_PhaseEnd_Reseau"] = "✅ Network diagnostics completed",
                ["LiveFeed_PhaseStart_Rapport"] = "📄 Generating report...",
                ["LiveFeed_PhaseEnd_Rapport"] = "✅ Report generated",
                // Scan status fallbacks
                ["ScanStatus_Preparation"] = "Preparing...",
                ["ScanStatus_Finalization"] = "Finalizing..."
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
                ["DeleteScanConfirmMessage"] = "¿Desea eliminar este escaneo?",
                // Scan phases labels (localized)
                ["PhaseLabel_PowerShell"] = "PowerShell",
                ["PhaseLabel_Capteurs"] = "Sensores",
                ["PhaseLabel_Compteurs"] = "Contadores",
                ["PhaseLabel_Signaux"] = "Señales",
                ["PhaseLabel_Telemetrie"] = "Telemetría",
                ["PhaseLabel_Reseau"] = "Red",
                ["PhaseLabel_Rapport"] = "Informe",
                // Live feed messages for phases
                ["LiveFeed_PhaseStart_PowerShell"] = "▶ Iniciando escaneo PowerShell...",
                ["LiveFeed_PhaseEnd_PowerShell"] = "✅ Escaneo PowerShell completado",
                ["LiveFeed_PhaseStart_Capteurs"] = "🔧 Recopilando sensores de hardware...",
                ["LiveFeed_PhaseEnd_Capteurs"] = "✅ Sensores recopilados",
                ["LiveFeed_PhaseStart_Compteurs"] = "📊 Recopilando contadores de rendimiento...",
                ["LiveFeed_PhaseEnd_Compteurs"] = "✅ Contadores recopilados",
                ["LiveFeed_PhaseStart_Signaux"] = "📡 Recopilando señales de diagnóstico...",
                ["LiveFeed_PhaseEnd_Signaux"] = "✅ Señales recopiladas",
                ["LiveFeed_PhaseStart_Telemetrie"] = "📈 Recopilando telemetría de procesos...",
                ["LiveFeed_PhaseEnd_Telemetrie"] = "✅ Telemetría recopilada",
                ["LiveFeed_PhaseStart_Reseau"] = "🌐 Diagnóstico de red en progreso...",
                ["LiveFeed_PhaseEnd_Reseau"] = "✅ Diagnóstico de red completado",
                ["LiveFeed_PhaseStart_Rapport"] = "📄 Generando informe...",
                ["LiveFeed_PhaseEnd_Rapport"] = "✅ Informe generado",
                // Scan status fallbacks
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
                        _rainBitsTimer.Start();
                    else
                        _rainBitsTimer.Stop();
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
                    OnPropertyChanged(nameof(NetworkUploadMbps));
                    OnPropertyChanged(nameof(NetworkLatencyMs));
                    OnPropertyChanged(nameof(NetworkSpeedTier));
                    OnPropertyChanged(nameof(NetworkRecommendation));
                    OnPropertyChanged(nameof(NetworkDownloadColor));
                    OnPropertyChanged(nameof(NetworkUploadColor));
                    OnPropertyChanged(nameof(NetworkLatencyColor));
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
        public double? NetworkUploadMbps => HealthReport?.UdisReport?.UploadMbps;
        public double? NetworkLatencyMs => HealthReport?.UdisReport?.LatencyMs;
        public string NetworkSpeedTier => HealthReport?.UdisReport?.NetworkSpeedTier ?? "Non mesuré";
        public string NetworkRecommendation => HealthReport?.UdisReport?.NetworkRecommendation ?? "";
        
        // Couleur pour le débit Download (vert > 100, jaune 25-100, rouge < 25)
        public string NetworkDownloadColor => NetworkDownloadMbps switch
        {
            >= 100 => "#22C55E",  // Vert
            >= 25 => "#F59E0B",   // Orange
            > 0 => "#EF4444",     // Rouge
            _ => "#6B7280"        // Gris si non mesuré
        };
        
        // Couleur pour le débit Upload (vert > 50, jaune 10-50, rouge < 10)
        public string NetworkUploadColor => NetworkUploadMbps switch
        {
            >= 50 => "#22C55E",   // Vert
            >= 10 => "#F59E0B",   // Orange
            > 0 => "#EF4444",     // Rouge
            _ => "#6B7280"        // Gris si non mesuré
        };
        
        // Couleur pour la latence (vert < 30, jaune 30-100, rouge > 100)
        public string NetworkLatencyColor => NetworkLatencyMs switch
        {
            <= 30 => "#22C55E",   // Vert
            <= 100 => "#F59E0B",  // Orange
            > 100 => "#EF4444",   // Rouge
            _ => "#6B7280"        // Gris si non mesuré
        };
        
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
        
        /// <summary>
        /// TÂCHE 6: Top 5 processus RAM comme collection pour tableau visuel
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
                // RAM % not calculated here (would need total RAM from HealthReport)
                RamPercent = 0
            }) ?? Enumerable.Empty<ProcessDisplayItem>();
        
        /// <summary>
        /// TÂCHE 6: Top 5 processus CPU comme collection
        /// </summary>
        public IEnumerable<ProcessDisplayItem> Top5CpuProcesses =>
            _lastProcessTelemetry?.TopByCpu?.Take(5).Select((p, i) => new ProcessDisplayItem
            {
                Rank = i + 1,
                ProcessName = p.Name,
                CpuPercent = p.CpuPercent,
                CpuDisplay = $"{p.CpuPercent:F1}%"
            }) ?? Enumerable.Empty<ProcessDisplayItem>();
        
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
        /// Lancer le SpeedTest réseau (async, non bloquant).
        /// </summary>
        private ICommand? _runSpeedTestCommand;
        public ICommand RunSpeedTestCommand => _runSpeedTestCommand ??= new RelayCommand(async _ =>
        {
            if (IsSpeedTestRunning) return;
            IsSpeedTestRunning = true;
            try
            {
                App.LogMessage("[SpeedTest] Démarrage du test LibreSpeed...");
                
                // Utiliser LibreSpeed CLI en priorité
                var libreResult = await _libreSpeedService.RunTestAsync();
                
                if (libreResult.Success && HealthReport?.UdisReport != null)
                {
                    // Mettre à jour le UdisReport avec les résultats LibreSpeed
                    HealthReport.UdisReport.DownloadMbps = libreResult.DownloadMbps;
                    HealthReport.UdisReport.UploadMbps = libreResult.UploadMbps;
                    HealthReport.UdisReport.LatencyMs = libreResult.PingMs;
                    HealthReport.UdisReport.NetworkSpeedTier = libreResult.SpeedTier;
                    HealthReport.UdisReport.NetworkRecommendation = GetSpeedRecommendation(libreResult);
                    
                    App.LogMessage($"[SpeedTest] LibreSpeed OK: Down={libreResult.DownloadMbps:F1} Mbps, Up={libreResult.UploadMbps:F1} Mbps, Ping={libreResult.PingMs:F1} ms");
                    
                    // Sauvegarder le résultat en JSON pour inspection LLM
                    var jsonPath = await _libreSpeedService.SaveResultToJsonAsync(libreResult);
                    if (!string.IsNullOrEmpty(jsonPath))
                        App.LogMessage($"[SpeedTest] JSON sauvegardé: {jsonPath}");
                }
                else if (HealthReport?.UdisReport != null)
                {
                    // Fallback sur l'ancienne méthode HTTP
                    App.LogMessage($"[SpeedTest] LibreSpeed échoué ({libreResult.Error}), fallback HTTP...");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var updatedUdis = await UnifiedDiagnosticScoreEngine.AddNetworkSpeedTestAsync(HealthReport.UdisReport, cts.Token);
                    App.LogMessage($"[SpeedTest] Fallback: Download={updatedUdis.DownloadMbps:F1} Mbps");
                    
                    // Sauvegarder le résultat en JSON même en cas d'erreur
                    var jsonPath = await _libreSpeedService.SaveResultToJsonAsync(libreResult);
                    if (!string.IsNullOrEmpty(jsonPath))
                        App.LogMessage($"[SpeedTest] JSON sauvegardé (avec erreur): {jsonPath}");
                }
                
                // Notifier la UI
                OnPropertyChanged(nameof(NetworkDownloadMbps));
                OnPropertyChanged(nameof(NetworkUploadMbps));
                OnPropertyChanged(nameof(NetworkLatencyMs));
                OnPropertyChanged(nameof(NetworkSpeedTier));
                OnPropertyChanged(nameof(NetworkRecommendation));
                OnPropertyChanged(nameof(NetworkDownloadColor));
                OnPropertyChanged(nameof(NetworkUploadColor));
                OnPropertyChanged(nameof(NetworkLatencyColor));
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
        public ObservableCollection<LiveFeedEntry> LiveFeedEntries { get; } = new ObservableCollection<LiveFeedEntry>();
        
        /// <summary>Pluie de 0 et 1 pour le fond du live feed (style matrix), animée en temps réel.</summary>
        public ObservableCollection<string> LiveFeedBackgroundBits { get; } = new ObservableCollection<string>();

        private void InitializeRainBits()
        {
            LiveFeedBackgroundBits.Clear();
            for (int i = 0; i < 240; i++)
                LiveFeedBackgroundBits.Add(_rainBitsRandom.Next(2) == 0 ? "0" : "1");
        }

        private void TickRainBits()
        {
            if (LiveFeedBackgroundBits.Count != 240) return;
            // Décaler d'une ligne vers le bas : lignes 1..11 deviennent 0..10, nouvelle ligne en 11
            var next = new List<string>();
            for (int i = 20; i < 240; i++)
                next.Add(LiveFeedBackgroundBits[i]);
            for (int i = 0; i < 20; i++)
                next.Add(_rainBitsRandom.Next(2) == 0 ? "0" : "1");
            LiveFeedBackgroundBits.Clear();
            foreach (var s in next)
                LiveFeedBackgroundBits.Add(s);
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
            if (f == "Tout") return true;
            if (f == "Erreurs" && e.IsError) return true;
            if (f == "Avertissements" && e.IsWarning) return true;
            if (f == "Important" && (e.IsError || e.IsWarning)) return true;
            return false;
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
            : (IsScanning ? (ProgressPercent >= 90 ? GetString("ScanStatus_Finalization") : GetString("ScanStatus_Preparation")) : "—");
        
        public ObservableCollection<SectionPhaseItem> SectionPhases { get; } = new ObservableCollection<SectionPhaseItem>();
        
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
        
        // Commands for detail windows (Drivers and Applications)
        public ICommand OpenDriversDetailsCommand { get; }
        public ICommand OpenAppsDetailsCommand { get; }
        /// <summary>Ouvre une fenêtre de liste (Périph. audio, Imprimantes, Obsolètes) selon le paramètre (Key).</summary>
        public ICommand OpenListDetailCommand { get; }
        
        // Command for collector errors details
        public ICommand ShowCollectorErrorsCommand { get; }

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

            _rainBitsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(220)
            };
            _rainBitsTimer.Tick += (s, e) => TickRainBits();
            InitializeRainBits();

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
            
            // Commands for detail windows
            OpenDriversDetailsCommand = new RelayCommand(OpenDriversDetails, () => _lastDriverInventory?.Available == true);
            OpenAppsDetailsCommand = new RelayCommand(OpenAppsDetails, () => !string.IsNullOrEmpty(_lastCombinedJsonContent));
            OpenListDetailCommand = new RelayCommand(OpenListDetail, _ => true);
            
            // Command for collector errors details
            ShowCollectorErrorsCommand = new RelayCommand(ShowCollectorErrorsDetails, () => IsCollectionPartialOrFailed);

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
                _cancelHandled = false;

                _scanStopwatch.Restart();
                _liveFeedTimer.Start();
                _scanStartTime = DateTimeOffset.Now;
                _jsonPathFromOutput = null;

                AddLiveFeedItem(GetString("LiveFeed_PhaseStart_PowerShell"));

                App.LogMessage("Démarrage du scan");
                App.LogMessage($"Start scan timestamp: {_scanStartTime:O}");
                App.LogMessage($"Scan output directory: {outputDir}");
                // Phase 0 (PowerShell) starts - progress = 0, will increase as it runs
                UpdateProgress(GetProgressForPhaseInProgress(0, 0.1), GetString("PhaseLabel_PowerShell"));

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
                // Phase 0 PowerShell runs - ceiling at end of phase 0 (~14%)
                StartScanProgressTimer(GetProgressForCompletedPhase(0) - 1);
                UpdateProgress(GetProgressForPhaseInProgress(0, 0.2), GetString("PhaseLabel_PowerShell"));
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

                _scanStopwatch.Stop();
                _liveFeedTimer.Stop();
                // Ne pas arrêter le timer ici : progression graduelle jusqu'à 100%

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

                AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_PowerShell"));
                App.LogMessage($"Scan terminé. ExitCode={exitCode}");
                // Phase 0 done = 14%, plafond suivant = 28% (progression graduelle)
                UpdateProgress(GetProgressForCompletedPhase(0), GetString("PhaseLabel_PowerShell"));
                SetSectionPhase(0, "Done");
                UpdateScanProgressCeiling(GetProgressForCompletedPhase(1));
                
                // === OPTIMISATION: Phases 1-3 en parallèle (Sensors, Counters, Signals) ===
                AddLiveFeedItem("Collecte données système en parallèle...");
                SetSectionPhase(1, "Running");
                SetSectionPhase(2, "Running");
                SetSectionPhase(3, "Running");

                HardwareSensorsResult sensorsResult;
                PerfCounterCollector.PerfCounterResult? perfResult = null;
                DiagnosticsSignals.DiagnosticSignalsResult? signalsResult = null;

                try
                {
                    App.LogMessage("[Parallel Collection] Démarrage collecte parallèle Sensors/Counters/Signals");
                    
                    // Préparer le SignalsOrchestrator avant le parallélisme
                    var signalsOrchestrator = new DiagnosticsSignals.SignalsOrchestrator();
                    signalsOrchestrator.SetAllowExternalNetworkTests(_allowExternalNetworkTests);
                    
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
                    
                    _lastSensorsResult = sensorsResult;
                    _lastPerfCounterResult = perfResult;
                    _lastDiagnosticSignals = signalsResult;
                    
                    var (avail, total) = sensorsResult.GetAvailabilitySummary();
                    App.LogMessage($"[Parallel Collection] Terminé: Sensors {avail}/{total}, Counters OK, Signals {signalsResult?.SuccessCount ?? 0}");
                    
                    // Check for security blocking
                    if (sensorsResult.BlockedBySecurity)
                    {
                        App.LogMessage($"[Sensors] ⚠️ BLOCKED BY SECURITY: {sensorsResult.BlockingMessage}");
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
                UpdateProgress(GetProgressForCompletedPhase(3), "Collecte système terminée");
                UpdateScanProgressCeiling(GetProgressForCompletedPhase(4));
                
                // Phase 4: Télémetrie (Process Telemetry)
                AddLiveFeedItem(GetString("LiveFeed_PhaseStart_Telemetrie"));
                SetSectionPhase(4, "Running");

                // === PHASE 2D: Process Telemetry C# Fallback (si PS a échoué) ===
                try
                {
                    UpdateProgress(GetProgressForPhaseInProgress(4, 0.5), GetString("PhaseLabel_Telemetrie"));
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
                    AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_Telemetrie"));
                    UpdateProgress(GetProgressForCompletedPhase(4), GetString("PhaseLabel_Telemetrie"));
                    SetSectionPhase(4, "Done");
                    UpdateScanProgressCeiling(GetProgressForCompletedPhase(5));
                    
                    // Phase 5: Réseau (Network)
                    AddLiveFeedItem(GetString("LiveFeed_PhaseStart_Reseau"));
                    SetSectionPhase(5, "Running");
                    UpdateProgress(GetProgressForPhaseInProgress(5, 0.5), GetString("PhaseLabel_Reseau"));
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
                AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_Reseau"));
                UpdateProgress(GetProgressForCompletedPhase(5), GetString("PhaseLabel_Reseau"));
                SetSectionPhase(5, "Done");
                UpdateScanProgressCeiling(GetProgressForCompletedPhase(6));
                
                // Phase 6: Rapport (Report generation)
                AddLiveFeedItem(GetString("LiveFeed_PhaseStart_Rapport"));
                SetSectionPhase(6, "Running");

                // === PHASE 2F: Inventaire pilotes (C#) ===
                try
                {
                    UpdateProgress(GetProgressForPhaseInProgress(6, 0.2), GetString("PhaseLabel_Rapport"));
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

                // === PHASE 2H: Security Info (C# - BitLocker/RDP/SMBv1) ===
                try
                {
                    UpdateProgress(98, "Collecting security info...");
                    var securityCollector = new SecurityInfoCollector();
                    _lastSecurityInfo = await securityCollector.CollectAsync(_scanCts.Token);
                    App.LogMessage($"[SecurityInfo] Completed: BitLocker={_lastSecurityInfo.BitLockerStatus}, RDP={_lastSecurityInfo.RdpStatus}, SMBv1={_lastSecurityInfo.SmbV1Status}");
                }
                catch (Exception ex)
                {
                    _lastSecurityInfo = null;
                    App.LogMessage($"[SecurityInfo] Erreur: {ex.Message}");
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
            AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_Rapport"));
            UpdateProgress(100, GetString("PhaseLabel_Rapport"));
            SetSectionPhase(6, "Done");
            StopScanProgressTimer();
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
                    // FIX: Utiliser le JSON combiné (contient diagnostic_signals, network_diagnostics, sensors_csharp)
                    // au lieu du JSON PowerShell brut qui ne contient pas les données C#
                    var healthReportJsonContent = !string.IsNullOrEmpty(_lastCombinedJsonContent) 
                        ? _lastCombinedJsonContent 
                        : jsonContent;
                    
                    if (!string.IsNullOrEmpty(_lastCombinedJsonContent))
                    {
                        App.LogMessage("[HealthReport] Utilisation du JSON combiné (avec diagnostic_signals, network_diagnostics)");
                    }
                    else
                    {
                        App.LogMessage("[HealthReport] WARN: JSON combiné non disponible, fallback sur PS brut");
                    }
                    
                    // Passer les capteurs hardware pour injection dans EvidenceData
                    var healthReport = HealthReportBuilder.Build(
                        healthReportJsonContent,
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

                // ===== VALIDATION COMPLÉTUDE UI (NON-BLOQUANT) =====
                try
                {
                    using var validationDoc = JsonDocument.Parse(jsonContent);
                    var validationResult = UiCompletenessValidator.Validate(validationDoc.RootElement, HealthReport, _lastSensorsResult);
                    if (!validationResult.AllValid)
                    {
                        App.LogMessage($"[UiValidator] WARNINGS: {validationResult.CriticalWarnings.Count}");
                        // Log le rapport détaillé en mode debug
                        if (ComprehensiveEvidenceExtractor.DebugPathsEnabled)
                        {
                            App.LogMessage(UiCompletenessValidator.GenerateReport(validationResult));
                        }
                    }
                }
                catch (Exception valEx)
                {
                    App.LogMessage($"[UiValidator] Erreur non-bloquante: {valEx.Message}");
                }
                // ===== FIN VALIDATION UI =====

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
                    UpdatesCsharp = _lastWindowsUpdateResult,
                    SecurityInfoCsharp = _lastSecurityInfo
                };
                
                // === EXTRACTION DES NŒUDS EXPLICITES (missingData, metadata, findings, errors, sections, paths) ===
                ExtractExplicitNodes(doc.RootElement, combined, outputDir);

                var combinedPath = Path.Combine(outputDir, "scan_result_combined.json");
                var combinedJson = JsonSerializer.Serialize(combined, HardwareSensorsResult.JsonOptions);
                await File.WriteAllTextAsync(combinedPath, combinedJson, Encoding.UTF8);
                App.LogMessage($"Rapport combiné généré: {combinedPath} (schemaVersion={diagnosticSnapshot.SchemaVersion})");
                
                _combinedJsonPath = combinedPath;
                _lastCombinedJsonContent = combinedJson; // Store for detail windows
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

                // Parse the JSON to extract InstalledApplications and StartupPrograms sections
                JsonElement? appsData = null;
                JsonElement? startupData = null;
                
                try
                {
                    using var doc = JsonDocument.Parse(_lastCombinedJsonContent);
                    var root = doc.RootElement;
                    
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
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ListDetail] Erreur: {ex.Message}");
            }
        }

        private List<string> GetAudioDevicesFromJson()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(_lastCombinedJsonContent)) return list;
            try
            {
                using var doc = JsonDocument.Parse(_lastCombinedJsonContent);
                var root = doc.RootElement;
                if (!root.TryGetProperty("scan_powershell", out var ps) || !ps.TryGetProperty("sections", out var sections) ||
                    !sections.TryGetProperty("Audio", out var audio) || !audio.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                    return list;
                foreach (var dev in devices.EnumerateArray())
                {
                    var name = dev.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var status = dev.TryGetProperty("status", out var s) ? s.GetString() : null;
                    list.Add(string.IsNullOrEmpty(name) ? "—" : (string.IsNullOrEmpty(status) ? name : $"{name} — {status}"));
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
                using var doc = JsonDocument.Parse(_lastCombinedJsonContent);
                var root = doc.RootElement;
                if (!root.TryGetProperty("scan_powershell", out var ps) || !ps.TryGetProperty("sections", out var sections) ||
                    !sections.TryGetProperty("Printers", out var printers) || !printers.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("printers", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return list;
                foreach (var p in arr.EnumerateArray())
                {
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var isDefault = p.TryGetProperty("default", out var d) && (d.ValueKind == JsonValueKind.True || (d.ValueKind == JsonValueKind.Number && d.GetInt32() != 0));
                    list.Add(string.IsNullOrEmpty(name) ? "—" : (isDefault ? $"{name} [Défaut]" : name));
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
                    var name = d.DeviceName ?? "—";
                    var ver = d.DriverVersion ?? "?";
                    list.Add($"{cls}: {name.Trim()} v{ver}");
                }
            }
            if (list.Count == 0 && !string.IsNullOrEmpty(_lastCombinedJsonContent))
            {
                try
                {
                    using var doc = JsonDocument.Parse(_lastCombinedJsonContent);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("driver_inventory", out var inv) && inv.TryGetProperty("drivers", out var drivers) && drivers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in drivers.EnumerateArray())
                        {
                            var cls = d.TryGetProperty("deviceClass", out var c) ? c.GetString() ?? "" : "";
                            var name = d.TryGetProperty("deviceName", out var n) ? n.GetString()?.Trim() ?? "—" : "—";
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
        /// PARTIE 2: Fenêtre WPF structurée avec tableau (remplace MessageBox)
        /// </summary>
        private void ShowCollectorErrorsDetails()
        {
            try
            {
                var errors = HealthReport?.Errors ?? new List<Models.ScanErrorInfo>();
                var missing = HealthReport?.MissingData ?? new List<string>();
                var collectorErrors = HealthReport?.CollectorErrorsLogical ?? 0;

                var window = new Views.CollectorErrorsWindow(errors, missing, collectorErrors)
                {
                    Owner = Application.Current?.MainWindow
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ShowCollectorErrors] Erreur: {ex.Message}");
                var errors = HealthReport?.Errors ?? new List<Models.ScanErrorInfo>();
                var missing = HealthReport?.MissingData ?? new List<string>();
                var errLine = errors.Count == 0 ? "Erreurs détectées: 0" : "Erreurs: " + string.Join(" ; ", errors.Select(e => $"[{e.Section}] {e.Message ?? e.Code}"));
                var missLine = missing.Count == 0 ? "Données manquantes: 0" : "Données manquantes: " + string.Join(" ; ", missing);
                System.Windows.MessageBox.Show(
                    errLine + "\n" + missLine,
                    "Erreurs collecteur",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
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
                var timestamp = $"[{DateTime.Now:HH:mm:ss}]";
                var displayText = $"{timestamp} {item}";
                LiveFeedItems.Insert(0, displayText);
                while (LiveFeedItems.Count > 100)
                {
                    LiveFeedItems.RemoveAt(LiveFeedItems.Count - 1);
                }
                
                var level = InferLiveFeedLevel(item);
                var entry = new LiveFeedEntry
                {
                    DisplayText = displayText,
                    IsError = level == "Error",
                    IsWarning = level == "Warning"
                };
                LiveFeedEntries.Insert(0, entry);
                while (LiveFeedEntries.Count > 200)
                {
                    LiveFeedEntries.RemoveAt(LiveFeedEntries.Count - 1);
                }
                _filteredLiveFeedView?.Refresh();
            });
        }
        
        private static string InferLiveFeedLevel(string message)
        {
            if (string.IsNullOrEmpty(message)) return "Info";
            var m = message.ToUpperInvariant();
            if (m.Contains("ERROR") || m.Contains("ERREUR") || m.Contains("EXCEPTION") || m.Contains("ÉCHEC")) return "Error";
            if (m.Contains("WARN") || m.Contains("ATTENTION") || m.Contains("⚠")) return "Warning";
            return "Info";
        }
        
        // Constants for step-based progress (7 phases, each ~14.3%)
        private const int TOTAL_PHASES = 7;
        private const double PROGRESS_PER_PHASE = 100.0 / TOTAL_PHASES; // ~14.28%
        
        /// <summary>
        /// Get progress percentage for a completed phase (0-6)
        /// Phase 0 done = 14%, Phase 1 done = 28%, ..., Phase 6 done = 100%
        /// </summary>
        private int GetProgressForCompletedPhase(int phaseIndex)
        {
            return (int)Math.Round((phaseIndex + 1) * PROGRESS_PER_PHASE);
        }
        
        /// <summary>
        /// Get progress percentage for a phase in progress (partial)
        /// </summary>
        private int GetProgressForPhaseInProgress(int phaseIndex, double internalProgress = 0.5)
        {
            var baseProgress = phaseIndex * PROGRESS_PER_PHASE;
            var phaseContribution = PROGRESS_PER_PHASE * internalProgress;
            return (int)Math.Round(baseProgress + phaseContribution);
        }
        
        private void InitializeSectionPhases()
        {
            SectionPhases.Clear();
            // Use localized labels for phases
            var phaseKeys = new[] { "PowerShell", "Capteurs", "Compteurs", "Signaux", "Telemetrie", "Reseau", "Rapport" };
            foreach (var key in phaseKeys)
            {
                var label = GetString($"PhaseLabel_{key}");
                // Fallback to key if localization not found
                if (string.IsNullOrEmpty(label) || label.StartsWith("PhaseLabel_"))
                    label = key;
                SectionPhases.Add(new SectionPhaseItem { Label = label, Status = "Pending" });
            }
        }
        
        private void SetSectionPhase(int index, string status)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (index >= 0 && index < SectionPhases.Count)
                {
                    SectionPhases[index].Status = status;
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
            // Ne pas écraser la section courante par le timer : garder la vraie section (PowerShell ou C#).
            if (reason != "Progression timer")
            {
                CurrentSection = reason;
                OnPropertyChanged(nameof(CurrentSection));
                OnPropertyChanged(nameof(CurrentSectionDisplay));
            }
            App.LogMessage($"Progress update: {ProgressPercent}% - {reason}");
        }

        /// <summary>Met à jour uniquement le pourcentage de progression (pour le timer), sans toucher à la section courante.</summary>
        private void SetProgressPercentOnly(int percent)
        {
            var normalized = Math.Max(0, Math.Min(100, percent));
            if (normalized < ProgressPercent) return;
            Progress = normalized;
            ProgressPercent = normalized;
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressPercent));
        }

        private void StartScanProgressTimer(int ceiling)
        {
            _scanProgressCeiling = Math.Max(0, Math.Min(100, ceiling));
            _scanProgressTimer.Start();
        }

        /// <summary>
        /// Met à jour le plafond de progression pour la phase en cours (timer continue, progression graduelle).
        /// </summary>
        private void UpdateScanProgressCeiling(int newCeiling)
        {
            _scanProgressCeiling = Math.Max(ProgressPercent, Math.Min(100, newCeiling));
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

            // Incrémenter uniquement le pourcentage, sans écraser la section courante (PowerShell ou C#).
            var increment = 1;
            SetProgressPercentOnly(Math.Min(_scanProgressCeiling, ProgressPercent + increment));
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
    
    /// <summary>
    /// TÂCHE 6: Item pour affichage tableau des processus (Top RAM / Top CPU)
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
    
    /// <summary>
    /// Entrée du live feed avec niveau (Info/Warning/Error) pour filtrage et couleur
    /// </summary>
    public class LiveFeedEntry
    {
        public string DisplayText { get; set; } = "";
        public bool IsError { get; set; }
        public bool IsWarning { get; set; }
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
