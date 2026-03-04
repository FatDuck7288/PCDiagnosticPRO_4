using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Code-behind pour FullReportView.
    /// FIX: PreviewMouseWheel redirige la molette vers le ScrollViewer quand le focus est dans un enfant (DataGrid, etc.).
    /// </summary>
    public partial class FullReportView : UserControl
    {
        public FullReportView()
        {
            InitializeComponent();
        }

        /// <summary>Invalidates the performance dataset cache and reloads from remote (or cache). User can re-open the report to see updated scores.</summary>
        private void PerformanceRefreshRequirements_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PerformanceDatasetLoader.InvalidateAndReload();
                MessageBox.Show("Exigences rechargées. Rouvrez le rapport ou relancez un scan pour voir les scores à jour.", "Dataset performance", MessageBoxButton.OK);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible de recharger le dataset : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UserControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            var sv = ContentScroll;
            if (sv == null) return;
            // Ne rediriger que si la souris est au-dessus de la zone de contenu (ScrollViewer)
            var src = e.OriginalSource as DependencyObject;
            if (src != null && !IsVisualChildOf(sv, src))
                return;
            
            double step = e.Delta > 0 ? 60 : -60;
            double offset = sv.VerticalOffset - step;
            offset = System.Math.Max(0, System.Math.Min(sv.ScrollableHeight, offset));
            if (System.Math.Abs(offset - sv.VerticalOffset) > 0.01)
            {
                sv.ScrollToVerticalOffset(offset);
                e.Handled = true;
            }
        }

        private void SummaryToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not FullReportViewModel vm)
                return;

            var summarySection = vm.Sections.FirstOrDefault(s => string.Equals(s.Id, "ScanSummary", StringComparison.OrdinalIgnoreCase));
            if (summarySection == null)
                return;

            vm.SelectedSection = summarySection;
            SectionListBox?.ScrollIntoView(summarySection);
            ContentScroll?.ScrollToTop();
        }

        private static bool IsVisualChildOf(DependencyObject parent, DependencyObject child)
        {
            var p = VisualTreeHelper.GetParent(child);
            while (p != null)
            {
                if (p == parent) return true;
                p = VisualTreeHelper.GetParent(p);
            }
            return false;
        }

        private async void CheckBiosUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.IsEnabled = false;
                btn.Content = "Vérification en cours...";
            }

            try
            {
                var result = await FirmwareUpdateCheckService.CheckViaWindowsUpdateAsync(CancellationToken.None).ConfigureAwait(true);

                string title, message;
                MessageBoxImage icon;

                if (!result.Success)
                {
                    title = "Vérification BIOS — Erreur";
                    message = $"Impossible de vérifier via Windows Update.\n\nCause : {result.ErrorMessage}\n\n" +
                              "Conseil : Vérifiez manuellement sur le site du fabricant de votre carte mère.";
                    icon = MessageBoxImage.Warning;
                }
                else if (result.UpdateAvailable)
                {
                    title = "Vérification BIOS — Mise à jour disponible";
                    message = $"Source : {result.Source}\n\n{result.Details}\n\n" +
                              "Pour appliquer : Paramètres Windows → Windows Update → Mises à jour facultatives.";
                    icon = MessageBoxImage.Information;
                }
                else
                {
                    title = "Vérification BIOS — À jour";
                    message = $"Source : {result.Source}\n\n{result.Details}";
                    icon = MessageBoxImage.Information;
                }

                MessageBox.Show(message, title, MessageBoxButton.OK, icon);
            }
            finally
            {
                if (sender is Button btn2)
                {
                    btn2.IsEnabled = true;
                    btn2.Content = "Vérifier mise à jour BIOS (Windows Update)";
                }
            }
        }
    }
}
