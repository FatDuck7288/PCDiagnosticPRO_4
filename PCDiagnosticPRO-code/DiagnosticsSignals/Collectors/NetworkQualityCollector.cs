using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// Collects network quality metrics: packet loss, jitter, RTT, DNS latency.
    /// </summary>
    public class NetworkQualityCollector : ISignalCollector
    {
        public string Name => "networkQuality";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);
        public int Priority => 5;

        private static readonly string[] PingTargets = { "1.1.1.1", "8.8.8.8" };
        private const int PingCount = 10;
        private const int PingTimeoutMs = 1000;

        public async Task<SignalResult> CollectAsync(CancellationToken ct)
        {
            try
            {
                var result = new NetworkQualityResult();
                var targetResults = new List<PingTargetResult>();

                // Get gateway
                string? gateway = GetDefaultGateway();
                var allTargets = gateway != null 
                    ? PingTargets.Append(gateway).ToArray() 
                    : PingTargets;

                // Ping each target
                foreach (var target in allTargets)
                {
                    if (ct.IsCancellationRequested) break;
                    
                    var pingResult = await PingTargetAsync(target, ct);
                    targetResults.Add(pingResult);
                }

                result.Targets = targetResults;

                // Calculate overall metrics
                var successfulTargets = targetResults.Where(t => t.Received > 0).ToList();
                if (successfulTargets.Count > 0)
                {
                    result.OverallLossPercent = Math.Round(targetResults.Average(t => t.LossPercent), 1);
                    result.OverallJitterMs = Math.Round(successfulTargets.Average(t => t.JitterMs), 2);
                    result.OverallRttAvg = Math.Round(successfulTargets.Average(t => t.RttAvg), 1);
                }

                // DNS latency test
                result.DnsMsP95 = await MeasureDnsLatencyAsync(ct);

                // TCP retransmit rate (optional)
                result.TcpRetransPerSec = GetTcpRetransmitRate();

                var quality = result.OverallLossPercent > 5 ? "suspect" :
                              result.OverallLossPercent > 1 ? "partial" : "ok";

                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "ICMP_Ping+DNS",
                    Quality = quality,
                    Notes = $"loss={result.OverallLossPercent}%, jitter={result.OverallJitterMs}ms, rtt={result.OverallRttAvg}ms",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "ICMP_Ping+DNS");
            }
        }

        private async Task<PingTargetResult> PingTargetAsync(string target, CancellationToken ct)
        {
            var result = new PingTargetResult { Ip = target, Sent = PingCount };
            var rtts = new List<long>();

            try
            {
                using var ping = new Ping();
                
                for (int i = 0; i < PingCount && !ct.IsCancellationRequested; i++)
                {
                    try
                    {
                        var reply = await ping.SendPingAsync(target, PingTimeoutMs);
                        if (reply.Status == IPStatus.Success)
                        {
                            result.Received++;
                            rtts.Add(reply.RoundtripTime);
                        }
                    }
                    catch { /* Timeout or error */ }
                    
                    await Task.Delay(50, ct); // Small delay between pings
                }

                result.Lost = result.Sent - result.Received;
                result.LossPercent = result.Sent > 0 
                    ? Math.Round((double)result.Lost / result.Sent * 100, 1) 
                    : 100;

                if (rtts.Count > 0)
                {
                    result.RttMin = rtts.Min();
                    result.RttMax = rtts.Max();
                    result.RttAvg = Math.Round(rtts.Average(), 1);
                    
                    // Calculate jitter (standard deviation)
                    if (rtts.Count > 1)
                    {
                        double avg = rtts.Average();
                        double sumSquares = rtts.Sum(r => Math.Pow(r - avg, 2));
                        result.JitterMs = Math.Round(Math.Sqrt(sumSquares / rtts.Count), 2);
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"Ping to {target} failed: {ex.Message}");
                result.Error = ex.Message;
            }

            return result;
        }

        private async Task<double> MeasureDnsLatencyAsync(CancellationToken ct)
        {
            var latencies = new List<double>();
            var testDomains = new[] { "www.google.com", "www.microsoft.com" };
            
            foreach (var domain in testDomains)
            {
                if (ct.IsCancellationRequested) break;
                
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        await Dns.GetHostAddressesAsync(domain);
                        sw.Stop();
                        latencies.Add(sw.ElapsedMilliseconds);
                    }
                    catch { /* DNS resolution failed */ }
                    
                    await Task.Delay(50, ct);
                }
            }

            if (latencies.Count == 0) return -1;
            
            // Return P95
            latencies.Sort();
            int p95Index = (int)Math.Ceiling(latencies.Count * 0.95) - 1;
            return latencies[Math.Max(0, p95Index)];
        }

        private double GetTcpRetransmitRate()
        {
            try
            {
                using var counter = new PerformanceCounter(
                    "TCPv4", "Segments Retransmitted/sec", "", true);
                counter.NextValue(); // First call returns 0
                Thread.Sleep(100);
                return Math.Round(counter.NextValue(), 2);
            }
            catch
            {
                return -1;
            }
        }

        private string? GetDefaultGateway()
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

    public class NetworkQualityResult
    {
        public List<PingTargetResult> Targets { get; set; } = new();
        public double OverallLossPercent { get; set; }
        public double OverallJitterMs { get; set; }
        public double OverallRttAvg { get; set; }
        public double DnsMsP95 { get; set; }
        public double TcpRetransPerSec { get; set; }
    }

    public class PingTargetResult
    {
        public string Ip { get; set; } = "";
        public int Sent { get; set; }
        public int Received { get; set; }
        public int Lost { get; set; }
        public double LossPercent { get; set; }
        public double RttMin { get; set; }
        public double RttMax { get; set; }
        public double RttAvg { get; set; }
        public double JitterMs { get; set; }
        public string? Error { get; set; }
    }
}
