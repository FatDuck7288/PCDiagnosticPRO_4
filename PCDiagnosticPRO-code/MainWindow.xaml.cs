using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

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
        
        public MainWindow()
        {
            InitializeComponent();
            App.LogMessage("MainWindow initialisé");
            
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
