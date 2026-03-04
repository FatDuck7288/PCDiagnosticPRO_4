
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PCDiagnosticPro.AI;
using PCDiagnosticPro.AI.Interfaces;
using PCDiagnosticPro.AI.Models;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.Views;

namespace PCDiagnosticPro.ViewModels
{
    public class ChatSupportViewModel : ViewModelBase
    {
        private readonly AiSettings _settings;
        private readonly SafetyPolicyEngine _safety;
        private readonly ContextPackBuilder _contextBuilder;
        private readonly PowerShellExecutor _powerShellExecutor;
        private readonly Action<string> _log;
        private readonly Func<string, CombinedScanResult?> _loadCombinedFromFile;
        private readonly Dispatcher? _uiDispatcher;
        private readonly bool _useDispatcher;
        private LlmRuntimeHost _runtimeHost;
        private readonly ModelDownloaderService _modelDownloader;
        private readonly ApiSecretProtector _apiSecretProtector = new();

        private ILlmClient? _client;
        private ILlmModelLoader? _modelLoader;
        private AiOrchestrator? _orchestrator;
        private CombinedScanResult? _loadedCombined;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _qwen3DownloadCts;
        private CancellationTokenSource? _qwenDownloadCts;
        private AutoFixEligibilityResult? _autoFixGate;
        private string _settingsPath;
        private CancellationTokenSource? _loadRunsCts;

        private bool _isStreaming;
        private bool _isAnalyzing;
        private bool _isExecutingAutofix;
        private bool _scriptGeneratedByPipeline;
        private bool _cancelRequestedByUser;
        private bool _hasLoadFailure;
        private string _loadFailureMessage = string.Empty;
        private string _loadFailureDetails = string.Empty;
        private string _loadFailureLogPath = string.Empty;
        private bool _isLoadingRuns;
        private int _analysisProgress;
        private bool _isAnalysisIndeterminate;
        private string _analysisStatusText = string.Empty;
        private string _inputText = string.Empty;
        private string _modelStatusMessage = "IA initializing...";
        private string _qwen3DownloadStatus = string.Empty;
        private ModelStatus _modelStatus = ModelStatus.NotInstalled;
        private int _qwen3DownloadProgress;
        private ScanRunEntry? _selectedRun;
        private ContextPack? _loadedContext;
        private string _contextSources = string.Empty;
        private RunAnalysisHeader? _currentRunHeader;
        private JudgeResult? _securityVerdict;
        private string _generatedScript = string.Empty;
        private bool _isDownloadingQwen3;
        private int _isInitializingModel = 0;

        private AiRunReport? _lastReport;
        private System.Windows.Threading.DispatcherTimer? _agentElapsedTimer;
        /// <summary>
        /// Running case summary — updated deterministically after each assistant response.
        /// Injected as {CASE_SUMMARY} in the prompt. Max 600 words.
        /// </summary>
        private string _caseSummary = string.Empty;
        private readonly Dictionary<string, CachedRunContext> _contextCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _contextCacheLock = new();
        private static readonly Regex LocalPathRegex = new(
            @"([A-Za-z]:[\\/][^\s\""']+|\\\\[^\s\""']+)",
            RegexOptions.Compiled);
        private static readonly JsonSerializerOptions _indentedJsonOptions = new() { WriteIndented = true };
        private static readonly Regex ThinkTokenRegex = new(@"<\/?think>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const int ChatSummaryMaxLines = 10;
        private const int DefaultRecentMessagesToKeep = 6;
        private const int MinimumRecentMessagesToKeep = 2;
        private const int QualityRetryMinimumOverlapPhrases = 2;
        private string _runSummaryCached = string.Empty;
        private string _runSummaryCachedKey = string.Empty;
        private string _modelLimitNotice = string.Empty;
        private readonly Dictionary<string, ConversationState> _conversationStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _conversationStateLock = new();

        public ChatSupportViewModel()
            : this(
                settings: null,
                safety: null,
                contextBuilder: null,
                powerShellExecutor: null,
                llmClient: null,
                modelLoader: null,
                autoInitialize: true,
                autoLoadRuns: true,
                logSink: null,
                loadCombinedOverride: null)
        {
        }

        internal ChatSupportViewModel(
            AiSettings? settings,
            SafetyPolicyEngine? safety,
            ContextPackBuilder? contextBuilder,
            PowerShellExecutor? powerShellExecutor,
            ILlmClient? llmClient,
            ILlmModelLoader? modelLoader,
            bool autoInitialize,
            bool autoLoadRuns,
            Action<string>? logSink,
            Func<string, CombinedScanResult?>? loadCombinedOverride,
            ModelDownloaderService? modelDownloader = null)
        {
            _settings = settings ?? AiSettingsLoader.LoadOrCreate();
            _settingsPath = AiSettingsLoader.GetEffectiveConfigPath();
            _safety = safety ?? new SafetyPolicyEngine(_settings);
            _contextBuilder = contextBuilder ?? new ContextPackBuilder(_settings);
            _powerShellExecutor = powerShellExecutor ?? new PowerShellExecutor();
            _log = logSink ?? App.LogMessage;
            _loadCombinedFromFile = loadCombinedOverride ?? ContextPackBuilder.LoadFromFile;
            _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);
            _useDispatcher = Application.Current?.Dispatcher != null && _uiDispatcher != null;
            _runtimeHost = LlmRuntimeHost.GetOrCreate(_settings);
            _modelDownloader = modelDownloader ?? new ModelDownloaderService();

            _client = llmClient;
            _modelLoader = modelLoader;

            SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, _ => CanSend);
            CancelCommand = new RelayCommand(_ => Cancel(), _ => CanCancel);
            AnalyseRunCommand = new AsyncRelayCommand(AnalyseRunAsync, _ => CanAnalyseRun);
            GenerateAutoFixScriptCommand = new AsyncRelayCommand(GenerateAutoFixScriptAsync, _ => CanGenerateAutoFixScript);
            LoadAvailableRunsCommand = new AsyncRelayCommand(LoadAvailableRunsAsync);
            ClearChatCommand = new RelayCommand(_ => ClearChat(), _ => CanClearChat);
            CopyLastAssistantMessageCommand = new RelayCommand(_ => CopyLastAssistantMessage(), _ => CanCopyLastAssistantMessage);
            CopyMessageCommand = new RelayCommand(param => CopyMessage(param as string ?? string.Empty));
            ToggleMessageExpandCommand = new RelayCommand(param => ToggleMessageExpand(param as ChatMessage));
            OpenMessageWindowCommand = new RelayCommand(param => OpenMessageWindow(param as ChatMessage));
            OpenLoadFailureLogCommand = new RelayCommand(_ => OpenLoadFailureLog(), _ => !string.IsNullOrWhiteSpace(LoadFailureLogPath));
            CopyLoadFailureLogPathCommand = new RelayCommand(_ => CopyLoadFailureLogPath(), _ => !string.IsNullOrWhiteSpace(LoadFailureLogPath));

            ChooseModelCommand = new AsyncRelayCommand(ChooseModelAsync);
            DownloadQwen3Command = new AsyncRelayCommand(DownloadQwen3Async, _ => CanDownloadQwen3);
            DownloadQwenCoderCommand = new AsyncRelayCommand(DownloadQwenCoderAsync, _ => CanDownloadQwenCoder);
            OpenModelsFolderCommand = new RelayCommand(_ => OpenModelsFolder());
            ShowInstallGuideCommand = new RelayCommand(_ => ShowInstallGuide());

            AutoFixCommand = new AsyncRelayCommand(ExecuteAutoFixAsync, _ => CanAutoFix);
            CopyPipelineLogCommand = new RelayCommand(_ => CopyPipelineDiagLog());
            OpenLiveScriptComposerCommand = new RelayCommand(_ => OpenLiveScriptComposer(), _ => _lastReport != null);
            OpenAutoFixLogsFolderCommand = new RelayCommand(_ => OpenAutoFixLogsFolder(), _ => CanOpenAutoFixLogsFolder);
            AddApiCommand = new RelayCommand(_ => OpenAddApiModal(), _ => !IsStreaming && !IsAnalyzing && !IsExecutingAutofix);
            ToggleInferenceModeCommand = new RelayCommand(_ => ToggleInferenceMode(), _ => !IsStreaming && !IsAnalyzing && !IsExecutingAutofix);

            Messages.CollectionChanged += OnMessagesCollectionChanged;
            PipelineLogs.CollectionChanged += OnPipelineLogsCollectionChanged;

            if (_client?.IsReady == true)
            {
                ModelStatus = ModelStatus.Ready;
                ModelStatusMessage = _client.StatusMessage;
            }

            if (autoInitialize)
            {
                _ = InitializeModelAsync(reload: false);
            }

            if (autoLoadRuns)
            {
                _ = LoadAvailableRunsAsync();
            }

        }

        public ObservableCollection<ChatMessage> Messages { get; } = new();
        public ObservableCollection<ScanRunEntry> AvailableRuns { get; } = new();
        public ObservableCollection<AgentStepLog> PipelineLogs { get; } = new();
        public ObservableCollection<ActionPlanItem> ManualPlanItems { get; } = new();
        public ObservableCollection<ActionPlanItem> AutoFixPlanItems { get; } = new();

        /// <summary>4-slot agent timeline cards for the pipeline UI.</summary>
        public ObservableCollection<AgentCardViewModel> AgentCards { get; } = new()
        {
            new AgentCardViewModel { AgentName = "Codeur", AgentIcon = "①" },
            new AgentCardViewModel { AgentName = "Reviewer", AgentIcon = "②" },
            new AgentCardViewModel { AgentName = "Refiner", AgentIcon = "③" },
            new AgentCardViewModel { AgentName = "Judge", AgentIcon = "④" },
        };

        private string _agentScriptPreview = string.Empty;
        public string AgentScriptPreview
        {
            get => _agentScriptPreview;
            private set => SetProperty(ref _agentScriptPreview, value);
        }

        private bool _showAgentTimeline;
        public bool ShowAgentTimeline
        {
            get => _showAgentTimeline;
            private set => SetProperty(ref _showAgentTimeline, value);
        }

        private string _agentDiffSummary = string.Empty;
        public string AgentDiffSummary
        {
            get => _agentDiffSummary;
            private set => SetProperty(ref _agentDiffSummary, value);
        }

        public ICommand SendMessageCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand AnalyseRunCommand { get; }
        public ICommand GenerateAutoFixScriptCommand { get; }
        public ICommand LoadAvailableRunsCommand { get; }
        public ICommand ClearChatCommand { get; }
        public ICommand CopyLastAssistantMessageCommand { get; }
        public ICommand CopyMessageCommand { get; }
        public ICommand ToggleMessageExpandCommand { get; }
        public ICommand OpenMessageWindowCommand { get; }
        public ICommand OpenLoadFailureLogCommand { get; }
        public ICommand CopyLoadFailureLogPathCommand { get; }
        public ICommand ChooseModelCommand { get; }
        public ICommand DownloadQwen3Command { get; }
        public ICommand DownloadQwenCoderCommand { get; }
        public ICommand OpenModelsFolderCommand { get; }
        public ICommand ShowInstallGuideCommand { get; }
        public ICommand AutoFixCommand { get; }
        public ICommand CopyPipelineLogCommand { get; }
        public ICommand OpenLiveScriptComposerCommand { get; }
        public ICommand OpenAutoFixLogsFolderCommand { get; }
        public ICommand AddApiCommand { get; }
        public ICommand ToggleInferenceModeCommand { get; }

        public string InputText
        {
            get => _inputText;
            set
            {
                if (SetProperty(ref _inputText, value))
                {
                    OnPropertyChanged(nameof(CanSend));
                }
            }
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set
            {
                if (SetProperty(ref _isStreaming, value))
                {
                    OnPropertyChanged(nameof(ShowRunSelectedPlaceholder));
                    RaiseComputedState();
                }
            }
        }

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (SetProperty(ref _isAnalyzing, value))
                {
                    OnPropertyChanged(nameof(ShowRunSelectedPlaceholder));
                    RaiseComputedState();
                }
            }
        }

        public int AnalysisProgress
        {
            get => _analysisProgress;
            private set => SetProperty(ref _analysisProgress, value);
        }

        public bool IsAnalysisIndeterminate
        {
            get => _isAnalysisIndeterminate;
            private set => SetProperty(ref _isAnalysisIndeterminate, value);
        }

        public string AnalysisStatusText
        {
            get => _analysisStatusText;
            private set => SetProperty(ref _analysisStatusText, value);
        }

        public bool IsExecutingAutofix
        {
            get => _isExecutingAutofix;
            set
            {
                if (SetProperty(ref _isExecutingAutofix, value))
                {
                    RaiseComputedState();
                }
            }
        }

        public ModelStatus ModelStatus
        {
            get => _modelStatus;
            private set
            {
                if (SetProperty(ref _modelStatus, value))
                {
                    RaiseComputedState();
                }
            }
        }

        public string ModelStatusMessage
        {
            get => _modelStatusMessage;
            private set
            {
                if (SetProperty(ref _modelStatusMessage, value))
                {
                    OnPropertyChanged(nameof(AiLocaleStatusLine));
                }
            }
        }

        public bool HasLoadFailure
        {
            get => _hasLoadFailure;
            private set
            {
                if (SetProperty(ref _hasLoadFailure, value))
                {
                    OnPropertyChanged(nameof(ShowNoRunPlaceholder));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string LoadFailureMessage
        {
            get => _loadFailureMessage;
            private set => SetProperty(ref _loadFailureMessage, value);
        }

        public string LoadFailureDetails
        {
            get => _loadFailureDetails;
            private set => SetProperty(ref _loadFailureDetails, value);
        }

        public string LoadFailureLogPath
        {
            get => _loadFailureLogPath;
            private set
            {
                if (SetProperty(ref _loadFailureLogPath, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsLoadingRuns
        {
            get => _isLoadingRuns;
            private set => SetProperty(ref _isLoadingRuns, value);
        }

        public string CurrentIaLocaleLabel => App.CurrentLanguage switch
        {
            "en" => "IA locale: English",
            "es" => "IA locale: Espanol",
            _ => "IA locale: Francais"
        };

        public string AiLocaleStatusLine => $"{CurrentIaLocaleLabel} | {ModelStatusMessage}";

        public string ModelLimitNotice
        {
            get => _modelLimitNotice;
            private set
            {
                if (SetProperty(ref _modelLimitNotice, value))
                {
                    OnPropertyChanged(nameof(HasModelLimitNotice));
                }
            }
        }

        public bool HasModelLimitNotice => !string.IsNullOrWhiteSpace(ModelLimitNotice);

        public string SettingsPath
        {
            get => _settingsPath;
            private set => SetProperty(ref _settingsPath, value);
        }

        public bool IsModelAvailable => _client?.IsReady == true;
        public bool IsModelLoading => ModelStatus == ModelStatus.Loading;
        public bool ShowModelBanner => !IsApiMode && !IsModelAvailable;
        public bool CanDownloadQwen3 => !IsApiMode && !IsModelAvailable && !IsModelLoading && !IsDownloadingQwen3;
        public bool CanDownloadQwenCoder => !IsApiMode && !IsModelAvailable && !IsModelLoading && !IsDownloadingQwen3;

        public bool IsDownloadingQwen3
        {
            get => _isDownloadingQwen3;
            private set
            {
                if (SetProperty(ref _isDownloadingQwen3, value))
                {
                    RaiseComputedState();
                }
            }
        }

        public int Qwen3DownloadProgress
        {
            get => _qwen3DownloadProgress;
            private set => SetProperty(ref _qwen3DownloadProgress, value);
        }

        public string Qwen3DownloadStatus
        {
            get => _qwen3DownloadStatus;
            private set => SetProperty(ref _qwen3DownloadStatus, value);
        }

        public string ModelIndicatorTooltip => ModelStatus switch
        {
            _ when IsApiMode && ModelStatus == ModelStatus.Ready => "IA API prete.",
            _ when IsApiMode && ModelStatus == ModelStatus.Loading => "Connexion API en cours...",
            _ when IsApiMode => "Configuration API requise.",
            ModelStatus.Ready => "IA locale prete.",
            ModelStatus.Loading => "Chargement du modele local...",
            ModelStatus.InvalidPath => "Modele local invalide ou introuvable.",
            ModelStatus.Error => "Erreur de chargement du modele local (voir logs).",
            _ => "Modele IA local non installe."
        };

        public string ModelDownloadInstructions => L(
            IsApiMode
                ? "Mode API actif: configurez Base URL, API Key et model via 'Add API'."
                : $"Si aucun modele configuré n'est disponible, utilise \"Download modèle .gguf\" ou \"Choose model .gguf\". Dossier modèles actuel : {ResolveModelDirectory()}.",
            IsApiMode
                ? "API mode active: configure Base URL, API key and model with 'Add API'."
                : $"If no configured model is available, use \"Download model .gguf\" or \"Choose model .gguf\". Current models folder: {ResolveModelDirectory()}.",
            IsApiMode
                ? "Modo API activo: configura Base URL, API Key y modelo con 'Add API'."
                : $"Si no hay un modelo configurado disponible, usa \"Download model .gguf\" o \"Choose model .gguf\". Carpeta actual de modelos: {ResolveModelDirectory()}.");

        public string ModelsRootPath => _settings.LlmModelsRoot;

        public ScanRunEntry? SelectedRun
        {
            get => _selectedRun;
            set
            {
                if (SetProperty(ref _selectedRun, value))
                {
                    SecurityVerdict = null;
                    GeneratedScript = string.Empty;
                    _autoFixGate = null;
                    _scriptGeneratedByPipeline = false;
                    _lastReport = null;
                    _runSummaryCached = string.Empty;
                    _runSummaryCachedKey = string.Empty;
                    // Reset context — PrefetchProblemDetectionAsync will rebuild it for the new run
                    LoadedContext = null;
                    ContextSources = string.Empty;
                    CurrentRunHeader = null;
                    PrefetchProblemDetectionAsync(value);
                    OnPropertyChanged(nameof(ShowNoRunPlaceholder));
                    OnPropertyChanged(nameof(ShowRunSelectedPlaceholder));
                    RaiseComputedState();
                }
            }
        }

        public bool ShowNoRunPlaceholder => SelectedRun == null && !HasLoadFailure;
        public bool ShowRunSelectedPlaceholder => SelectedRun != null && !HasMessages && !IsStreaming && !IsAnalyzing;

        public bool CanAnalyseRun => SelectedRun != null && !IsStreaming && !IsAnalyzing && !IsExecutingAutofix;
        public bool CanGenerateAutoFixScript => SelectedRun != null && HasContext && IsModelAvailable && !IsStreaming && !IsAnalyzing && !IsExecutingAutofix;

        public bool CanCancel => IsStreaming || IsAnalyzing || IsExecutingAutofix || IsDownloadingQwen3;

        public ContextPack? LoadedContext
        {
            get => _loadedContext;
            private set
            {
                if (SetProperty(ref _loadedContext, value))
                {
                    OnPropertyChanged(nameof(HasContext));
                    OnPropertyChanged(nameof(IsContextTruncated));
                    OnPropertyChanged(nameof(ExcludedFindingsCount));
                }
            }
        }

        public bool HasContext => LoadedContext != null;
        public bool HasMessages => Messages.Count > 0;
        public bool HasPipelineLogs => PipelineLogs.Count > 0;

        public string ContextSources
        {
            get => _contextSources;
            private set => SetProperty(ref _contextSources, value);
        }

        /// <summary>True when the loaded context was truncated due to token budget limits.</summary>
        public bool IsContextTruncated => LoadedContext?.Truncated == true;

        /// <summary>Number of findings excluded from the AI context due to truncation.</summary>
        public int ExcludedFindingsCount => LoadedContext?.ExcludedFindingsCount ?? 0;

        public RunAnalysisHeader? CurrentRunHeader
        {
            get => _currentRunHeader;
            private set => SetProperty(ref _currentRunHeader, value);
        }

        public JudgeResult? SecurityVerdict
        {
            get => _securityVerdict;
            private set
            {
                if (SetProperty(ref _securityVerdict, value))
                {
                    OnPropertyChanged(nameof(HasVerdict));
                    RaiseComputedState();
                }
            }
        }

        public bool HasVerdict => SecurityVerdict != null;

        /// <summary>True when a verdict exists AND the AutoFix is blocked (for UI warning card).</summary>
        public bool HasVerdictBlocked => HasVerdict && _autoFixGate?.IsApproved == false;

        /// <summary>Compact one-line verdict + score summary for UI display.</summary>
        public string VerdictSummary
        {
            get
            {
                if (SecurityVerdict == null) return string.Empty;
                if (SecurityVerdict.IsMissingScriptError || string.Equals(AutoFixBlockedBy, "MissingScript", StringComparison.OrdinalIgnoreCase))
                {
                    return "AutoFix failed: MissingScript - scores: N/A";
                }

                return $"{SecurityVerdict.VerdictDisplay} - global {SecurityVerdict.OverallScore0_100}/100  " +
                       $"(S:{SecurityVerdict.SecurityScore0_100} A:{SecurityVerdict.AccuracyScore0_100} " +
                       $"M:{SecurityVerdict.MinimalityScore0_100} V:{SecurityVerdict.ReversibilityScore0_100})";
            }
        }

        /// <summary>
        /// Human-readable category of what blocked AutoFix — "HardBlock", "ScoreGate",
        /// "MissingScript", or empty string (not blocked).
        /// </summary>
        public string AutoFixBlockedBy => _autoFixGate?.BlockedBy ?? string.Empty;

        /// <summary>Display label for the blocking category.</summary>
        public string AutoFixBlockedByDisplay => _autoFixGate?.BlockedByDisplay ?? string.Empty;

        /// <summary>Top blocking reasons (max 3) for the verdict card.</summary>
        public System.Collections.ObjectModel.ObservableCollection<string> VerdictBlockingReasons { get; }
            = new();

        /// <summary>Top warning reasons (max 3, non-blocking) for the verdict card.</summary>
        public System.Collections.ObjectModel.ObservableCollection<string> VerdictWarningReasons { get; }
            = new();

        public string GeneratedScript
        {
            get => _generatedScript;
            private set
            {
                if (SetProperty(ref _generatedScript, value))
                {
                    OnPropertyChanged(nameof(HasScript));
                    RaiseComputedState();
                }
            }
        }

        public bool HasScript => !string.IsNullOrWhiteSpace(GeneratedScript);

        public bool HasActionPlan => ManualPlanItems.Count > 0 || AutoFixPlanItems.Count > 0;

        public bool CanSend => !IsStreaming && !IsAnalyzing && !IsExecutingAutofix && !string.IsNullOrWhiteSpace(InputText);

        public bool CanClearChat => Messages.Count > 0 && !IsStreaming && !IsAnalyzing && !IsExecutingAutofix;
        public bool CanCopyLastAssistantMessage => Messages.Any(m => m.IsAssistant && !string.IsNullOrWhiteSpace(m.Content));
        public bool IsApiMode => string.Equals(_settings.InferenceMode, "API", StringComparison.OrdinalIgnoreCase);
        public string InferenceModeBadge => IsApiMode ? "API" : "Local";
        public string InferenceToggleLabel => IsApiMode ? "Switch to Local" : "Switch to API";
        public bool CanOpenAutoFixLogsFolder =>
            !string.IsNullOrWhiteSpace(_lastReport?.AutoFixTraceDirectory)
            && Directory.Exists(_lastReport?.AutoFixTraceDirectory ?? string.Empty);

        /// <summary>AutoFix enabled only when a script is generated and the safety gate approves it.</summary>
        public bool CanAutoFix =>
            !IsStreaming &&
            !IsAnalyzing &&
            !IsExecutingAutofix &&
            _scriptGeneratedByPipeline &&
            HasScript &&
            _autoFixGate?.IsApproved == true;

        private async Task InitializeModelAsync(bool reload)
        {
            if (Interlocked.CompareExchange(ref _isInitializingModel, 1, 0) != 0)
            {
                Log("[AI] InitializeModelAsync already in progress, skipping duplicate call.");
                return;
            }

            try
            {
                await InitializeModelCoreAsync(reload).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _isInitializingModel, 0);
            }
        }

        private async Task InitializeModelCoreAsync(bool reload)
        {
            if (reload)
            {
                _runtimeHost.Unload();
                DetachOrchestratorEvents();
                _orchestrator = null;
            }

            _settings.Normalize();
            SettingsPath = AiSettingsLoader.GetEffectiveConfigPath();
            OnPropertyChanged(nameof(ModelsRootPath));

            EnsureLlmRuntime();
            if (IsApiMode)
            {
                var apiValidation = _modelLoader!.ValidateModelPath(_settings.ApiProvider?.BaseUrl ?? string.Empty);
                ModelStatus = apiValidation.Status;
                ModelStatusMessage = apiValidation.Message;
                if (apiValidation.Status != ModelStatus.Ready)
                {
                    AddSystemMessage(L(
                        "Configuration API incomplete. Ouvrez 'Add API'.",
                        "API configuration is incomplete. Open 'Add API'.",
                        "La configuracion API esta incompleta. Abre 'Add API'."));
                    return;
                }

                ModelStatus = ModelStatus.Loading;
                ModelStatusMessage = "Connecting API...";
                var apiLoaded = await _modelLoader.TryLoadAsync(string.Empty, _settings.ContextWindow, _settings.Threads, _settings.GpuLayers).ConfigureAwait(false);
                if (!apiLoaded)
                {
                    ModelStatus = _modelLoader.Status;
                    ModelStatusMessage = _modelLoader.StatusMessage;
                    AddSystemMessage(L(
                        $"IA API indisponible. Erreur: {SanitizeForUi(_modelLoader.StatusMessage)}",
                        $"API AI unavailable. Error: {SanitizeForUi(_modelLoader.StatusMessage)}",
                        $"IA API no disponible. Error: {SanitizeForUi(_modelLoader.StatusMessage)}"));
                    return;
                }
            }
            else
            {
                var modelsRoot = _settings.LlmModelsRoot;
                var rootExists = Directory.Exists(modelsRoot);
                Log($"[AI] LLM models root: {modelsRoot} (exists={rootExists}) profile={_settings.ActiveModelProfile}");
                if (!rootExists)
                {
                    ModelStatus = ModelStatus.NotInstalled;
                    ModelStatusMessage = $"Dossier modèles introuvable: {modelsRoot}";
                    AddSystemMessage(L(
                        $"Dossier modèles introuvable: {modelsRoot}. Choisis un dossier/modèle via \"Choose model .gguf\".",
                        $"Models folder not found: {modelsRoot}. Choose a folder/model with \"Choose model .gguf\".",
                        $"Carpeta de modelos no encontrada: {modelsRoot}. Elige una carpeta/modelo con \"Choose model .gguf\"."));
                    return;
                }

                var validation = _modelLoader!.ValidateModelPath(_settings.ModelPath);
                ModelStatus = validation.Status;
                ModelStatusMessage = validation.Message;

                if (validation.Status != ModelStatus.Ready)
                {
                    AddSystemMessage(L(
                        "IA non disponible. Selectionne le modele local requis.",
                        "AI unavailable. Select the required local model.",
                        "IA no disponible. Selecciona el modelo local requerido."));
                    return;
                }

                ModelStatus = ModelStatus.Loading;
                ModelStatusMessage = "Loading local model...";

                var loaded = await _modelLoader.TryLoadAsync(
                    _settings.ModelPath,
                    _settings.ContextWindow,
                    _settings.Threads,
                    _settings.GpuLayers);

                if (!loaded)
                {
                    ModelStatus = _modelLoader.Status;
                    ModelStatusMessage = _modelLoader.StatusMessage;
                    var loadDetail = SanitizeForUi(_modelLoader.StatusMessage ?? string.Empty);
                    // Detect oversized model (RAM issue) to give actionable guidance.
                    long fileSizeMb = 0;
                    try { fileSizeMb = new FileInfo(_settings.ModelPath).Length / (1024 * 1024); } catch { }
                    var ramHint = fileSizeMb > 8000
                        ? L(
                            $" Modele de {fileSizeMb / 1024}GB trop grand — RAM insuffisante. Choisis un modele plus petit (Q4 3-7B).",
                            $" Model {fileSizeMb / 1024}GB too large — insufficient RAM. Choose a smaller model (Q4 3-7B).",
                            $" Modelo de {fileSizeMb / 1024}GB demasiado grande — RAM insuficiente. Elige un modelo mas pequeno (Q4 3-7B).")
                        : string.Empty;
                    AddSystemMessage(string.IsNullOrWhiteSpace(loadDetail)
                        ? L(
                            "IA non disponible. Verifie le modele local.",
                            "AI unavailable. Check the local model.",
                            "IA no disponible. Verifica el modelo local.")
                        : L(
                            $"IA non disponible. Erreur: {loadDetail}{ramHint}",
                            $"AI unavailable. Error: {loadDetail}{ramHint}",
                            $"IA no disponible. Error: {loadDetail}{ramHint}"));
                    return;
                }
            }

            ModelStatus = ModelStatus.Ready;
            ModelStatusMessage = _client?.StatusMessage ?? "Ready";

            // Lazy orchestrator: keep chat startup light and instantiate pipeline only on demand.
            DetachOrchestratorEvents();
            _orchestrator = null;

            // Warmup the model once to reduce first-token latency on the first real user request.
            try
            {
                using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(20, _settings.TimeoutSeconds)));
                var warmupOk = await _client!.PingAsync(warmupCts.Token).ConfigureAwait(false);
                Log($"[AI][Warmup] done ok={warmupOk}");
            }
            catch (Exception ex)
            {
                Log($"[AI][Warmup] failed: {ex.Message}");
            }

            AddSystemMessage(IsApiMode
                ? L(
                    "IA API prete. Contexte run et chat actifs.",
                    "API AI ready. Run context and chat are active.",
                    "IA API lista. Contexto del run y chat activos.")
                : L(
                    "IA locale prete. Contexte run et chat actifs.",
                    "Local AI ready. Run context and chat are active.",
                    "IA local lista. Contexto del run y chat activos."));
        }

        private async Task ChooseModelAsync(object? _)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Select local GGUF model",
                    Filter = "GGUF model (*.gguf)|*.gguf",
                    CheckFileExists = true,
                    Multiselect = false,
                    InitialDirectory = Directory.Exists(_settings.LlmModelsRoot)
                        ? _settings.LlmModelsRoot
                        : Directory.Exists(_settings.ModelsDirectory)
                            ? _settings.ModelsDirectory
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

                if (dlg.ShowDialog() != true)
                {
                    return;
                }

                await ApplySelectedModelAsync(dlg.FileName);
            }
            catch (Exception ex)
            {
                Log($"[AI] Model selection failed: {ex}");
                MessageBox.Show(
                    "Model selection failed. Check logs for details.",
                    "LLM",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async Task DownloadQwen3Async(object? _)
        {
            if (IsDownloadingQwen3)
            {
                return;
            }

            var targetDirectory = ResolveModelDirectory();
            if (TryFindExistingQwen3Model(targetDirectory, out var existingQwen3Path))
            {
                Qwen3DownloadStatus = L(
                    "Modèle Qwen3-8B déjà présent. Activation en cours...",
                    "Qwen3-8B model already present. Activating...",
                    "Modelo Qwen3-8B ya presente. Activando...");
                Qwen3DownloadProgress = 100;
                await ApplySelectedModelAsync(existingQwen3Path);
                return;
            }

            _qwen3DownloadCts?.Dispose();
            _qwen3DownloadCts = new CancellationTokenSource();
            var qwen3Cts = _qwen3DownloadCts;
            try
            {
                await DownloadModelCoreAsync(
                    targetDirectory,
                    (p, ct) => _modelDownloader.DownloadQwen3Q4Async(targetDirectory, p, ct),
                    qwen3Cts);
            }
            finally
            {
                qwen3Cts.Dispose();
                if (ReferenceEquals(_qwen3DownloadCts, qwen3Cts))
                {
                    _qwen3DownloadCts = null;
                }
            }
        }

        private async Task DownloadQwenCoderAsync(object? _)
        {
            if (IsDownloadingQwen3)
            {
                return;
            }

            var targetDirectory = ResolveModelDirectory();
            if (TryFindExistingQwenCoderModel(targetDirectory, out var existingQwenPath))
            {
                Qwen3DownloadStatus = L(
                    "Modèle Qwen2.5-Coder déjà présent. Activation en cours...",
                    "Qwen2.5-Coder model already present. Activating...",
                    "Modelo Qwen2.5-Coder ya presente. Activando...");
                Qwen3DownloadProgress = 100;
                await ApplySelectedModelAsync(existingQwenPath);
                return;
            }

            _qwenDownloadCts?.Dispose();
            _qwenDownloadCts = new CancellationTokenSource();
            var qwenCts = _qwenDownloadCts;
            try
            {
                await DownloadModelCoreAsync(
                    targetDirectory,
                    (p, ct) => _modelDownloader.DownloadQwenCoderQ4Async(targetDirectory, p, ct),
                    qwenCts);
            }
            finally
            {
                qwenCts.Dispose();
                if (ReferenceEquals(_qwenDownloadCts, qwenCts))
                {
                    _qwenDownloadCts = null;
                }
            }
        }

        private async Task DownloadModelCoreAsync(
            string targetDirectory,
            Func<IProgress<ModelDownloadProgress>, CancellationToken, Task<ModelDownloadResult>> downloadFunc,
            CancellationTokenSource cts)
        {
            try
            {
                IsDownloadingQwen3 = true;
                Qwen3DownloadProgress = 0;
                Qwen3DownloadStatus = L(
                    "Téléchargement du modèle en cours...",
                    "Downloading model...",
                    "Descargando modelo...");

                // Unload the current model to release any file lock before overwriting the target.
                _runtimeHost.Unload();
                RaiseComputedState();

                _settings.Qwen3ModelDirectory = targetDirectory;
                _settings.ModelsDirectory = targetDirectory;
                _settings.LlmModelsRoot = targetDirectory;
                OnPropertyChanged(nameof(ModelsRootPath));

                var progress = new Progress<ModelDownloadProgress>(UpdateQwen3DownloadProgress);
                var result = await downloadFunc(progress, cts.Token);

                if (result.Cancelled)
                {
                    Qwen3DownloadStatus = L(
                        "Téléchargement annulé.",
                        "Download cancelled.",
                        "Descarga cancelada.");
                    AddSystemMessage(Qwen3DownloadStatus);
                    return;
                }

                if (!result.Success)
                {
                    var failedMessage = L(
                        $"Échec du téléchargement: {result.ErrorMessage}",
                        $"Download failed: {result.ErrorMessage}",
                        $"Fallo en la descarga: {result.ErrorMessage}");
                    Qwen3DownloadStatus = failedMessage;
                    AddSystemMessage(failedMessage);
                    MessageBox.Show(
                        SanitizeForUi(failedMessage),
                        "Model download",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                Qwen3DownloadProgress = 100;
                Qwen3DownloadStatus = L(
                    "Téléchargement terminé. Chargement du modèle...",
                    "Download complete. Loading model...",
                    "Descarga completada. Cargando modelo...");

                if (await ApplySelectedModelAsync(result.FilePath))
                {
                    Qwen3DownloadStatus = L(
                        $"Modèle installé dans {targetDirectory}.",
                        $"Model installed in {targetDirectory}.",
                        $"Modelo instalado en {targetDirectory}.");
                    AddSystemMessage(Qwen3DownloadStatus);
                }
            }
            catch (Exception ex)
            {
                Log($"[AI] Model download failed: {ex}");
                Qwen3DownloadStatus = L(
                    "Le téléchargement du modèle a échoué. Vérifie les logs.",
                    "Model download failed. Check logs.",
                    "La descarga del modelo falló. Revisa los logs.");
                MessageBox.Show(
                    SanitizeForUi(Qwen3DownloadStatus),
                    "Model download",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsDownloadingQwen3 = false;
            }
        }

        private void OpenModelsFolder()
        {
            var target = ResolveModelDirectory();
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show(
                    "Aucun dossier modèles n'est configuré.",
                    "LLM",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!Directory.Exists(target))
            {
                var create = MessageBox.Show(
                    $"Le dossier modèles n'existe pas:\n{target}\n\nVoulez-vous le créer ?",
                    "LLM",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (create != MessageBoxResult.Yes)
                    return;
            }

            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true
            });
        }

        private void ShowInstallGuide()
        {
            var message = L(
                $"Installation du modèle configuré:\n\n1) Clique 'Download model .gguf' ou 'Choose model .gguf'.\n2) Le fichier est stocké dans {ResolveModelDirectory()}.\n3) Le chemin est sauvegardé dans ai_settings.json et le modèle est chargé automatiquement.",
                $"Configured model setup:\n\n1) Click 'Download model .gguf' or 'Choose model .gguf'.\n2) The file is stored in {ResolveModelDirectory()}.\n3) The path is saved in ai_settings.json and the model is loaded automatically.",
                $"Configuración del modelo:\n\n1) Pulsa 'Download model .gguf' o 'Choose model .gguf'.\n2) El archivo se guarda en {ResolveModelDirectory()}.\n3) La ruta se guarda en ai_settings.json y el modelo se carga automáticamente.");
            MessageBox.Show(message, "LLM", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task<bool> ApplySelectedModelAsync(string modelPath)
        {
            EnsureLlmRuntime();

            // The user explicitly chose this file via the file picker.
            // Disable allowlist enforcement entirely for user selections — the allowlist
            // guards against config-injection attacks, not against deliberate user choices.
            // Also add to the list so subsequent startup validation keeps passing.
            var normalizedModelPath = modelPath.Trim().Trim('"');
            var chosenFileName = Path.GetFileName(normalizedModelPath);
            _settings.EnforceModelAllowList = false;
            Log($"[AI] EnforceModelAllowList disabled for user-selected model '{chosenFileName}'.");

            if (!string.IsNullOrWhiteSpace(chosenFileName) &&
                !_settings.AllowedModelFileNames.Any(f =>
                    string.Equals(f, chosenFileName, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.AllowedModelFileNames.Add(chosenFileName);
                Log($"[AI] Model '{chosenFileName}' added to allowlist (user-selected).");
            }

            var validation = _modelLoader!.ValidateModelPath(normalizedModelPath, computeChecksum: false);
            if (validation.Status != ModelStatus.Ready)
            {
                MessageBox.Show(
                    SanitizeForUi(validation.Message),
                    "Invalid model",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            _settings.ModelPath = validation.NormalizedPath;
            var modelDirectory = Path.GetDirectoryName(validation.NormalizedPath);
            if (!string.IsNullOrWhiteSpace(modelDirectory))
            {
                _settings.LlmModelsRoot = modelDirectory;
                _settings.ModelsDirectory = modelDirectory;
                if (IsQwen3FileName(Path.GetFileName(validation.NormalizedPath)))
                {
                    _settings.Qwen3ModelDirectory = modelDirectory;
                }
                OnPropertyChanged(nameof(ModelsRootPath));
            }

            if (!AiSettingsLoader.Save(_settings, out var savedPath))
            {
                MessageBox.Show(
                    "Unable to persist ai_settings.json.",
                    "AI Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            SettingsPath = savedPath;
            await InitializeModelAsync(reload: true);
            return true;
        }

        private string ResolveModelDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_settings.LlmModelsRoot))
            {
                return _settings.LlmModelsRoot.Trim();
            }

            if (!string.IsNullOrWhiteSpace(_settings.ModelsDirectory))
            {
                return _settings.ModelsDirectory.Trim();
            }

            if (!string.IsNullOrWhiteSpace(_settings.Qwen3ModelDirectory))
            {
                return _settings.Qwen3ModelDirectory.Trim();
            }

            return AiSettings.DefaultLlmModelsRoot;
        }

        private bool TryFindExistingQwen3Model(string directory, out string modelPath)
        {
            modelPath = string.Empty;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            foreach (var fileName in ModelDownloaderService.KnownQwen3FileNames)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    modelPath = candidate;
                    return true;
                }
            }

            var fallback = Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => IsQwen3FileName(Path.GetFileName(path)));
            if (string.IsNullOrWhiteSpace(fallback))
            {
                return false;
            }

            modelPath = fallback;
            return true;
        }

        private static bool IsQwen3FileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            return fileName.Contains("qwen3", StringComparison.OrdinalIgnoreCase)
                   && fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryFindExistingQwenCoderModel(string directory, out string modelPath)
        {
            modelPath = string.Empty;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            foreach (var fileName in ModelDownloaderService.KnownQwenCoderFileNames)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    modelPath = candidate;
                    return true;
                }
            }

            var fallback = Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => IsQwenCoderFileName(Path.GetFileName(path)));
            if (string.IsNullOrWhiteSpace(fallback))
            {
                return false;
            }

            modelPath = fallback;
            return true;
        }

        private static bool IsQwenCoderFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            return fileName.Contains("qwen", StringComparison.OrdinalIgnoreCase)
                   && fileName.Contains("coder", StringComparison.OrdinalIgnoreCase)
                   && fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateQwen3DownloadProgress(ModelDownloadProgress progress)
        {
            RunOnUiThread(() =>
            {
                if (progress.TotalBytes.HasValue && progress.TotalBytes.Value > 0)
                {
                    var percent = (int)Math.Clamp(
                        progress.BytesDownloaded * 100L / progress.TotalBytes.Value,
                        0,
                        100);
                    Qwen3DownloadProgress = percent;
                    Qwen3DownloadStatus = L(
                        $"Téléchargement du modèle {percent}% ({FormatBytes(progress.BytesDownloaded)} / {FormatBytes(progress.TotalBytes.Value)})",
                        $"Model download {percent}% ({FormatBytes(progress.BytesDownloaded)} / {FormatBytes(progress.TotalBytes.Value)})",
                        $"Descarga del modelo {percent}% ({FormatBytes(progress.BytesDownloaded)} / {FormatBytes(progress.TotalBytes.Value)})");
                    return;
                }

                Qwen3DownloadStatus = L(
                    $"Téléchargement du modèle ({FormatBytes(progress.BytesDownloaded)})",
                    $"Model download ({FormatBytes(progress.BytesDownloaded)})",
                    $"Descarga del modelo ({FormatBytes(progress.BytesDownloaded)})");
            });
        }

        private static string FormatBytes(long bytes)
        {
            const double kilo = 1024d;
            const double mega = kilo * 1024d;
            const double giga = mega * 1024d;

            if (bytes >= giga)
            {
                return $"{bytes / giga:F2} GB";
            }

            if (bytes >= mega)
            {
                return $"{bytes / mega:F2} MB";
            }

            if (bytes >= kilo)
            {
                return $"{bytes / kilo:F2} KB";
            }

            return $"{Math.Max(0, bytes)} B";
        }

        internal async Task SendMessageForTestAsync(string userText)
        {
            await SendMessageInternalAsync(userText);
        }

        private async Task SendMessageAsync(object? _)
        {
            await SendMessageInternalAsync(InputText);
        }

        private async Task SendMessageInternalAsync(string rawUserText)
        {
            var userText = rawUserText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userText))
            {
                return;
            }

            var chatRequestId = Guid.NewGuid().ToString("N");
            Log($"[AI][ChatRequestId:{chatRequestId}] INPUT accepted len={userText.Length}");

            InputText = string.Empty;
            await DispatchAsync(() =>
            {
                Messages.Add(new ChatMessage { Role = ChatRole.User, Content = userText });
            });

            if (!IsModelAvailable || _client == null)
            {
                AddSystemMessage(IsApiMode
                    ? L(
                        "IA API indisponible. Verifie la configuration Add API.",
                        "API AI unavailable. Check Add API configuration.",
                        "IA API no disponible. Verifica la configuracion Add API.")
                    : L(
                        "IA non disponible. Verifie le modele local.",
                        "AI unavailable. Check the local model.",
                        "IA no disponible. Verifica el modelo local."));
                Log($"[AI][ChatRequestId:{chatRequestId}] END ok=false reason=model_unavailable");
                return;
            }

            if (LoadedContext == null)
            {
                AddSystemMessage(L(
                    "Aucun run charge. Selectionne un rapport puis clique Analyze selected run.",
                    "No run context loaded. Select a report then click Analyze selected run.",
                    "No hay contexto de run cargado. Selecciona un reporte y pulsa Analyze selected run."));
                Log($"[AI][ChatRequestId:{chatRequestId}] END ok=false reason=no_context");
                return;
            }
            var context = LoadedContext!;
            var runId = CurrentRunHeader?.RunId ?? context.RunId ?? "unknown";
            var intent = UserIntentClassifier.Classify(userText);
            var conversationState = GetConversationState(runId);
            conversationState.LastUserIntent = intent;
            conversationState.UpdatedUtc = DateTime.UtcNow;

            if (TryBuildJudgeDiscussionReply(userText, out var judgeReply))
            {
                await DispatchAsync(() =>
                {
                    Messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = judgeReply
                    });
                    OnPropertyChanged(nameof(CanCopyLastAssistantMessage));
                });
                Log($"[AI][ChatRequestId:{chatRequestId}] END ok=true handled=judge_discussion");
                return;
            }

            var previousAssistantContent = Messages
                .Where(m => m.IsAssistant && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content)
                .LastOrDefault() ?? string.Empty;

            var assistant = new ChatMessage { Role = ChatRole.Assistant, Content = string.Empty };
            await DispatchAsync(() => Messages.Add(assistant));

            IsStreaming = true;
            _cancelRequestedByUser = false;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            using var requestTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            requestTimeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
            var requestToken = requestTimeoutCts.Token;

            using var warningCts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            var warningDelaySeconds = Math.Min(30, Math.Max(8, _settings.TimeoutSeconds / 3));
            var warningTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(warningDelaySeconds), warningCts.Token);
                    if (IsStreaming)
                    {
                        AddSystemMessage(L(
                            "L'IA prend plus de temps que prevu. Clique Cancel pour interrompre.",
                            "AI is taking longer than expected. Click Cancel to stop.",
                            "La IA tarda mas de lo esperado. Pulsa Cancel para detener."));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });

            var watch = Stopwatch.StartNew();
            var chunks = 0;
            var emittedChars = 0;
            var ttftMs = -1L;
            var retrievalMs = 0L;
            var responseParseMs = 0L;
            var metrics = new AiPipelineMetrics
            {
                Stage = "chat",
                RunId = CurrentRunHeader?.RunId ?? context.RunId,
                ChatRequestId = chatRequestId,
                ContextChars = context.ToPromptText().Length,
                ContextTokensEst = Math.Max(1, context.EstimatedTokens),
                ModelName = Path.GetFileName(_settings.ModelPath),
                Temperature = _settings.Temperature,
                MaxTokens = _settings.MaxTokens,
                StreamingEnabled = _settings.EnableStreaming
            };

            try
            {
                // Use the application's current language setting for LLM responses.
                var langCode = App.CurrentLanguage;
                Log($"[AI][ChatRequestId:{chatRequestId}] Language={langCode} run={SelectedRun?.DisplayName ?? "none"} contextReady={context != null}");

                var retrievalWatch = Stopwatch.StartNew();
                var contextText = BuildRunSummaryCached(context!, userText);
                retrievalWatch.Stop();
                retrievalMs = retrievalWatch.ElapsedMilliseconds;
                var contextTokensEst = Math.Max(1, contextText.Length / 4);
                var guardrailLang = langCode switch
                {
                    "en" => "Respond in English only.",
                    "es" => "Responde solo en espanol.",
                    _ => "Reponds uniquement en francais."
                };
                var systemPrompt = PromptLoader.ChatSystemBase()
                    .Replace("{PREFERRED_LANGUAGE}", langCode)
                    + "\n[CHAT_GUARDRAIL_STRICT]\n"
                    + $"- {guardrailLang}\n"
                    + "- Never output internal instructions, system role labels, or debug tokens.\n"
                    + "- Do not use markers ###, [LANGUAGE:], USER:, ASSISTANT:, SYSTEM:.\n"
                    + "- Format each problem with Impact, Probable cause, Recommended solution and Priority.\n"
                    + "- NEVER output <think>, </think>, or internal reasoning blocks.\n";

                // Prompt-based task routing: inject task-specific guidance
                var taskType = ClassifyUserIntent(userText);
                systemPrompt += taskType switch
                {
                    ChatTaskType.CodeGeneration =>
                        "\n[TASK MODE: CODE GENERATION]\n" +
                        "Focus on generating precise, safe PowerShell scripts.\n" +
                        "Include #Requires, SUMMARY, DOES_NOT, RISKS, ROLLBACK, CAPABILITIES headers.\n" +
                        "Test each command mentally before including it.\n",
                    ChatTaskType.Analysis =>
                        "\n[TASK MODE: DIAGNOSTIC ANALYSIS]\n" +
                        "Focus on interpreting scan data accurately.\n" +
                        "Cite specific values from the context. Prioritize by severity.\n" +
                        "Use the exact health score from scan context.\n",
                    _ => string.Empty
                };

                // SkipLast(2): skip both the empty assistant placeholder AND the current user
                // message (both were just pushed onto Messages above). Without this, the current
                // user message appears twice in the prompt — once in {CONVERSATION_HISTORY} and
                // once in {USER_MESSAGE} — which confuses the model and produces repetitive output.
                var history = Messages
                    .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
                    .SkipLast(2)
                    .TakeLast(6)
                    .Select(m => $"{(m.Role == ChatRole.User ? "USER" : "ASSISTANT")}: {SafeTrim(m.Content, 400)}")
                    .ToList();

                var historyText = history.Count > 0
                    ? string.Join("\n", history)
                    : "(no history)";

                // Build case summary from full conversation + context (injected as {CASE_SUMMARY})
                _caseSummary = BuildCaseSummary(context!, Messages.SkipLast(2));

                var langName = langCode switch
                {
                    "en" => "English",
                    "es" => "Espanol",
                    _ => "Francais"
                };

                var userPrompt = PromptLoader.ChatSupportBase()
                    .Replace("{CONTEXT_PACK}", contextText)
                    .Replace("{SCAN_SUMMARY}", contextText)
                    .Replace("{CONVERSATION_HISTORY}", historyText)
                    .Replace("{CASE_SUMMARY}", _caseSummary)
                    .Replace("{USER_MESSAGE}", userText)
                    .Replace("{RUN_ID}", CurrentRunHeader?.RunId ?? context!.RunId)
                    .Replace("{PREFERRED_LANGUAGE_NAME}", langName);

                WriteTraceFile(chatRequestId, "SYSTEM_PROMPT.txt", systemPrompt);
                WriteTraceFile(chatRequestId, "USER_PROMPT.txt", userPrompt);
                WriteTraceFile(chatRequestId, "prompt_meta.json", JsonSerializer.Serialize(new
                {
                    chatRequestId,
                    runId = CurrentRunHeader?.RunId ?? context!.RunId,
                    userMessage = userText,
                    historyCount = history.Count,
                    contextChars = contextText.Length,
                    contextTokensEstimated = contextTokensEst,
                    promptChars = userPrompt.Length + systemPrompt.Length
                }, _indentedJsonOptions));

                metrics.PromptChars = userPrompt.Length + systemPrompt.Length;
                metrics.PromptTokensEst = Math.Max(1, metrics.PromptChars / 4);
                metrics.ContextChars = contextText.Length;
                metrics.ContextTokensEst = contextTokensEst;
                metrics.RetrievalMs = retrievalMs;
                var chatRunId = CurrentRunHeader?.RunId ?? context?.RunId ?? string.Empty;
                var chatContextStats =
                    $"runId={chatRunId} contextChars={contextText.Length} contextTokens≈{contextTokensEst} sources=[{ContextSources}]";
                Log($"[AI][ChatRequestId:{chatRequestId}] START lang={langCode} userLen={userText.Length} contextLen={contextText.Length} contextTokens≈{contextTokensEst} historyCount={history.Count} promptLen={userPrompt.Length} streaming={_settings.EnableStreaming} maxTokens={_settings.MaxTokens} temperature={_settings.Temperature:F2} contextWindow={_settings.ContextWindow} timeoutSec={_settings.TimeoutSeconds} retrievalMs={retrievalMs} {chatContextStats}");

                AppendTraceLog(
                    chatRequestId,
                    $"PRE_LLM_CALL model={Path.GetFileName(_settings.ModelPath)} n_ctx={_settings.ContextWindow} maxTokens={_settings.MaxTokens} temperature={_settings.Temperature:F2} timeoutMs={_settings.TimeoutSeconds * 1000} promptChars={metrics.PromptChars} streaming={_settings.EnableStreaming} retrievalMs={retrievalMs} {chatContextStats}");

                void ApplyParsedAssistantContent(LlmStructuredResponse parsed, string fullRawOutput)
                {
                    var lang = App.CurrentLanguage;
                    string displayText;
                    string displayMode;

                    WriteTraceFile(chatRequestId, "RAW_OUTPUT.txt", fullRawOutput);
                    AppendTraceLog(chatRequestId, $"PARSED parse_success={parsed.ParseSuccess} parse_error={parsed.ParseError ?? "none"} user_response_len={parsed.UserResponse?.Length ?? 0}");

                    if (parsed.ParseSuccess)
                    {
                        WriteTraceFile(chatRequestId, "PARSED_JSON.txt", JsonSerializer.Serialize(parsed, _indentedJsonOptions));
                    }
                    else
                    {
                        WriteTraceFile(chatRequestId, "PARSE_ERRORS.txt", $"ParseSuccess={parsed.ParseSuccess}\nParseError={parsed.ParseError}\nUserResponseLen={parsed.UserResponse?.Length ?? 0}\nRawInputLen={parsed.RawInput?.Length ?? 0}");
                    }

                    if (parsed.ParseSuccess)
                    {
                        var sanitized = LlmOutputSanitizer.SanitizeChatAssistantOutput(
                            parsed.UserResponse ?? string.Empty, lang);

                        Log($"[AI][ChatRequestId:{chatRequestId}] sanitizer: truncated={sanitized.TruncatedAtInvalidPattern} fallback={sanitized.FallbackApplied} trigger='{sanitized.TriggerPattern}' rejected={sanitized.RejectedByWhitelist} keptLines={sanitized.KeptLines} droppedLines={sanitized.DroppedLines} firstDropReason={sanitized.FirstDropReason ?? "none"}");
                        WriteTraceFile(chatRequestId, "SANITIZED_OUTPUT.txt", sanitized.Text);
                        AppendTraceLog(chatRequestId, $"SANITIZER keptLines={sanitized.KeptLines} droppedLines={sanitized.DroppedLines} firstDropReason={sanitized.FirstDropReason ?? "none"}");

                        if (sanitized.FallbackApplied && !string.IsNullOrWhiteSpace(fullRawOutput))
                        {
                            // RAW OUTPUT FALLBACK MODE
                            var effectiveRaw = GetEffectiveRawForDisplay(fullRawOutput);
                            displayText = L(
                                    "[Mode texte brut — le formatage IA a echoue]\n\n",
                                    "[Raw text mode — AI formatting failed]\n\n",
                                    "[Modo texto sin formato — el formato IA fallo]\n\n")
                                + effectiveRaw;
                            displayMode = "RawText";
                        }
                        else
                        {
                            displayText = sanitized.Text;
                            displayMode = sanitized.FallbackApplied ? "FallbackGeneric" : "Parsed";
                        }
                    }
                    else
                    {
                        var trimmed = LlmOutputSanitizer.TrimAtFirstControlPattern(
                            parsed.UserResponse ?? string.Empty, out var trigger);

                        if (!string.IsNullOrWhiteSpace(trigger))
                            Log($"[AI][ChatRequestId:{chatRequestId}] fallback trim at '{trigger}'.");

                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            var effectiveRaw = GetEffectiveRawForDisplay(trimmed);
                            displayText = string.IsNullOrWhiteSpace(effectiveRaw)
                                ? L(
                                    "[Mode texte brut — le formatage IA a echoue]\n\n" +
                                    "L'IA a renvoye uniquement du texte interne, sans reponse exploitable.",
                                    "[Raw text mode — AI formatting failed]\n\n" +
                                    "The AI returned only internal reasoning text, with no usable answer.",
                                    "[Modo texto sin formato — el formato IA fallo]\n\n" +
                                    "La IA devolvio solo texto interno, sin una respuesta util.")
                                : effectiveRaw;
                            displayMode = "RawText";
                        }
                        else if (!string.IsNullOrWhiteSpace(fullRawOutput))
                        {
                            // RAW OUTPUT FALLBACK MODE
                            var effectiveRaw = GetEffectiveRawForDisplay(fullRawOutput);
                            displayText = L(
                                    "[Mode texte brut — le formatage IA a echoue]\n\n",
                                    "[Raw text mode — AI formatting failed]\n\n",
                                    "[Modo texto sin formato — el formato IA fallo]\n\n")
                                + effectiveRaw;
                            displayMode = "RawText";
                        }
                        else
                        {
                            displayText = LlmOutputSanitizer.BuildFallback(lang);
                            displayMode = "FallbackGeneric";
                            Log($"[AI][ChatRequestId:{chatRequestId}] fallback: raw empty → built generic fallback.");
                        }
                    }

                    AppendTraceLog(chatRequestId, $"DISPLAY_MODE={displayMode} displayLen={displayText.Length}");
                    Log($"[AI][ChatRequestId:{chatRequestId}] DISPLAY_MODE={displayMode} displayLen={displayText.Length}");

                    var finalAnswer = EnsureQuestionFirstAnswer(userText, displayText, context!, out var relevancePass, out var rewriteApplied);
                    Log($"[AI][AnswerRelevance] pass={relevancePass} rewrite={rewriteApplied}");
                    assistant.Content = SanitizeForUi(finalAnswer);
                    OnPropertyChanged(nameof(CanCopyLastAssistantMessage));

                    if (parsed.ParseSuccess && parsed.AgentPayload?.TriggerPipeline == true)
                    {
                        Log($"[AI][ChatRequestId:{chatRequestId}] agent_payload.trigger_pipeline=true");
                        _ = TryActivateAutoFixFromAgentPayloadAsync(parsed.AgentPayload);
                    }
                }

                if (_settings.EnableStreaming)
                {
                    // Buffer all tokens — structured JSON cannot be parsed mid-stream.
                    var tokenSb = new StringBuilder();
                    long lastUiPushMs = 0;

                    await foreach (var token in _client.StreamAsync(systemPrompt, userPrompt, requestToken))
                    {
                        if (ttftMs < 0)
                        {
                            ttftMs = watch.ElapsedMilliseconds;
                            AppendTraceLog(chatRequestId, $"FIRST_TOKEN ttftMs={ttftMs}");
                        }

                        chunks++;
                        emittedChars += token.Length;
                        tokenSb.Append(token);

                        if ((chunks % 12 == 0 || watch.ElapsedMilliseconds - lastUiPushMs >= 250) && tokenSb.Length > 0)
                        {
                            lastUiPushMs = watch.ElapsedMilliseconds;
                            var accumulated = tokenSb.ToString();
                            // Only show preview once we have enough content to be meaningful.
                            // If model starts with JSON, keep a neutral progress indicator until parsing completes.
                            var isStructured = accumulated.TrimStart().StartsWith("{", StringComparison.Ordinal);
                            if (isStructured)
                            {
                                var tokenCountPreview = L(
                                    $"[Generation en cours... {tokenSb.Length} caracteres]",
                                    $"[Generating... {tokenSb.Length} characters]",
                                    $"[Generando... {tokenSb.Length} caracteres]");
                                await DispatchAsync(() =>
                                {
                                    assistant.Content = tokenCountPreview;
                                });
                            }
                            else if (accumulated.Length >= 80)
                            {
                                var preview = GetEffectiveRawForDisplay(accumulated);
                                await DispatchAsync(() =>
                                {
                                    assistant.Content = SanitizeForUi(preview);
                                });
                            }
                        }
                    }

                    var fullRaw = tokenSb.ToString();
                    AppendTraceLog(
                        chatRequestId,
                        $"POST_LLM_CALL elapsedMs={watch.ElapsedMilliseconds} rawLen={fullRaw.Length} wasCancelled=false");
                    var parseWatch = Stopwatch.StartNew();
                    var parsed = LlmResponseParser.Parse(fullRaw, App.CurrentLanguage);
                    parseWatch.Stop();
                    responseParseMs = parseWatch.ElapsedMilliseconds;
                    Log($"[AI][ChatRequestId:{chatRequestId}] parse_success={parsed.ParseSuccess} parse_error={parsed.ParseError ?? "none"} user_response_len={parsed.UserResponse?.Length ?? 0} has_payload={parsed.AgentPayload != null} trigger={parsed.AgentPayload?.TriggerPipeline == true}");

                    await DispatchAsync(() => ApplyParsedAssistantContent(parsed, fullRaw));
                }
                else
                {
                    var generated = await _client.GenerateAsync(systemPrompt, userPrompt, requestToken);
                    ttftMs = watch.ElapsedMilliseconds;
                    chunks = 1;
                    emittedChars = generated.Length;
                    AppendTraceLog(
                        chatRequestId,
                        $"POST_LLM_CALL elapsedMs={watch.ElapsedMilliseconds} rawLen={generated.Length} wasCancelled=false");
                    var parseWatch = Stopwatch.StartNew();
                    var parsed = LlmResponseParser.Parse(generated, App.CurrentLanguage);
                    parseWatch.Stop();
                    responseParseMs = parseWatch.ElapsedMilliseconds;
                    Log($"[AI][ChatRequestId:{chatRequestId}] parse_success={parsed.ParseSuccess} parse_error={parsed.ParseError ?? "none"} user_response_len={parsed.UserResponse?.Length ?? 0}");

                    await DispatchAsync(() => ApplyParsedAssistantContent(parsed, generated));
                }

                if (string.IsNullOrWhiteSpace(assistant.Content))
                {
                    assistant.Content = L(
                        "Je n'ai pas pu produire de reponse exploitable. Reessaie avec une question plus precise.",
                        "I could not produce a usable answer. Retry with a more specific question.",
                        "No pude producir una respuesta util. Intentalo con una pregunta mas especifica.");
                    OnPropertyChanged(nameof(CanCopyLastAssistantMessage));
                }

                metrics.InferenceMs = watch.ElapsedMilliseconds;
                metrics.TtftMs = ttftMs < 0 ? metrics.InferenceMs : ttftMs;
                metrics.ResponseParseMs = responseParseMs;
                metrics.RetrievalMs = retrievalMs;
                metrics.GeneratedTokens = Math.Max(1, emittedChars / 4);
                metrics.TokensPerSecond = metrics.InferenceMs > 0
                    ? (metrics.GeneratedTokens * 1000.0) / metrics.InferenceMs
                    : 0;
                Log($"[AI][PipelineMetrics] {metrics.ToLogLine()}");

                Log($"[AI][ChatRequestId:{chatRequestId}] END ok=true durationMs={watch.ElapsedMilliseconds} ttftMs={metrics.TtftMs} retrievalMs={retrievalMs} parseMs={responseParseMs} chunks={chunks} chars={emittedChars} approxTokens={Math.Max(1, emittedChars / 4)}");
            }
            catch (OperationCanceledException ex)
            {
                var userCancelled = _cancelRequestedByUser;
                var timedOut = !userCancelled && requestTimeoutCts.IsCancellationRequested;

                await DispatchAsync(() =>
                {
                    if (string.IsNullOrWhiteSpace(assistant.Content))
                    {
                        assistant.Content = userCancelled
                            ? L("Generation annulee.", "Generation cancelled.", "Generacion cancelada.")
                            : L(
                                "L'IA prend plus de temps que prevu. Reessaie ou reduis la question.",
                                "AI is taking longer than expected. Retry or reduce the request.",
                                "La IA tarda mas de lo esperado. Reintenta o reduce la solicitud.");
                    }
                    else
                    {
                        assistant.Content += userCancelled
                            ? "\n" + L("[Generation annulee]", "[Generation cancelled]", "[Generacion cancelada]")
                            : "\n" + L("[Generation interrompue: delai depasse]", "[Generation interrupted: timeout]", "[Generacion interrumpida: tiempo agotado]");
                    }

                    OnPropertyChanged(nameof(CanCopyLastAssistantMessage));
                });

                Log($"[AI][ChatRequestId:{chatRequestId}] END ok=false cancelled=true userCancelled={userCancelled} timeout={timedOut} durationMs={watch.ElapsedMilliseconds} chunks={chunks} chars={emittedChars} exception={ex}");
            }
            catch (Exception ex)
            {
                await DispatchAsync(() =>
                {
                    assistant.Content = L(
                        "IA non disponible. Verifie le modele local.",
                        "AI unavailable. Check the local model.",
                        "IA no disponible. Verifica el modelo local.");
                    OnPropertyChanged(nameof(CanCopyLastAssistantMessage));
                });

                Log($"[AI][ChatRequestId:{chatRequestId}] END ok=false cancelled=false timeout=false durationMs={watch.ElapsedMilliseconds} chunks={chunks} chars={emittedChars} exception={ex}");
            }
            finally
            {
                warningCts.Cancel();
                try
                {
                    await warningTask;
                }
                catch (OperationCanceledException)
                {
                }

                IsStreaming = false;
                _cancelRequestedByUser = false;

                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task TryActivateAutoFixFromAgentPayloadAsync(AgentPayload payload)
        {
            if (LoadedContext == null || !IsModelAvailable || SelectedRun == null)
            {
                Log("[AI] TryActivateAutoFixFromAgentPayload: skipped — no context or model unavailable.");
                return;
            }

            if (IsAnalyzing || IsStreaming)
            {
                Log("[AI] TryActivateAutoFixFromAgentPayload: skipped — pipeline already running.");
                return;
            }

            AddSystemMessage(L(
                "L'IA a demande la generation d'un script AutoFix. Lancement du pipeline...",
                "AI requested AutoFix script generation. Launching pipeline...",
                "La IA solicito la generacion del script AutoFix. Iniciando pipeline..."));

            await GenerateAutoFixScriptAsync(null).ConfigureAwait(false);
        }

        internal async Task AnalyseRunForTestAsync(ScanRunEntry run)
        {
            SelectedRun = run;
            await AnalyseRunInternalAsync(run);
        }

        private async Task AnalyseRunAsync(object? _)
        {
            await AnalyseRunInternalAsync(SelectedRun);
        }

        private async Task AnalyseRunInternalAsync(ScanRunEntry? run)
        {
            if (run == null)
            {
                return;
            }

            var traceId = Guid.NewGuid().ToString("N");
            var analysisWatch = Stopwatch.StartNew();
            IsStreaming = true;
            IsAnalyzing = true;
            _cancelRequestedByUser = false;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            PipelineLogs.Clear();
            IProgress<AnalysisProgressUpdate> progress = new Progress<AnalysisProgressUpdate>(ApplyAnalysisProgress);
            progress.Report(new AnalysisProgressUpdate(5, L("Preparation...", "Preparing...", "Preparando..."), true));

            try
            {
                // --- Step 1: Load context ---
                progress.Report(new AnalysisProgressUpdate(10, L("Chargement du JSON...", "Loading JSON...", "Cargando JSON..."), true));
                var load = await LoadContextForRunAsync(run.CombinedJsonPath, _cts.Token).ConfigureAwait(false);
                if (load.combined == null || load.context == null || load.header == null)
                {
                    AddSystemMessage(L(
                        "Impossible de charger le run selectionne.",
                        "Unable to load the selected run.",
                        "No se pudo cargar el run seleccionado."));
                    LogAnalysisTrace(traceId, "FAIL", "context_load_null", null, null, null, analysisWatch.ElapsedMilliseconds);
                    return;
                }

                _loadedCombined = load.combined;

                progress.Report(new AnalysisProgressUpdate(25, L("Construction du contexte...", "Building context...", "Construyendo contexto..."), true));
                LoadedContext = load.context;
                CurrentRunHeader = load.header;
                ContextSources = string.Join(", ", LoadedContext.SourcesUsed);
                Log($"[AI][Analyze][{traceId}] context loaded: run={CurrentRunHeader.RunId} hash={load.fileHash} cacheHit={load.cacheHit} jsonBytes={load.jsonBytes} contextChars={LoadedContext.ToPromptText().Length} contextTokens={LoadedContext.EstimatedTokens} sources=[{ContextSources}]");

                BuildActionPlanFromDeterministic(load.combined);
                SecurityVerdict = null;
                GeneratedScript = string.Empty;
                _autoFixGate = null;
                _scriptGeneratedByPipeline = false;
                _lastReport = null;

                AddSystemMessage(
                    L(
                        $"Run charge : {CurrentRunHeader.RunId} | {CurrentRunHeader.DateDisplay} | Couverture {CurrentRunHeader.CollectionPercent:F0}%.",
                        $"Run loaded: {CurrentRunHeader.RunId} | {CurrentRunHeader.DateDisplay} | Coverage {CurrentRunHeader.CollectionPercent:F0}%.",
                        $"Run cargado: {CurrentRunHeader.RunId} | {CurrentRunHeader.DateDisplay} | Cobertura {CurrentRunHeader.CollectionPercent:F0}%."));

                // WP7: Truncation transparency banner
                if (load.context?.Truncated == true)
                {
                    var excluded = load.context.ExcludedFindingsCount;
                    var excludedSections = load.context.ExcludedSections.Count > 0
                        ? string.Join(", ", load.context.ExcludedSections)
                        : "n/a";
                    AddSystemMessage(L(
                        $"Note : {excluded} constat(s) exclus du contexte IA (sections tronquees: {excludedSections}). Les donnees les plus critiques ont ete conservees.",
                        $"Note: {excluded} finding(s) were excluded from the AI context (truncated sections: {excludedSections}). The most critical data was kept.",
                        $"Nota: {excluded} hallazgo(s) fueron excluidos del contexto IA (secciones truncadas: {excludedSections}). Se conservaron los datos mas criticos."));
                }

                // --- Step 2: Run LLM analysis if model available ---
                if (!IsModelAvailable || _client == null)
                {
                    AddSystemMessage(L(
                        "IA non disponible. Le contexte a ete charge mais l'analyse IA n'a pas pu etre lancee. Verifie le modele local.",
                        "AI unavailable. Context loaded but AI analysis could not run. Check the local model.",
                        "IA no disponible. Contexto cargado pero el analisis IA no pudo ejecutarse. Verifica el modelo local."));
                    LogAnalysisTrace(traceId, "PARTIAL", "model_unavailable", null, null, null, analysisWatch.ElapsedMilliseconds);
                    progress.Report(new AnalysisProgressUpdate(100, L("Contexte charge (IA indisponible).", "Context loaded (AI unavailable).", "Contexto cargado (IA no disponible)."), false));
                    return;
                }

                progress.Report(new AnalysisProgressUpdate(40, L("Analyse IA en cours...", "Running AI analysis...", "Ejecutando analisis IA..."), true));

                var langCode = App.CurrentLanguage;
                var contextText = LoadedContext.ToPromptText();

                // Build a focused analysis prompt — simpler than the full chat prompt
                var analysisSystemPrompt = BuildAnalysisSystemPrompt(langCode);
                var analysisUserPrompt = BuildAnalysisUserPrompt(contextText, langCode);

                var modelName = Path.GetFileName(_settings.ModelPath);
                var promptChars = analysisSystemPrompt.Length + analysisUserPrompt.Length;
                var contextPackStats =
                    $"runId={CurrentRunHeader?.RunId} sources={ContextSources} contextChars={contextText.Length} estTokens={LoadedContext.EstimatedTokens} truncated={LoadedContext.Truncated} excludedFindings={LoadedContext.ExcludedFindingsCount} excludedSections={LoadedContext.ExcludedSections.Count}";
                Log($"[AI][Analyze][{traceId}] LLM START lang={langCode} systemLen={analysisSystemPrompt.Length} userLen={analysisUserPrompt.Length} totalPromptChars={promptChars} maxTokens={_settings.MaxTokens} contextWindow={_settings.ContextWindow} timeoutSec={_settings.TimeoutSeconds} model={modelName} {contextPackStats}");
                AppendTraceLog(
                    traceId,
                    $"PRE_LLM_CALL model={modelName} n_ctx={_settings.ContextWindow} maxTokens={_settings.MaxTokens} timeoutMs={_settings.TimeoutSeconds * 1000} promptChars={promptChars} {contextPackStats}");

                using var analysisCts = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
                analysisCts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

                var inferenceWatch = Stopwatch.StartNew();
                long analysisTtftMs = -1;
                var analysisChunks = 0;
                string rawLlmOutput;
                bool wasCancelled = false;
                Exception? inferenceException = null;
                try
                {
                    if (_settings.EnableStreaming)
                    {
                        var sb = new StringBuilder();
                        await foreach (var token in _client.StreamAsync(analysisSystemPrompt, analysisUserPrompt, analysisCts.Token))
                        {
                            if (analysisTtftMs < 0)
                            {
                                analysisTtftMs = inferenceWatch.ElapsedMilliseconds;
                                AppendTraceLog(traceId, $"FIRST_TOKEN ttftMs={analysisTtftMs}");
                            }
                            analysisChunks++;
                            sb.Append(token);
                        }
                        if (sb.Length < 80)
                        {
                            AppendTraceLog(traceId, $"STREAM_NO_PREVIEW short_output_len={sb.Length}");
                        }
                        rawLlmOutput = sb.ToString().Trim();
                    }
                    else
                    {
                        rawLlmOutput = await _client.GenerateAsync(analysisSystemPrompt, analysisUserPrompt, analysisCts.Token);
                        analysisTtftMs = inferenceWatch.ElapsedMilliseconds;
                        analysisChunks = 1;
                    }
                }
                catch (OperationCanceledException ex)
                {
                    wasCancelled = true;
                    inferenceException = ex;
                    rawLlmOutput = string.Empty;
                    throw; // re-throw to be caught by the outer catch
                }
                inferenceWatch.Stop();

                Log($"[AI][Analyze][{traceId}] LLM DONE inferenceMs={inferenceWatch.ElapsedMilliseconds} ttftMs={analysisTtftMs} chunks={analysisChunks} rawLen={rawLlmOutput.Length} wasCancelled={wasCancelled} exception={inferenceException?.GetType().Name}:{inferenceException?.Message}");
                AppendTraceLog(
                    traceId,
                    $"POST_LLM_CALL elapsedMs={inferenceWatch.ElapsedMilliseconds} ttftMs={analysisTtftMs} chunks={analysisChunks} rawLen={rawLlmOutput.Length} wasCancelled={wasCancelled} exceptionType={inferenceException?.GetType().Name} exceptionMessage={inferenceException?.Message}");
                WriteTraceFile(traceId, "RAW_OUTPUT.txt", rawLlmOutput);
                LogAnalysisTrace(traceId, "RAW_OUTPUT", null, rawLlmOutput, null, null, inferenceWatch.ElapsedMilliseconds);

                progress.Report(new AnalysisProgressUpdate(85, L("Traitement de la reponse...", "Processing response...", "Procesando respuesta..."), true));

                // --- Step 3: Parse and sanitize ---
                var parseWatch = Stopwatch.StartNew();
                var parsed = LlmResponseParser.Parse(rawLlmOutput, langCode);
                parseWatch.Stop();
                Log($"[AI][Analyze][{traceId}] parseMs={parseWatch.ElapsedMilliseconds} parse_success={parsed.ParseSuccess} parse_error={parsed.ParseError ?? "none"} user_response_len={parsed.UserResponse?.Length ?? 0}");
                AppendTraceLog(traceId, $"PARSED parseMs={parseWatch.ElapsedMilliseconds} parse_success={parsed.ParseSuccess} parse_error={parsed.ParseError ?? "none"} user_response_len={parsed.UserResponse?.Length ?? 0}");

                if (parsed.ParseSuccess)
                {
                    WriteTraceFile(traceId, "PARSED_JSON.txt", JsonSerializer.Serialize(parsed, _indentedJsonOptions));
                }
                else
                {
                    WriteTraceFile(traceId, "PARSE_ERRORS.txt", $"ParseSuccess={parsed.ParseSuccess}\nParseError={parsed.ParseError}\nUserResponseLen={parsed.UserResponse?.Length ?? 0}\nRawInputLen={parsed.RawInput?.Length ?? 0}");
                }

                string displayText;
                string displayMode;
                if (parsed.ParseSuccess && !string.IsNullOrWhiteSpace(parsed.UserResponse))
                {
                    var sanitized = LlmOutputSanitizer.SanitizeChatAssistantOutput(parsed.UserResponse, langCode);
                    Log($"[AI][Analyze][{traceId}] sanitized: truncated={sanitized.TruncatedAtInvalidPattern} fallback={sanitized.FallbackApplied} trigger='{sanitized.TriggerPattern}' rejected={sanitized.RejectedByWhitelist} keptLines={sanitized.KeptLines} droppedLines={sanitized.DroppedLines} firstDropReason={sanitized.FirstDropReason ?? "none"}");
                    WriteTraceFile(traceId, "SANITIZED_OUTPUT.txt", sanitized.Text);
                    LogAnalysisTrace(traceId, "SANITIZED", null, null, parsed.UserResponse, sanitized.Text, 0);
                    AppendTraceLog(traceId, $"SANITIZER keptLines={sanitized.KeptLines} droppedLines={sanitized.DroppedLines} firstDropReason={sanitized.FirstDropReason ?? "none"}");

                    if (sanitized.FallbackApplied && !string.IsNullOrWhiteSpace(rawLlmOutput))
                    {
                        // RAW OUTPUT FALLBACK MODE: sanitizer killed all lines but LLM produced text.
                        // Show raw text (after stripping internal <think> markers) instead of generic fallback.
                        var effectiveRaw = GetEffectiveRawForDisplay(rawLlmOutput);
                        displayText = L(
                                "[Mode texte brut — le formatage IA a echoue]\n\n",
                                "[Raw text mode — AI formatting failed]\n\n",
                                "[Modo texto sin formato — el formato IA fallo]\n\n")
                            + effectiveRaw;
                        displayMode = "RawText";
                        AppendTraceLog(traceId, $"DISPLAY_MODE=RawText reason=sanitizer_fallback_but_raw_has_text rawLen={rawLlmOutput.Length} effectiveRawLen={effectiveRaw.Length}");
                    }
                    else
                    {
                        displayText = sanitized.Text;
                        displayMode = sanitized.FallbackApplied ? "FallbackGeneric" : "Parsed";
                    }
                }
                else
                {
                    // Parse failed — use raw text with light trimming (not the strict line-by-line sanitizer)
                    var trimmed = LlmOutputSanitizer.TrimAtFirstControlPattern(
                        parsed.UserResponse ?? rawLlmOutput, out var trigger);
                    Log($"[AI][Analyze][{traceId}] fallback trim: trigger='{trigger}' resultLen={trimmed.Length}");

                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        var effectiveRaw = GetEffectiveRawForDisplay(trimmed);
                        displayText = string.IsNullOrWhiteSpace(effectiveRaw)
                            ? L(
                                "[Mode texte brut — le formatage IA a echoue]\n\n" +
                                "L'IA a renvoye uniquement du texte interne, sans reponse exploitable.",
                                "[Raw text mode — AI formatting failed]\n\n" +
                                "The AI returned only internal reasoning text, with no usable answer.",
                                "[Modo texto sin formato — el formato IA fallo]\n\n" +
                                "La IA devolvio solo texto interno, sin una respuesta util.")
                            : effectiveRaw;
                        displayMode = "RawText";
                    }
                    else if (!string.IsNullOrWhiteSpace(rawLlmOutput))
                    {
                        // RAW OUTPUT FALLBACK MODE: trim removed everything but raw has content
                        var effectiveRaw = GetEffectiveRawForDisplay(rawLlmOutput);
                        displayText = L(
                                "[Mode texte brut — le formatage IA a echoue]\n\n",
                                "[Raw text mode — AI formatting failed]\n\n",
                                "[Modo texto sin formato — el formato IA fallo]\n\n")
                            + effectiveRaw;
                        displayMode = "RawText";
                    }
                    else
                    {
                        displayText = LlmOutputSanitizer.BuildFallback(langCode);
                        displayMode = "FallbackGeneric";
                        Log($"[AI][Analyze][{traceId}] empty after trim AND raw empty → built fallback");
                    }
                    LogAnalysisTrace(traceId, "FALLBACK", null, null, parsed.UserResponse, displayText, 0);
                }

                AppendTraceLog(traceId, $"DISPLAY_MODE={displayMode} displayLen={displayText.Length}");
                Log($"[AI][Analyze][{traceId}] DISPLAY_MODE={displayMode} displayLen={displayText.Length}");

                // Display the analysis result as an assistant message
                await DispatchAsync(() =>
                {
                    var templated = EnsureQuestionFirstAnswer(
                        L("Analyser le scan selectionne", "Analyze selected run", "Analizar el run seleccionado"),
                        displayText,
                        LoadedContext!,
                        out _,
                        out _);
                    Messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Assistant,
                        Content = SanitizeForUi(templated)
                    });
                    OnPropertyChanged(nameof(CanCopyLastAssistantMessage));
                });

                // If agent_payload requests pipeline trigger, schedule it
                if (parsed.ParseSuccess && parsed.AgentPayload?.TriggerPipeline == true)
                {
                    Log($"[AI][Analyze][{traceId}] agent_payload.trigger_pipeline=true");
                    _ = TryActivateAutoFixFromAgentPayloadAsync(parsed.AgentPayload);
                }

                LogAnalysisTrace(traceId, "DONE", null, null, null, null, analysisWatch.ElapsedMilliseconds);
                progress.Report(new AnalysisProgressUpdate(100, L("Analyse terminee.", "Analysis complete.", "Analisis completado."), false));
            }
            catch (OperationCanceledException)
            {
                AddSystemMessage(_cancelRequestedByUser
                    ? L("Analyse du run annulee.", "Run analysis cancelled.", "Analisis del run cancelado.")
                    : L(
                        "Analyse interrompue (delai depasse ou annulation).",
                        "Analysis interrupted (timeout or cancellation).",
                        "Analisis interrumpido (tiempo agotado o cancelacion)."));
                LogAnalysisTrace(traceId, "CANCELLED", _cancelRequestedByUser ? "user" : "timeout", null, null, null, analysisWatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                AddSystemMessage(L(
                    "Erreur pendant l'analyse du run. Consulte les logs IA.",
                    "Error during run analysis. Check AI logs.",
                    "Error durante el analisis del run. Revisa los logs de IA."));
                Log($"[AI][Analyze][{traceId}] ERROR: {ex}");
                LogAnalysisTrace(traceId, "ERROR", ex.Message, null, null, null, analysisWatch.ElapsedMilliseconds);
            }
            finally
            {
                IsStreaming = false;
                IsAnalyzing = false;
                _cancelRequestedByUser = false;

                ResetAnalysisProgress();

                _cts?.Dispose();
                _cts = null;
                RaiseComputedState();
            }
        }

        private async Task GenerateAutoFixScriptAsync(object? _)
        {
            if (SelectedRun == null)
            {
                AddSystemMessage(L(
                    "Selectionne d'abord un run.",
                    "Select a run first.",
                    "Selecciona primero un run."));
                return;
            }

            if (LoadedContext == null || CurrentRunHeader == null)
            {
                await AnalyseRunInternalAsync(SelectedRun).ConfigureAwait(false);
                if (LoadedContext == null || CurrentRunHeader == null)
                {
                    return;
                }
            }

            if (!IsModelAvailable || _client == null)
            {
                AddSystemMessage(L(
                    "IA non disponible. Verifie le modele local.",
                    "AI unavailable. Check the local model.",
                    "IA no disponible. Verifica el modelo local."));
                return;
            }

            IsStreaming = true;
            IsAnalyzing = true;
            _cancelRequestedByUser = false;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            PipelineLogs.Clear();
            ResetAgentCards();
            ShowAgentTimeline = true;
            IProgress<AnalysisProgressUpdate> progress = new Progress<AnalysisProgressUpdate>(ApplyAnalysisProgress);
            progress.Report(new AnalysisProgressUpdate(10, "Pipeline agents...", true));

            try
            {
                EnsureOrchestratorInitialized();
                if (_orchestrator == null)
                {
                    AddSystemMessage(L(
                        "Pipeline IA indisponible.",
                        "AI pipeline unavailable.",
                        "Pipeline de IA no disponible."));
                    return;
                }

                var lastUserMsg = Messages
                    .LastOrDefault(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Content))
                    ?.Content ?? string.Empty;
                var caseSummarySnippet = string.IsNullOrWhiteSpace(_caseSummary)
                    ? string.Empty
                    : _caseSummary.Length > 400
                        ? _caseSummary[..397] + "..."
                        : _caseSummary;
                var goalParts = new List<string>
                {
                    "Generate a safe, deterministic PowerShell AutoFix script for the critical issues found in this scan."
                };
                if (!string.IsNullOrWhiteSpace(caseSummarySnippet))
                    goalParts.Add($"Conversation context: {caseSummarySnippet}");
                if (!string.IsNullOrWhiteSpace(lastUserMsg) && lastUserMsg.Length <= 300)
                    goalParts.Add($"User's last request: {lastUserMsg}");
                var goal = string.Join(" ", goalParts);
                Log($"[AI][AutoFix] goal built: len={goal.Length} hasSummary={!string.IsNullOrWhiteSpace(caseSummarySnippet)} hasLastMsg={!string.IsNullOrWhiteSpace(lastUserMsg)}");
                var pipelineStart = Stopwatch.StartNew();
                _lastReport = await _orchestrator.RunPipelineAsync(
                    goal,
                    LoadedContext,
                    CurrentRunHeader.RunId,
                    CurrentRunHeader,
                    _cts.Token).ConfigureAwait(false);
                pipelineStart.Stop();

                progress.Report(new AnalysisProgressUpdate(78, "Synthese...", true));

                SecurityVerdict = _lastReport.JudgeResult;
                GeneratedScript = _lastReport.FinalScript;

                MergePlanWithAiReport(_lastReport);
                _autoFixGate = _safety.EvaluateForAutoFix(GeneratedScript, SecurityVerdict);
                _scriptGeneratedByPipeline = true;

                // Populate verdict card collections.
                RefreshVerdictCardCollections();

                // Compute diff summary between Agent1 draft and Agent3 refined
                AgentDiffSummary = BuildDiffSummary(_lastReport);
                if (!string.IsNullOrWhiteSpace(GeneratedScript))
                    AgentScriptPreview = GeneratedScript.Length > 800
                        ? GeneratedScript[..797] + "\n..."
                        : GeneratedScript;

                var metrics = new AiPipelineMetrics
                {
                    Stage = "script_pipeline",
                    RunId = CurrentRunHeader.RunId,
                    ContextChars = LoadedContext.ToPromptText().Length,
                    ContextTokensEst = LoadedContext.EstimatedTokens,
                    InferenceMs = pipelineStart.ElapsedMilliseconds,
                    PromptChars = 0,
                    PromptTokensEst = 0,
                    GeneratedTokens = Math.Max(1, GeneratedScript.Length / 4),
                    TokensPerSecond = pipelineStart.ElapsedMilliseconds > 0
                        ? (Math.Max(1, GeneratedScript.Length / 4) * 1000.0) / pipelineStart.ElapsedMilliseconds
                        : 0,
                    ModelName = Path.GetFileName(_settings.ModelPath)
                };
                _lastReport.PipelineMetrics.Add(metrics);
                Log($"[AI][PipelineMetrics] {metrics.ToLogLine()}");

                AddSystemMessage(BuildPipelineSummaryMessage());
                progress.Report(new AnalysisProgressUpdate(100, "Pipeline termine.", false));
            }
            catch (OperationCanceledException)
            {
                AddSystemMessage(_cancelRequestedByUser
                    ? L("Pipeline annule.", "Pipeline cancelled.", "Pipeline cancelado.")
                    : L("Pipeline interrompu.", "Pipeline interrupted.", "Pipeline interrumpido."));
            }
            catch (Exception ex)
            {
                AddSystemMessage(L(
                    "Erreur pendant la generation du script AutoFix.",
                    "Error while generating AutoFix script.",
                    "Error al generar el script de AutoFix."));
                Log($"[AI] GenerateAutoFixScriptAsync error: {ex}");
            }
            finally
            {
                IsStreaming = false;
                IsAnalyzing = false;
                _cancelRequestedByUser = false;
                ResetAnalysisProgress();
                StopAgentElapsedTimer();
                // Keep timeline visible so user can see final results; hide on next pipeline start.
                _cts?.Dispose();
                _cts = null;
                RaiseComputedState();
            }
        }

        private async Task ExecuteAutoFixAsync(object? _)
        {
            if (!_scriptGeneratedByPipeline || _lastReport == null || string.IsNullOrWhiteSpace(GeneratedScript))
            {
                MessageBox.Show(
                    L(
                        "AutoFix indisponible: aucun script final issu du pipeline 4 agents.",
                        "AutoFix unavailable: no final script from the 4-agent pipeline.",
                        "AutoFix no disponible: no hay script final del pipeline de 4 agentes."),
                    "AutoFix",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_autoFixGate?.IsApproved != true)
            {
                var reason = _autoFixGate?.Reasons.Count > 0
                    ? string.Join("\n- ", _autoFixGate.Reasons)
                    : L("Raison non disponible.", "Reason unavailable.", "Razon no disponible.");
                MessageBox.Show(
                    L(
                        $"AutoFix bloque par la politique de securite.\n\n- {reason}",
                        $"AutoFix blocked by safety policy.\n\n- {reason}",
                        $"AutoFix bloqueado por la politica de seguridad.\n\n- {reason}"),
                    L("AutoFix bloque", "AutoFix blocked", "AutoFix bloqueado"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Strict pre-execution validation: never execute a script that fails the latest deterministic gate.
            var preflightJudge = _safety.Analyse(GeneratedScript);
            var preflightGate = _safety.EvaluateForAutoFix(GeneratedScript, preflightJudge);
            if (!preflightGate.IsApproved)
            {
                _autoFixGate = preflightGate;
                SecurityVerdict = preflightJudge;
                var reason = preflightGate.Reasons.Count > 0
                    ? string.Join("\n- ", preflightGate.Reasons)
                    : L("Raison non disponible.", "Reason unavailable.", "Razon no disponible.");

                MessageBox.Show(
                    L(
                        $"AutoFix bloque apres validation stricte pre-execution.\n\n- {reason}",
                        $"AutoFix blocked after strict pre-execution validation.\n\n- {reason}",
                        $"AutoFix bloqueado tras validacion estricta previa a ejecucion.\n\n- {reason}"),
                    L("Validation AutoFix", "AutoFix validation", "Validacion AutoFix"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                RaiseComputedState();
                return;
            }

            if (_settings.RequireUserConfirmation)
            {
                var summary = BuildExecutionSummary(_lastReport);
                var confirm = MessageBox.Show(
                    summary,
                    L("Confirmer l'execution AutoFix", "Confirm AutoFix execution", "Confirmar ejecucion AutoFix"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            IsExecutingAutofix = true;
            _cancelRequestedByUser = false;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                var runId = CurrentRunHeader?.RunId
                    ?? LoadedContext?.RunId
                    ?? _lastReport.RunId
                    ?? "unknown";
                var requiresAdmin = _lastReport.ScriptDraft?.RequiresAdmin == true;
                var simulateOnly = _settings.EnableSimulationForMutatingScripts && _safety.RequiresSimulation(GeneratedScript);
                var scriptHash = ComputeScriptSha256(GeneratedScript);
                Log($"[AI][AutoFix] START runId={runId} scriptHash={scriptHash} requiresAdmin={requiresAdmin} simulateOnly={simulateOnly}");

                var execution = await _powerShellExecutor.ExecuteAsync(
                    runId,
                    GeneratedScript,
                    requiresAdmin,
                    _settings.TimeoutSeconds,
                    simulateOnly,
                    _cts.Token);

                _lastReport.ExecutionLogs = execution.Logs;
                _lastReport.ExecutionWorkingDirectory = execution.WorkingDirectory;
                _lastReport.ExecutionLogPath = execution.ExecutionLogPath;
                _lastReport.TranscriptPath = execution.TranscriptPath;
                _lastReport.RebootRequired = _lastReport.RebootRequired || execution.RebootRequired;
                _lastReport.RebootReason = _lastReport.RebootRequired
                    ? "Execution output and/or system state indicates reboot required."
                    : string.Empty;

                var reportPath = Path.Combine(execution.WorkingDirectory, "AiRunReport.json");
                var reportJson = JsonSerializer.Serialize(_lastReport, _indentedJsonOptions);
                File.WriteAllText(reportPath, reportJson, Encoding.UTF8);
                Log($"[AI][AutoFix] END runId={runId} scriptHash={scriptHash} exitCode={execution.ExitCode} log={execution.ExecutionLogPath} transcript={execution.TranscriptPath}");

                AddSystemMessage(simulateOnly
                    ? L(
                        $"Simulation AutoFix terminee. ExitCode={execution.ExitCode}. Aucun changement n'a ete applique.",
                        $"AutoFix simulation completed. ExitCode={execution.ExitCode}. No changes were applied.",
                        $"Simulacion AutoFix terminada. ExitCode={execution.ExitCode}. No se aplicaron cambios.")
                    : L(
                        $"AutoFix termine. ExitCode={execution.ExitCode}. Les journaux sont sauvegardes.",
                        $"AutoFix completed. ExitCode={execution.ExitCode}. Logs were saved.",
                        $"AutoFix terminado. ExitCode={execution.ExitCode}. Los logs fueron guardados."));

                if (!simulateOnly && _lastReport.RebootRequired)
                {
                    var askReboot = MessageBox.Show(
                        L(
                            "AutoFix termine. Un redemarrage est necessaire pour appliquer les modifications. Redemarrer maintenant ?",
                            "AutoFix completed. A reboot is required to apply changes. Reboot now?",
                            "AutoFix terminado. Se requiere reinicio para aplicar cambios. Reiniciar ahora?"),
                        L("Redemarrage requis", "Reboot required", "Reinicio requerido"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question,
                        MessageBoxResult.No);

                    if (askReboot == MessageBoxResult.Yes)
                    {
                        var finalConfirm = MessageBox.Show(
                            L(
                                "Le systeme va redemarrer immediatement. Confirmer ?",
                                "System will reboot immediately. Confirm?",
                                "El sistema se reiniciara inmediatamente. Confirmar?"),
                            L("Confirmation finale", "Final confirmation", "Confirmacion final"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning,
                            MessageBoxResult.No);
                        if (finalConfirm == MessageBoxResult.Yes)
                        {
                            PowerShellExecutor.RestartNow();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AddSystemMessage(L(
                    "Execution AutoFix annulee.",
                    "AutoFix execution cancelled.",
                    "Ejecucion de AutoFix cancelada."));
            }
            catch (Exception ex)
            {
                AddSystemMessage(L(
                    "Echec de l'execution AutoFix. Consulte les logs techniques.",
                    "AutoFix execution failed. Check technical logs.",
                    "Fallo en la ejecucion de AutoFix. Revisa los logs tecnicos."));
                Log($"[AI] ExecuteAutoFixAsync error: {ex}");
            }
            finally
            {
                IsExecutingAutofix = false;
                _cancelRequestedByUser = false;
                _cts?.Dispose();
                _cts = null;
                RaiseComputedState();
            }
        }

        private async void PrefetchProblemDetectionAsync(ScanRunEntry? run)
        {
            if (run == null)
            {
                LoadedContext = null;
                ContextSources = string.Empty;
                CurrentRunHeader = null;
                return;
            }

            try
            {
                var load = await LoadContextForRunAsync(run.CombinedJsonPath).ConfigureAwait(false);
                if (!ReferenceEquals(SelectedRun, run))
                {
                    return; // Run changed while we were loading — discard stale result
                }

                RunOnUiThread(() =>
                {
                    if (load.combined != null)
                    {
                        _loadedCombined = load.combined;
                        if (load.context != null)
                        {
                            LoadedContext = load.context;
                            ContextSources = string.Join(", ", load.context.SourcesUsed);
                            CurrentRunHeader = load.header;
                            Log($"[AI][ContextPipeline] prefetch run={run.DisplayName} hash={load.fileHash} cacheHit={load.cacheHit} parseMs={load.parseMs} contextBuildMs={load.contextBuildMs} contextTokens={load.context.EstimatedTokens}");
                        }
                    }
                    else
                    {
                        LoadedContext = null;
                        ContextSources = string.Empty;
                        CurrentRunHeader = null;
                        Log($"[AI] PrefetchProblemDetection: JSON not loaded for run={run.CombinedJsonPath}");
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"[AI] PrefetchProblemDetection failed: {ex.Message}");
                RunOnUiThread(() =>
                {
                    LoadedContext = null;
                    ContextSources = string.Empty;
                });
            }
        }

        private void Cancel()
        {
            _cancelRequestedByUser = true;
            _cts?.Cancel();
            _qwen3DownloadCts?.Cancel();
        }

        private void ClearChat()
        {
            Messages.Clear();
        }

        private void CopyLastAssistantMessage()
        {
            var text = Messages
                .LastOrDefault(m => m.IsAssistant && !string.IsNullOrWhiteSpace(m.Content))
                ?.Content;

            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
                AddSystemMessage("Derniere reponse copiee dans le presse-papiers.");
            }
            catch (Exception ex)
            {
                Log($"[AI] Clipboard copy failed: {ex.Message}");
                AddSystemMessage("Impossible de copier la reponse dans le presse-papiers.");
            }
        }

        public void ReportViewLoadFailure(string message, string crashLogPath, Exception? exception = null)
        {
            var safeMessage = string.IsNullOrWhiteSpace(message)
                ? "Chat & Support failed to load: click to open log"
                : SanitizeForUi(message);
            var safeDetails = exception == null
                ? string.Empty
                : SanitizeForUi($"{exception.GetType().Name}: {exception.Message}");

            Log($"[AI][ChatSupportView] load failure. message={safeMessage} log={crashLogPath} ex={exception}");

            RunOnUiThread(() =>
            {
                LoadFailureMessage = safeMessage;
                LoadFailureDetails = safeDetails;
                LoadFailureLogPath = crashLogPath ?? string.Empty;
                HasLoadFailure = true;
            });
        }

        public void NotifyLocaleChanged()
        {
            OnPropertyChanged(nameof(CurrentIaLocaleLabel));
            OnPropertyChanged(nameof(AiLocaleStatusLine));
        }

        private void OpenLoadFailureLog()
        {
            if (string.IsNullOrWhiteSpace(LoadFailureLogPath) || !File.Exists(LoadFailureLogPath))
            {
                AddSystemMessage("Fichier de log introuvable.");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = LoadFailureLogPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log($"[AI] OpenLoadFailureLog failed: {ex}");
                AddSystemMessage("Impossible d'ouvrir le fichier de log.");
            }
        }

        private void CopyLoadFailureLogPath()
        {
            if (string.IsNullOrWhiteSpace(LoadFailureLogPath))
            {
                return;
            }

            try
            {
                Clipboard.SetText(LoadFailureLogPath);
                AddSystemMessage("Chemin du log copie dans le presse-papiers.");
            }
            catch (Exception ex)
            {
                Log($"[AI] CopyLoadFailureLogPath failed: {ex}");
                AddSystemMessage("Impossible de copier le chemin du log.");
            }
        }

        private void CopyPipelineDiagLog()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Pipeline Diagnostic Log ===");
            if (_lastReport != null)
            {
                sb.AppendLine($"TraceId  : {_lastReport.AiRunId}");
                sb.AppendLine($"RunId    : {_lastReport.RunId}");
                sb.AppendLine($"Model    : {_lastReport.ModelName}");
                sb.AppendLine();
                sb.AppendLine("--- Agents ---");
                foreach (var step in _lastReport.Steps)
                    sb.AppendLine($"  [{step.Status}] {step.AgentName} — {step.Error ?? "ok"}");
                sb.AppendLine();
            }

            if (SecurityVerdict != null)
            {
                var j = SecurityVerdict;
                sb.AppendLine("--- Safety Verdict ---");
                sb.AppendLine($"  Verdict   : {j.VerdictDisplay}");
                sb.AppendLine($"  Global    : {j.OverallScore0_100}/100");
                sb.AppendLine($"  Security  : {j.SecurityScore0_100}/100");
                sb.AppendLine($"  Relevance : {j.RelevanceScore0_100}/100");
                sb.AppendLine($"  Robustness: {j.RobustnessScore0_100}/100");
                sb.AppendLine($"  UX        : {j.UxScore0_100}/100");
                sb.AppendLine($"  Accuracy  : {j.AccuracyScore0_100}/100");
                sb.AppendLine($"  Minimality: {j.MinimalityScore0_100}/100");
                sb.AppendLine($"  Reversibl : {j.ReversibilityScore0_100}/100");
                sb.AppendLine($"  Efficiency: {j.EfficiencyScore0_100}/100");
                sb.AppendLine($"  Readabilty: {j.ReadabilityScore0_100}/100");
                sb.AppendLine($"  JudgeError: {j.JudgeError} {(string.IsNullOrWhiteSpace(j.JudgeErrorMessage) ? string.Empty : $"({j.JudgeErrorMessage})")}");
                sb.AppendLine();
                sb.AppendLine("--- Reasons ---");
                foreach (var r in j.Reasons)
                    sb.AppendLine($"  {r}");
                sb.AppendLine();
                sb.AppendLine("--- Flags ---");
                foreach (var f in j.Flags)
                    sb.AppendLine($"  {f}");
                sb.AppendLine();
                if (j.Violations.Count > 0)
                {
                    sb.AppendLine("--- Violations ---");
                    foreach (var v in j.Violations.Take(12))
                        sb.AppendLine($"  {v.Severity} {v.Code} | evidence={v.EvidenceLine} | fix={v.Fix}");
                    sb.AppendLine();
                }
            }

            if (_autoFixGate != null)
            {
                sb.AppendLine("--- AutoFix Gate ---");
                sb.AppendLine($"  IsApproved : {_autoFixGate.IsApproved}");
                sb.AppendLine($"  BlockedBy  : {_autoFixGate.BlockedBy}");
                foreach (var r in _autoFixGate.BlockingReasons)
                    sb.AppendLine($"  [BLOCK] {r}");
                foreach (var w in _autoFixGate.WarningReasons)
                    sb.AppendLine($"  [WARN]  {w}");
            }

            sb.AppendLine("=== End ===");

            try
            {
                Clipboard.SetText(sb.ToString());
                AddSystemMessage(L(
                    "Journal de diagnostic copie dans le presse-papiers.",
                    "Diagnostic log copied to clipboard.",
                    "Log de diagnostico copiado al portapapeles."));
            }
            catch (Exception ex)
            {
                Log($"[AI] Clipboard copy failed: {ex.Message}");
            }
        }

        private void OpenLiveScriptComposer()
        {
            if (_lastReport == null)
            {
                AddSystemMessage(L(
                    "Aucun run AutoFix a afficher.",
                    "No AutoFix run available to display.",
                    "No hay ejecucion AutoFix para mostrar."));
                return;
            }

            try
            {
                var window = new LiveScriptComposerWindow(_lastReport)
                {
                    Owner = Application.Current?.MainWindow
                };
                window.Show();
            }
            catch (Exception ex)
            {
                Log($"[AI] OpenLiveScriptComposer failed: {ex}");
                AddSystemMessage(L(
                    "Impossible d'ouvrir Live Script Composer.",
                    "Unable to open Live Script Composer.",
                    "No se pudo abrir Live Script Composer."));
            }
        }

        private void OpenAutoFixLogsFolder()
        {
            var traceDir = _lastReport?.AutoFixTraceDirectory ?? string.Empty;
            if (string.IsNullOrWhiteSpace(traceDir) || !Directory.Exists(traceDir))
            {
                AddSystemMessage(L(
                    "Dossier de logs AutoFix introuvable.",
                    "AutoFix logs folder not found.",
                    "Carpeta de logs AutoFix no encontrada."));
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = traceDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log($"[AI] OpenAutoFixLogsFolder failed: {ex}");
                AddSystemMessage(L(
                    "Impossible d'ouvrir le dossier de logs.",
                    "Unable to open logs folder.",
                    "No se pudo abrir la carpeta de logs."));
            }
        }

        private void ToggleInferenceMode()
        {
            _settings.InferenceMode = IsApiMode ? "Local" : "API";
            _settings.Normalize();
            if (AiSettingsLoader.Save(_settings, _settingsPath, out var savedPath) && !string.IsNullOrWhiteSpace(savedPath))
            {
                _settingsPath = savedPath;
            }

            DetachOrchestratorEvents();
            _orchestrator = null;
            _runtimeHost = LlmRuntimeHost.GetOrCreate(_settings);
            _client = null;
            _modelLoader = null;
            EnsureLlmRuntime();
            _ = InitializeModelAsync(reload: true);

            AddSystemMessage(IsApiMode
                ? L("Mode IA: API actif.", "AI mode: API active.", "Modo IA: API activo.")
                : L("Mode IA: Local actif.", "AI mode: Local active.", "Modo IA: Local activo."));

            RaiseComputedState();
        }

        private void OpenAddApiModal()
        {
            try
            {
                var existing = _settings.ApiProvider ?? new ApiProviderSettings();
                var window = new AddApiWindow(existing)
                {
                    Owner = Application.Current?.MainWindow
                };

                if (window.ShowDialog() != true)
                {
                    return;
                }

                var updated = window.Result;
                var fallbackEncryption = false;
                if (!string.IsNullOrWhiteSpace(window.ApiKeyPlaintext))
                {
                    updated.EncryptedApiKey = _apiSecretProtector.Protect(window.ApiKeyPlaintext, out fallbackEncryption);
                }
                else
                {
                    // Keep previous secret if user saves without entering a new key.
                    updated.EncryptedApiKey = existing.EncryptedApiKey;
                }

                _settings.ApiProvider = updated;
                _settings.InferenceMode = "API";
                _settings.Normalize();
                if (AiSettingsLoader.Save(_settings, _settingsPath, out var savedPath) && !string.IsNullOrWhiteSpace(savedPath))
                {
                    _settingsPath = savedPath;
                }

                DetachOrchestratorEvents();
                _orchestrator = null;
                _runtimeHost = LlmRuntimeHost.GetOrCreate(_settings);
                _client = null;
                _modelLoader = null;
                EnsureLlmRuntime();
                _ = InitializeModelAsync(reload: true);

                AddSystemMessage(L(
                    "Configuration API enregistree. Mode API active.",
                    "API configuration saved. API mode is active.",
                    "Configuracion API guardada. El modo API esta activo."));

                if (fallbackEncryption)
                {
                    AddSystemMessage(L(
                        "Avertissement: DPAPI indisponible, chiffrement local de secours utilise.",
                        "Warning: DPAPI unavailable, local fallback encryption used.",
                        "Aviso: DPAPI no disponible, se uso cifrado local alternativo."));
                }

                RaiseComputedState();
            }
            catch (Exception ex)
            {
                Log($"[AI] OpenAddApiModal failed: {ex}");
                AddSystemMessage(L(
                    "Impossible d'enregistrer la configuration API.",
                    "Unable to save API configuration.",
                    "No se pudo guardar la configuracion API."));
            }
        }

        private void CopyMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Log($"[AI] CopyMessage clipboard failed: {ex.Message}");
            }
        }

        private void ToggleMessageExpand(ChatMessage? message)
        {
            if (message == null) return;
            message.IsExpanded = !message.IsExpanded;
        }

        private void OpenMessageWindow(ChatMessage? message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Content))
            {
                return;
            }

            var textBox = new System.Windows.Controls.TextBox
            {
                Text = message.Content,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var copyButton = new System.Windows.Controls.Button
            {
                Content = "Copier",
                HorizontalAlignment = HorizontalAlignment.Right,
                Width = 90,
                Height = 30
            };
            copyButton.Click += (_, _) =>
            {
                try { Clipboard.SetText(message.Content); } catch { }
            };

            var panel = new System.Windows.Controls.DockPanel { Margin = new Thickness(12) };
            System.Windows.Controls.DockPanel.SetDock(copyButton, System.Windows.Controls.Dock.Bottom);
            panel.Children.Add(copyButton);
            panel.Children.Add(textBox);

            var window = new Window
            {
                Title = "Reponse complete",
                Width = 980,
                Height = 720,
                MinWidth = 680,
                MinHeight = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow,
                Content = panel
            };

            window.ShowDialog();
        }

        private async Task LoadAvailableRunsAsync(object? _ = null)
        {
            var loadCts = new CancellationTokenSource();
            var previousLoad = Interlocked.Exchange(ref _loadRunsCts, loadCts);
            previousLoad?.Cancel();
            previousLoad?.Dispose();

            RunOnUiThread(() =>
            {
                IsLoadingRuns = true;
                if (HasLoadFailure)
                {
                    HasLoadFailure = false;
                    LoadFailureMessage = string.Empty;
                    LoadFailureDetails = string.Empty;
                    LoadFailureLogPath = string.Empty;
                }
            });
            try
            {
                var discoveredRuns = await Task.Run(() => DiscoverAvailableRuns(loadCts.Token), loadCts.Token).ConfigureAwait(false);
                await DispatchAsync(() =>
                {
                    AvailableRuns.Clear();
                    foreach (var run in discoveredRuns)
                    {
                        AvailableRuns.Add(run);
                    }

                    if (SelectedRun != null &&
                        !discoveredRuns.Any(r => string.Equals(r.CombinedJsonPath, SelectedRun.CombinedJsonPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        SelectedRun = null;
                    }
                });
            }
            catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
            {
                Log("[AI] LoadAvailableRunsAsync cancelled.");
            }
            catch (Exception ex)
            {
                Log($"[AI] LoadAvailableRunsAsync error: {ex}");
                AddSystemMessage(L(
                    "Impossible de rafraichir la liste des scans.",
                    "Unable to refresh scan list.",
                    "No se pudo actualizar la lista de scans."));
            }
            finally
            {
                if (ReferenceEquals(_loadRunsCts, loadCts))
                {
                    _loadRunsCts = null;
                }

                loadCts.Dispose();
                RunOnUiThread(() =>
                {
                    IsLoadingRuns = false;
                    RaiseComputedState();
                });
            }
        }

        private List<ScanRunEntry> DiscoverAvailableRuns(CancellationToken token)
        {
            var runs = new List<ScanRunEntry>();

            var canonicalMetas = ScanStorageService.EnumerateScans();
            foreach (var meta in canonicalMetas
                         .Where(m => !string.IsNullOrWhiteSpace(m.CombinedJsonPath) && File.Exists(m.CombinedJsonPath))
                         .OrderByDescending(m => m.StartTime)
                         .Take(60))
            {
                token.ThrowIfCancellationRequested();

                var path = meta.CombinedJsonPath!;
                var modified = File.GetLastWriteTime(path);
                var statusSuffix = meta.Status switch
                {
                    ScanStatus.Success => string.Empty,
                    ScanStatus.Partial => " [PARTIAL]",
                    ScanStatus.Failed => " [FAILED]",
                    ScanStatus.Cancelled => " [CANCELLED]",
                    _ => " [RUNNING]"
                };

                runs.Add(new ScanRunEntry
                {
                    DisplayName = $"{meta.StartTime.ToLocalTime():yyyy-MM-dd HH:mm} - {meta.RunId}{statusSuffix}",
                    CombinedJsonPath = path,
                    LastModified = modified
                });
            }

            if (runs.Count > 0)
            {
                return runs;
            }

            var preferred = new List<string>();
            foreach (var reportsDir in GetReportSearchDirectories())
            {
                token.ThrowIfCancellationRequested();
                preferred.AddRange(Directory.GetFiles(reportsDir, "scan_result_combined*.json", SearchOption.AllDirectories));
            }

            if (preferred.Count == 0)
            {
                foreach (var reportsDir in GetReportSearchDirectories())
                {
                    token.ThrowIfCancellationRequested();
                    preferred.AddRange(Directory.GetFiles(reportsDir, "Scan_*.json", SearchOption.TopDirectoryOnly));
                }
            }

            foreach (var file in preferred
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(File.GetLastWriteTime)
                         .Take(40))
            {
                token.ThrowIfCancellationRequested();

                var modified = File.GetLastWriteTime(file);
                runs.Add(new ScanRunEntry
                {
                    DisplayName = $"{modified:yyyy-MM-dd HH:mm} - {Path.GetFileName(file)}",
                    CombinedJsonPath = file,
                    LastModified = modified
                });
            }

            return runs;
        }

        private static IEnumerable<string> GetReportSearchDirectories()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directories = new[]
            {
                ScanStorageService.BaseDir,
                Path.Combine(localAppData, App.AppDataFolderName, "Reports"),
                Path.Combine(localAppData, App.LegacyAppDataFolderName, "Rapports"),
                Path.Combine(localAppData, App.LegacyAppDataFolderName, "Reports")
            };

            return directories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists);
        }

        private void BuildActionPlanFromDeterministic(CombinedScanResult combined)
        {
            ManualPlanItems.Clear();
            AutoFixPlanItems.Clear();

            foreach (var item in _contextBuilder.BuildDeterministicPlan(combined))
            {
                if (item.Category == ActionPlanCategory.AutoFix)
                {
                    AutoFixPlanItems.Add(item);
                }
                else
                {
                    ManualPlanItems.Add(item);
                }
            }

            OnPropertyChanged(nameof(HasActionPlan));
        }

        private void MergePlanWithAiReport(AiRunReport report)
        {
            report.ManualActionPlan = ManualPlanItems.ToList();
            report.AutoFixActionPlan = AutoFixPlanItems.ToList();

            if (!string.IsNullOrWhiteSpace(report.FinalScript))
            {
                var item = new ActionPlanItem
                {
                    Title = "Run approved AutoFix script",
                    Description = "Execute generated script with confirmation and full logging.",
                    Reason = "Generated by 4-agent pipeline for current run.",
                    Source = "ai_pipeline",
                    Severity = "medium",
                    Category = ActionPlanCategory.AutoFix,
                    RequiresAdmin = report.ScriptDraft?.RequiresAdmin == true
                };

                if (!AutoFixPlanItems.Any(x => x.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    AutoFixPlanItems.Add(item);
                }
            }

            report.ManualActionPlan = ManualPlanItems.ToList();
            report.AutoFixActionPlan = AutoFixPlanItems.ToList();
            OnPropertyChanged(nameof(HasActionPlan));
        }

        private string BuildExecutionSummary(AiRunReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine(L(
                "Ce script AutoFix va modifier votre systeme. Continuer ?",
                "This AutoFix script will modify your system. Continue?",
                "Este script AutoFix modificara el sistema. Continuar?"));
            sb.AppendLine();
            sb.AppendLine(L("Ce qu'il fait :", "What it does:", "Que hace:"));
            sb.AppendLine(L(
                "- Applique uniquement les actions du script genere pour ce run.",
                "- Applies only actions generated for this run.",
                "- Aplica solo acciones generadas para este run."));
            sb.AppendLine(L(
                "- Ecrit des journaux complets et une transcription pour la tracabilite.",
                "- Writes full logs and transcript for traceability.",
                "- Escribe logs completos y transcripcion para trazabilidad."));
            sb.AppendLine();
            sb.AppendLine(L("Ce qu'il ne fait pas :", "What it does not do:", "Que no hace:"));
            sb.AppendLine(L("- Aucune execution cachee.", "- No hidden execution.", "- Ninguna ejecucion oculta."));
            sb.AppendLine(L(
                "- Aucune commande bloquee par la politique de securite.",
                "- No blocked command is allowed by policy.",
                "- Ningun comando bloqueado es permitido por politica."));
            sb.AppendLine();

            if (report.ScriptDraft?.Risks?.Count > 0)
            {
                sb.AppendLine(L("Risques :", "Risks:", "Riesgos:"));
                foreach (var risk in report.ScriptDraft.Risks.Take(5))
                {
                    sb.AppendLine($"- {risk}");
                }
            }

            if (report.ScriptDraft?.Rollback?.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(L("Retour arriere possible :", "Rollback options:", "Opciones de rollback:"));
                foreach (var rb in report.ScriptDraft.Rollback.Take(5))
                {
                    sb.AppendLine($"- {rb}");
                }
            }

            sb.AppendLine();
            sb.AppendLine(L(
                $"Verdict securite : {report.JudgeResult?.VerdictDisplay ?? "N/A"}",
                $"Security verdict: {report.JudgeResult?.VerdictDisplay ?? "N/A"}",
                $"Veredicto de seguridad: {report.JudgeResult?.VerdictDisplay ?? "N/A"}"));
            sb.AppendLine(L(
                $"Scores (global/securite/pertinence/robustesse/ux) : {report.JudgeResult?.OverallScore0_100 ?? 0}/100, {report.JudgeResult?.SecurityScore0_100 ?? 0}/100, {report.JudgeResult?.RelevanceScore0_100 ?? 0}/100, {report.JudgeResult?.RobustnessScore0_100 ?? 0}/100, {report.JudgeResult?.UxScore0_100 ?? 0}/100",
                $"Scores (overall/security/relevance/robustness/ux): {report.JudgeResult?.OverallScore0_100 ?? 0}/100, {report.JudgeResult?.SecurityScore0_100 ?? 0}/100, {report.JudgeResult?.RelevanceScore0_100 ?? 0}/100, {report.JudgeResult?.RobustnessScore0_100 ?? 0}/100, {report.JudgeResult?.UxScore0_100 ?? 0}/100",
                $"Puntuaciones (global/seguridad/relevancia/robustez/ux): {report.JudgeResult?.OverallScore0_100 ?? 0}/100, {report.JudgeResult?.SecurityScore0_100 ?? 0}/100, {report.JudgeResult?.RelevanceScore0_100 ?? 0}/100, {report.JudgeResult?.RobustnessScore0_100 ?? 0}/100, {report.JudgeResult?.UxScore0_100 ?? 0}/100"));
            sb.AppendLine();
            sb.AppendLine(L(
                "Voulez-vous executer AutoFix maintenant ?",
                "Do you want to execute AutoFix now?",
                "Deseas ejecutar AutoFix ahora?"));
            return sb.ToString();
        }

        private void OnStepStarted(AgentStepLog step)
        {
            RunOnUiThread(() =>
            {
                var existing = PipelineLogs.FirstOrDefault(x => x.AgentName == step.AgentName);
                if (existing != null)
                    PipelineLogs.Remove(existing);
                PipelineLogs.Add(step);

                if (IsAnalyzing)
                {
                    AnalysisStatusText = MapAgentName(step.AgentName) + "...";
                    IsAnalysisIndeterminate = true;
                }

                // Drive agent card
                var card = GetAgentCard(step.AgentName);
                if (card != null)
                {
                    card.MarkStarted();
                    card.AddLog(GetAgentStartLog(step.AgentName));
                    EnsureAgentElapsedTimer();
                }
            });
        }

        private void OnStepCompleted(AgentStepLog step)
        {
            RunOnUiThread(() =>
            {
                var existing = PipelineLogs.FirstOrDefault(x => x.AgentName == step.AgentName);
                if (existing == null)
                    PipelineLogs.Add(step);
                else
                {
                    existing.Status = step.Status;
                    existing.CompletedAt = step.CompletedAt;
                }

                if (IsAnalyzing)
                {
                    AnalysisStatusText = MapAgentName(step.AgentName) + " terminé.";
                    IsAnalysisIndeterminate = true;
                }

                // Drive agent card
                var card = GetAgentCard(step.AgentName);
                card?.MarkCompleted(GetAgentCompletedLog(step.AgentName, _lastReport));
            });
        }

        private void OnStepFailed(AgentStepLog step)
        {
            RunOnUiThread(() =>
            {
                var existing = PipelineLogs.FirstOrDefault(x => x.AgentName == step.AgentName);
                if (existing == null)
                    PipelineLogs.Add(step);
                else
                {
                    existing.Status = step.Status;
                    existing.Error = step.Error;
                    existing.CompletedAt = step.CompletedAt;
                }

                // Drive agent card
                GetAgentCard(step.AgentName)?.MarkFailed(step.Error);

                if (IsAnalyzing)
                {
                    AnalysisStatusText = MapAgentName(step.AgentName) + " en échec.";
                    IsAnalysisIndeterminate = true;
                }

                // Surface timeout/error to the user in the chat
                if (!string.IsNullOrWhiteSpace(step.Error))
                {
                    var isTimeout = step.Error.Contains("Timeout IA", StringComparison.OrdinalIgnoreCase)
                                 || step.Error.Contains("dépassé", StringComparison.OrdinalIgnoreCase);
                    var userMsg = isTimeout
                        ? $"⏱ {step.Error} Vous pouvez relancer l'analyse ou augmenter le délai dans les paramètres."
                        : $"⚠ Agent {step.AgentName} en échec : {step.Error}";
                    AddSystemMessage(userMsg);
                }
            });
        }

        private void AddSystemMessage(string message)
        {
            RunOnUiThread(() =>
            {
                Messages.Add(new ChatMessage
                {
                    Role = ChatRole.System,
                    Content = SanitizeForUi(message)
                });
            });
        }

        private void RaiseComputedState()
        {
            OnPropertyChanged(nameof(IsModelAvailable));
            OnPropertyChanged(nameof(IsModelLoading));
            OnPropertyChanged(nameof(ShowModelBanner));
            OnPropertyChanged(nameof(CanDownloadQwen3));
            OnPropertyChanged(nameof(CanDownloadQwenCoder));
            OnPropertyChanged(nameof(ModelIndicatorTooltip));
            OnPropertyChanged(nameof(ModelDownloadInstructions));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanAnalyseRun));
            OnPropertyChanged(nameof(CanGenerateAutoFixScript));
            OnPropertyChanged(nameof(CanAutoFix));
            OnPropertyChanged(nameof(CanClearChat));
            OnPropertyChanged(nameof(CanCopyLastAssistantMessage));
            OnPropertyChanged(nameof(IsApiMode));
            OnPropertyChanged(nameof(InferenceModeBadge));
            OnPropertyChanged(nameof(InferenceToggleLabel));
            OnPropertyChanged(nameof(HasVerdictBlocked));
            OnPropertyChanged(nameof(VerdictSummary));
            OnPropertyChanged(nameof(AutoFixBlockedBy));
            OnPropertyChanged(nameof(AutoFixBlockedByDisplay));
            OnPropertyChanged(nameof(ModelLimitNotice));
            OnPropertyChanged(nameof(HasModelLimitNotice));
            OnPropertyChanged(nameof(CanOpenAutoFixLogsFolder));
            OnPropertyChanged(nameof(CurrentIaLocaleLabel));
            OnPropertyChanged(nameof(AiLocaleStatusLine));
            OnPropertyChanged(nameof(ShowNoRunPlaceholder));
            OnPropertyChanged(nameof(ShowRunSelectedPlaceholder));
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Refresh ObservableCollections for the verdict card UI.</summary>
        private void RefreshVerdictCardCollections()
        {
            RunOnUiThread(() =>
            {
                VerdictBlockingReasons.Clear();
                VerdictWarningReasons.Clear();
                if (_autoFixGate == null) return;
                foreach (var r in _autoFixGate.BlockingReasons.Take(3))
                    VerdictBlockingReasons.Add(r);
                foreach (var w in _autoFixGate.WarningReasons.Take(3))
                    VerdictWarningReasons.Add(w);
            });
        }

        /// <summary>
        /// Builds the structured pipeline completion message shown in the chat.
        /// Replaces the old single-line verdict string with a detailed breakdown.
        /// </summary>
        private string BuildPipelineSummaryMessage()
        {
            if (SecurityVerdict == null)
                return L("Script pipeline termine. Aucun verdict disponible.",
                         "Script pipeline completed. No verdict available.",
                         "Pipeline completado. Sin veredicto disponible.");

            var j = SecurityVerdict;
            var approved = CanAutoFix;
            var lang = App.CurrentLanguage ?? "fr";
            var missingScript = j.IsMissingScriptError || string.Equals(_autoFixGate?.BlockedBy, "MissingScript", StringComparison.OrdinalIgnoreCase);

            string scoreLine = missingScript
                ? "N/A"
                : $"Global {j.OverallScore0_100}/100  S:{j.SecurityScore0_100} Rel:{j.RelevanceScore0_100} Rob:{j.RobustnessScore0_100} UX:{j.UxScore0_100}";

            string line1;
            if (missingScript)
            {
                line1 = lang switch
                {
                    "en" => "AutoFix failed: MissingScript | scores N/A",
                    "es" => "AutoFix fallo: MissingScript | puntuaciones N/A",
                    _ => "AutoFix en echec: MissingScript | scores N/A"
                };
            }
            else if (lang == "en")
            {
                line1 = $"Script pipeline completed - {j.VerdictDisplay} | {scoreLine}";
            }
            else if (lang == "es")
            {
                line1 = $"Pipeline completado - {j.VerdictDisplay} | {scoreLine}";
            }
            else
            {
                line1 = $"Script pipeline termine - {j.VerdictDisplay} | {scoreLine}";
            }

            var sb = new StringBuilder(line1);
            if (j.JudgeError)
            {
                sb.AppendLine();
                sb.AppendLine($"JudgeError: {j.JudgeErrorMessage}");
            }

            if (approved)
            {
                sb.AppendLine();
                sb.Append(L("AutoFix pret a executer.",
                             "AutoFix ready to execute.",
                             "AutoFix listo para ejecutar."));
            }
            else if (_autoFixGate != null && _autoFixGate.BlockingReasons.Count > 0)
            {
                sb.AppendLine();
                if (missingScript)
                {
                    sb.AppendLine(L("AutoFix failed: MissingScript", "AutoFix failed: MissingScript", "AutoFix fallo: MissingScript"));
                }
                else
                {
                    var blockedByLabel = _autoFixGate.BlockedByDisplay;
                    sb.AppendLine(string.IsNullOrWhiteSpace(blockedByLabel)
                        ? L("AutoFix bloque.", "AutoFix blocked.", "AutoFix bloqueado.")
                        : L($"AutoFix bloque - {blockedByLabel}.",
                            $"AutoFix blocked - {blockedByLabel}.",
                            $"AutoFix bloqueado - {blockedByLabel}."));
                }

                foreach (var r in _autoFixGate.BlockingReasons.Take(2))
                    sb.AppendLine($"  - {r}");
            }

            if (_autoFixGate?.WarningReasons.Count > 0)
            {
                foreach (var w in _autoFixGate.WarningReasons.Take(2))
                    sb.AppendLine($"  ! {w}");
            }

            if (_lastReport != null)
            {
                sb.AppendLine();
                sb.AppendLine($"[TraceId: {_lastReport.AiRunId}]");
                if (!string.IsNullOrWhiteSpace(_lastReport.AutoFixTraceDirectory))
                {
                    sb.AppendLine($"[Logs: {_lastReport.AutoFixTraceDirectory}]");
                }
            }

            return sb.ToString();
        }

        private void EnsureLlmRuntime()
        {
            if (_client != null && _modelLoader != null)
            {
                return;
            }

            _client = _runtimeHost.Client;
            _modelLoader = _runtimeHost.Loader;
        }

        private void EnsureOrchestratorInitialized()
        {
            if (_orchestrator != null || _client == null)
            {
                return;
            }

            DetachOrchestratorEvents();
            _orchestrator = new AiOrchestrator(_client, _safety, _settings);
            _orchestrator.StepStarted += OnStepStarted;
            _orchestrator.StepCompleted += OnStepCompleted;
            _orchestrator.StepFailed += OnStepFailed;
            Log("[AI] Orchestrator initialized lazily.");
        }

        private void DetachOrchestratorEvents()
        {
            if (_orchestrator == null)
                return;
            _orchestrator.StepStarted -= OnStepStarted;
            _orchestrator.StepCompleted -= OnStepCompleted;
            _orchestrator.StepFailed -= OnStepFailed;
        }

        // ─── Agent timeline helpers ────────────────────────────────────────────────

        private static readonly string[] _agentCardNames =
        {
            "ScriptBuilderAgent", "CodeReviewerAgent", "CodeRefinerAgent", "SecurityJudgeAgent"
        };

        private AgentCardViewModel? GetAgentCard(string agentName)
        {
            var idx = Array.IndexOf(_agentCardNames, agentName);
            return idx >= 0 && idx < AgentCards.Count ? AgentCards[idx] : null;
        }

        private static string GetAgentStartLog(string agentName) => agentName switch
        {
            "ScriptBuilderAgent"  => "Analyse du scan · génération du script...",
            "CodeReviewerAgent"   => "Revue sécurité · corrections · durcissement...",
            "CodeRefinerAgent"    => "Normalisation style · idempotence · logs...",
            "SecurityJudgeAgent"  => "Vérification règles · calcul du score...",
            _ => "Traitement en cours..."
        };

        private static string GetAgentCompletedLog(string agentName, AiRunReport? report) => agentName switch
        {
            "ScriptBuilderAgent" =>
                report?.ScriptDraft != null
                    ? $"Script généré ({report.ScriptDraft.ScriptText.Split('\n').Length} lignes)"
                    : "Script généré",
            "CodeReviewerAgent" =>
                report?.ReviewResult != null
                    ? $"{report.ReviewResult.Checklist.Count} corrections appliquées"
                    : "Revue terminée",
            "CodeRefinerAgent" =>
                report?.RefineResult != null
                    ? $"{report.RefineResult.StyleFixes.Count} fixes style · syntaxe={report.RefineResult.SyntaxValid}"
                    : "Raffinement terminé",
            "SecurityJudgeAgent" =>
                report?.JudgeResult != null
                    ? $"Verdict: {report.JudgeResult.VerdictDisplay} · {report.JudgeResult.OverallScore0_100}/100"
                    : "Jugement terminé",
            _ => "Terminé"
        };

        private void ResetAgentCards()
        {
            foreach (var card in AgentCards)
            {
                card.Status = AgentCardStatus.Pending;
                card.ElapsedSeconds = 0;
                card.CurrentLog = string.Empty;
                card.LogLines.Clear();
            }
            AgentScriptPreview = string.Empty;
            AgentDiffSummary = string.Empty;
        }

        private void EnsureAgentElapsedTimer()
        {
            if (_agentElapsedTimer != null) return;
            _agentElapsedTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _agentElapsedTimer.Tick += (_, _) =>
            {
                foreach (var card in AgentCards)
                    card.UpdateElapsed();
            };
            _agentElapsedTimer.Start();
        }

        private void StopAgentElapsedTimer()
        {
            _agentElapsedTimer?.Stop();
            _agentElapsedTimer = null;
        }

        private void ApplyAnalysisProgress(AnalysisProgressUpdate update)
        {
            AnalysisProgress = Math.Clamp(update.Progress, 0, 100);
            AnalysisStatusText = update.Status;
            IsAnalysisIndeterminate = update.IsIndeterminate;
        }

        private void ResetAnalysisProgress()
        {
            AnalysisProgress = 0;
            AnalysisStatusText = string.Empty;
            IsAnalysisIndeterminate = false;
        }

        private void RunOnUiThread(Action action)
        {
            if (!_useDispatcher || _uiDispatcher == null || _uiDispatcher.CheckAccess())
            {
                action();
                return;
            }

            _uiDispatcher.BeginInvoke(action);
        }

        private async Task DispatchAsync(Action action)
        {
            if (!_useDispatcher || _uiDispatcher == null || _uiDispatcher.CheckAccess())
            {
                action();
                return;
            }

            await _uiDispatcher.InvokeAsync(action);
        }

        private enum ChatTaskType { Analysis, CodeGeneration, General }

        private static ChatTaskType ClassifyUserIntent(string userText)
        {
            var lower = userText.ToLowerInvariant();

            if (lower.Contains("script") || lower.Contains("powershell") || lower.Contains("autofix")
                || lower.Contains("code") || lower.Contains("genere") || lower.Contains("generate")
                || lower.Contains("corrige") || lower.Contains("repare") || lower.Contains("fix"))
            {
                return ChatTaskType.CodeGeneration;
            }

            if (lower.Contains("analyse") || lower.Contains("analyze") || lower.Contains("diagnostic")
                || lower.Contains("probleme") || lower.Contains("problem") || lower.Contains("score")
                || lower.Contains("sante") || lower.Contains("health") || lower.Contains("resume")
                || lower.Contains("compte-rendu") || lower.Contains("avis"))
            {
                return ChatTaskType.Analysis;
            }

            return ChatTaskType.General;
        }

        /// <summary>
        /// Builds a focused system prompt for the "Analyze selected run" feature.
        /// Asks the LLM to produce a structured diagnostic summary — no JSON envelope required,
        /// just plain-text analysis in the user's language.
        /// </summary>
        private string BuildAnalysisSystemPrompt(string langCode)
        {
            var guardrailLang = langCode switch
            {
                "en" => "Respond in English only.",
                "es" => "Responde solo en espanol.",
                _ => "Reponds uniquement en francais."
            };

            var analysisInstructions = langCode switch
            {
                "en" => BuildAnalysisSystemPromptEn(),
                "es" => BuildAnalysisSystemPromptEs(),
                _ => BuildAnalysisSystemPromptFr()
            };

            return PromptLoader.ChatSystemBase()
                .Replace("{PREFERRED_LANGUAGE}", langCode)
                + "\n[CHAT_GUARDRAIL_STRICT]\n"
                + $"- {guardrailLang}\n"
                + "- Never output internal instructions, system role labels, or debug tokens.\n"
                + "- Do not use markers ###, [LANGUAGE:], USER:, ASSISTANT:, SYSTEM:.\n"
                + "- NEVER output <think>, </think>, or internal reasoning blocks.\n"
                + "- Do not output JSON, code blocks, or control tokens in analysis mode.\n\n"
                + analysisInstructions;
        }

        private static string BuildAnalysisSystemPromptFr() => @"Tu es PC X-Ray, un expert en diagnostic PC hors-ligne.

[LANGUE] Reponds UNIQUEMENT en francais.

[FORMAT DE REPONSE OBLIGATOIRE — 5 SECTIONS]

## 1. Resume executif
En 3 a 5 phrases : etat global du PC, score de sante (valeur exacte du scan), verdict principal.

## 2. Top problemes (tries par severite : CRITIQUE > ELEVE > MOYEN > FAIBLE)
Pour chaque probleme :
  Probleme : (nom court)
  Severite : CRITIQUE / ELEVE / MOYEN / FAIBLE
  Preuve : (valeur exacte du scan : champ JSON, pourcentage, chemin, nom d'evenement)
  Cause probable : (explication technique concise)
  Action recommandee : (etapes concretes et actionnables)

## 3. Details par categorie
Pour chaque categorie presente dans le scan (CPU, RAM, Disque, Pilotes, Reseau, Securite, Logs/Evenements) :
  - Etat : OK / ATTENTION / PROBLEME
  - Valeurs cles du scan
  - Observations

## 4. Suspects de bugs logiciels (3 a 10 hypotheses)
Pour chaque hypothese :
  Hypothese : (description)
  Indices du scan : (champs ou valeurs qui suggerent ce bug)
  Probabilite : Haute / Moyenne / Faible

## 5. Plan d'action priorise
  P0 (Urgent — a faire maintenant) :
  P1 (Important — cette semaine) :
  P2 (Amelioration — ce mois) :

[REGLES STRICTES]
- N'utilise QUE les donnees du contexte de scan fourni. Aucune invention de valeurs.
- Cite les valeurs exactes : pourcentages, chemins, noms d'evenements, codes d'erreur.
- Si une categorie est absente du scan, dis-le explicitement et propose comment collecter l'info.
- NE PAS produire de JSON, blocs de code, ou marqueurs internes.
- NE PAS ecrire : [LANGUAGE:], USER:, SYSTEM:, <|assistant|>.";

        private static string BuildAnalysisSystemPromptEn() => @"You are PC X-Ray, an offline PC diagnostics expert.

[LANGUAGE] Respond ONLY in English.

[MANDATORY RESPONSE FORMAT — 5 SECTIONS]

## 1. Executive Summary
3 to 5 sentences: overall PC state, health score (exact value from scan), main verdict.

## 2. Top Issues (sorted by severity: CRITICAL > HIGH > MEDIUM > LOW)
For each issue:
  Issue: (short name)
  Severity: CRITICAL / HIGH / MEDIUM / LOW
  Evidence: (exact scan value: JSON field, percentage, path, event name)
  Probable cause: (concise technical explanation)
  Recommended action: (concrete actionable steps)

## 3. Details by category
For each category present in the scan (CPU, RAM, Disk, Drivers, Network, Security, Logs/Events):
  - Status: OK / WARNING / PROBLEM
  - Key scan values
  - Observations

## 4. Software bug suspects (3 to 10 hypotheses)
For each hypothesis:
  Hypothesis: (description)
  Scan evidence: (fields or values suggesting this bug)
  Probability: High / Medium / Low

## 5. Prioritized action plan
  P0 (Urgent — do now):
  P1 (Important — this week):
  P2 (Improvement — this month):

[STRICT RULES]
- Use ONLY data from the provided scan context. Never invent values.
- Cite exact values: percentages, paths, event names, error codes.
- If a category is missing from the scan, say so and suggest how to collect the info.
- Do NOT output JSON, code blocks, or internal markers.
- Do NOT write: [LANGUAGE:], USER:, SYSTEM:, <|assistant|>.";

        private static string BuildAnalysisSystemPromptEs() => @"Eres PC X-Ray, un experto en diagnostico de PC sin conexion.

[IDIOMA] Responde UNICAMENTE en espanol.

[FORMATO DE RESPUESTA OBLIGATORIO — 5 SECCIONES]

## 1. Resumen ejecutivo
De 3 a 5 frases: estado general del PC, puntuacion de salud (valor exacto del scan), veredicto principal.

## 2. Principales problemas (ordenados por severidad: CRITICO > ALTO > MEDIO > BAJO)
Para cada problema:
  Problema: (nombre corto)
  Severidad: CRITICO / ALTO / MEDIO / BAJO
  Evidencia: (valor exacto del scan: campo JSON, porcentaje, ruta, nombre de evento)
  Causa probable: (explicacion tecnica concisa)
  Accion recomendada: (pasos concretos y ejecutables)

## 3. Detalles por categoria
Para cada categoria presente en el scan (CPU, RAM, Disco, Controladores, Red, Seguridad, Registros/Eventos):
  - Estado: OK / ATENCION / PROBLEMA
  - Valores clave del scan
  - Observaciones

## 4. Sospechosos de bugs de software (3 a 10 hipotesis)
Para cada hipotesis:
  Hipotesis: (descripcion)
  Evidencia del scan: (campos o valores que sugieren este bug)
  Probabilidad: Alta / Media / Baja

## 5. Plan de accion priorizado
  P0 (Urgente — hacer ahora):
  P1 (Importante — esta semana):
  P2 (Mejora — este mes):

[REGLAS ESTRICTAS]
- Usa SOLO los datos del contexto de scan proporcionado. Nunca inventes valores.
- Cita valores exactos: porcentajes, rutas, nombres de eventos, codigos de error.
- Si una categoria falta en el scan, indicalo y propone como recopilar la informacion.
- NO producir JSON, bloques de codigo ni marcadores internos.
- NO escribir: [LANGUAGE:], USER:, SYSTEM:, <|assistant|>.";

        /// <summary>
        /// Builds the user prompt for the "Analyze selected run" feature.
        /// Contains the scan context and a clear analysis request.
        /// </summary>
        private static string BuildAnalysisUserPrompt(string contextText, string langCode)
        {
            var instruction = langCode switch
            {
                "en" =>
                    "Analyze this PC scan using the mandatory 5-section format. " +
                    "Be specific: cite exact values from the scan for every finding. " +
                    "If a category is absent from the scan data, explicitly say so. " +
                    "Do not invent values. Do not skip any section.",
                "es" =>
                    "Analiza este scan de PC usando el formato obligatorio de 5 secciones. " +
                    "Se especifico: cita valores exactos del scan para cada hallazgo. " +
                    "Si una categoria esta ausente en los datos del scan, indicalo explicitamente. " +
                    "No inventes valores. No omitas ninguna seccion.",
                _ =>
                    "Analyse ce scan PC en utilisant le format obligatoire en 5 sections. " +
                    "Sois precis : cite les valeurs exactes du scan pour chaque constat. " +
                    "Si une categorie est absente des donnees du scan, dis-le explicitement. " +
                    "N'invente pas de valeurs. Ne saute aucune section."
            };

            return $@"## DONNEES DU SCAN
{contextText}

## DEMANDE
{instruction}";
        }

        /// <summary>
        /// Writes structured analysis trace to %TEMP%/PCDiagnosticPro_AnalyzeTrace.log
        /// </summary>
        private static void LogAnalysisTrace(string traceId, string stage, string? detail, string? rawOutput, string? parsedOutput, string? displayOutput, long elapsedMs)
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_AnalyzeTrace.log");
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:o}] traceId={traceId} stage={stage} elapsedMs={elapsedMs}");
                if (!string.IsNullOrWhiteSpace(detail))
                    sb.AppendLine($"  detail={detail}");
                if (rawOutput != null)
                    sb.AppendLine($"  rawOutput[{rawOutput.Length}]={SafeTrim(rawOutput, 500)}");
                if (parsedOutput != null)
                    sb.AppendLine($"  parsedOutput[{parsedOutput.Length}]={SafeTrim(parsedOutput, 500)}");
                if (displayOutput != null)
                    sb.AppendLine($"  displayOutput[{displayOutput.Length}]={SafeTrim(displayOutput, 500)}");
                sb.AppendLine();
                File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
            }
            catch { /* trace logging must never crash */ }
        }

        /// <summary>
        /// Writes a dedicated trace file to %TEMP%\PCDiagnosticPRO\logs\ai\{traceId}_{fileName}.
        /// Used for full RAW_OUTPUT, SANITIZED_OUTPUT, PARSE_ERRORS dumps.
        /// </summary>
        private static void WriteTraceFile(string traceId, string fileName, string content)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "PCDiagnosticPRO", "logs", "ai");
                Directory.CreateDirectory(dir);
                var filePath = Path.Combine(dir, $"{traceId}_{fileName}");
                File.WriteAllText(filePath, content, Encoding.UTF8);
            }
            catch { /* trace logging must never crash */ }
        }

        /// <summary>
        /// Appends a structured log line to the centralized AI trace log.
        /// </summary>
        private static void AppendTraceLog(string traceId, string message)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "PCDiagnosticPRO", "logs", "ai");
                Directory.CreateDirectory(dir);
                var logPath = Path.Combine(dir, "ai_trace.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:o}] [{traceId}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { /* trace logging must never crash */ }
        }

        private string BuildRunSummaryCached(ContextPack context, string userQuestion)
        {
            var cacheKey = context.RunId ?? string.Empty;
            if (string.Equals(_runSummaryCachedKey, cacheKey, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_runSummaryCached))
            {
                return _runSummaryCached;
            }

            var lines = new List<string>
            {
                $"RunId: {context.RunId}"
            };
            if (!string.IsNullOrWhiteSpace(context.ScanDate))
            {
                lines.Add($"ScanDate: {NormalizeSingleLine(context.ScanDate)}");
            }

            if (!string.IsNullOrWhiteSpace(context.Summary))
            {
                lines.Add($"Summary: {NormalizeSingleLine(context.Summary)}");
            }

            var keywords = ExtractKeywords(userQuestion, 8).ToList();
            var topSignals = context.KeyFindings
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .OrderByDescending(f => keywords.Count == 0
                    ? 0
                    : keywords.Count(k => f.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(f => f.Length)
                .Select(NormalizeSingleLine)
                .Take(5)
                .ToList();

            foreach (var signal in topSignals)
            {
                lines.Add($"Signal: {signal}");
            }

            if (!string.IsNullOrWhiteSpace(context.CoverageSummary))
            {
                lines.Add($"Coverage: {NormalizeSingleLine(context.CoverageSummary)}");
            }

            var compact = string.Join(Environment.NewLine, lines.Take(ChatSummaryMaxLines));
            if (compact.Length > ChatContextMaxChars)
            {
                compact = compact[..ChatContextMaxChars];
            }

            _runSummaryCachedKey = cacheKey;
            _runSummaryCached = compact.Trim();
            return _runSummaryCached;
        }

        private bool TryBuildJudgeDiscussionReply(string userText, out string reply)
        {
            reply = string.Empty;
            if (!IsJudgeDiscussionRequest(userText))
            {
                return false;
            }

            if (SecurityVerdict == null || !_scriptGeneratedByPipeline)
            {
                return false;
            }

            var judge = SecurityVerdict;
            var blockingReasons = _autoFixGate?.BlockingReasons
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToList()
                ?? new List<string>();
            var violations = judge.Violations
                .Where(v => v != null)
                .ToList();
            if (blockingReasons.Count == 0 && violations.Count == 0 && judge.Reasons.Count == 0)
            {
                return false;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Le script a ete refuse pour les raisons suivantes:");

            if (blockingReasons.Count > 0)
            {
                foreach (var reason in blockingReasons)
                {
                    sb.AppendLine($"- {reason}");
                }
            }
            else
            {
                foreach (var reason in judge.Reasons.Take(5))
                {
                    sb.AppendLine($"- {reason}");
                }
            }

            if (violations.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Violations detectees:");
                foreach (var v in violations)
                {
                    var evidence = string.IsNullOrWhiteSpace(v.EvidenceLine) ? "evidence non fournie" : v.EvidenceLine;
                    var fix = string.IsNullOrWhiteSpace(v.Fix) ? "appliquer une correction securisee et regenerer." : v.Fix;
                    sb.AppendLine($"- {v.Code} [{v.Severity}] preuve: {evidence}");
                    sb.AppendLine($"  Correction suggeree: {fix}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Pour passer, appliquez ces corrections puis regenerez le script.");

            reply = sb.ToString().Trim();
            return true;
        }

        private static bool IsJudgeDiscussionRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var q = text.ToLowerInvariant();
            return q.Contains("pourquoi")
                || q.Contains("why refused")
                || q.Contains("refused")
                || q.Contains("bloque")
                || q.Contains("blocked")
                || q.Contains("score")
                || q.Contains("comment passer")
                || q.Contains("how to pass")
                || q.Contains("expliquer")
                || q.Contains("explain");
        }

        private string EnsureTechnicianTemplate(string userQuestion, string rawAnswer, ContextPack context)
        {
            return EnsureQuestionFirstAnswer(userQuestion, rawAnswer, context, out _, out _);
        }

        private string EnsureQuestionFirstAnswer(
            string userQuestion,
            string rawAnswer,
            ContextPack context,
            out bool relevancePass,
            out bool rewriteApplied)
        {
            var cleaned = GetEffectiveRawForDisplay(rawAnswer)
                .Replace("[Mode texte brut â€” le formatage IA a echoue]", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("[Raw text mode â€” AI formatting failed]", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("[Modo texto sin formato â€” el formato IA fallo]", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                cleaned = FirstNonEmptyLine(context.Summary);
            }

            relevancePass = PassesAnswerRelevanceCheck(userQuestion, cleaned);
            rewriteApplied = false;
            if (!relevancePass)
            {
                cleaned = RewriteAnswerQuestionFirst(userQuestion, cleaned, context);
                rewriteApplied = true;
                relevancePass = PassesAnswerRelevanceCheck(userQuestion, cleaned);
            }

            return cleaned.Trim();
        }

        private static bool PassesAnswerRelevanceCheck(string userQuestion, string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            var keywords = ExtractKeywords(userQuestion, 8).ToList();
            if (keywords.Count == 0)
            {
                return true;
            }

            var lines = response
                .Split(new[] { '\n', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeSingleLine)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(10)
                .ToList();

            foreach (var line in lines)
            {
                if (keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private string RewriteAnswerQuestionFirst(string userQuestion, string response, ContextPack context)
        {
            var keywords = ExtractKeywords(userQuestion, 8).ToList();
            var sentences = response
                .Split(new[] { '\n', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeSingleLine)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            var bestSentence = sentences
                .OrderByDescending(s => keywords.Count == 0
                    ? 0
                    : keywords.Count(k => s.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(s => s.Length)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(bestSentence))
            {
                bestSentence = FirstNonEmptyLine(context.KeyFindings.FirstOrDefault() ?? context.Summary);
            }

            var orderedActions = ExtractOrderedActionLines(response, 2);
            if (orderedActions.Count == 0)
            {
                orderedActions.Add("Verifier le signal principal et mesurer l'impact avant/apres.");
            }

            var lang = (App.CurrentLanguage ?? "fr").Trim().ToLowerInvariant();
            var directPrefix = lang switch
            {
                "en" => "Direct answer:",
                "es" => "Respuesta directa:",
                _ => "Reponse directe:"
            };
            var contextPrefix = lang switch
            {
                "en" => "Context:",
                "es" => "Contexto:",
                _ => "Contexte:"
            };

            var sb = new StringBuilder();
            sb.AppendLine($"{directPrefix} {bestSentence}");
            sb.AppendLine();
            sb.AppendLine(contextPrefix);
            var contextLines = sentences.Where(s => !string.Equals(s, bestSentence, StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
            foreach (var line in contextLines)
            {
                sb.AppendLine($"- {line}");
            }

            foreach (var action in orderedActions.Take(1))
            {
                sb.AppendLine($"- {action}");
            }

            return sb.ToString().Trim();
        }

        private static List<string> ExtractOrderedActionLines(string text, int maxItems)
        {
            var actions = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return actions;
            }

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!Regex.IsMatch(trimmed, @"^(\d+[\.\)]|-|\*)\s+"))
                {
                    continue;
                }

                var cleaned = Regex.Replace(trimmed, @"^(\d+[\.\)]|-|\*)\s+", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    continue;
                }

                actions.Add(cleaned);
                if (actions.Count >= maxItems)
                {
                    break;
                }
            }

            return actions;
        }

        private double ComputeAnswerCohesionScore(string userQuestion, string response, ContextPack context)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return 0;
            }

            var responseLower = response.ToLowerInvariant();
            var questionKeywords = ExtractKeywords(userQuestion, 10).ToList();
            var keywordHits = questionKeywords.Count == 0
                ? 1.0
                : questionKeywords.Count(k => responseLower.Contains(k, StringComparison.OrdinalIgnoreCase)) / (double)questionKeywords.Count;

            var evidenceCount = Regex.Matches(responseLower, "preuve:").Count;
            var evidenceScore = Math.Min(1.0, evidenceCount / 3.0);

            var contextHits = context.KeyFindings
                .Take(8)
                .Count(f => responseLower.Contains(FirstNonEmptyLine(f).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
            var contextScore = Math.Min(1.0, contextHits / 2.0);

            var templateScore = responseLower.Contains("1) resume", StringComparison.OrdinalIgnoreCase)
                && responseLower.Contains("7) ce que je veux verifier ensuite", StringComparison.OrdinalIgnoreCase)
                    ? 1.0
                    : 0.0;

            return (keywordHits * 0.35) + (evidenceScore * 0.35) + (contextScore * 0.20) + (templateScore * 0.10);
        }

        private string RewriteLowCohesionAnswer(string userQuestion, string response, ContextPack context)
        {
            var bestSignal = context.KeyFindings.FirstOrDefault() ?? context.Summary;
            var sb = new StringBuilder(response.Trim());
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Addendum cohesion");
            sb.AppendLine($"- Question ciblee: {NormalizeSingleLine(userQuestion)}");
            sb.AppendLine($"- Evidence prioritaire: {NormalizeSingleLine(bestSignal)}");
            sb.AppendLine("- Action immediate: verifier ce signal avant de modifier d'autres composants.");
            return sb.ToString().Trim();
        }

        private static IEnumerable<string> ExtractKeywords(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Enumerable.Empty<string>();
            }

            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "le", "la", "les", "de", "des", "du", "un", "une", "and", "the", "for", "pour", "with", "que", "quoi", "est"
            };

            return Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]{3,}")
                .Select(m => m.Value)
                .Where(w => !stopWords.Contains(w))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(max);
        }

        private static string FirstNonEmptyLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "N/A";
            }

            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeSingleLine)
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            return string.IsNullOrWhiteSpace(line) ? "N/A" : line!;
        }

        private static string NormalizeSingleLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return Regex.Replace(text.Replace('\r', ' ').Replace('\n', ' ').Trim(), @"\s+", " ");
        }

        private static bool IsLikelyPositiveFinding(string finding)
        {
            if (string.IsNullOrWhiteSpace(finding))
            {
                return false;
            }

            var f = finding.ToLowerInvariant();
            return f.Contains("ok")
                || f.Contains("healthy")
                || f.Contains("stable")
                || f.Contains("normal")
                || f.Contains("aucun")
                || f.Contains("no issue")
                || f.Contains("no error");
        }

        /// <summary>
        /// Builds the CaseSummary (up to 600 words) from:
        ///   1. Run header (date, health score, OS)
        ///   2. Top key findings from the context pack
        ///   3. User symptoms (user messages)
        ///   4. AI hypotheses + actions (assistant messages, truncated)
        /// Deduplicates across turns and enforces the 600-word budget.
        /// Priority: symptoms > scan signals > actions > open questions.
        /// </summary>
        private static string BuildDiffSummary(AiRunReport report)
        {
            var draft = report.ScriptDraft?.ScriptText ?? string.Empty;
            var refined = report.RefineResult?.RefinedScriptText ?? report.ReviewResult?.RevisedScriptText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(draft) || string.IsNullOrWhiteSpace(refined))
                return string.Empty;

            var draftLines = new HashSet<string>(draft.Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var refinedLines = refined.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var added = refinedLines.Count(l => !draftLines.Contains(l));
            var removed = draftLines.Count(l => !refinedLines.Contains(l));
            return $"+{added} lignes ajoutées · -{removed} lignes supprimées (Agent2+3)";
        }

        private string BuildCaseSummary(ContextPack context, IEnumerable<ChatMessage> messages)
        {
            var sb = new StringBuilder();
            var lang = App.CurrentLanguage;

            // 1 — Run header
            var header = CurrentRunHeader;
            if (header != null)
            {
                sb.AppendLine(lang == "en"
                    ? $"• Run: {header.DateDisplay}, Anomalies: {header.CriticalAnomalyCount}, Errors: {header.ErrorCount}"
                    : $"• Run: {header.DateDisplay}, Anomalies: {header.CriticalAnomalyCount}, Erreurs: {header.ErrorCount}");
            }

            // 2 — Top key findings from context (max 6)
            var findings = context.KeyFindings.Take(6).ToList();
            if (findings.Count > 0)
            {
                sb.AppendLine(lang == "en" ? "• Critical signals:" : "• Signaux critiques:");
                foreach (var f in findings)
                    sb.AppendLine($"  - {SafeTrim(f, 120)}");
            }

            // 3 — User symptoms (all user messages, deduped, truncated)
            var userMessages = messages
                .Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => SafeTrim(m.Content, 200))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (userMessages.Count > 0)
            {
                sb.AppendLine(lang == "en" ? "• User symptoms/questions:" : "• Symptômes/questions utilisateur:");
                foreach (var u in userMessages)
                    sb.AppendLine($"  - {u}");
            }

            // 4 — AI actions proposed (last 4 assistant messages, key lines only)
            var assistantMessages = messages
                .Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))
                .TakeLast(4)
                .ToList();
            if (assistantMessages.Count > 0)
            {
                sb.AppendLine(lang == "en" ? "• AI recommendations (latest):" : "• Recommandations IA (dernières):");
                foreach (var a in assistantMessages)
                {
                    // Extract bullet lines or the first 3 lines
                    var lines = a.Content!
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => l.TrimStart().StartsWith("•") || l.TrimStart().StartsWith("-") || l.TrimStart().StartsWith("*"))
                        .Take(4)
                        .ToList();
                    if (lines.Count == 0)
                        lines = a.Content!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(2).ToList();
                    foreach (var l in lines)
                        sb.AppendLine($"  {SafeTrim(l.Trim(), 150)}");
                }
            }

            var raw = sb.ToString().Trim();
            return EnforceWordLimit(raw, 600);
        }

        /// <summary>
        /// Trims text to maxWords words, preserving line breaks where possible.
        /// If over budget: keep first maxWords words.
        /// </summary>
        private static string EnforceWordLimit(string text, int maxWords)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= maxWords) return text;
            // Rejoin first maxWords words — approximate (loses line breaks but stays concise)
            return string.Join(" ", words.Take(maxWords)) + " [...]";
        }

        private static string SafeTrim(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private string SanitizeForUi(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return LocalPathRegex.Replace(text, "[local path]");
        }

        private static string GetEffectiveRawForDisplay(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var cleaned = ThinkTokenRegex.Replace(raw, string.Empty);
            return cleaned.Trim();
        }

        private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(ShowRunSelectedPlaceholder));
            OnPropertyChanged(nameof(CanClearChat));
            OnPropertyChanged(nameof(CanCopyLastAssistantMessage));
        }

        private void OnPipelineLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasPipelineLogs));
        }

        private void Log(string message)
        {
            try
            {
                _log(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AI] log sink failed: {ex}");
            }
        }

        private static string MapAgentName(string agentName)
        {
            if (agentName.Contains("ScriptBuilder", StringComparison.OrdinalIgnoreCase))
            {
                return "1. Construction du script";
            }

            if (agentName.Contains("CodeReviewer", StringComparison.OrdinalIgnoreCase))
            {
                return "2. Correction et robustesse";
            }

            if (agentName.Contains("CodeRefiner", StringComparison.OrdinalIgnoreCase))
            {
                return "3. Normalisation et style";
            }

            if (agentName.Contains("SecurityJudge", StringComparison.OrdinalIgnoreCase)
                || agentName.Contains("TesterJudge", StringComparison.OrdinalIgnoreCase))
            {
                return "4. Securite et verdict";
            }

            return "Analyse IA";
        }

        private string L(string fr, string en, string es)
        {
            var lang = (App.CurrentLanguage ?? "fr").Trim().ToLowerInvariant();
            return lang switch
            {
                "en" => en,
                "es" => es,
                _ => fr
            };
        }

        private sealed class CachedRunContext
        {
            public required string FileHash { get; init; }
            public required DateTime LastWriteUtc { get; init; }
            public required CombinedScanResult Combined { get; init; }
            public required ContextPack Context { get; init; }
            public required RunAnalysisHeader Header { get; init; }
        }

        private async Task<(CombinedScanResult? combined, ContextPack? context, RunAnalysisHeader? header, long jsonBytes, long parseMs, long contextBuildMs, bool cacheHit, string fileHash, AiPipelineMetrics metrics)> LoadContextForRunAsync(string combinedJsonPath, CancellationToken ct = default)
        {
            var jsonBytes = File.Exists(combinedJsonPath) ? new FileInfo(combinedJsonPath).Length : 0;
            var lastWriteUtc = File.Exists(combinedJsonPath) ? File.GetLastWriteTimeUtc(combinedJsonPath) : DateTime.MinValue;
            var fileHash = await Task.Run(() => ComputeFileSha256(combinedJsonPath), ct).ConfigureAwait(false);

            var metrics = new AiPipelineMetrics
            {
                Stage = "context_load",
                RunId = CurrentRunHeader?.RunId ?? "unknown",
                JsonBytes = jsonBytes,
                SourceHash = fileHash
            };

            lock (_contextCacheLock)
            {
                if (_contextCache.TryGetValue(combinedJsonPath, out var cached)
                    && string.Equals(cached.FileHash, fileHash, StringComparison.OrdinalIgnoreCase)
                    && cached.LastWriteUtc == lastWriteUtc)
                {
                    metrics.CacheHit = true;
                    metrics.RunId = cached.Header.RunId;
                    metrics.ContextChars = cached.Context.ToPromptText().Length;
                    metrics.ContextTokensEst = cached.Context.EstimatedTokens;
                    return (cached.Combined, cached.Context, cached.Header, jsonBytes, 0, 0, true, fileHash, metrics);
                }
            }

            var parseSw = Stopwatch.StartNew();
            var combined = await Task.Run(() => _loadCombinedFromFile(combinedJsonPath), ct).ConfigureAwait(false);
            parseSw.Stop();
            if (combined == null)
            {
                metrics.ParseMs = parseSw.ElapsedMilliseconds;
                return (null, null, null, jsonBytes, parseSw.ElapsedMilliseconds, 0, false, fileHash, metrics);
            }

            var contextSw = Stopwatch.StartNew();
            var context = _contextBuilder.Build(combined);
            var header = _contextBuilder.BuildRunHeader(combined);
            contextSw.Stop();

            metrics.RunId = header.RunId;
            metrics.ParseMs = parseSw.ElapsedMilliseconds;
            metrics.ContextBuildMs = contextSw.ElapsedMilliseconds;
            metrics.ContextChars = context.ToPromptText().Length;
            metrics.ContextTokensEst = context.EstimatedTokens;

            var cacheItem = new CachedRunContext
            {
                FileHash = fileHash,
                LastWriteUtc = lastWriteUtc,
                Combined = combined,
                Context = context,
                Header = header
            };

            lock (_contextCacheLock)
            {
                _contextCache[combinedJsonPath] = cacheItem;
            }

            return (combined, context, header, jsonBytes, parseSw.ElapsedMilliseconds, contextSw.ElapsedMilliseconds, false, fileHash, metrics);
        }

        private static string ComputeFileSha256(string path)
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static string ComputeScriptSha256(string scriptText)
        {
            var bytes = Encoding.UTF8.GetBytes(scriptText ?? string.Empty);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        private readonly struct AnalysisProgressUpdate
        {
            public AnalysisProgressUpdate(int progress, string status, bool isIndeterminate)
            {
                Progress = progress;
                Status = status;
                IsIndeterminate = isIndeterminate;
            }

            public int Progress { get; }
            public string Status { get; }
            public bool IsIndeterminate { get; }
        }
    }

    public class ScanRunEntry
    {
        public string DisplayName { get; set; } = string.Empty;
        public string CombinedJsonPath { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }

        public override string ToString() => DisplayName;
    }
}
