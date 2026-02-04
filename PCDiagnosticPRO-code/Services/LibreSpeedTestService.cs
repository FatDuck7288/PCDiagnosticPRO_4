using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Service de test de vitesse internet utilisant LibreSpeed CLI.
    /// Gère l'installation automatique, l'exécution et le parsing des résultats.
    /// </summary>
    public class LibreSpeedTestService
    {
        private const string ToolsDir = "Tools";
        private const string LibreSpeedDir = "librespeed";
        private const string ExeName = "librespeed-cli.exe";
        
        // URL officielle de la release LibreSpeed CLI
        private const string DownloadUrl = "https://github.com/librespeed/speedtest-cli/releases/download/v1.0.10/librespeed-cli_1.0.10_windows_amd64.zip";
        
        private readonly string _installPath;
        private readonly string _exePath;
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(120);

        public LibreSpeedTestService()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDir = Path.Combine(localAppData, "PCDiagnosticPRO");
            _installPath = Path.Combine(appDir, ToolsDir, LibreSpeedDir);
            _exePath = Path.Combine(_installPath, ExeName);
        }

        /// <summary>
        /// Résultat du test de vitesse LibreSpeed
        /// </summary>
        public class SpeedTestResult
        {
            public bool Success { get; set; }
            public string? Error { get; set; }
            
            // Résultats principaux
            public double? DownloadMbps { get; set; }
            public double? UploadMbps { get; set; }
            public double? PingMs { get; set; }
            public double? JitterMs { get; set; }
            
            // Informations serveur
            public string? ServerName { get; set; }
            public string? ServerLocation { get; set; }
            public string? ServerSponsor { get; set; }
            
            // Métadonnées
            public DateTime TestTime { get; set; } = DateTime.Now;
            public string? RawJson { get; set; }
            
            // Tiers de performance (basé sur download)
            public string SpeedTier => DownloadMbps switch
            {
                >= 500 => "Ultra-rapide (Fibre)",
                >= 100 => "Très rapide",
                >= 50 => "Rapide",
                >= 25 => "Correct",
                >= 10 => "Lent",
                > 0 => "Très lent",
                _ => "Non mesuré"
            };
            
            // Couleur du tier
            public string SpeedTierColor => DownloadMbps switch
            {
                >= 100 => "#22C55E",  // Vert
                >= 25 => "#F59E0B",   // Orange
                > 0 => "#EF4444",     // Rouge
                _ => "#6B7280"        // Gris
            };
        }

        /// <summary>
        /// Vérifie si LibreSpeed CLI est installé
        /// </summary>
        public bool IsInstalled => File.Exists(_exePath);

        /// <summary>
        /// Installe LibreSpeed CLI si non présent
        /// </summary>
        public async Task<bool> EnsureInstalledAsync(CancellationToken cancellationToken = default)
        {
            if (IsInstalled)
            {
                App.LogMessage("[LibreSpeed] CLI déjà installé");
                return true;
            }

            App.LogMessage("[LibreSpeed] Installation de LibreSpeed CLI...");
            
            try
            {
                // Créer le répertoire d'installation
                Directory.CreateDirectory(_installPath);
                
                var zipPath = Path.Combine(_installPath, "librespeed-cli.zip");
                
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromMinutes(5);
                
                // Télécharger le ZIP
                App.LogMessage($"[LibreSpeed] Téléchargement depuis {DownloadUrl}");
                var response = await httpClient.GetAsync(DownloadUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                
                var zipData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                await File.WriteAllBytesAsync(zipPath, zipData, cancellationToken);
                
                // Extraire le ZIP
                App.LogMessage("[LibreSpeed] Extraction...");
                ZipFile.ExtractToDirectory(zipPath, _installPath, overwriteFiles: true);
                
                // Nettoyer le ZIP
                File.Delete(zipPath);
                
                // Vérifier que l'exe existe maintenant
                if (!File.Exists(_exePath))
                {
                    // Chercher l'exe dans les sous-dossiers
                    var files = Directory.GetFiles(_installPath, "*.exe", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        var foundExe = files[0];
                        File.Move(foundExe, _exePath);
                    }
                }
                
                if (File.Exists(_exePath))
                {
                    App.LogMessage($"[LibreSpeed] Installation réussie: {_exePath}");
                    return true;
                }
                else
                {
                    App.LogMessage("[LibreSpeed] ERREUR: exe non trouvé après extraction");
                    return false;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[LibreSpeed] Erreur d'installation: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Exécute le test de vitesse avec LibreSpeed CLI
        /// </summary>
        public async Task<SpeedTestResult> RunTestAsync(CancellationToken cancellationToken = default)
        {
            var result = new SpeedTestResult();
            
            try
            {
                // S'assurer que LibreSpeed est installé
                if (!await EnsureInstalledAsync(cancellationToken))
                {
                    result.Error = "Impossible d'installer LibreSpeed CLI";
                    return result;
                }

                App.LogMessage("[LibreSpeed] Démarrage du test de vitesse...");
                
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeout);

                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = "--json --no-icmp", // Format JSON, pas de ping ICMP (évite problèmes admin)
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = _installPath
                };

                using var process = new Process { StartInfo = psi };
                var output = new System.Text.StringBuilder();
                var error = new System.Text.StringBuilder();
                
                process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };
                
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                // Attendre avec timeout
                var completed = await Task.Run(() => process.WaitForExit((int)_timeout.TotalMilliseconds), cts.Token);
                
                if (!completed)
                {
                    try { process.Kill(); } catch { }
                    result.Error = "Timeout du test de vitesse (>120s)";
                    App.LogMessage("[LibreSpeed] TIMEOUT");
                    return result;
                }

                var jsonOutput = output.ToString().Trim();
                result.RawJson = jsonOutput;
                
                if (process.ExitCode != 0)
                {
                    result.Error = $"Erreur LibreSpeed (code {process.ExitCode}): {error}";
                    App.LogMessage($"[LibreSpeed] Exit code {process.ExitCode}: {error}");
                    return result;
                }

                // Parser le JSON
                result = ParseJsonResult(jsonOutput);
                
                App.LogMessage($"[LibreSpeed] Test terminé: Down={result.DownloadMbps:F2} Mbps, Up={result.UploadMbps:F2} Mbps, Ping={result.PingMs:F1} ms");
            }
            catch (OperationCanceledException)
            {
                result.Error = "Test annulé";
                App.LogMessage("[LibreSpeed] Test annulé");
            }
            catch (Exception ex)
            {
                result.Error = $"Erreur: {ex.Message}";
                App.LogMessage($"[LibreSpeed] Erreur: {ex.Message}");
            }
            
            return result;
        }

        /// <summary>
        /// Parse le résultat JSON de LibreSpeed CLI
        /// </summary>
        private SpeedTestResult ParseJsonResult(string json)
        {
            var result = new SpeedTestResult { RawJson = json };
            
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    result.Error = "Réponse JSON vide";
                    return result;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                // LibreSpeed CLI peut retourner un tableau ou un objet
                var testResult = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                    ? root[0]
                    : root;

                // LibreSpeed CLI --json : vitesses en bit/s (doc officielle). Conversion bps → Mbps si valeur > 10_000.
                const double bpsToMbps = 1.0 / 1_000_000.0;
                static double ToMbps(double raw)
                {
                    if (raw <= 0) return 0;
                    return raw >= 10_000 ? raw * bpsToMbps : raw; // déjà en Mbps si petit
                }
                if (testResult.TryGetProperty("download", out var dl))
                {
                    var raw = dl.GetDouble();
                    result.DownloadMbps = ToMbps(raw);
                    App.LogMessage($"[LibreSpeed] Download: raw={raw} → {result.DownloadMbps:F2} Mbps");
                }
                
                if (testResult.TryGetProperty("upload", out var ul))
                {
                    var raw = ul.GetDouble();
                    result.UploadMbps = ToMbps(raw);
                    App.LogMessage($"[LibreSpeed] Upload: raw={raw} → {result.UploadMbps:F2} Mbps");
                }
                
                if (testResult.TryGetProperty("ping", out var ping))
                    result.PingMs = ping.GetDouble();
                
                if (testResult.TryGetProperty("jitter", out var jitter))
                    result.JitterMs = jitter.GetDouble();
                
                // Informations serveur
                if (testResult.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.Object)
                {
                    if (server.TryGetProperty("name", out var name))
                        result.ServerName = name.GetString();
                    if (server.TryGetProperty("location", out var loc))
                        result.ServerLocation = loc.GetString();
                    if (server.TryGetProperty("sponsor", out var sponsor))
                        result.ServerSponsor = sponsor.GetString();
                }
                
                result.Success = result.DownloadMbps.HasValue || result.UploadMbps.HasValue;
                
                if (!result.Success)
                {
                    result.Error = "Aucune donnée de vitesse dans la réponse";
                }
            }
            catch (JsonException ex)
            {
                result.Error = $"Erreur parsing JSON: {ex.Message}";
                App.LogMessage($"[LibreSpeed] JSON parse error: {ex.Message}");
            }
            
            return result;
        }

        /// <summary>
        /// Sauvegarde le résultat du test de vitesse en JSON pour inspection par LLM
        /// </summary>
        public async Task<string?> SaveResultToJsonAsync(SpeedTestResult result, string? outputDir = null)
        {
            try
            {
                // Utiliser le dossier Rapports par défaut
                if (string.IsNullOrEmpty(outputDir))
                {
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    outputDir = Path.Combine(localAppData, "PCDiagnosticPRO", "Rapports");
                }
                
                if (!Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);
                
                // Générer un nom de fichier unique avec timestamp et run ID
                var runId = Guid.NewGuid().ToString("N").Substring(0, 8);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"speedtest_{timestamp}_{runId}.json";
                var filePath = Path.Combine(outputDir, fileName);
                
                // Créer l'objet JSON structuré pour LLM
                var jsonResult = new
                {
                    // Métadonnées
                    Timestamp = result.TestTime.ToString("o"),
                    RunId = runId,
                    Provider = "LibreSpeed CLI",
                    
                    // Résultats principaux
                    DownloadMbps = result.DownloadMbps,
                    UploadMbps = result.UploadMbps,
                    LatencyMs = result.PingMs,
                    JitterMs = result.JitterMs,
                    PacketLossPercent = (double?)null, // LibreSpeed ne mesure pas la perte
                    
                    // Serveur
                    ServerName = result.ServerName,
                    ServerLocation = result.ServerLocation,
                    ServerSponsor = result.ServerSponsor,
                    
                    // Interface réseau (à enrichir si disponible)
                    PublicIp = (string?)null,
                    InterfaceName = (string?)null,
                    InterfaceType = (string?)null,
                    WifiBand = (string?)null,
                    LinkSpeedMbps = (double?)null,
                    
                    // Analyse
                    SpeedTier = result.SpeedTier,
                    Success = result.Success,
                    
                    // Erreur
                    ErrorCode = result.Success ? (string?)null : "TEST_FAILED",
                    ErrorMessage = result.Error
                };
                
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                var json = JsonSerializer.Serialize(jsonResult, options);
                
                await File.WriteAllTextAsync(filePath, json);
                
                App.LogMessage($"[LibreSpeed] Résultat sauvegardé: {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[LibreSpeed] Erreur sauvegarde JSON: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fallback HTTP simple si LibreSpeed CLI échoue
        /// </summary>
        public async Task<SpeedTestResult> RunFallbackTestAsync(CancellationToken cancellationToken = default)
        {
            var result = new SpeedTestResult();
            
            try
            {
                App.LogMessage("[LibreSpeed] Fallback HTTP test...");
                
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                
                // Test de téléchargement (10 MB)
                var downloadUrl = "http://speedtest.tele2.net/10MB.zip";
                var sw = Stopwatch.StartNew();
                
                var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseContentRead, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                
                sw.Stop();
                
                if (bytes.Length > 0 && sw.ElapsedMilliseconds > 0)
                {
                    result.DownloadMbps = (bytes.Length * 8.0) / (sw.ElapsedMilliseconds * 1000.0);
                    result.Success = true;
                    result.ServerName = "Fallback HTTP Test";
                    App.LogMessage($"[LibreSpeed] Fallback: {result.DownloadMbps:F2} Mbps ({bytes.Length} bytes in {sw.ElapsedMilliseconds} ms)");
                }
            }
            catch (Exception ex)
            {
                result.Error = $"Fallback failed: {ex.Message}";
                App.LogMessage($"[LibreSpeed] Fallback error: {ex.Message}");
            }
            
            return result;
        }
    }
}
