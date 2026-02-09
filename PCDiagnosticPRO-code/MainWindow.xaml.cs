using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        // Animation sprite globe (30 frames, 30 FPS)
        private DispatcherTimer? _globeSpriteTimer;
        private BitmapImage[]? _globeSpriteFrames;
        private int _currentGlobeSpriteFrame = 0;
        private const int GLOBE_SPRITE_FPS = 30;
        private const int GLOBE_SPRITE_FRAME_COUNT = 30;

        public MainWindow()
        {
            InitializeComponent();
            App.LogMessage("MainWindow initialisé");

            Loaded += OnMainWindowLoaded;
        }

        /// <summary>
        /// Charge les 30 frames sprites PNG et initialise le timer d'animation 30fps.
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
        /// Charge les 30 frames sprites depuis eEART2/ (00.png à 29.png).
        /// Priorité: eEART2/, sinon Assets/Animations/.
        /// </summary>
        private void LoadGlobeSpriteFrames()
        {
            try
            {
                // Rechercher eEART2/ en remontant l'arborescence depuis l'exe
                // (bin/Debug/net8.0-windows/ → projet → solution → racine)
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
        /// Pencil button on the report detail page — triggers RenameScanCommand on SelectedHistoryScan.
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

        /// <summary>Double-clic sur le titre dans la liste d'historique → active le mode renommage.</summary>
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

        /// <summary>Quand le TextBox perd le focus → valider le renommage.</summary>
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
    }
}
