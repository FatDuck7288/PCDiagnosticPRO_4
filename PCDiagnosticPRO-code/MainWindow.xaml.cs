using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro
{
    /// <summary>
    /// Code-behind pour MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // DEBUG: Timer pour audit layout du ring pendant le scan
        private DispatcherTimer? _ringLayoutAuditTimer;
        private bool _ringAuditLogged = false;

        // Animation sprite globe (12 frames, 12 FPS)
        private DispatcherTimer? _globeSpriteTimer;
        private BitmapImage[]? _globeSpriteFrames;
        private int _currentGlobeSpriteFrame = 0;
        private const int GLOBE_SPRITE_FPS = 12;
        private const int GLOBE_SPRITE_FRAME_COUNT = 12;

        public MainWindow()
        {
            InitializeComponent();
            App.LogMessage("MainWindow initialisé");

            Loaded += OnMainWindowLoaded;

            // DEBUG: Audit layout ring - log les tailles réelles après le premier rendu
            Loaded += (s, e) => LogRingLayoutAudit("OnLoaded");
            SizeChanged += (s, e) => LogRingLayoutAudit("OnSizeChanged");
            
            // Timer pour logger pendant le scan (toutes les 2 secondes, max 3 fois)
            _ringLayoutAuditTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            int auditCount = 0;
            _ringLayoutAuditTimer.Tick += (s, e) =>
            {
                if (auditCount++ >= 3)
                {
                    _ringLayoutAuditTimer.Stop();
                    return;
                }
                LogRingLayoutAudit($"DuringScan_{auditCount}");
            };
        }

        /// <summary>
        /// Charge les sprites globe (12 PNG) et branche PropertyChanged pour animation.
        /// </summary>
        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            LoadGlobeSpriteFrames();
            InitializeGlobeSpriteTimer();
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnMainViewModelPropertyChanged;
            }
        }

        /// <summary>
        /// Charge les 12 frames sprites depuis Assets/Animations.
        /// Priorité : 1.png à 12.png, sinon pattern _N- dans le nom de fichier.
        /// </summary>
        private void LoadGlobeSpriteFrames()
        {
            try
            {
                var animDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Animations");
                if (!Directory.Exists(animDir))
                {
                    App.LogMessage("[GlobeSprite] Dossier Assets/Animations non trouvé");
                    return;
                }

                var frameDict = new Dictionary<int, string>();

                // Priorité : fichiers 1.png, 2.png, ... 12.png
                for (int i = 1; i <= GLOBE_SPRITE_FRAME_COUNT; i++)
                {
                    var simplePath = Path.Combine(animDir, $"{i}.png");
                    if (File.Exists(simplePath))
                    {
                        frameDict[i] = simplePath;
                    }
                }

                // Sinon : extraire le numéro depuis le pattern _N- dans le nom
                if (frameDict.Count < GLOBE_SPRITE_FRAME_COUNT)
                {
                    var pngFiles = Directory.GetFiles(animDir, "*.png");
                    var frameRegex = new Regex(@"_(\d+)-[a-f0-9-]+\.png$", RegexOptions.IgnoreCase);
                    foreach (var file in pngFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        var match = frameRegex.Match(fileName);
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int frameNum))
                        {
                            if (frameNum >= 1 && frameNum <= GLOBE_SPRITE_FRAME_COUNT && !frameDict.ContainsKey(frameNum))
                            {
                                frameDict[frameNum] = file;
                            }
                        }
                    }
                }

                if (frameDict.Count < GLOBE_SPRITE_FRAME_COUNT)
                {
                    App.LogMessage($"[GlobeSprite] Seulement {frameDict.Count}/{GLOBE_SPRITE_FRAME_COUNT} frames trouvées");
                }

                if (frameDict.Count == 0)
                {
                    App.LogMessage("[GlobeSprite] Aucune frame valide trouvée");
                    return;
                }

                // Charger les frames dans l'ordre 1-12
                _globeSpriteFrames = new BitmapImage[GLOBE_SPRITE_FRAME_COUNT];
                for (int i = 1; i <= GLOBE_SPRITE_FRAME_COUNT; i++)
                {
                    if (frameDict.TryGetValue(i, out var filePath))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze(); // Thread-safe, meilleure perf
                        _globeSpriteFrames[i - 1] = bitmap;
                    }
                }

                // Afficher la première frame par défaut (statique)
                if (_globeSpriteFrames[0] != null)
                {
                    SpeedTestGlobeImage.Source = _globeSpriteFrames[0];
                }

                App.LogMessage($"[GlobeSprite] {frameDict.Count} frames chargées avec succès");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GlobeSprite] Erreur chargement: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialise le timer d'animation sprite (12 FPS = ~83ms par frame).
        /// </summary>
        private void InitializeGlobeSpriteTimer()
        {
            _globeSpriteTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / GLOBE_SPRITE_FPS)
            };
            _globeSpriteTimer.Tick += OnGlobeSpriteTimerTick;
        }

        /// <summary>
        /// Tick du timer: avance à la frame suivante.
        /// </summary>
        private void OnGlobeSpriteTimerTick(object? sender, EventArgs e)
        {
            if (_globeSpriteFrames == null || _globeSpriteFrames.Length == 0) return;

            _currentGlobeSpriteFrame = (_currentGlobeSpriteFrame + 1) % GLOBE_SPRITE_FRAME_COUNT;
            var frame = _globeSpriteFrames[_currentGlobeSpriteFrame];
            if (frame != null)
            {
                SpeedTestGlobeImage.Source = frame;
            }
        }

        /// <summary>
        /// Démarre l'animation sprite du globe.
        /// </summary>
        private void StartGlobeSpriteAnimation()
        {
            if (_globeSpriteTimer == null || _globeSpriteFrames == null) return;
            _currentGlobeSpriteFrame = 0;
            _globeSpriteTimer.Start();
            App.LogMessage("[GlobeSprite] Animation démarrée");
        }

        /// <summary>
        /// Arrête l'animation sprite du globe et revient à la frame 1.
        /// </summary>
        private void StopGlobeSpriteAnimation()
        {
            if (_globeSpriteTimer == null) return;
            _globeSpriteTimer.Stop();
            _currentGlobeSpriteFrame = 0;
            if (_globeSpriteFrames != null && _globeSpriteFrames.Length > 0 && _globeSpriteFrames[0] != null)
            {
                SpeedTestGlobeImage.Source = _globeSpriteFrames[0];
            }
            App.LogMessage("[GlobeSprite] Animation arrêtée");
        }

        /// <summary>
        /// Réagit aux changements de IsSpeedTestRunning pour contrôler l'animation.
        /// </summary>
        private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.IsSpeedTestRunning)) return;

            if (sender is MainViewModel vm)
            {
                if (vm.IsSpeedTestRunning)
                {
                    StartGlobeSpriteAnimation();
                }
                else
                {
                    StopGlobeSpriteAnimation();
                }
            }
        }

        /// <summary>
        /// DEBUG: Loggue les ActualWidth/Height du conteneur ring et de ses parents.
        /// RequiredSize = 400 (calculé dans XAML). Si ActualSize &lt; 400, le ring sera clippé.
        /// </summary>
        private void LogRingLayoutAudit(string context)
        {
            try
            {
                const double RequiredSize = 460.0; // MinVisualExtent: 300 + 2*50 + 2*14 + 32
                
                // Récupérer les éléments nommés
                var ringRow = FindName("RingRowContainer") as FrameworkElement;
                var ringBorder = FindName("RingOuterBorder") as FrameworkElement;
                var ringInner = FindName("RingInnerContainer") as FrameworkElement;
                
                if (ringRow == null || ringBorder == null)
                {
                    App.LogMessage($"[RingLayoutAudit:{context}] Éléments non trouvés (normal si pas en vue Healthcheck)");
                    return;
                }
                
                // Récupérer les tailles réelles
                var rowW = ringRow.ActualWidth;
                var rowH = ringRow.ActualHeight;
                var borderW = ringBorder.ActualWidth;
                var borderH = ringBorder.ActualHeight;
                var innerW = ringInner?.ActualWidth ?? 0;
                var innerH = ringInner?.ActualHeight ?? 0;
                
                // Vérifier si taille insuffisante
                bool isClipped = borderW < RequiredSize || borderH < RequiredSize;
                string status = isClipped ? "⚠️ CLIPPING PROBABLE" : "✅ OK";
                
                App.LogMessage($"[RingLayoutAudit:{context}] {status}");
                App.LogMessage($"  RingRowContainer: {rowW:F0}x{rowH:F0}");
                App.LogMessage($"  RingOuterBorder: {borderW:F0}x{borderH:F0} (Required: {RequiredSize})");
                App.LogMessage($"  RingInnerContainer: {innerW:F0}x{innerH:F0}");
                App.LogMessage($"  DPI: {VisualTreeHelper.GetDpi(this).PixelsPerDip:F2}");
                
                if (isClipped)
                {
                    App.LogMessage($"  ❌ INSUFFISANT: ActualWidth={borderW:F0} < Required={RequiredSize} OU ActualHeight={borderH:F0} < Required={RequiredSize}");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[RingLayoutAudit:{context}] Erreur: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Démarre l'audit du ring pendant le scan (appelé par le ViewModel si besoin)
        /// </summary>
        public void StartRingAuditDuringScan()
        {
            _ringAuditLogged = false;
            _ringLayoutAuditTimer?.Start();
        }

        /// <summary>
        /// FIX #7: Copie le contenu du tooltip dans le presse-papiers.
        /// </summary>
        private void CopyTooltipToClipboard(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is string tooltip && !string.IsNullOrEmpty(tooltip))
            {
                try
                {
                    Clipboard.SetText(tooltip);
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[CopyTooltip] Erreur: {ex.Message}");
                }
            }
        }

        private void OpenContextMenu(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.ContextMenu != null)
            {
                element.ContextMenu.PlacementTarget = element;
                element.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// Toggle l'affichage du champ de renommage du rapport
        /// </summary>
        private void ToggleReportRename(object sender, RoutedEventArgs e)
        {
            try
            {
                var textBox = FindName("ReportNameTextBox") as TextBox;
                var display = FindName("ReportNameDisplay") as StackPanel;
                
                if (textBox == null || display == null) return;
                
                if (textBox.Visibility == Visibility.Collapsed)
                {
                    // Activer le mode édition
                    textBox.Visibility = Visibility.Visible;
                    display.Visibility = Visibility.Collapsed;
                    textBox.Focus();
                    textBox.SelectAll();
                    
                    // Fermer l'édition quand on perd le focus ou appuie sur Entrée
                    textBox.LostFocus -= ReportNameTextBox_LostFocus;
                    textBox.LostFocus += ReportNameTextBox_LostFocus;
                    textBox.KeyDown -= ReportNameTextBox_KeyDown;
                    textBox.KeyDown += ReportNameTextBox_KeyDown;
                }
                else
                {
                    // Désactiver le mode édition
                    textBox.Visibility = Visibility.Collapsed;
                    display.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ToggleReportRename] Erreur: {ex.Message}");
            }
        }

        private void ReportNameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = FindName("ReportNameTextBox") as TextBox;
            var display = FindName("ReportNameDisplay") as StackPanel;
            if (textBox != null) textBox.Visibility = Visibility.Collapsed;
            if (display != null) display.Visibility = Visibility.Visible;
        }

        private void ReportNameTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Escape)
            {
                var textBox = FindName("ReportNameTextBox") as TextBox;
                var display = FindName("ReportNameDisplay") as StackPanel;
                if (textBox != null) textBox.Visibility = Visibility.Collapsed;
                if (display != null) display.Visibility = Visibility.Visible;
            }
        }
    }
}
