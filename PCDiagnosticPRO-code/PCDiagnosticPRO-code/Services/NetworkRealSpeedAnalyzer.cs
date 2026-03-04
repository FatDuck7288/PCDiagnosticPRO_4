using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Vitesse réseau réelle — SpeedTest async non bloquant.
    /// Mesure Download (Mbps), Latency (ms). Upload optionnel si endpoint disponible.
    /// Grille : &lt;5 = Navigation only, 5-20 = Streaming HD, 20-100 = Gaming OK, &gt;100 = Cloud ready.
    /// </summary>
    public static class NetworkRealSpeedAnalyzer
    {
        private static readonly HttpClient SharedClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestHeaders = { { "User-Agent", "PC-Diagnostic-Pro/1.0" } }
        };

        /// <summary>URL de test téléchargement (fichier ~1 MB)</summary>
        private const string DownloadTestUrl = "https://proof.ovh.net/files/1Mb.dat";

        public class NetworkSpeedResult
        {
            public double? DownloadMbps { get; set; }
            public double? UploadMbps { get; set; }
            public double? LatencyMs { get; set; }
            public string SpeedTier { get; set; } = "Non disponible";
            public string Recommendation { get; set; } = "Non mesuré";
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }
        }

        /// <summary>
        /// Mesure asynchrone non bloquante : download + latence.
        /// </summary>
        public static async Task<NetworkSpeedResult> MeasureAsync(CancellationToken cancellationToken = default)
        {
            var result = new NetworkSpeedResult();
            var sw = Stopwatch.StartNew();
            try
            {
                var latencyTask = MeasureLatencyAsync(cancellationToken);
                var downloadTask = MeasureDownloadSpeedAsync(cancellationToken);

                await Task.WhenAll(latencyTask, downloadTask).ConfigureAwait(false);

                result.LatencyMs = await latencyTask.ConfigureAwait(false);
                var downloadMbps = await downloadTask.ConfigureAwait(false);
                result.DownloadMbps = downloadMbps;

                if (downloadMbps.HasValue)
                {
                    result.SpeedTier = GetSpeedTier(downloadMbps.Value);
                    result.Recommendation = GetRecommendation(downloadMbps.Value);
                    result.Success = true;
                }
                else if (result.LatencyMs.HasValue)
                {
                    result.SpeedTier = "Latence seule (download non disponible)";
                    result.Success = true;
                }
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Annulé";
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                App.LogMessage($"[NetworkSpeed] Erreur: {ex.Message}");
            }
            sw.Stop();
            return result;
        }

        private static async Task<double?> MeasureLatencyAsync(CancellationToken ct)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var req = new HttpRequestMessage(HttpMethod.Head, DownloadTestUrl);
                using var resp = await SharedClient.SendAsync(req, ct).ConfigureAwait(false);
                sw.Stop();
                return resp.IsSuccessStatusCode ? sw.Elapsed.TotalMilliseconds : (double?)null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<double?> MeasureDownloadSpeedAsync(CancellationToken ct)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var bytes = await SharedClient.GetByteArrayAsync(DownloadTestUrl, ct).ConfigureAwait(false);
                sw.Stop();
                if (sw.Elapsed.TotalSeconds <= 0) return null;
                double mbps = (bytes.Length * 8.0 / 1_000_000.0) / sw.Elapsed.TotalSeconds;
                return Math.Round(mbps, 2);
            }
            catch
            {
                return null;
            }
        }

        private static string GetSpeedTier(double downloadMbps)
        {
            if (downloadMbps < 5) return "Navigation only";
            if (downloadMbps < 20) return "Streaming HD";
            if (downloadMbps < 100) return "Gaming OK";
            return "Gaming compétitif / Cloud ready";
        }

        private static string GetRecommendation(double downloadMbps)
        {
            if (downloadMbps < 5) return "Débit limité — navigation et messagerie uniquement.";
            if (downloadMbps < 20) return "Streaming HD et télétravail OK.";
            if (downloadMbps < 100) return "Jeux et visio fluides.";
            return "Très bon débit — cloud et compétitif.";
        }
    }
}
