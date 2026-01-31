using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services.NetworkDiagnostics
{
    /// <summary>
    /// Complete network diagnostics collector.
    /// Measures latency, jitter, packet loss, DNS latency, and download throughput.
    /// All operations have strict timeouts and are async/cancellation-safe.
    /// </summary>
    public class NetworkDiagnosticsCollector
    {
        private const int PingCount = 30;
        private const int PingTimeoutMs = 1000;
        private const int OperationTimeoutMs = 8000;
        private const int ThroughputTestBytes = 10 * 1024 * 1024; // 10 MB max
        private const int RunCount = 3; // 3 runs, take median

        private static readonly string[] PingTargets = { "1.1.1.1", "8.8.8.8" };
        private static readonly string[] DnsTestDomains = { 
            "microsoft.com", "cloudflare.com", "google.com", "windows.com", "example.com" 
        };
        
        // Stable download test URLs (small files, fast mirrors)
        private static readonly string[] DownloadTestUrls = {
            "http://speedtest.tele2.net/1MB.zip",
            "http://proof.ovh.net/files/1Mb.dat"
        };

        public async Task<NetworkDiagnosticsResult> CollectAsync(CancellationToken ct = default)
        {
            var result = new NetworkDiagnosticsResult
            {
                Timestamp = DateTime.UtcNow,
                Available = true,
                Source = "NetworkDiagnosticsCollector"
            };

            var sw = Stopwatch.StartNew();

            try
            {
                // 1. Get gateway
                result.Gateway = GetDefaultGateway();

                // 2. Ping tests (gateway + external targets)
                var allTargets = result.Gateway != null 
                    ? PingTargets.Prepend(result.Gateway).ToArray() 
                    : PingTargets;

                result.InternetTargets = await CollectPingMetricsAsync(allTargets, ct);

                // 3. DNS latency tests
                result.DnsTests = await CollectDnsLatencyAsync(ct);

                // 4. Throughput test (download only)
                result.Throughput = await CollectThroughputAsync(ct);

                // 5. Generate recommendations
                result.Recommendations = GenerateRecommendations(result);

                // Calculate overall metrics
                CalculateOverallMetrics(result);

                result.Quality = DetermineQuality(result);
            }
            catch (OperationCanceledException)
            {
                result.Available = false;
                result.Reason = "cancelled";
            }
            catch (Exception ex)
            {
                result.Available = false;
                result.Reason = $"error: {ex.Message}";
                App.LogMessage($"[NetworkDiagnostics] Error: {ex.Message}");
            }

            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            
            App.LogMessage($"[NetworkDiagnostics] Completed in {result.DurationMs}ms");
            return result;
        }

        private async Task<List<PingMetrics>> CollectPingMetricsAsync(string[] targets, CancellationToken ct)
        {
            var results = new List<PingMetrics>();

            foreach (var target in targets)
            {
                if (ct.IsCancellationRequested) break;

                var metrics = new PingMetrics { Target = target };
                var rtts = new List<double>();

                try
                {
                    using var ping = new Ping();
                    int sent = 0, received = 0;

                    for (int i = 0; i < PingCount && !ct.IsCancellationRequested; i++)
                    {
                        sent++;
                        try
                        {
                            var reply = await ping.SendPingAsync(target, PingTimeoutMs);
                            if (reply.Status == IPStatus.Success)
                            {
                                received++;
                                rtts.Add(reply.RoundtripTime);
                            }
                        }
                        catch { /* Timeout or error */ }

                        await Task.Delay(30, ct); // Small delay between pings
                    }

                    metrics.Sent = sent;
                    metrics.Received = received;
                    metrics.LossPercent = sent > 0 ? Math.Round((sent - received) * 100.0 / sent, 1) : 100;

                    if (rtts.Count > 0)
                    {
                        rtts.Sort();
                        metrics.LatencyMsMin = rtts.Min();
                        metrics.LatencyMsMax = rtts.Max();
                        metrics.LatencyMsP50 = CalculatePercentile(rtts, 50);
                        metrics.LatencyMsP95 = CalculatePercentile(rtts, 95);

                        // Jitter = standard deviation
                        if (rtts.Count > 1)
                        {
                            double avg = rtts.Average();
                            double sumSq = rtts.Sum(r => Math.Pow(r - avg, 2));
                            metrics.JitterMsP95 = Math.Round(Math.Sqrt(sumSq / rtts.Count), 2);
                        }

                        metrics.Available = true;
                    }
                    else
                    {
                        metrics.Available = false;
                        metrics.Reason = "all_packets_lost";
                    }
                }
                catch (Exception ex)
                {
                    metrics.Available = false;
                    metrics.Reason = ex.Message;
                }

                results.Add(metrics);
            }

            return results;
        }

        private async Task<List<DnsTestResult>> CollectDnsLatencyAsync(CancellationToken ct)
        {
            var results = new List<DnsTestResult>();

            foreach (var domain in DnsTestDomains)
            {
                if (ct.IsCancellationRequested) break;

                var testResult = new DnsTestResult { Domain = domain };

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(OperationTimeoutMs);

                    var sw = Stopwatch.StartNew();
                    var addresses = await Dns.GetHostAddressesAsync(domain);
                    sw.Stop();

                    testResult.ResolveMs = sw.ElapsedMilliseconds;
                    testResult.Success = addresses.Length > 0;
                    testResult.IpCount = addresses.Length;
                }
                catch (Exception ex)
                {
                    testResult.Success = false;
                    testResult.Error = ex.Message;
                }

                results.Add(testResult);
            }

            return results;
        }

        private async Task<ThroughputResult> CollectThroughputAsync(CancellationToken ct)
        {
            var result = new ThroughputResult { Available = false };
            var downloadSamples = new List<double>();

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(OperationTimeoutMs)
            };

            // Try each URL until we get a valid result
            foreach (var url in DownloadTestUrls)
            {
                if (ct.IsCancellationRequested) break;

                for (int run = 0; run < RunCount && !ct.IsCancellationRequested; run++)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(OperationTimeoutMs);

                        var sw = Stopwatch.StartNew();
                        
                        using var response = await httpClient.GetAsync(url, 
                            HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        
                        if (!response.IsSuccessStatusCode) continue;

                        using var stream = await response.Content.ReadAsStreamAsync();
                        var buffer = new byte[81920]; // 80KB buffer
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                        {
                            totalRead += bytesRead;
                            if (totalRead >= ThroughputTestBytes) break;
                        }

                        sw.Stop();

                        if (totalRead > 0 && sw.ElapsedMilliseconds > 0)
                        {
                            double mbps = (totalRead * 8.0) / (sw.ElapsedMilliseconds * 1000.0); // Mbps
                            downloadSamples.Add(Math.Round(mbps, 2));
                        }
                    }
                    catch (Exception ex)
                    {
                        App.LogMessage($"[NetworkDiagnostics] Download test failed ({url}): {ex.Message}");
                    }
                }

                if (downloadSamples.Count >= RunCount) break;
            }

            if (downloadSamples.Count > 0)
            {
                downloadSamples.Sort();
                result.DownloadMbpsMedian = downloadSamples[downloadSamples.Count / 2];
                result.DownloadSamples = downloadSamples;
                result.Available = true;
            }
            else
            {
                result.Reason = "download_test_failed";
            }

            // Upload: not available without stable endpoint
            result.UploadMbpsMedian = null;
            result.UploadReason = "no_stable_endpoint";

            return result;
        }

        private List<NetworkRecommendation> GenerateRecommendations(NetworkDiagnosticsResult result)
        {
            var recommendations = new List<NetworkRecommendation>();

            // Check packet loss
            var avgLoss = result.InternetTargets.Where(t => t.Available).Select(t => t.LossPercent).DefaultIfEmpty(0).Average();
            if (avgLoss > 2)
            {
                recommendations.Add(new NetworkRecommendation
                {
                    Text = $"Packet loss detected ({avgLoss:F1}%). Network may be unstable.",
                    Severity = avgLoss > 5 ? "high" : "medium"
                });
            }

            // Check jitter
            var maxJitter = result.InternetTargets.Where(t => t.Available).Select(t => t.JitterMsP95).DefaultIfEmpty(0).Max();
            if (maxJitter > 30)
            {
                recommendations.Add(new NetworkRecommendation
                {
                    Text = $"High jitter detected ({maxJitter:F1}ms). Gaming/streaming may be affected.",
                    Severity = maxJitter > 50 ? "high" : "medium"
                });
            }

            // Check DNS
            var dnsFailures = result.DnsTests.Count(d => !d.Success);
            var dnsP95 = result.DnsTests.Where(d => d.Success).Select(d => d.ResolveMs).DefaultIfEmpty(0).OrderByDescending(x => x).FirstOrDefault();
            if (dnsFailures > 0)
            {
                recommendations.Add(new NetworkRecommendation
                {
                    Text = $"DNS resolution failures ({dnsFailures}/{result.DnsTests.Count}). Check DNS configuration.",
                    Severity = "high"
                });
            }
            else if (dnsP95 > 250)
            {
                recommendations.Add(new NetworkRecommendation
                {
                    Text = $"Slow DNS resolution ({dnsP95}ms). Consider using faster DNS.",
                    Severity = "medium"
                });
            }

            // Check throughput
            if (result.Throughput.Available)
            {
                var mbps = result.Throughput.DownloadMbpsMedian ?? 0;
                if (mbps < 5)
                {
                    recommendations.Add(new NetworkRecommendation
                    {
                        Text = $"Low download speed ({mbps:F1} Mbps). Navigation only. Gaming not recommended.",
                        Severity = "high"
                    });
                }
                else if (mbps < 20)
                {
                    recommendations.Add(new NetworkRecommendation
                    {
                        Text = $"Moderate speed ({mbps:F1} Mbps). Streaming HD possible. Gaming may have issues.",
                        Severity = "medium"
                    });
                }
                else if (mbps >= 100)
                {
                    recommendations.Add(new NetworkRecommendation
                    {
                        Text = $"Excellent speed ({mbps:F1} Mbps). Gaming and cloud gaming possible.",
                        Severity = "info"
                    });
                }
            }

            return recommendations;
        }

        private void CalculateOverallMetrics(NetworkDiagnosticsResult result)
        {
            var available = result.InternetTargets.Where(t => t.Available).ToList();
            if (available.Count > 0)
            {
                result.OverallLatencyMsP50 = Math.Round(available.Average(t => t.LatencyMsP50), 1);
                result.OverallLatencyMsP95 = Math.Round(available.Max(t => t.LatencyMsP95), 1);
                result.OverallLossPercent = Math.Round(available.Average(t => t.LossPercent), 1);
                result.OverallJitterMsP95 = Math.Round(available.Max(t => t.JitterMsP95), 2);
            }

            var successfulDns = result.DnsTests.Where(d => d.Success).Select(d => d.ResolveMs).ToList();
            if (successfulDns.Count > 0)
            {
                successfulDns.Sort();
                result.DnsP95Ms = CalculatePercentile(successfulDns.Select(x => (double)x).ToList(), 95);
            }
        }

        private string DetermineQuality(NetworkDiagnosticsResult result)
        {
            if (result.OverallLossPercent > 5 || result.DnsTests.Any(d => !d.Success))
                return "suspect";
            if (result.OverallLossPercent > 1 || result.OverallJitterMsP95 > 30)
                return "partial";
            return "ok";
        }

        private static double CalculatePercentile(List<double> sortedValues, int percentile)
        {
            if (sortedValues.Count == 0) return 0;
            int index = (int)Math.Ceiling(sortedValues.Count * percentile / 100.0) - 1;
            return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
        }

        private static string? GetDefaultGateway()
        {
            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var nic in nics)
                {
                    var props = nic.GetIPProperties();
                    var gateway = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (gateway != null)
                        return gateway.Address.ToString();
                }
            }
            catch { }
            return null;
        }
    }

    #region Result Models

    public class NetworkDiagnosticsResult
    {
        public bool Available { get; set; }
        public string Source { get; set; } = "";
        public string? Reason { get; set; }
        public string Quality { get; set; } = "ok";
        public DateTime Timestamp { get; set; }
        public long DurationMs { get; set; }
        
        public string? Gateway { get; set; }
        public List<PingMetrics> InternetTargets { get; set; } = new();
        public List<DnsTestResult> DnsTests { get; set; } = new();
        public ThroughputResult Throughput { get; set; } = new();
        public List<NetworkRecommendation> Recommendations { get; set; } = new();
        
        // Overall metrics
        public double OverallLatencyMsP50 { get; set; }
        public double OverallLatencyMsP95 { get; set; }
        public double OverallLossPercent { get; set; }
        public double OverallJitterMsP95 { get; set; }
        public double DnsP95Ms { get; set; }
    }

    public class PingMetrics
    {
        public string Target { get; set; } = "";
        public bool Available { get; set; }
        public string? Reason { get; set; }
        public int Sent { get; set; }
        public int Received { get; set; }
        public double LossPercent { get; set; }
        public double LatencyMsMin { get; set; }
        public double LatencyMsMax { get; set; }
        public double LatencyMsP50 { get; set; }
        public double LatencyMsP95 { get; set; }
        public double JitterMsP95 { get; set; }
    }

    public class DnsTestResult
    {
        public string Domain { get; set; } = "";
        public bool Success { get; set; }
        public long ResolveMs { get; set; }
        public int IpCount { get; set; }
        public string? Error { get; set; }
    }

    public class ThroughputResult
    {
        public bool Available { get; set; }
        public string? Reason { get; set; }
        public double? DownloadMbpsMedian { get; set; }
        public List<double>? DownloadSamples { get; set; }
        public double? UploadMbpsMedian { get; set; }
        public string? UploadReason { get; set; }
    }

    public class NetworkRecommendation
    {
        public string Text { get; set; } = "";
        public string Severity { get; set; } = "info";
    }

    #endregion
}
