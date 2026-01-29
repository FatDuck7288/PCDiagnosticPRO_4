using System.Windows.Controls;

namespace PCDiagnosticPro.Controls
{
    /// <summary>
    /// Animation overlay pour le scan - affiche une animation matrix + fichiers + loupe
    /// Visible uniquement pendant IsScanning = true
    /// </summary>
    public partial class ScanAnimationOverlay : UserControl
    {
        public ScanAnimationOverlay()
        {
            InitializeComponent();
        }
    }
}
