using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCDiagnosticPro.ViewModels;
using WpfAnimatedGif;

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

        /// <summary>
        /// True si le GIF planète est chargé (planet.gif présent). Utilisé pour Play/Pause selon IsSpeedTestRunning.
        /// </summary>
        private bool _speedTestPlanetGifLoaded;
        
        /// <summary>
        /// FIX #2: Storyboard pour rotation du fallback emoji quand GIF absent
        /// </summary>
        private Storyboard? _fallbackRotationStoryboard;

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
        /// Charge le GIF planète (Assets/Animations/planet.gif) si présent, et branche Play/Pause sur IsSpeedTestRunning.
        /// Si planet.gif est absent : fallback emoji affiché, TODO côté déploiement.
        /// </summary>
        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var gifPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Animations", "planet.gif");
                if (File.Exists(gifPath))
                {
                    var uri = new Uri(gifPath, UriKind.Absolute);
                    var bitmap = new BitmapImage(uri);
                    ImageBehavior.SetAnimatedSource(SpeedTestPlanetImage, bitmap);
                    SpeedTestPlanetImage.Visibility = Visibility.Visible;
                    SpeedTestPlanetFallback.Visibility = Visibility.Collapsed;
                    _speedTestPlanetGifLoaded = true;

                    var controller = ImageBehavior.GetAnimationController(SpeedTestPlanetImage);
                    controller?.Pause();
                }
                else
                {
                    // FIX #2: GIF absent - use fallback emoji with rotation storyboard
                    SpeedTestPlanetFallback.Visibility = Visibility.Visible;
                    // Get storyboard from XAML resources
                    if (SpeedTestPlanetFallback.Parent is FrameworkElement parentGrid)
                    {
                        _fallbackRotationStoryboard = parentGrid.TryFindResource("SpeedTestFallbackRotation") as Storyboard;
                    }
                }

                if (DataContext is MainViewModel vm)
                {
                    vm.PropertyChanged += OnMainViewModelPropertyChanged;
                    if (_speedTestPlanetGifLoaded && vm.IsSpeedTestRunning)
                    {
                        var ctrl = ImageBehavior.GetAnimationController(SpeedTestPlanetImage);
                        ctrl?.Play();
                    }
                    else if (!_speedTestPlanetGifLoaded && vm.IsSpeedTestRunning && _fallbackRotationStoryboard != null)
                    {
                        // Start fallback rotation if speedtest already running
                        _fallbackRotationStoryboard.Begin(this, true);
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SpeedTestPlanet] Erreur chargement GIF: {ex.Message}");
                SpeedTestPlanetFallback.Visibility = Visibility.Visible;
            }
        }

        private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.IsSpeedTestRunning))
                return;
            try
            {
                var vm = DataContext as MainViewModel;
                if (vm == null) return;
                
                if (_speedTestPlanetGifLoaded)
                {
                    // GIF loaded - control via WpfAnimatedGif
                    var controller = ImageBehavior.GetAnimationController(SpeedTestPlanetImage);
                    if (controller != null)
                    {
                        if (vm.IsSpeedTestRunning)
                            controller.Play();
                        else
                            controller.Pause();
                    }
                }
                else if (_fallbackRotationStoryboard != null)
                {
                    // FIX #2: GIF absent - control fallback rotation storyboard
                    if (vm.IsSpeedTestRunning)
                        _fallbackRotationStoryboard.Begin(this, true);
                    else
                        _fallbackRotationStoryboard.Stop(this);
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SpeedTestPlanet] Erreur Play/Pause: {ex.Message}");
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
                const double RequiredSize = 400.0;
                
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
    }
}
