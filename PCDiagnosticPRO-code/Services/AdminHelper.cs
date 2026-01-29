using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Gère la détection du mode administrateur et la relance UAC si nécessaire.
    /// </summary>
    public static class AdminHelper
    {
        /// <summary>
        /// Vérifie si l'application s'exécute en mode administrateur.
        /// </summary>
        public static bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Propose à l'utilisateur de relancer en mode administrateur.
        /// </summary>
        /// <param name="message">Message à afficher</param>
        /// <returns>True si l'utilisateur choisit de relancer</returns>
        public static bool PromptAndRestartAsAdmin(string message)
        {
            var result = MessageBox.Show(
                message + "\n\nVoulez-vous relancer l'application en mode administrateur ?",
                "Droits administrateur requis",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                return RestartAsAdmin();
            }

            return false;
        }

        /// <summary>
        /// Relance l'application avec élévation UAC.
        /// </summary>
        /// <returns>True si la relance a réussi</returns>
        public static bool RestartAsAdmin()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    App.LogMessage("[AdminHelper] Impossible de déterminer le chemin de l'exécutable");
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas" // Demande élévation UAC
                };

                Process.Start(startInfo);
                
                // Fermer l'application actuelle
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Application.Current.Shutdown();
                });

                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // L'utilisateur a annulé la demande UAC
                App.LogMessage("[AdminHelper] Utilisateur a annulé la demande UAC");
                return false;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AdminHelper] Erreur relance admin: {ex.Message}");
                MessageBox.Show(
                    $"Impossible de relancer en mode administrateur.\n\n{ex.Message}",
                    "Erreur",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Message explicatif pour l'utilisateur sur l'importance du mode admin.
        /// </summary>
        public static string GetAdminExplanation()
        {
            return "Le mode administrateur permet un diagnostic plus complet :\n" +
                   "• Accès aux capteurs de température (CPU, GPU, disques)\n" +
                   "• Lecture des compteurs de performance système\n" +
                   "• Analyse des journaux d'événements Windows\n" +
                   "• Vérification de l'intégrité système (SFC/DISM)";
        }
    }
}
