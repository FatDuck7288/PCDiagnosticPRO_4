using System;
using System.Collections.Concurrent;
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
        private const int ThroughputTestBytes = 25 * 1024 * 1024; // 25 MB max for accurate measurement
        private const int RunCount = 3; // 3 runs, take median/max
        private const int UploadTestBytes = 20 * 1024 * 1024; // 20 MB — must be large enough for TCP to ramp up on fast links (300+ Mbps)
        private const int UploadWarmupBytes = 512 * 1024; // 512 KB warmup to prime TCP + TLS
        private const int UploadMinElapsedMs = 200; // Discard samples shorter than 200ms
        private const int UploadParallelStreams = 4; // Parallel streams to saturate the link (like speedtest.net)

        private static readonly string[] PingTargets = { "1.1.1.1", "8.8.8.8" };
        private static readonly string[] DnsTestDomains = { 
            "microsoft.com", "cloudflare.com", "google.com", "windows.com", "example.com" 
        };
        
        // Stable download test URLs - use larger files (10MB+) for accurate speed measurement
        private static readonly string[] DownloadTestUrls = {
            "http://speedtest.tele2.net/10MB.zip",      // 10 MB - primary
            "http://proof.ovh.net/files/10Mb.dat",      // 10 Mb = 1.25 MB - fallback
            "http://speedtest.tele2.net/1MB.zip"       // 1 MB - last resort
        };
        
        // Upload endpoints — Cloudflare CDN first (edge servers globally), Tele2 as fallback.
        private static readonly string[] UploadTestUrls = {
            "https://speed.cloudflare.com/__up",        // Cloudflare CDN — global edge servers, closest to user
            "http://speedtest.tele2.net/upload.php"     // Tele2 — Europe (Sweden), fallback
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

            using var httpClient = new HttpClient(new SocketsHttpHandler
            {
                MaxConnectionsPerServer = UploadParallelStreams * 2,
                EnableMultipleHttp2Connections = false
            })
            {
                DefaultRequestVersion = HttpVersion.Version11, // Force HTTP/1.1: one TCP conn per stream
                Timeout = TimeSpan.FromSeconds(60)
            };

            // === DOWNLOAD TEST ===
            foreach (var url in DownloadTestUrls)
            {
                if (ct.IsCancellationRequested) break;

                for (int run = 0; run < RunCount && !ct.IsCancellationRequested; run++)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(OperationTimeoutMs * 2); // 16 seconds for larger files

                        var sw = Stopwatch.StartNew();
                        
                        using var response = await httpClient.GetAsync(url, 
                            HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        
                        if (!response.IsSuccessStatusCode) continue;

                        using var stream = await response.Content.ReadAsStreamAsync();
                        var buffer = new byte[131072]; // 128KB buffer for better throughput
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                        {
                            totalRead += bytesRead;
                            if (totalRead >= ThroughputTestBytes) break;
                        }

                        sw.Stop();

                        // Only count if we downloaded at least 500KB (to avoid TCP slow-start distortion)
                        if (totalRead >= 512 * 1024 && sw.ElapsedMilliseconds > 100)
                        {
                            double mbps = (totalRead * 8.0) / (sw.ElapsedMilliseconds * 1000.0); // Mbps
                            downloadSamples.Add(Math.Round(mbps, 2));
                            App.LogMessage($"[NetworkDiagnostics] Download sample: {totalRead / 1024}KB in {sw.ElapsedMilliseconds}ms = {mbps:F2} Mbps");
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

            // === UPLOAD TEST — delegate to shared helper ===
            var uploadResult = await RunParallelUploadAsync(httpClient, ct, "NetworkDiagnostics");

            if (uploadResult.HasValue)
            {
                result.UploadMbpsMedian = uploadResult.Value;
                result.UploadSamples = new List<double> { uploadResult.Value };
                result.UploadReason = null;
            }
            else
            {
                result.UploadMbpsMedian = null;
                result.UploadReason = "upload_test_failed";
            }

            return result;
        }

        /// <summary>
        /// Runs ONLY the upload test (parallel multi-stream) — no ping, DNS, or download.
        /// Used as a cross-check when LibreSpeed upload seems low.
        /// Returns the best upload speed in Mbps, or null on failure.
        /// </summary>
        public async Task<double?> CollectUploadOnlyAsync(CancellationToken ct = default)
        {
            using var httpClient = new HttpClient(new SocketsHttpHandler
            {
                MaxConnectionsPerServer = UploadParallelStreams * 2,
                EnableMultipleHttp2Connections = false
            })
            {
                DefaultRequestVersion = HttpVersion.Version11, // Force HTTP/1.1: separate TCP connection per stream
                Timeout = TimeSpan.FromSeconds(60)
            };

            return await RunParallelUploadAsync(httpClient, ct, "UploadCrossCheck");
        }

        /// <summary>
        /// Core parallel upload engine. Tries ALL servers, uses N parallel HTTP/1.1 streams per run,
        /// measures upload time precisely (until server confirms receipt via ResponseHeadersRead),
        /// and takes the global MAX across all servers and all runs.
        /// </summary>
        private async Task<double?> RunParallelUploadAsync(HttpClient httpClient, CancellationToken ct, string logPrefix)
        {
            var allSamples = new List<double>();
            var uploadData = new byte[UploadTestBytes];
            new Random().NextBytes(uploadData);
            int perStreamBytes = UploadTestBytes / UploadParallelStreams;

            // Try ALL servers (don't break early) — take the best result across all.
            // Different servers may be closer/faster depending on user's location.
            foreach (var url in UploadTestUrls)
            {
                if (ct.IsCancellationRequested) break;

                App.LogMessage($"[{logPrefix}] Testing upload to {url} ...");

                // Warmup: prime TCP + TLS on multiple connections (separate HTTP/1.1 connections)
                try
                {
                    var warmupData = new byte[UploadWarmupBytes];
                    var warmupTasks = new List<Task>();
                    for (int s = 0; s < UploadParallelStreams; s++)
                    {
                        warmupTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                using var wCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                wCts.CancelAfter(OperationTimeoutMs);
                                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                                {
                                    Version = HttpVersion.Version11, // Force HTTP/1.1
                                    Content = new ByteArrayContent(warmupData)
                                };
                                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                                using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, wCts.Token);
                                // Drain body to recycle connection
                                if (resp.Content != null)
                                    await resp.Content.ReadAsByteArrayAsync(wCts.Token).ConfigureAwait(false);
                            }
                            catch { }
                        }, ct));
                    }
                    await Task.WhenAll(warmupTasks);
                    App.LogMessage($"[{logPrefix}] Warmup done ({UploadParallelStreams} streams, {url})");
                }
                catch { }

                // Immediately run test (no idle gap — preserves TCP congestion window)
                for (int run = 0; run < RunCount && !ct.IsCancellationRequested; run++)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(OperationTimeoutMs * 4);

                        App.LogMessage($"[{logPrefix}] Upload run {run + 1}/{RunCount} ({UploadParallelStreams} streams × {perStreamBytes / (1024*1024)}MB = {UploadTestBytes / (1024*1024)}MB total)");

                        // Each stream records its own upload completion time (until server ACKs via response headers).
                        // The wall-clock upload duration = MAX of all stream completion times.
                        var streamCompletionMs = new ConcurrentBag<long>();
                        var streamSuccess = new ConcurrentBag<bool>();
                        var sw = Stopwatch.StartNew();

                        var streamTasks = new List<Task>();
                        for (int s = 0; s < UploadParallelStreams; s++)
                        {
                            int streamIdx = s;
                            streamTasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    // Each stream gets its own data chunk
                                    var streamData = new byte[perStreamBytes];
                                    Array.Copy(uploadData, streamIdx * perStreamBytes, streamData, 0, perStreamBytes);

                                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                                    {
                                        Version = HttpVersion.Version11, // Force HTTP/1.1 = separate TCP conn
                                        Content = new ByteArrayContent(streamData)
                                    };
                                    req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                                    // ResponseHeadersRead: returns when server sends response headers
                                    // = server has fully received our upload body (that's the upload time).
                                    using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                                    // Record completion time BEFORE reading response body
                                    streamCompletionMs.Add(sw.ElapsedMilliseconds);
                                    streamSuccess.Add(resp.IsSuccessStatusCode);

                                    // Drain response body to recycle the TCP connection for next run
                                    if (resp.Content != null)
                                        await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                                }
                                catch
                                {
                                    streamSuccess.Add(false);
                                }
                            }, cts.Token));
                        }

                        await Task.WhenAll(streamTasks);

                        int successCount = streamSuccess.Count(ok => ok);
                        long uploadMs = streamCompletionMs.Count > 0 ? streamCompletionMs.Max() : sw.ElapsedMilliseconds;
                        long totalBytes = (long)successCount * perStreamBytes;

                        App.LogMessage($"[{logPrefix}] Upload timing: {uploadMs}ms, {successCount}/{UploadParallelStreams} streams OK, totalBytes={totalBytes}, per-stream=[{string.Join(", ", streamCompletionMs.Select(t => t + "ms"))}]");

                        if (successCount > 0 && uploadMs > UploadMinElapsedMs)
                        {
                            // Mbps = (bytes × 8) / (ms × 1000) = megabits / second
                            double mbps = (totalBytes * 8.0) / (uploadMs * 1000.0);
                            allSamples.Add(Math.Round(mbps, 2));
                            App.LogMessage($"[{logPrefix}] Upload sample: {totalBytes / 1024}KB in {uploadMs}ms = {mbps:F2} Mbps");
                        }
                        else if (successCount > 0)
                        {
                            App.LogMessage($"[{logPrefix}] Upload sample discarded (too fast: {uploadMs}ms < {UploadMinElapsedMs}ms)");
                        }
                        else
                        {
                            App.LogMessage($"[{logPrefix}] Upload failed: all streams failed for {url}");
                        }
                    }
                    catch (Exception ex)
                    {
                        App.LogMessage($"[{logPrefix}] Upload failed ({url}): {ex.Message}");
                    }
                }
            }

            if (allSamples.Count > 0)
            {
                allSamples.Sort();
                double best = allSamples[allSamples.Count - 1];
                App.LogMessage($"[{logPrefix}] FINAL upload: best={best:F2} Mbps, all samples=[{string.Join(", ", allSamples.Select(s => s.ToString("F2")))}]");
                return best;
            }

            App.LogMessage($"[{logPrefix}] No valid upload samples collected");
            return null;
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
        public List<double>? UploadSamples { get; set; }
        public string? UploadReason { get; set; }
    }

    public class NetworkRecommendation
    {
        public string Text { get; set; } = "";
        public string Severity { get; set; } = "info";
    }

    #endregion
}
