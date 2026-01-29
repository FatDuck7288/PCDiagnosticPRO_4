using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Résultat de la tentative d'élévation admin
    /// </summary>
    public enum ElevationResult
    {
        Success,
        UserCancelled,
        AlreadyElevated,
        Error
    }

    /// <summary>
    /// Service pour gérer les privilèges administrateur
    /// </summary>
    public static class AdminService
    {
        /// <summary>
        /// Argument pour indiquer que l'application a déjà été relancée en admin
        /// </summary>
        public const string ElevatedArg = "--elevated";

        /// <summary>
        /// Vérifie si l'application s'exécute en mode administrateur
        /// </summary>
        public static bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur vérification admin: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Vérifie si l'application a été lancée avec l'argument --elevated
        /// </summary>
        public static bool WasLaunchedElevated()
        {
            var args = Environment.GetCommandLineArgs();
            return args.Any(a => string.Equals(a, ElevatedArg, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Redémarre l'application en mode administrateur avec protection anti-boucle
        /// </summary>
        /// <returns>Résultat de la tentative d'élévation</returns>
        public static ElevationResult RestartAsAdmin()
        {
            // Protection anti-boucle: si déjà lancé avec --elevated, ne pas relancer
            if (WasLaunchedElevated())
            {
                App.LogMessage("RestartAsAdmin: Application déjà lancée avec --elevated, abandon pour éviter boucle infinie");
                return ElevationResult.AlreadyElevated;
            }

            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    App.LogMessage("RestartAsAdmin: Chemin de l'exécutable introuvable");
                    return ElevationResult.Error;
                }

                App.LogMessage($"RestartAsAdmin: Tentative d'élévation pour {exePath}");

                // Collecter les arguments existants (sauf --elevated s'il existe déjà)
                var existingArgs = Environment.GetCommandLineArgs()
                    .Skip(1) // Premier argument = chemin de l'exe
                    .Where(a => !string.Equals(a, ElevatedArg, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Ajouter l'argument --elevated
                existingArgs.Add(ElevatedArg);
                var arguments = string.Join(" ", existingArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                App.LogMessage($"RestartAsAdmin: Lancement avec arguments: {arguments}");

                Process.Start(startInfo);
                
                App.LogMessage("RestartAsAdmin: Nouveau processus lancé, fermeture de l'application courante");
                Environment.Exit(0);
                
                return ElevationResult.Success;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: L'utilisateur a annulé la boîte de dialogue UAC
                App.LogMessage("RestartAsAdmin: Utilisateur a annulé la demande UAC (erreur 1223)");
                return ElevationResult.UserCancelled;
            }
            catch (Exception ex)
            {
                App.LogMessage($"RestartAsAdmin: Erreur lors du redémarrage en admin: {ex.Message}");
                return ElevationResult.Error;
            }
        }

        /// <summary>
        /// Teste l'élévation admin avec logging détaillé (pour --diag-elevation)
        /// </summary>
        public static int DiagnoseElevation()
        {
            var logLines = new System.Collections.Generic.List<string>();
            
            void Log(string message)
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                logLines.Add(line);
                Console.WriteLine(line);
                App.LogMessage($"[DIAG-ELEVATION] {message}");
            }

            try
            {
                Log("=== Diagnostic d'élévation admin ===");
                Log($"ProcessPath: {Environment.ProcessPath}");
                Log($"CommandLine: {Environment.CommandLine}");
                Log($"Arguments: {string.Join(", ", Environment.GetCommandLineArgs())}");
                Log($"IsRunningAsAdmin: {IsRunningAsAdmin()}");
                Log($"WasLaunchedElevated: {WasLaunchedElevated()}");

                using var identity = WindowsIdentity.GetCurrent();
                Log($"CurrentUser: {identity.Name}");
                
                var principal = new WindowsPrincipal(identity);
                Log($"IsInRole(Administrator): {principal.IsInRole(WindowsBuiltInRole.Administrator)}");

                if (WasLaunchedElevated())
                {
                    Log("Test réussi: Application lancée avec --elevated");
                    if (IsRunningAsAdmin())
                    {
                        Log("SUCCÈS: Application en mode administrateur");
                        SaveDiagLog(logLines, 0);
                        return 0;
                    }
                    else
                    {
                        Log("ÉCHEC: --elevated présent mais pas admin");
                        SaveDiagLog(logLines, 1);
                        return 1;
                    }
                }
                else
                {
                    Log("Tentative d'élévation...");
                    var result = RestartAsAdmin();
                    Log($"Résultat: {result}");
                    
                    if (result == ElevationResult.UserCancelled)
                    {
                        Log("INFO: Utilisateur a annulé UAC");
                        SaveDiagLog(logLines, 2);
                        return 2;
                    }
                    else if (result == ElevationResult.Error)
                    {
                        Log("ERREUR: Échec de l'élévation");
                        SaveDiagLog(logLines, 3);
                        return 3;
                    }
                    
                    // Si Success, l'application va se fermer
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION: {ex}");
                SaveDiagLog(logLines, 99);
                return 99;
            }
        }

        private static void SaveDiagLog(System.Collections.Generic.List<string> lines, int exitCode)
        {
            try
            {
                var logPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"PCDiagnosticPro_elevation_diag_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                
                lines.Add($"Exit code: {exitCode}");
                System.IO.File.WriteAllLines(logPath, lines);
                Console.WriteLine($"Log sauvegardé: {logPath}");
            }
            catch
            {
                // Ignorer les erreurs de sauvegarde du log
            }
        }
    }
}
