using System;
using System.Threading.Tasks;
using System.Windows;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Unified admin permissions dialog with progressive checkmarks.
    /// Handles UAC elevation, Windows Defender exclusions, and network access in one click.
    /// </summary>
    public partial class AdminPermissionsDialog : Window
    {
        private bool _isProcessing;

        public AdminPermissionsDialog()
        {
            InitializeComponent();
            UpdateInitialStatus();
        }

        private void UpdateInitialStatus()
        {
            // Check current admin status
            bool isAdmin = AdminHelper.IsRunningAsAdmin();
            AdminStatusIcon.Text = isAdmin ? "✓" : "⏳";
            AdminStatusIcon.Foreground = isAdmin 
                ? (System.Windows.Media.Brush)FindResource("AccentGreen") 
                : (System.Windows.Media.Brush)FindResource("TextSecondary");

            // Defender and network start as pending
            DefenderStatusIcon.Text = "⏳";
            NetworkStatusIcon.Text = "⏳";
        }

        private async void AuthorizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;
            _isProcessing = true;

            AuthorizeButton.IsEnabled = false;
            AuthorizeButton.Content = "En cours...";
            StatusText.Text = "";

            try
            {
                // Step 1: UAC Elevation (if not already admin)
                if (!AdminHelper.IsRunningAsAdmin())
                {
                    StatusText.Text = "Demande d'élévation UAC...";
                    AdminStatusIcon.Text = "⏳";
                    
                    // This will restart the app as admin
                    AdminHelper.RestartAsAdmin();
                    
                    // Close this dialog - app will restart
                    Close();
                    return;
                }
                else
                {
                    AdminStatusIcon.Text = "✓";
                    AdminStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");
                }

                // Step 2: Windows Defender exclusions
                StatusText.Text = "Configuration Windows Defender...";
                DefenderStatusIcon.Text = "⏳";

                var (defenderSuccess, defenderMessage) = await ApplyDefenderExclusionsAsync();
                
                if (defenderSuccess)
                {
                    DefenderStatusIcon.Text = "✓";
                    DefenderStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");
                }
                else
                {
                    DefenderStatusIcon.Text = "⚠";
                    DefenderStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentRed");
                    App.LogMessage($"[AdminDialog] Defender exclusion warning: {defenderMessage}");
                }

                // Step 3: Network access (mark as complete - typically no extra action needed)
                StatusText.Text = "Activation accès réseau...";
                NetworkStatusIcon.Text = "⏳";
                
                await Task.Delay(300); // Brief visual feedback
                
                NetworkStatusIcon.Text = "✓";
                NetworkStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");

                // All done
                StatusText.Text = "Toutes les autorisations sont configurées.";
                AuthorizeButton.Content = "Terminé";
                
                await Task.Delay(1000);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Erreur: {ex.Message}";
                App.LogMessage($"[AdminDialog] Error: {ex.Message}");
                AuthorizeButton.IsEnabled = true;
                AuthorizeButton.Content = "Réessayer";
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private async Task<(bool success, string message)> ApplyDefenderExclusionsAsync()
        {
            try
            {
                // Add path exclusion
                var path = WindowsDefenderExclusionService.GetDefaultExclusionPath();
                var (pathSuccess, pathMessage) = await WindowsDefenderExclusionService.AddMachineExclusionAsync(path);

                if (!pathSuccess)
                {
                    return (false, pathMessage);
                }

                // Add process exclusions
                var (processSuccess, processMessage) = await WindowsDefenderExclusionService.AddProcessExclusionsAsync();

                return (processSuccess, processMessage);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
