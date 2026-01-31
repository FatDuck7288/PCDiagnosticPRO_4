using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// FIX 7: Optional Internet speed test using Ookla Speedtest CLI.
    /// ONLY triggered when AllowExternalNetworkTests = true AND user opts in.
    /// Does NOT auto-download any executable.
    /// </summary>
    public class InternetSpeedTestCollector : ISignalCollector
    {
        public string Name => "internetSpeedTest";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(90); // Speed tests can take time
        public int Priority => 10; // Run last

        // Known paths where Speedtest CLI might be installed
        private static readonly string[] SpeedtestPaths = new[]
        {
            @"C:\Program Files\Ookla\Speedtest\speedtest.exe",
            @"C:\Program Files (x86)\Ookla\Speedtest\speedtest.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Speedtest", "speedtest.exe"),
            "speedtest.exe", // In PATH
            "speedtest" // Linux/WSL
        };

        /// <summary>
        /// Check if speedtest is enabled (external tests allowed)
        /// </summary>
        public static bool AllowExternalNetworkTests { get; set; } = false;

        public async Task<SignalResult> CollectAsync(CancellationToken ct)
        {
            // CRITICAL: Only run if explicitly allowed
            if (!AllowExternalNetworkTests)
            {
                return new SignalResult
                {
                    Name = Name,
                    Value = new InternetSpeedTestResult
                    {
                        Available = false,
                        Reason = "external_tests_disabled",
                        Message = "Test vitesse Internet désactivé (AllowExternalNetworkTests=false)"
                    },
                    Available = false,
                    Source = "InternetSpeedTestCollector",
                    Reason = "external_tests_disabled",
                    Notes = "Test non exécuté - option désactivée",
                    Timestamp = DateTime.UtcNow
                };
            }

            try
            {
                // Find speedtest executable
                var speedtestPath = FindSpeedtestExecutable();
                
                if (string.IsNullOrEmpty(speedtestPath))
                {
                    SignalsLogger.LogWarning(Name, "Speedtest CLI not found");
                    return new SignalResult
                    {
                        Name = Name,
                        Value = new InternetSpeedTestResult
                        {
                            Available = false,
                            Reason = "tool_not_installed",
                            Message = "Outil non installé : installez Ookla Speedtest CLI depuis https://www.speedtest.net/apps/cli"
                        },
                        Available = false,
                        Source = "InternetSpeedTestCollector",
                        Reason = "tool_not_installed",
                        Notes = "Speedtest CLI non trouvé",
                        Timestamp = DateTime.UtcNow
                    };
                }

                SignalsLogger.LogInfo(Name, $"Running speedtest from: {speedtestPath}");
                
                var result = await RunSpeedtestAsync(speedtestPath, ct);
                
                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = result.Available,
                    Source = "SpeedtestCLI",
                    Quality = result.Available ? GetVerdictQuality(result.Verdict) : "error",
                    Notes = result.Available ? $"Down={result.DownloadMbps:F1}Mbps, Up={result.UploadMbps:F1}Mbps, Ping={result.PingMs}ms" : result.Reason,
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (OperationCanceledException)
            {
                return SignalResult.Unavailable(Name, "cancelled", "InternetSpeedTestCollector");
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "InternetSpeedTestCollector");
            }
        }

        private string? FindSpeedtestExecutable()
        {
            foreach (var path in SpeedtestPaths)
            {
                try
                {
                    if (File.Exists(path))
                        return path;

                    // Check if in PATH
                    if (!path.Contains(Path.DirectorySeparatorChar) && !path.Contains(Path.AltDirectorySeparatorChar))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "where",
                            Arguments = path,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        };

                        using var process = Process.Start(psi);
                        if (process != null)
                        {
                            var output = process.StandardOutput.ReadToEnd();
                            process.WaitForExit(5000);
                            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                            {
                                var foundPath = output.Split('\n')[0].Trim();
                                if (File.Exists(foundPath))
                                    return foundPath;
                            }
                        }
                    }
                }
                catch { /* Ignore search errors */ }
            }

            return null;
        }

        private async Task<InternetSpeedTestResult> RunSpeedtestAsync(string speedtestPath, CancellationToken ct)
        {
            var result = new InternetSpeedTestResult { Timestamp = DateTime.UtcNow };
            var sw = Stopwatch.StartNew();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = speedtestPath,
                    Arguments = "--accept-license --accept-gdpr --format=json",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    result.Reason = "process_start_failed";
                    result.Message = "Impossible de démarrer le processus speedtest";
                    return result;
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                // Wait with timeout
                var completed = await Task.Run(() => process.WaitForExit((int)DefaultTimeout.TotalMilliseconds), ct);
                
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;

                if (!completed)
                {
                    try { process.Kill(); } catch { }
                    result.Reason = "timeout";
                    result.Message = $"Test vitesse interrompu après {DefaultTimeout.TotalSeconds}s";
                    return result;
                }

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    result.Reason = "speedtest_error";
                    result.Message = !string.IsNullOrEmpty(error) ? error : $"Speedtest exit code: {process.ExitCode}";
                    SignalsLogger.LogWarning(Name, $"Speedtest failed: {result.Message}");
                    return result;
                }

                // Parse JSON output
                result = ParseSpeedtestOutput(output, result);
            }
            catch (Exception ex)
            {
                result.Reason = "execution_error";
                result.Message = ex.Message;
            }

            return result;
        }

        private InternetSpeedTestResult ParseSpeedtestOutput(string json, InternetSpeedTestResult result)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Download speed (bytes/s -> Mbps)
                if (root.TryGetProperty("download", out var download) &&
                    download.TryGetProperty("bandwidth", out var downBw))
                {
                    result.DownloadMbps = Math.Round(downBw.GetDouble() * 8 / 1_000_000, 2);
                }

                // Upload speed (bytes/s -> Mbps)
                if (root.TryGetProperty("upload", out var upload) &&
                    upload.TryGetProperty("bandwidth", out var upBw))
                {
                    result.UploadMbps = Math.Round(upBw.GetDouble() * 8 / 1_000_000, 2);
                }

                // Ping/Latency
                if (root.TryGetProperty("ping", out var ping))
                {
                    if (ping.TryGetProperty("latency", out var latency))
                        result.PingMs = Math.Round(latency.GetDouble(), 1);
                    if (ping.TryGetProperty("jitter", out var jitter))
                        result.JitterMs = Math.Round(jitter.GetDouble(), 2);
                }

                // Packet loss (if available)
                if (root.TryGetProperty("packetLoss", out var packetLoss))
                {
                    result.PacketLossPercent = Math.Round(packetLoss.GetDouble(), 2);
                }

                // Server info
                if (root.TryGetProperty("server", out var server))
                {
                    if (server.TryGetProperty("name", out var serverName))
                        result.ServerName = serverName.GetString();
                    if (server.TryGetProperty("location", out var location))
                        result.ServerLocation = location.GetString();
                    if (server.TryGetProperty("country", out var country))
                        result.ServerCountry = country.GetString();
                }

                // ISP
                if (root.TryGetProperty("isp", out var isp))
                {
                    result.Isp = isp.GetString();
                }

                // Result URL
                if (root.TryGetProperty("result", out var resultObj) &&
                    resultObj.TryGetProperty("url", out var url))
                {
                    result.ResultUrl = url.GetString();
                }

                // Calculate verdict
                result.Verdict = CalculateVerdict(result.DownloadMbps);
                result.Available = true;
                result.Message = $"Test réussi: {result.Verdict}";
            }
            catch (JsonException ex)
            {
                result.Reason = "json_parse_error";
                result.Message = $"Erreur parsing JSON: {ex.Message}";
                SignalsLogger.LogWarning(Name, $"JSON parse error: {ex.Message}");
            }

            return result;
        }

        private string CalculateVerdict(double downloadMbps)
        {
            // Verdicts based on download speed
            if (downloadMbps >= 300)
                return "Excellente";
            if (downloadMbps >= 100)
                return "Très bonne";
            if (downloadMbps >= 20)
                return "Bonne";
            return "Lente";
        }

        private string GetVerdictQuality(string? verdict)
        {
            return verdict switch
            {
                "Excellente" or "Très bonne" => "ok",
                "Bonne" => "partial",
                "Lente" => "suspect",
                _ => "partial"
            };
        }
    }

    public class InternetSpeedTestResult
    {
        public bool Available { get; set; }
        public string? Reason { get; set; }
        public string? Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public long DurationMs { get; set; }

        // Speed results
        public double DownloadMbps { get; set; }
        public double UploadMbps { get; set; }
        public double PingMs { get; set; }
        public double JitterMs { get; set; }
        public double PacketLossPercent { get; set; } = -1; // -1 = not measured

        // Server info
        public string? ServerName { get; set; }
        public string? ServerLocation { get; set; }
        public string? ServerCountry { get; set; }
        public string? Isp { get; set; }
        public string? ResultUrl { get; set; }

        // Verdict
        public string? Verdict { get; set; } // Lente, Bonne, Très bonne, Excellente
    }
}
