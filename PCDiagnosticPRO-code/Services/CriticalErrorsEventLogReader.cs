using System;
using System.Diagnostics.Eventing.Reader;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Lecture synchrone du journal des événements pour les erreurs critiques (WHEA, BSOD, Kernel-Power 41).
    /// Utilisé en dernier recours quand diagnostic_signals et event_logs_detailed sont absents du JSON.
    /// </summary>
    public static class CriticalErrorsEventLogReader
    {
        private const int DaysBack = 30;

        /// <summary>
        /// Comptages des erreurs critiques sur les 30 derniers jours.
        /// </summary>
        public struct CriticalCounts
        {
            public int Whea;
            public int Bsod;
            public int KernelPower;
            public bool Success;
        }

        /// <summary>
        /// Lit le journal System pour WHEA, Kernel-Power 41 et BugCheck/WER-SystemErrorReporting (30 j).
        /// En cas d'erreur (accès refusé, etc.), retourne Success = false et des comptes à 0.
        /// </summary>
        public static CriticalCounts GetCounts()
        {
            var result = new CriticalCounts();
            var startTime = DateTime.UtcNow.AddDays(-DaysBack);

            try
            {
                result.Whea = CountEvents("System",
                    "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]",
                    startTime, 500);
                result.Bsod = CountEvents("System",
                    "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or (Provider[@Name='BugCheck'] and EventID=1001)]]",
                    startTime, 500);
                result.KernelPower = CountEvents("System",
                    "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41]]",
                    startTime, 500);
                result.Success = true;
            }
            catch (UnauthorizedAccessException ex)
            {
                App.LogMessage($"[CriticalErrorsEventLogReader] Accès refusé: {ex.Message}");
            }
            catch (EventLogNotFoundException) { /* Log absent */ }
            catch (Exception ex)
            {
                App.LogMessage($"[CriticalErrorsEventLogReader] Erreur: {ex.Message}");
            }

            return result;
        }

        private static int CountEvents(string logName, string xpath, DateTime startTimeUtc, int maxCount)
        {
            int count = 0;
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, xpath)
                {
                    ReverseDirection = true
                };
                using var reader = new EventLogReader(query);
                for (EventRecord record = reader.ReadEvent(); record != null && count < maxCount; record = reader.ReadEvent())
                {
                    if (record.TimeCreated.HasValue)
                    {
                        var t = record.TimeCreated.Value;
                        var utc = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
                        if (utc < startTimeUtc) break;
                    }
                    count++;
                }
            }
            catch (EventLogNotFoundException) { }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception) { throw; }

            return count;
        }
    }
}
