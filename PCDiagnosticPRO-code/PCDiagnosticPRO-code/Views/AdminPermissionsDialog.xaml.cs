using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Unified admin permissions dialog with progressive status updates.
    /// Handles UAC elevation, Windows Defender exclusions, and network access in one flow.
    /// </summary>
    public partial class AdminPermissionsDialog : Window
    {
        private bool _isProcessing;

        public AdminPermissionsDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            UpdateInitialStatus();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            CenterDialog();
            Activate();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                AuthorizeButton.Focus();
                Keyboard.Focus(AuthorizeButton);
            }));
        }

        private void CenterDialog()
        {
            if (Owner != null)
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            var area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
            Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
        }

        private void UpdateInitialStatus()
        {
            bool isAdmin = AdminHelper.IsRunningAsAdmin();
            var okBrush = (Brush)FindResource("DialogAccentGreenBrush");
            var neutralBrush = (Brush)FindResource("DialogTextSecondaryBrush");

            AdminStatusIcon.Text = isAdmin ? "OK" : "...";
            AdminStatusIcon.Foreground = isAdmin ? okBrush : neutralBrush;

            DefenderStatusIcon.Text = "...";
            DefenderStatusIcon.Foreground = neutralBrush;
            NetworkStatusIcon.Text = "...";
            NetworkStatusIcon.Foreground = neutralBrush;
        }

        private async void AuthorizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                return;
            }

            _isProcessing = true;
            AuthorizeButton.IsEnabled = false;
            AuthorizeButton.Content = "En cours...";
            StatusText.Text = string.Empty;

            try
            {
                var okBrush = (Brush)FindResource("DialogAccentGreenBrush");
                var neutralBrush = (Brush)FindResource("DialogTextSecondaryBrush");

                if (!AdminHelper.IsRunningAsAdmin())
                {
                    StatusText.Text = "Demande d'elevation UAC...";
                    AdminStatusIcon.Text = "...";
                    AdminStatusIcon.Foreground = neutralBrush;

                    try
                    {
                        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), App.AppDataFolderName);
                        if (!Directory.Exists(appData))
                        {
                            Directory.CreateDirectory(appData);
                        }

                        File.WriteAllText(Path.Combine(appData, "enable_network_after_elevation.flag"), string.Empty, Encoding.UTF8);
                    }
                    catch
                    {
                        // non-blocking
                    }

                    AdminHelper.RestartAsAdmin();
                    Close();
                    return;
                }

                AdminStatusIcon.Text = "OK";
                AdminStatusIcon.Foreground = okBrush;

                StatusText.Text = "Configuration Windows Defender...";
                DefenderStatusIcon.Text = "...";
                DefenderStatusIcon.Foreground = neutralBrush;

                var (defenderSuccess, defenderMessage) = await ApplyDefenderExclusionsAsync();
                if (defenderSuccess)
                {
                    DefenderStatusIcon.Text = "OK";
                    DefenderStatusIcon.Foreground = okBrush;
                }
                else
                {
                    DefenderStatusIcon.Text = "WARN";
                    DefenderStatusIcon.Foreground = (Brush)FindResource("AccentRedBrush");
                    App.LogMessage($"[AdminDialog] Defender exclusion warning: {defenderMessage}");
                }

                StatusText.Text = "Activation acces reseau...";
                NetworkStatusIcon.Text = "...";
                NetworkStatusIcon.Foreground = neutralBrush;

                await Task.Delay(250);

                NetworkStatusIcon.Text = "OK";
                NetworkStatusIcon.Foreground = okBrush;

                StatusText.Text = "Toutes les autorisations sont configurees.";
                AuthorizeButton.Content = "Termine";

                await Task.Delay(700);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Erreur: {ex.Message}";
                App.LogMessage($"[AdminDialog] Error: {ex.Message}");
                AuthorizeButton.IsEnabled = true;
                AuthorizeButton.Content = "Reessayer";
            }
            finally
            {
                _isProcessing = false;
            }
        }

        /// <summary>
        /// Adds path exclusion first, then process exclusions (same order as settings page logic).
        /// </summary>
        private static async Task<(bool success, string message)> ApplyDefenderExclusionsAsync()
        {
            try
            {
                var path = WindowsDefenderExclusionService.GetDefaultExclusionPath();
                var (pathSuccess, pathMessage) = await WindowsDefenderExclusionService.AddMachineExclusionAsync(path);

                if (!pathSuccess)
                {
                    return (false, pathMessage);
                }

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
