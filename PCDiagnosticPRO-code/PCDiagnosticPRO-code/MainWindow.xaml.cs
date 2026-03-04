using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.ViewModels;
using PCDiagnosticPro.Views;

namespace PCDiagnosticPro
{
    /// <summary>
    /// Code-behind pour MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Animation sprite globe (30 frames, 30 FPS)
        private DispatcherTimer? _globeSpriteTimer;
        private BitmapImage[]? _globeSpriteFrames;
        private int _currentGlobeSpriteFrame = 0;
        private const int GLOBE_SPRITE_FPS = 30;
        private const int GLOBE_SPRITE_FRAME_COUNT = 30;
        private readonly InfoContextResolver _infoContextResolver = new();
        private readonly InfoExplanationService _infoExplanationService = new();

        /// <summary>Preserved window bounds baseline remembered from user-resized window.</summary>
        private double _savedWidth, _savedHeight, _savedLeft, _savedTop;

        public MainWindow()
        {
            App.BootLog("MainWindow ctor begin");
            try
            {
                InitializeComponent();
                App.BootLog("MainWindow InitializeComponent end");
            }
            catch (Exception ex)
            {
                App.BootLog($"MainWindow InitializeComponent exception: {ex}");
                throw;
            }

            App.LogMessage("MainWindow initialisé");
            StateChanged += OnWindowStateChanged;
            Loaded += OnMainWindowLoaded;
            App.BootLog("MainWindow ctor end");
        }

        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            // Fullscreen/maximized remains allowed.
        }

        /// <summary>
        /// Charge les 30 frames sprites PNG et initialise le timer d'animation 30fps.
        /// </summary>
        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            App.BootLog($"MainWindow Loaded event handle={handle}");
            App.BootLog(
                $"MainWindow visibility state={WindowState} visibility={Visibility} opacity={Opacity:0.###} showInTaskbar={ShowInTaskbar}");

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            if (Opacity <= 0)
            {
                Opacity = 1;
            }
            if (!ShowInTaskbar)
            {
                ShowInTaskbar = true;
            }

            Topmost = true;
            Activate();
            Topmost = false;
            LoadGlobeSpriteFrames();
            InitializeGlobeSpriteTimer();
            // Remember initial window size so we can preserve it when entering Scan (avoid "reset to fullscreen").
            _savedLeft = Left;
            _savedTop = Top;
            _savedWidth = Width;
            _savedHeight = Height;
            SizeChanged += OnSizeChangedRememberOrRestore;
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnMainViewModelPropertyChanged;
            }
        }

        /// <summary>
        /// When NOT on Scan view: remember current size (user may have resized).
        /// </summary>
        private void OnSizeChangedRememberOrRestore(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is MainViewModel vm && (vm.CurrentView == "Healthcheck" || vm.IsScanning))
                return;
            _savedLeft = Left;
            _savedTop = Top;
            _savedWidth = Width;
            _savedHeight = Height;
        }

        /// <summary>
        /// Charge les 30 frames sprites depuis eEART2/ (00.png à 29.png).
        /// Priorité: eEART2/, sinon Assets/Animations/.
        /// </summary>
        private void LoadGlobeSpriteFrames()
        {
            try
            {
                // Rechercher eEART2/ en remontant l'arborescence depuis l'exe
                // (bin/Debug/net8.0-windows/ â†’ projet â†’ solution â†’ racine)
                string? sourceDir = FindDirectoryUpward(AppContext.BaseDirectory, "eEART2");
                
                // Sinon chercher dans Assets/Animations/ (dans le dossier de sortie, copié par le .csproj)
                if (sourceDir == null)
                {
                    var animDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Animations");
                    if (Directory.Exists(animDir))
                        sourceDir = animDir;
                }

                if (sourceDir == null)
                {
                    App.LogMessage("[GlobeSprite] Dossiers eEART2/ et Assets/Animations/ non trouvés");
                    return;
                }

                // Charger les 30 frames (00.png à 29.png)
                _globeSpriteFrames = new BitmapImage[GLOBE_SPRITE_FRAME_COUNT];
                int loadedCount = 0;

                for (int i = 0; i < GLOBE_SPRITE_FRAME_COUNT; i++)
                {
                    var framePath = Path.Combine(sourceDir, $"{i:D2}.png");
                    if (!File.Exists(framePath))
                    {
                        App.LogMessage($"[GlobeSprite] Frame {i:D2}.png non trouvée dans {sourceDir}");
                        continue;
                    }

                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(framePath, UriKind.Absolute);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad; // Préchargement immédiat
                        bitmap.EndInit();
                        bitmap.Freeze(); // Thread-safe, meilleure performance
                        _globeSpriteFrames[i] = bitmap;
                        loadedCount++;
                    }
                    catch (Exception ex)
                    {
                        App.LogMessage($"[GlobeSprite] Erreur chargement frame {i:D2}: {ex.Message}");
                    }
                }

                if (loadedCount == 0)
                {
                    App.LogMessage("[GlobeSprite] Aucune frame valide chargée");
                    _globeSpriteFrames = null;
                    return;
                }

                // Afficher la première frame par défaut (statique)
                if (_globeSpriteFrames[0] != null)
                {
                    SpeedTestGlobeImage.Source = _globeSpriteFrames[0];
                }

                App.LogMessage($"[GlobeSprite] {loadedCount}/{GLOBE_SPRITE_FRAME_COUNT} frames chargées depuis {sourceDir}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GlobeSprite] Erreur chargement: {ex.Message}");
            }
        }

        /// <summary>
        /// Remonte l'arborescence (max 8 niveaux) depuis startDir pour trouver un sous-dossier nommé dirName.
        /// Retourne le chemin complet du dossier trouvé, ou null.
        /// </summary>
        private static string? FindDirectoryUpward(string startDir, string dirName)
        {
            var current = startDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < 8; i++)
            {
                var candidate = Path.Combine(current, dirName);
                if (Directory.Exists(candidate))
                    return candidate;
                var parent = Path.GetDirectoryName(current);
                if (parent == null || parent == current) break;
                current = parent;
            }
            return null;
        }

        /// <summary>
        /// Initialise le timer d'animation sprite (30 FPS = ~33.333ms par frame).
        /// </summary>
        private void InitializeGlobeSpriteTimer()
        {
            _globeSpriteTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / GLOBE_SPRITE_FPS) // ~33.333ms pour 30fps
            };
            _globeSpriteTimer.Tick += OnGlobeSpriteTimerTick;
        }

        /// <summary>
        /// Tick du timer: avance à la frame suivante (boucle 0→29→0).
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
        /// Démarre l'animation sprite du globe (30fps).
        /// </summary>
        private void StartGlobeSpriteAnimation()
        {
            if (_globeSpriteTimer == null || _globeSpriteFrames == null) return;

            _currentGlobeSpriteFrame = 0;
            if (_globeSpriteFrames[0] != null)
            {
                SpeedTestGlobeImage.Source = _globeSpriteFrames[0];
            }
            _globeSpriteTimer.Start();
            App.LogMessage("[GlobeSprite] Animation démarrée (30fps)");
        }

        /// <summary>
        /// Arrête l'animation sprite du globe et revient à la frame 0.
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
        /// Réagit aux changements de IsSpeedTestRunning pour contrôler l'animation sprite.
        /// Préserve aussi la taille/position de la fenêtre quand on passe en vue Scan ou au démarrage du scan.
        /// </summary>

        private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MainViewModel vm) return;

            if (e.PropertyName == nameof(MainViewModel.IsSpeedTestRunning))
            {
                if (vm.IsSpeedTestRunning)
                    StartGlobeSpriteAnimation();
                else
                    StopGlobeSpriteAnimation();
                return;
            }

            // Préserver les proportions de la fenêtre quand on va sur Scan ou qu'on démarre le scan (éviter passage en fullscreen).
            if (e.PropertyName == nameof(MainViewModel.CurrentView) || e.PropertyName == nameof(MainViewModel.IsScanning))
            {
                if (vm.CurrentView == "Healthcheck" || vm.IsScanning)
                {
                    // Do NOT overwrite _saved* with current size here: something may already have set the window to 1100x800.
                    // We use the size we remembered from Home (OnSizeChangedRememberOrRestore / Loaded). Only if never set, use current.
                    if (_savedWidth <= 0 || _savedHeight <= 0)
                    {
                        var rect = RestoreBounds;
                        if (rect.Width > 0 && rect.Height > 0)
                        {
                            _savedLeft = rect.Left;
                            _savedTop = rect.Top;
                            _savedWidth = rect.Width;
                            _savedHeight = rect.Height;
                        }
                        else
                        {
                            _savedLeft = Left;
                            _savedTop = Top;
                            _savedWidth = Width;
                            _savedHeight = Height;
                        }
                    }
                }
            }
        }

        // #region agent log
        private void HealthcheckScrollViewer_Loaded(object sender, RoutedEventArgs e)
        {
            void MeasureAndLog()
            {
                try
                {
                    var sv = HealthcheckScrollViewer;
                    if (sv == null) return;
                    var verticalBar = sv.Template?.FindName("PART_VerticalScrollBar", sv) as ScrollBar;
                    double scrollBarWidth = verticalBar != null ? verticalBar.ActualWidth : -1;
                    double rightPanelLeft = -1;
                    if (sv.Content is Grid g && g.Children.Count > 1 && g.Children[1] is Border rightBorder)
                        rightPanelLeft = rightBorder.BorderThickness.Left;
                    var logPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".cursor", "debug.log");
                    logPath = Path.GetFullPath(logPath);
                    var line = "{\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ",\"location\":\"MainWindow.xaml.cs:HealthcheckScrollViewer_Loaded\",\"message\":\"Bar widths\",\"data\":{\"verticalScrollBarActualWidth\":" + scrollBarWidth.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"rightPanelBorderLeft\":" + rightPanelLeft.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"progressBarRefHeight\":4},\"hypothesisId\":\"H1\"}" + Environment.NewLine;
                    File.AppendAllText(logPath, line);
                }
                catch (Exception) { }
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)MeasureAndLog);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)MeasureAndLog);
        }
        // #endregion agent log

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
                element.ContextMenu.DataContext = DataContext;
                element.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// Opens the Kernel Power detail window with real event data.
        /// FIX #5: Route vers la fenêtre contextuelle appropriée selon le type de métrique.
        /// </summary>
        private void OpenKernelPowerInfoWindow(object sender, RoutedEventArgs e)
        {
            // Essayer d'abord de résoudre le contexte depuis la ligne cliquée
            if (sender is FrameworkElement fe)
            {
                var section = FindAncestorDataContext<HealthSection>(fe);
                var item = ResolveEvidenceItemFromVisualTree(fe);
                
                if (item != null && section != null)
                {
                    var context = _infoContextResolver.ResolveFromMetric(section, item);
                    
                    // Si le contexte est Kernel-Power, ouvrir la fenêtre spécialisée
                    if (context.ContextId == InfoContextId.KernelPower)
                    {
                        var vm = DataContext as PCDiagnosticPro.ViewModels.MainViewModel;
                        var kpData = vm?.KernelPowerEvents;
                        var win = new KernelPowerInfoWindow(kpData) { Owner = this };
                        win.ShowDialog();
                        return;
                    }
                    
                    // Pour les autres contextes, ouvrir la fenêtre unifiée
                    ShowUnifiedInfoDialog(context);
                    return;
                }
                
                // Fallback: si on a une section mais pas d'item, utiliser le contexte de section
                if (section != null)
                {
                    var fallbackContextId = section.Domain switch
                    {
                        HealthDomain.CPU => InfoContextId.CPUTemperature,
                        HealthDomain.GPU => InfoContextId.GPULoad,
                        HealthDomain.Storage => InfoContextId.DiskTemp,
                        HealthDomain.SystemStability => InfoContextId.KernelPower,
                        HealthDomain.Network => InfoContextId.NetworkLoss,
                        HealthDomain.Security => InfoContextId.SecurityAntivirus,
                        _ => InfoContextId.Unknown
                    };
                    
                    if (fallbackContextId != InfoContextId.Unknown)
                    {
                        var fallbackContext = _infoContextResolver.ResolveFromSection(section, fallbackContextId);
                        ShowUnifiedInfoDialog(fallbackContext);
                        return;
                    }
                }
            }
            
            // Dernier fallback: fenêtre Kernel-Power par défaut
            var vmDefault = DataContext as PCDiagnosticPro.ViewModels.MainViewModel;
            var kpDataDefault = vmDefault?.KernelPowerEvents;
            var winDefault = new KernelPowerInfoWindow(kpDataDefault) { Owner = this };
            winDefault.ShowDialog();
        }

        /// <summary>
        /// Open contextual explanation for any row with dynamic adaptation to key/value/severity.
        /// </summary>
        private void OpenCyberpunkInfoWindow(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not FrameworkElement fe)
                    return;

                var section = FindAncestorDataContext<HealthSection>(fe) ?? BuildFallbackSection(HealthDomain.OS);
                var item = ResolveEvidenceItemFromVisualTree(fe);

                if (item != null)
                {
                    var context = _infoContextResolver.ResolveFromMetric(section, item);
                    ShowUnifiedInfoDialog(context);
                    return;
                }

                // Fallback: open a section-level explanation instead of doing nothing.
                var fallbackContextId = section.Domain switch
                {
                    HealthDomain.CPU => InfoContextId.CPUTemperature,
                    HealthDomain.GPU => InfoContextId.GPULoad,
                    HealthDomain.Storage => InfoContextId.DiskTemp,
                    HealthDomain.SystemStability => InfoContextId.KernelPower,
                    HealthDomain.Network => InfoContextId.NetworkLoss,
                    _ => InfoContextId.Unknown
                };
                var fallbackContext = _infoContextResolver.ResolveFromSection(section, fallbackContextId);
                ShowUnifiedInfoDialog(fallbackContext);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[InfoDialog] OpenCyberpunkInfoWindow failed: {ex.Message}");
            }
        }

        private void OpenDiskTemperatureInfoWindow(object sender, RoutedEventArgs e)
        {
            var section = FindAncestorDataContext<HealthSection>(sender as DependencyObject) ??
                          BuildFallbackSection(HealthDomain.Storage);
            var context = _infoContextResolver.ResolveFromSection(section, InfoContextId.DiskTemp);
            var lines = _infoExplanationService.BuildInfoLines(context);
            var win = new DiskTemperatureInfoWindow(lines) { Owner = this };
            win.ShowDialog();
        }

        private void ShowUnifiedInfoDialog(InfoContext context)
        {
            var title = BuildInfoDialogTitle(context);
            var lines = _infoExplanationService.BuildInfoLines(context);
            var win = new CyberpunkInfoWindow(title, lines) { Owner = this };
            win.ShowDialog();
        }

        private static string BuildInfoDialogTitle(InfoContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.MetricLabel))
                return context.MetricLabel;

            return context.ContextId switch
            {
                InfoContextId.DiskTemp => "Température disque",
                InfoContextId.SMARTHealth => "Santé SMART",
                InfoContextId.TDR => "TDR",
                InfoContextId.WHEA => "Erreurs WHEA",
                InfoContextId.VRAM => "VRAM",
                InfoContextId.GPULoad => "Charge GPU",
                InfoContextId.CPUTemperature => "Température CPU",
                InfoContextId.CPUThrottle => "Throttling CPU",
                InfoContextId.KernelPower => "Kernel-Power",
                InfoContextId.RestorePoints => "Points de restauration",
                InfoContextId.RebootRequired => "Redémarrage requis",
                InfoContextId.UpdatesPending => "Updates Windows",
                InfoContextId.BSOD => "BSOD",
                InfoContextId.NetworkLoss => "Perte de paquets",
                InfoContextId.SecurityAntivirus => "Antivirus",
                InfoContextId.SecurityFirewall => "Pare-feu",
                InfoContextId.SecuritySecureBoot => "Secure Boot",
                InfoContextId.SecurityBitLocker => "BitLocker",
                InfoContextId.SecurityUac => "UAC",
                InfoContextId.SecuritySmbV1 => "SMBv1",
                InfoContextId.SecurityTamperProtection => "Protection contre altération",
                InfoContextId.SecurityRealTimeProtection => "Protection en temps réel",
                InfoContextId.SecurityVbs => "VBS",
                InfoContextId.SecurityCredentialGuard => "Credential Guard",
                InfoContextId.SecurityMemoryIntegrity => "Intégrité mémoire",
                InfoContextId.SecurityAsr => "Règles ASR",
                _ => "Information"
            }; 
        }

        private static T? FindAncestorDataContext<T>(DependencyObject? start) where T : class
        {
            var current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.DataContext is T candidate)
                    return candidate;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static EvidenceItem? ResolveEvidenceItemFromVisualTree(DependencyObject? start)
        {
            var current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe)
                {
                    var item = TryCreateEvidenceItem(fe.DataContext);
                    if (item != null)
                        return item;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static EvidenceItem? TryCreateEvidenceItem(object? candidate)
        {
            if (candidate is EvidenceItem evidenceItem)
                return evidenceItem;

            if (candidate == null)
                return null;

            var candidateType = candidate.GetType();
            var keyProperty = candidateType.GetProperty("Key");
            var valueProperty = candidateType.GetProperty("Value");
            if (keyProperty == null || valueProperty == null)
                return null;

            var key = TextEncodingNormalizer.Normalize(keyProperty.GetValue(candidate)?.ToString() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(key))
                return null;

            var value = TextEncodingNormalizer.Normalize(valueProperty.GetValue(candidate)?.ToString() ?? string.Empty);
            return new EvidenceItem
            {
                Key = key,
                Value = value
            };
        }

        private static HealthSection BuildFallbackSection(HealthDomain domain)
        {
            return new HealthSection
            {
                Domain = domain,
                DisplayName = domain.ToString(),
                EvidenceData = new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Opens Windows Update settings page (ms-settings:windowsupdate).
        /// Used when pending updates count is clicked from OS report row.
        /// </summary>
        private void OpenWindowsUpdateSettings(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:windowsupdate",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                App.LogMessage($"[OpenWindowsUpdateSettings] Erreur: {ex.Message}");
            }
        }

        /// <summary>
        /// Pencil button on the report detail page - triggers RenameScanCommand on SelectedHistoryScan.
        /// Uses the same unified IsRenaming flow as Page A.
        /// </summary>
        private void ReportPencil_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.MainViewModel vm && vm.SelectedHistoryScan != null)
            {
                vm.RenameScanCommand.Execute(vm.SelectedHistoryScan);
            }
        }

        // ========== INLINE RENAME (liste d'historique) ==========

        /// <summary>Double-clic sur le titre dans la liste d'historique â†’ active le mode renommage.</summary>
        private void HistoryTitle_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2 && sender is FrameworkElement fe && fe.DataContext is ViewModels.ScanHistoryItem item)
            {
                if (DataContext is ViewModels.MainViewModel vm)
                    vm.RenameScanCommand.Execute(item);
            }
        }

        /// <summary>Quand le TextBox inline apparaît, focus + sélectionner tout le texte.</summary>
        private void InlineRenameBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.Visibility == Visibility.Visible)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }

        /// <summary>Quand le TextBox perd le focus â†’ valider le renommage.</summary>
        private void InlineRenameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.Tag is ViewModels.ScanHistoryItem item)
            {
                if (DataContext is ViewModels.MainViewModel vm)
                    vm.CommitRename(item);
            }
        }

        /// <summary>Entrée = valider (commit + validate), Escape = annuler (cancel without saving).</summary>
        private void InlineRenameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.Tag is ViewModels.ScanHistoryItem item)
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    if (DataContext is ViewModels.MainViewModel vm)
                        vm.CommitRename(item);
                    e.Handled = true;
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                {
                    if (DataContext is ViewModels.MainViewModel vm)
                        vm.CancelRename(item);
                    e.Handled = true;
                }
            }
        }

        /// <summary>
        /// Forces mouse wheel events to always scroll the main DetailScrollViewer,
        /// preventing nested ScrollViewers (e.g. horizontal scroll in Performance bar chart)
        /// from capturing vertical wheel events and creating a "dead zone".
        /// </summary>
        private void DetailScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;

            var sv = DetailScrollViewer;
            if (sv == null) return;

            // Compute new offset
            double step = e.Delta > 0 ? -48 : 48;
            double newOffset = sv.VerticalOffset + step;
            newOffset = Math.Max(0, Math.Min(sv.ScrollableHeight, newOffset));
            sv.ScrollToVerticalOffset(newOffset);
            e.Handled = true;
        }
    }
}


