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
    /// P1-2: NetworkQualityCollector - OFFLINE STRICT (LOCAL ONLY)
    /// NO external IPs (no 8.8.8.8, no 1.1.1.1, no speedtest WAN)
    /// Measures: link speed, latency, jitter, loss to gateway/DNS local/localhost
    /// Includes recommendation grid based on local link quality
    /// </summary>
    public class NetworkQualityCollector : ISignalCollector
    {
        public string Name => "networkQuality";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(45);
        public int Priority => 5;

        private const int PingCount = 30;
        private const int PingTimeoutMs = 1000;

        // RFC1918 private IP ranges
        private static readonly string[] PrivateRanges = { "10.", "172.16.", "172.17.", "172.18.", "172.19.",
            "172.20.", "172.21.", "172.22.", "172.23.", "172.24.", "172.25.", "172.26.", "172.27.",
            "172.28.", "172.29.", "172.30.", "172.31.", "192.168." };

        public async Task<SignalResult> CollectAsync(CancellationToken ct)
        {
            try
            {
                var result = new NetworkQualityResultOffline();
                var targetResults = new List<PingTargetResultOffline>();

                // 1. Get adapter info (link speed, type)
                var adapterInfo = GetPrimaryAdapterInfo();
                result.AdapterName = adapterInfo.Name;
                result.AdapterType = adapterInfo.Type;
                result.LinkSpeedMbps = adapterInfo.SpeedMbps;
                result.IsWifi = adapterInfo.IsWifi;
                result.WifiSignalPercent = adapterInfo.WifiSignalPercent;

                // 2. Gateway ping (local)
                string? gateway = adapterInfo.Gateway;
                if (gateway != null && IsLocalIp(gateway))
                {
                    var pingResult = await PingTargetAsync(gateway, "gateway", ct);
                    targetResults.Add(pingResult);
                }
                else
                {
                    targetResults.Add(new PingTargetResultOffline
                    {
                        Ip = gateway ?? "unknown",
                        Label = "gateway",
                        Available = false,
                        Reason = gateway == null ? "gateway_not_found" : "gateway_not_local"
                    });
                }

                // 3. Localhost ping (always available, baseline)
                var localhostResult = await PingTargetAsync("127.0.0.1", "localhost", ct);
                targetResults.Add(localhostResult);

                // 4. Local DNS servers ping (only if RFC1918)
                var dnsServers = adapterInfo.DnsServers;
                foreach (var dns in dnsServers.Take(2))
                {
                    if (IsLocalIp(dns))
                    {
                        var dnsResult = await PingTargetAsync(dns, "dns_local", ct);
                        targetResults.Add(dnsResult);
                    }
                    else
                    {
                        targetResults.Add(new PingTargetResultOffline
                        {
                            Ip = dns,
                            Label = "dns",
                            Available = false,
                            Reason = "dns_not_local_rfc1918"
                        });
                    }
                }

                result.Targets = targetResults;

                // 5. Calculate overall metrics from successful local targets
                var successfulTargets = targetResults.Where(t => t.Available && t.Received > 0).ToList();
                if (successfulTargets.Count > 0)
                {
                    result.LatencyMsP50 = Math.Round(successfulTargets.Average(t => t.LatencyMsP50), 1);
                    result.LatencyMsP95 = Math.Round(successfulTargets.Max(t => t.LatencyMsP95), 1);
                    result.JitterMsP95 = Math.Round(successfulTargets.Max(t => t.JitterMsP95), 2);
                    result.PacketLossPercent = Math.Round(successfulTargets.Average(t => t.LossPercent), 1);
                }

                // 6. DNS latency test (resolve local hostname only - no external domains)
                result.DnsLatencyMs = await MeasureLocalDnsAsync(ct);

                // 7. TCP retransmit rate (if available via perf counters)
                result.TcpRetransmitRate = GetTcpRetransmitRate();

                // 8. Calculate connection verdict
                result.CalculateVerdict();

                // 9. Generate recommendations based on local quality
                result.Recommendations = GenerateRecommendations(result);

                var quality = result.PacketLossPercent > 5 ? "suspect" :
                              result.PacketLossPercent > 1 ? "partial" : "ok";

                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "NetworkQualityCollector_LocalOnly",
                    Quality = quality,
                    Notes = $"OFFLINE: link={result.LinkSpeedMbps}Mbps, latencyP95={result.LatencyMsP95}ms, loss={result.PacketLossPercent}%",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "NetworkQualityCollector_LocalOnly");
            }
        }

        #region Adapter Info

        private AdapterInfo GetPrimaryAdapterInfo()
        {
            var info = new AdapterInfo();

            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .OrderByDescending(n => n.Speed) // Prefer fastest
                    .ToList();

                foreach (var nic in nics)
                {
                    var props = nic.GetIPProperties();
                    var gateway = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                    if (gateway != null)
                    {
                        info.Name = nic.Name;
                        info.Type = nic.NetworkInterfaceType.ToString();
                        info.SpeedMbps = nic.Speed / 1_000_000;
                        info.Gateway = gateway.Address.ToString();
                        info.IsWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

                        // Get DNS servers
                        foreach (var dns in props.DnsAddresses)
                        {
                            if (dns.AddressFamily == AddressFamily.InterNetwork)
                                info.DnsServers.Add(dns.ToString());
                        }

                        // Wi-Fi signal strength (if applicable)
                        if (info.IsWifi)
                        {
                            info.WifiSignalPercent = TryGetWifiSignalStrength();
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"GetPrimaryAdapterInfo failed: {ex.Message}");
            }

            return info;
        }

        private int? TryGetWifiSignalStrength()
        {
            try
            {
                // Use netsh wlan show interfaces to get signal strength
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return null;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);

                // Parse "Signal : 85%"
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("Signal") && line.Contains("%"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length >= 2)
                        {
                            var signalStr = parts[1].Trim().Replace("%", "");
                            if (int.TryParse(signalStr, out int signal))
                                return signal;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region Ping Tests

        private async Task<PingTargetResultOffline> PingTargetAsync(string target, string label, CancellationToken ct)
        {
            var result = new PingTargetResultOffline
            {
                Ip = target,
                Label = label,
                Sent = PingCount,
                Available = true
            };

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

                    await Task.Delay(30, ct);
                }

                result.Lost = result.Sent - result.Received;
                result.LossPercent = result.Sent > 0
                    ? Math.Round((double)result.Lost / result.Sent * 100, 1)
                    : 100;

                if (rtts.Count > 0)
                {
                    rtts.Sort();
                    result.LatencyMsMin = rtts.Min();
                    result.LatencyMsMax = rtts.Max();
                    result.LatencyMsP50 = GetPercentile(rtts, 50);
                    result.LatencyMsP95 = GetPercentile(rtts, 95);

                    // Calculate jitter (differences between consecutive RTTs)
                    if (rtts.Count > 1)
                    {
                        var jitters = new List<double>();
                        for (int i = 1; i < rtts.Count; i++)
                            jitters.Add(Math.Abs(rtts[i] - rtts[i - 1]));
                        jitters.Sort();
                        result.JitterMsP95 = GetPercentile(jitters, 95);
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"Ping to {target} ({label}) failed: {ex.Message}");
                result.Available = false;
                result.Reason = ex.Message;
            }

            return result;
        }

        #endregion

        #region DNS Test

        private async Task<double> MeasureLocalDnsAsync(CancellationToken ct)
        {
            try
            {
                // Only resolve local machine name - NO external domains
                var sw = Stopwatch.StartNew();
                await Dns.GetHostAddressesAsync(Environment.MachineName);
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }
            catch
            {
                return -1;
            }
        }

        #endregion

        #region TCP Stats

        private double GetTcpRetransmitRate()
        {
            try
            {
                using var counter = new PerformanceCounter("TCPv4", "Segments Retransmitted/sec", "", true);
                counter.NextValue();
                Thread.Sleep(100);
                return Math.Round(counter.NextValue(), 2);
            }
            catch
            {
                return -1;
            }
        }

        #endregion

        #region Recommendations

        private List<NetworkRecommendation> GenerateRecommendations(NetworkQualityResultOffline result)
        {
            var recs = new List<NetworkRecommendation>();

            // Link speed recommendations
            if (result.LinkSpeedMbps > 0)
            {
                if (result.LinkSpeedMbps < 5)
                {
                    recs.Add(new NetworkRecommendation
                    {
                        Category = "speed",
                        Severity = "high",
                        Text = "Débit très faible (<5 Mbps). Navigation basique uniquement. Gaming et streaming déconseillés.",
                        Action = "Vérifier le câble réseau ou la connexion Wi-Fi"
                    });
                }
                else if (result.LinkSpeedMbps < 20)
                {
                    recs.Add(new NetworkRecommendation
                    {
                        Category = "speed",
                        Severity = "medium",
                        Text = "Débit modéré (5-20 Mbps). Streaming HD possible. Gaming déconseillé si latence/jitter élevés.",
                        Action = null
                    });
                }
                else if (result.LinkSpeedMbps < 100)
                {
                    recs.Add(new NetworkRecommendation
                    {
                        Category = "speed",
                        Severity = "low",
                        Text = "Bon débit (20-100 Mbps). Gaming possible si perte <1% et jitter <20ms.",
                        Action = null
                    });
                }
                else
                {
                    recs.Add(new NetworkRecommendation
                    {
                        Category = "speed",
                        Severity = "info",
                        Text = "Excellent débit (>100 Mbps). Gaming compétitif et cloud gaming possibles.",
                        Action = null
                    });
                }
            }

            // Packet loss recommendations
            if (result.PacketLossPercent > 2)
            {
                recs.Add(new NetworkRecommendation
                {
                    Category = "stability",
                    Severity = "high",
                    Text = $"Perte de paquets élevée ({result.PacketLossPercent}%). Réseau instable.",
                    Action = "Vérifier le câble, redémarrer le routeur, ou changer de canal Wi-Fi"
                });
            }
            else if (result.PacketLossPercent > 0.5)
            {
                recs.Add(new NetworkRecommendation
                {
                    Category = "stability",
                    Severity = "medium",
                    Text = $"Perte de paquets modérée ({result.PacketLossPercent}%). Peut affecter le gaming.",
                    Action = null
                });
            }

            // Jitter recommendations
            if (result.JitterMsP95 > 30)
            {
                recs.Add(new NetworkRecommendation
                {
                    Category = "latency",
                    Severity = "high",
                    Text = $"Jitter élevé ({result.JitterMsP95:F1}ms). Appels vidéo et gaming affectés.",
                    Action = "Réduire le nombre d'appareils connectés ou utiliser une connexion filaire"
                });
            }
            else if (result.JitterMsP95 > 15)
            {
                recs.Add(new NetworkRecommendation
                {
                    Category = "latency",
                    Severity = "medium",
                    Text = $"Jitter modéré ({result.JitterMsP95:F1}ms). Gaming compétitif peut être affecté.",
                    Action = null
                });
            }

            // Wi-Fi signal recommendations
            if (result.IsWifi && result.WifiSignalPercent.HasValue)
            {
                if (result.WifiSignalPercent < 40)
                {
                    recs.Add(new NetworkRecommendation
                    {
                        Category = "wifi",
                        Severity = "high",
                        Text = $"Signal Wi-Fi faible ({result.WifiSignalPercent}%). Connexion instable probable.",
                        Action = "Rapprocher l'appareil du routeur ou utiliser un répéteur Wi-Fi"
                    });
                }
                else if (result.WifiSignalPercent < 60)
                {
                    recs.Add(new NetworkRecommendation
                    {
                        Category = "wifi",
                        Severity = "medium",
                        Text = $"Signal Wi-Fi moyen ({result.WifiSignalPercent}%). Performance réduite possible.",
                        Action = null
                    });
                }
            }

            // No recommendations = good!
            if (recs.Count == 0)
            {
                recs.Add(new NetworkRecommendation
                {
                    Category = "general",
                    Severity = "info",
                    Text = "Réseau local en bon état. Aucun problème détecté.",
                    Action = null
                });
            }

            return recs;
        }

        #endregion

        #region Helpers

        private bool IsLocalIp(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            if (ip == "127.0.0.1" || ip.StartsWith("127.")) return true;
            foreach (var range in PrivateRanges)
            {
                if (ip.StartsWith(range)) return true;
            }
            return false;
        }

        private double GetPercentile(List<long> sortedValues, int percentile)
        {
            if (sortedValues.Count == 0) return 0;
            int index = (int)Math.Ceiling(sortedValues.Count * percentile / 100.0) - 1;
            return sortedValues[Math.Max(0, Math.Min(index, sortedValues.Count - 1))];
        }

        private double GetPercentile(List<double> sortedValues, int percentile)
        {
            if (sortedValues.Count == 0) return 0;
            int index = (int)Math.Ceiling(sortedValues.Count * percentile / 100.0) - 1;
            return sortedValues[Math.Max(0, Math.Min(index, sortedValues.Count - 1))];
        }

        #endregion
    }

    #region Models

    public class AdapterInfo
    {
        public string Name { get; set; } = "Unknown";
        public string Type { get; set; } = "Unknown";
        public long SpeedMbps { get; set; }
        public string? Gateway { get; set; }
        public List<string> DnsServers { get; set; } = new();
        public bool IsWifi { get; set; }
        public int? WifiSignalPercent { get; set; }
    }

    public class NetworkQualityResultOffline
    {
        // Adapter info
        public string AdapterName { get; set; } = "";
        public string AdapterType { get; set; } = "";
        public long LinkSpeedMbps { get; set; }
        public bool IsWifi { get; set; }
        public int? WifiSignalPercent { get; set; }

        // Ping targets
        public List<PingTargetResultOffline> Targets { get; set; } = new();

        // Overall metrics
        public double LatencyMsP50 { get; set; }
        public double LatencyMsP95 { get; set; }
        public double JitterMsP95 { get; set; }
        public double PacketLossPercent { get; set; }
        public double DnsLatencyMs { get; set; }
        public double TcpRetransmitRate { get; set; }

        // Connection verdict
        public string ConnectionVerdict { get; set; } = "Unknown"; // Excellent, Bon, Moyen, Mauvais
        public string VerdictReason { get; set; } = "";

        // Recommendations
        public List<NetworkRecommendation> Recommendations { get; set; } = new();
        
        /// <summary>
        /// Calculate connection verdict based on local metrics
        /// </summary>
        public void CalculateVerdict()
        {
            // Excellent: linkSpeed >= 300 Mbps, loss < 1%, latency p95 < 20ms, jitter < 10ms
            // Bon: linkSpeed >= 100 Mbps, loss < 2%, latency p95 < 35ms
            // Moyen: loss 2-5% or latency p95 35-80ms
            // Mauvais: loss > 5% or latency p95 > 80ms or linkSpeed < 30 Mbps

            var reasons = new List<string>();

            if (PacketLossPercent > 5)
            {
                ConnectionVerdict = "Mauvais";
                reasons.Add($"Perte paquets élevée ({PacketLossPercent}%)");
            }
            else if (LatencyMsP95 > 80)
            {
                ConnectionVerdict = "Mauvais";
                reasons.Add($"Latence très élevée ({LatencyMsP95}ms)");
            }
            else if (LinkSpeedMbps > 0 && LinkSpeedMbps < 30)
            {
                ConnectionVerdict = "Mauvais";
                reasons.Add($"Débit lien faible ({LinkSpeedMbps} Mbps)");
            }
            else if (PacketLossPercent > 2 || (LatencyMsP95 > 35 && LatencyMsP95 <= 80))
            {
                ConnectionVerdict = "Moyen";
                if (PacketLossPercent > 2) reasons.Add($"Perte paquets ({PacketLossPercent}%)");
                if (LatencyMsP95 > 35) reasons.Add($"Latence modérée ({LatencyMsP95}ms)");
            }
            else if (LinkSpeedMbps >= 300 && PacketLossPercent < 1 && LatencyMsP95 < 20 && JitterMsP95 < 10)
            {
                ConnectionVerdict = "Excellent";
                reasons.Add($"Lien {LinkSpeedMbps} Mbps, perte {PacketLossPercent}%, latence {LatencyMsP95}ms");
            }
            else if (LinkSpeedMbps >= 100 && PacketLossPercent < 2 && LatencyMsP95 < 35)
            {
                ConnectionVerdict = "Bon";
                reasons.Add($"Lien {LinkSpeedMbps} Mbps, perte {PacketLossPercent}%, latence {LatencyMsP95}ms");
            }
            else
            {
                ConnectionVerdict = "Moyen";
                reasons.Add($"Lien {LinkSpeedMbps} Mbps, perte {PacketLossPercent}%, latence {LatencyMsP95}ms");
            }

            VerdictReason = string.Join("; ", reasons);
        }
    }

    public class PingTargetResultOffline
    {
        public string Ip { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Available { get; set; } = true;
        public string? Reason { get; set; }
        public int Sent { get; set; }
        public int Received { get; set; }
        public int Lost { get; set; }
        public double LossPercent { get; set; }
        public double LatencyMsMin { get; set; }
        public double LatencyMsMax { get; set; }
        public double LatencyMsP50 { get; set; }
        public double LatencyMsP95 { get; set; }
        public double JitterMsP95 { get; set; }
    }

    public class NetworkRecommendation
    {
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "info";
        public string Text { get; set; } = "";
        public string? Action { get; set; }
    }

    #endregion
}
