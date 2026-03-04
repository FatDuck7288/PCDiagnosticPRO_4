using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Collects security information that PowerShell doesn't provide:
    /// - BitLocker status (with Windows Home detection)
    /// - RDP enabled status (registry + service)
    /// - SMBv1 enabled status (registry + feature)
    /// </summary>
    public class SecurityInfoCollector
    {
        public class SecurityInfoResult
        {
            public bool Available { get; set; } = true;
            public string? ErrorMessage { get; set; }
            
            // BitLocker
            public bool? BitLockerEnabled { get; set; }
            public string BitLockerStatus { get; set; } = "unknown";
            public string BitLockerSource { get; set; } = "";
            public string BitLockerReason { get; set; } = "";
            public string BitLockerConfidence { get; set; } = "none";
            public bool IsWindowsHome { get; set; }
            public bool? DeviceEncryptionEnabled { get; set; } // For Windows Home

            // Defender hardening
            public bool? RealTimeProtectionEnabled { get; set; }
            public string RealTimeProtectionStatus { get; set; } = "unknown";
            public string RealTimeProtectionSource { get; set; } = "";
            public string RealTimeProtectionReason { get; set; } = "";
            public string RealTimeProtectionConfidence { get; set; } = "none";

            public bool? TamperProtectionEnabled { get; set; }
            public string TamperProtectionStatus { get; set; } = "unknown";
            public string TamperProtectionSource { get; set; } = "";
            public string TamperProtectionReason { get; set; } = "";
            public string TamperProtectionConfidence { get; set; } = "none";

            // Device Guard / Core isolation
            public bool? VbsEnabled { get; set; }
            public string VbsStatus { get; set; } = "unknown";
            public string VbsSource { get; set; } = "";
            public string VbsReason { get; set; } = "";
            public string VbsConfidence { get; set; } = "none";

            public bool? CredentialGuardEnabled { get; set; }
            public string CredentialGuardStatus { get; set; } = "unknown";
            public string CredentialGuardSource { get; set; } = "";
            public string CredentialGuardReason { get; set; } = "";
            public string CredentialGuardConfidence { get; set; } = "none";

            public bool? MemoryIntegrityEnabled { get; set; }
            public string MemoryIntegrityStatus { get; set; } = "unknown";
            public string MemoryIntegritySource { get; set; } = "";
            public string MemoryIntegrityReason { get; set; } = "";
            public string MemoryIntegrityConfidence { get; set; } = "none";

            // ASR
            public bool? AsrEnabled { get; set; }
            public int? AsrRulesCount { get; set; }
            public string AsrStatus { get; set; } = "unknown";
            public string AsrSource { get; set; } = "";
            public string AsrReason { get; set; } = "";
            public string AsrConfidence { get; set; } = "none";
            
            // RDP
            public bool? RdpEnabled { get; set; }
            public string RdpStatus { get; set; } = "unknown";
            public string RdpSource { get; set; } = "";
            
            // SMBv1
            public bool? SmbV1Enabled { get; set; }
            public string SmbV1Status { get; set; } = "unknown";
            public string SmbV1Source { get; set; } = "";
            
            // Timestamp
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }
        
        public async Task<SecurityInfoResult> CollectAsync(CancellationToken ct = default)
        {
            return await Task.Run(() => CollectInternal(ct), ct);
        }
        
        private SecurityInfoResult CollectInternal(CancellationToken ct)
        {
            var result = new SecurityInfoResult();
            
            try
            {
                // Detect Windows edition
                result.IsWindowsHome = IsWindowsHomeEdition();
                
                // Collect BitLocker status
                CollectBitLocker(result);
                if (ct.IsCancellationRequested) return result;

                // Collect modern Defender/Device Guard checks
                CollectDefenderHardening(result);
                if (ct.IsCancellationRequested) return result;

                CollectDeviceGuard(result);
                if (ct.IsCancellationRequested) return result;

                CollectAsrRules(result);
                if (ct.IsCancellationRequested) return result;
                
                // Collect RDP status
                CollectRdp(result);
                if (ct.IsCancellationRequested) return result;
                
                // Collect SMBv1 status
                CollectSmbV1(result);
            }
            catch (Exception ex)
            {
                result.Available = false;
                result.ErrorMessage = ex.Message;
                App.LogMessage($"[SecurityInfoCollector] Error: {ex.Message}");
            }
            
            return result;
        }
        
        #region BitLocker Detection

        private sealed class BitLockerProbeResult
        {
            public bool? Enabled { get; set; }
            public string Status { get; set; } = "unknown";
            public string Source { get; set; } = string.Empty;
            public string Reason { get; set; } = string.Empty;
            public string Confidence { get; set; } = "none";
        }
        
        private void CollectBitLocker(SecurityInfoResult result)
        {
            try
            {
                var isAdmin = AdminHelper.IsRunningAsAdmin();

                // Priority: cmdlet -> manage-bde -> WMI (all editions)
                var cmdletProbe = TryBitLockerCmdlet();
                if (cmdletProbe != null)
                {
                    ApplyBitLockerProbe(result, cmdletProbe);
                    return;
                }
                
                var manageBdeProbe = TryManageBdeDetailed();
                if (manageBdeProbe != null)
                {
                    ApplyBitLockerProbe(result, manageBdeProbe);
                    return;
                }

                var wmiProbe = TryWmiBitLockerDetailed();
                if (wmiProbe != null)
                {
                    ApplyBitLockerProbe(result, wmiProbe);
                    return;
                }

                // Windows Home fallback: conservative registry evidence only.
                if (result.IsWindowsHome)
                {
                    result.DeviceEncryptionEnabled = CheckDeviceEncryption();
                    result.BitLockerEnabled = result.DeviceEncryptionEnabled;
                    if (result.DeviceEncryptionEnabled == true)
                    {
                        result.BitLockerStatus = "enabled";
                        result.BitLockerSource = "DeviceEncryptionRegistry";
                        result.BitLockerReason = "HomeSKUFallback";
                        result.BitLockerConfidence = "medium";
                    }
                    else if (result.DeviceEncryptionEnabled == false)
                    {
                        result.BitLockerStatus = "disabled";
                        result.BitLockerSource = "DeviceEncryptionRegistry";
                        result.BitLockerReason = "HomeSKUFallback";
                        result.BitLockerConfidence = "medium";
                    }
                    else
                    {
                        result.BitLockerStatus = "unavailable";
                        result.BitLockerSource = "DeviceEncryptionRegistry";
                        result.BitLockerReason = "NotSupported";
                        result.BitLockerConfidence = "none";
                    }
                    return;
                }

                result.BitLockerEnabled = null;
                result.BitLockerStatus = isAdmin ? "unavailable" : "unavailable_rights";
                result.BitLockerSource = "none";
                result.BitLockerReason = isAdmin ? "Error" : "AccessDenied";
                result.BitLockerConfidence = isAdmin ? "none" : "low";
                
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] BitLocker check error: {ex.Message}");
                result.BitLockerEnabled = null;
                result.BitLockerStatus = "error";
                result.BitLockerSource = "exception";
                result.BitLockerReason = "Error";
                result.BitLockerConfidence = "none";
            }
        }

        private static void ApplyBitLockerProbe(SecurityInfoResult result, BitLockerProbeResult probe)
        {
            result.BitLockerEnabled = probe.Enabled;
            result.BitLockerStatus = probe.Status;
            result.BitLockerSource = probe.Source;
            result.BitLockerReason = probe.Reason;
            result.BitLockerConfidence = probe.Confidence;

            if (result.IsWindowsHome && probe.Enabled.HasValue)
                result.DeviceEncryptionEnabled = probe.Enabled.Value;
        }
        
        private bool? CheckDeviceEncryption()
        {
            try
            {
                // Check Device Encryption status via registry
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\BitLocker\Status");
                if (key != null)
                {
                    var encryptionStatus = ConvertObjectToNullableInt(key.GetValue("EncryptionStatus"));
                    if (encryptionStatus.HasValue)
                    {
                        return encryptionStatus.Value > 0;
                    }

                    var protectionStatus = ConvertObjectToNullableInt(key.GetValue("ProtectionStatus"));
                    if (protectionStatus.HasValue)
                    {
                        return protectionStatus.Value > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] Device encryption check failed: {ex.Message}");
            }
            return null;
        }

        private BitLockerProbeResult? TryBitLockerCmdlet()
        {
            try
            {
                var script =
                    "$ErrorActionPreference='Stop'; " +
                    "$v=Get-BitLockerVolume -MountPoint 'C:' -ErrorAction Stop | Select-Object -First 1; " +
                    "if($null -eq $v){'unavailable|cmdlet|NoVolume|none'} " +
                    "elseif($v.ProtectionStatus -eq 1 -or $v.ProtectionStatus -eq 'On'){'enabled|cmdlet|ok|high'} " +
                    "elseif($v.ProtectionStatus -eq 0 -or $v.ProtectionStatus -eq 'Off'){'disabled|cmdlet|ok|high'} " +
                    "else{'unavailable|cmdlet|Unknown|low'}";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return null;

                var output = process.StandardOutput.ReadToEnd().Trim();
                var stderr = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit(6000);

                if (!string.IsNullOrWhiteSpace(output))
                {
                    var line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(line) && line.Contains('|'))
                    {
                        var tokens = line.Split('|');
                        if (tokens.Length >= 4)
                        {
                            return new BitLockerProbeResult
                            {
                                Enabled = tokens[0] switch
                                {
                                    "enabled" => true,
                                    "disabled" => false,
                                    _ => null
                                },
                                Status = tokens[0],
                                Source = tokens[1],
                                Reason = tokens[2],
                                Confidence = tokens[3]
                            };
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(stderr) &&
                    stderr.Contains("Get-BitLockerVolume", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] BitLocker cmdlet probe failed: {ex.Message}");
            }

            return null;
        }
        
        private BitLockerProbeResult? TryManageBdeDetailed()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "manage-bde",
                    Arguments = "-status C:",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return null;
                
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                var normalized = output.ToLowerInvariant();
                var errorNormalized = (error ?? string.Empty).ToLowerInvariant();

                if (errorNormalized.Contains("access", StringComparison.Ordinal) ||
                    errorNormalized.Contains("denied", StringComparison.Ordinal) ||
                    errorNormalized.Contains("accès", StringComparison.Ordinal))
                {
                    return new BitLockerProbeResult
                    {
                        Enabled = null,
                        Status = "unavailable_rights",
                        Source = "manage-bde",
                        Reason = "AccessDenied",
                        Confidence = "low"
                    };
                }
                
                if (normalized.Contains("protection on") || normalized.Contains("protection activée") || normalized.Contains("protection active"))
                {
                    return new BitLockerProbeResult
                    {
                        Enabled = true,
                        Status = "enabled",
                        Source = "manage-bde",
                        Reason = "ok",
                        Confidence = "high"
                    };
                }
                if (normalized.Contains("protection off") || normalized.Contains("protection désactivée"))
                {
                    return new BitLockerProbeResult
                    {
                        Enabled = false,
                        Status = "disabled",
                        Source = "manage-bde",
                        Reason = "ok",
                        Confidence = "high"
                    };
                }

                var hasZeroPercent = normalized.Contains("0%") || normalized.Contains("0.0%") || normalized.Contains("0,0%");
                var hasFullPercent = normalized.Contains("100%") || normalized.Contains("100.0%") || normalized.Contains("100,0%");

                if (hasZeroPercent && !hasFullPercent)
                {
                    return new BitLockerProbeResult
                    {
                        Enabled = false,
                        Status = "disabled",
                        Source = "manage-bde",
                        Reason = "EncryptionPercent0",
                        Confidence = "medium"
                    };
                }

                if (hasFullPercent || normalized.Contains("fully encrypted") || normalized.Contains("entièrement chiffré"))
                {
                    return new BitLockerProbeResult
                    {
                        Enabled = true,
                        Status = "enabled",
                        Source = "manage-bde",
                        Reason = "EncryptionPercent100",
                        Confidence = "medium"
                    };
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] manage-bde failed: {ex.Message}");
            }
            return null;
        }
        
        private BitLockerProbeResult? TryWmiBitLockerDetailed()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                    "SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = 'C:'");
                
                foreach (ManagementObject volume in searcher.Get())
                {
                    var protectionStatus = volume["ProtectionStatus"];
                    if (protectionStatus != null)
                    {
                        var status = Convert.ToInt32(protectionStatus);
                        return new BitLockerProbeResult
                        {
                            Enabled = status == 1 ? true : status == 0 ? false : null,
                            Status = status == 1 ? "enabled" : status == 0 ? "disabled" : "unavailable",
                            Source = "WMI_Win32_EncryptableVolume",
                            Reason = status == 2 ? "Unknown" : "ok",
                            Confidence = status == 2 ? "low" : "high"
                        };
                    }
                }
            }
            catch (ManagementException ex)
            {
                App.LogMessage($"[SecurityInfoCollector] WMI BitLocker query failed: {ex.Message}");
                if (ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase))
                {
                    return new BitLockerProbeResult
                    {
                        Enabled = null,
                        Status = "unavailable_rights",
                        Source = "WMI_Win32_EncryptableVolume",
                        Reason = "AccessDenied",
                        Confidence = "low"
                    };
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] WMI BitLocker exception: {ex.Message}");
            }
            return null;
        }
        
        #endregion

        #region Modern Security Signals (Defender / Device Guard / ASR)

        private void CollectDefenderHardening(SecurityInfoResult result)
        {
            try
            {
                bool? realTime = null;
                bool? tamper = null;

                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        @"root\Microsoft\Windows\Defender",
                        "SELECT RealTimeProtectionEnabled, IsTamperProtected FROM MSFT_MpComputerStatus");

                    foreach (ManagementObject status in searcher.Get())
                    {
                        realTime = ConvertObjectToNullableBool(status["RealTimeProtectionEnabled"]);
                        tamper = ConvertObjectToNullableBool(status["IsTamperProtected"]);
                        break;
                    }

                    if (realTime.HasValue)
                    {
                        result.RealTimeProtectionEnabled = realTime;
                        result.RealTimeProtectionStatus = realTime.Value ? "enabled" : "disabled";
                        result.RealTimeProtectionSource = "WMI_MSFT_MpComputerStatus";
                        result.RealTimeProtectionReason = "ok";
                        result.RealTimeProtectionConfidence = "high";
                    }

                    if (tamper.HasValue)
                    {
                        result.TamperProtectionEnabled = tamper;
                        result.TamperProtectionStatus = tamper.Value ? "enabled" : "disabled";
                        result.TamperProtectionSource = "WMI_MSFT_MpComputerStatus";
                        result.TamperProtectionReason = "ok";
                        result.TamperProtectionConfidence = "high";
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[SecurityInfoCollector] Defender WMI probe failed: {ex.Message}");
                }

                if (!result.RealTimeProtectionEnabled.HasValue)
                {
                    using var rtpKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Real-Time Protection");
                    var disableRealtime = rtpKey?.GetValue("DisableRealtimeMonitoring");
                    var disabled = ConvertObjectToNullableBool(disableRealtime);
                    if (disabled.HasValue)
                    {
                        result.RealTimeProtectionEnabled = !disabled.Value;
                        result.RealTimeProtectionStatus = result.RealTimeProtectionEnabled.Value ? "enabled" : "disabled";
                        result.RealTimeProtectionSource = "Registry_Defender_RTP";
                        result.RealTimeProtectionReason = "ok";
                        result.RealTimeProtectionConfidence = "medium";
                    }
                }

                if (!result.TamperProtectionEnabled.HasValue)
                {
                    using var tamperKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Features");
                    var tamperValue = tamperKey?.GetValue("TamperProtection");
                    var tamperRaw = ConvertObjectToNullableInt(tamperValue);
                    if (tamperRaw.HasValue)
                    {
                        // Common values: 5 = enabled, 0 = disabled.
                        result.TamperProtectionEnabled = tamperRaw.Value > 0;
                        result.TamperProtectionStatus = result.TamperProtectionEnabled.Value ? "enabled" : "disabled";
                        result.TamperProtectionSource = "Registry_Defender_Features";
                        result.TamperProtectionReason = "ok";
                        result.TamperProtectionConfidence = "medium";
                    }
                }

                if (!result.RealTimeProtectionEnabled.HasValue)
                {
                    result.RealTimeProtectionStatus = "unavailable";
                    result.RealTimeProtectionSource = "none";
                    result.RealTimeProtectionReason = "NotSupported";
                    result.RealTimeProtectionConfidence = "none";
                }

                if (!result.TamperProtectionEnabled.HasValue)
                {
                    result.TamperProtectionStatus = "unavailable";
                    result.TamperProtectionSource = "none";
                    result.TamperProtectionReason = "NotSupported";
                    result.TamperProtectionConfidence = "none";
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] Defender hardening error: {ex.Message}");
                result.RealTimeProtectionStatus = "error";
                result.RealTimeProtectionSource = "exception";
                result.RealTimeProtectionReason = "Error";
                result.RealTimeProtectionConfidence = "none";
                result.TamperProtectionStatus = "error";
                result.TamperProtectionSource = "exception";
                result.TamperProtectionReason = "Error";
                result.TamperProtectionConfidence = "none";
            }
        }

        private void CollectDeviceGuard(SecurityInfoResult result)
        {
            try
            {
                int? vbsStatus = null;
                HashSet<int> configured = new();
                HashSet<int> running = new();

                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        @"root\Microsoft\Windows\DeviceGuard",
                        "SELECT VirtualizationBasedSecurityStatus, SecurityServicesConfigured, SecurityServicesRunning FROM Win32_DeviceGuard");

                    foreach (ManagementObject item in searcher.Get())
                    {
                        vbsStatus = ConvertObjectToNullableInt(item["VirtualizationBasedSecurityStatus"]);
                        configured = ConvertObjectToIntSet(item["SecurityServicesConfigured"]);
                        running = ConvertObjectToIntSet(item["SecurityServicesRunning"]);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[SecurityInfoCollector] DeviceGuard WMI probe failed: {ex.Message}");
                }

                if (vbsStatus.HasValue)
                {
                    result.VbsEnabled = vbsStatus.Value > 0;
                    result.VbsStatus = result.VbsEnabled.Value ? "enabled" : "disabled";
                    result.VbsSource = "WMI_Win32_DeviceGuard";
                    result.VbsReason = "ok";
                    result.VbsConfidence = "high";
                }
                else
                {
                    using var dgKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard");
                    var enableVbs = ConvertObjectToNullableInt(dgKey?.GetValue("EnableVirtualizationBasedSecurity"));
                    if (enableVbs.HasValue)
                    {
                        result.VbsEnabled = enableVbs.Value > 0;
                        result.VbsStatus = result.VbsEnabled.Value ? "enabled" : "disabled";
                        result.VbsSource = "Registry_DeviceGuard";
                        result.VbsReason = "ok";
                        result.VbsConfidence = "medium";
                    }
                }

                bool? credGuard = null;
                if (running.Count > 0 || configured.Count > 0)
                {
                    credGuard = running.Contains(1) || configured.Contains(1);
                    result.CredentialGuardEnabled = credGuard;
                    result.CredentialGuardStatus = credGuard.Value ? "enabled" : "disabled";
                    result.CredentialGuardSource = "WMI_Win32_DeviceGuard";
                    result.CredentialGuardReason = "ok";
                    result.CredentialGuardConfidence = running.Count > 0 ? "high" : "medium";
                }

                bool? memoryIntegrity = null;
                if (running.Count > 0 || configured.Count > 0)
                {
                    memoryIntegrity = running.Contains(2) || configured.Contains(2);
                    result.MemoryIntegrityEnabled = memoryIntegrity;
                    result.MemoryIntegrityStatus = memoryIntegrity.Value ? "enabled" : "disabled";
                    result.MemoryIntegritySource = "WMI_Win32_DeviceGuard";
                    result.MemoryIntegrityReason = "ok";
                    result.MemoryIntegrityConfidence = running.Count > 0 ? "high" : "medium";
                }

                if (!result.MemoryIntegrityEnabled.HasValue)
                {
                    using var hvciKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                    var hvciEnabled = ConvertObjectToNullableInt(hvciKey?.GetValue("Enabled"));
                    if (hvciEnabled.HasValue)
                    {
                        result.MemoryIntegrityEnabled = hvciEnabled.Value > 0;
                        result.MemoryIntegrityStatus = result.MemoryIntegrityEnabled.Value ? "enabled" : "disabled";
                        result.MemoryIntegritySource = "Registry_HVCI";
                        result.MemoryIntegrityReason = "ok";
                        result.MemoryIntegrityConfidence = "medium";
                    }
                }

                if (!result.CredentialGuardEnabled.HasValue)
                {
                    result.CredentialGuardStatus = "unavailable";
                    result.CredentialGuardSource = "none";
                    result.CredentialGuardReason = "NotSupported";
                    result.CredentialGuardConfidence = "none";
                }

                if (!result.MemoryIntegrityEnabled.HasValue)
                {
                    result.MemoryIntegrityStatus = "unavailable";
                    result.MemoryIntegritySource = "none";
                    result.MemoryIntegrityReason = "NotSupported";
                    result.MemoryIntegrityConfidence = "none";
                }

                if (!result.VbsEnabled.HasValue)
                {
                    result.VbsStatus = "unavailable";
                    result.VbsSource = "none";
                    result.VbsReason = "NotSupported";
                    result.VbsConfidence = "none";
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] DeviceGuard collection error: {ex.Message}");
                result.VbsStatus = "error";
                result.VbsSource = "exception";
                result.VbsReason = "Error";
                result.VbsConfidence = "none";
                result.CredentialGuardStatus = "error";
                result.CredentialGuardSource = "exception";
                result.CredentialGuardReason = "Error";
                result.CredentialGuardConfidence = "none";
                result.MemoryIntegrityStatus = "error";
                result.MemoryIntegritySource = "exception";
                result.MemoryIntegrityReason = "Error";
                result.MemoryIntegrityConfidence = "none";
            }
        }

        private void CollectAsrRules(SecurityInfoResult result)
        {
            try
            {
                using var rulesKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\ASR\Rules");
                if (rulesKey == null)
                {
                    result.AsrEnabled = null;
                    result.AsrRulesCount = null;
                    result.AsrStatus = "unavailable";
                    result.AsrSource = "Registry_ASR";
                    result.AsrReason = "NotSupported";
                    result.AsrConfidence = "none";
                    return;
                }

                var names = rulesKey.GetValueNames() ?? Array.Empty<string>();
                int configuredCount = 0;
                int activeCount = 0;

                foreach (var name in names)
                {
                    var value = ConvertObjectToNullableInt(rulesKey.GetValue(name));
                    if (!value.HasValue)
                        continue;

                    configuredCount++;
                    if (value.Value != 0)
                        activeCount++;
                }

                result.AsrRulesCount = configuredCount;
                result.AsrEnabled = activeCount > 0;
                result.AsrStatus = activeCount > 0 ? "enabled" : "disabled";
                result.AsrSource = "Registry_ASR";
                result.AsrReason = configuredCount == 0 ? "NoRulesConfigured" : "ok";
                result.AsrConfidence = "medium";
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] ASR collection error: {ex.Message}");
                result.AsrEnabled = null;
                result.AsrRulesCount = null;
                result.AsrStatus = "error";
                result.AsrSource = "exception";
                result.AsrReason = "Error";
                result.AsrConfidence = "none";
            }
        }

        private static bool? ConvertObjectToNullableBool(object? value)
        {
            if (value == null)
                return null;

            try
            {
                return value switch
                {
                    bool b => b,
                    byte bt => bt != 0,
                    short s => s != 0,
                    int i => i != 0,
                    uint ui => ui != 0,
                    long l => l != 0,
                    ulong ul => ul != 0,
                    string text when bool.TryParse(text, out var parsedBool) => parsedBool,
                    string text when int.TryParse(text, out var parsedInt) => parsedInt != 0,
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static int? ConvertObjectToNullableInt(object? value)
        {
            if (value == null)
                return null;

            try
            {
                return value switch
                {
                    byte bt => bt,
                    short s => s,
                    int i => i,
                    uint ui => (int)ui,
                    long l => (int)l,
                    ulong ul => (int)ul,
                    string text when int.TryParse(text, out var parsed) => parsed,
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static HashSet<int> ConvertObjectToIntSet(object? value)
        {
            var set = new HashSet<int>();
            if (value == null)
                return set;

            switch (value)
            {
                case ushort[] ushorts:
                    foreach (var item in ushorts)
                        set.Add(item);
                    break;
                case uint[] uints:
                    foreach (var item in uints)
                        set.Add((int)item);
                    break;
                case int[] ints:
                    foreach (var item in ints)
                        set.Add(item);
                    break;
                case object[] objs:
                    foreach (var item in objs)
                    {
                        var parsed = ConvertObjectToNullableInt(item);
                        if (parsed.HasValue)
                            set.Add(parsed.Value);
                    }
                    break;
            }

            return set;
        }

        #endregion
        
        #region RDP Detection
        
        private void CollectRdp(SecurityInfoResult result)
        {
            try
            {
                // Primary: Registry check for fDenyTSConnections
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
                if (key != null)
                {
                    var fDeny = key.GetValue("fDenyTSConnections");
                    if (fDeny != null)
                    {
                        // fDenyTSConnections = 0 means RDP is ENABLED
                        // fDenyTSConnections = 1 means RDP is DISABLED
                        result.RdpEnabled = Convert.ToInt32(fDeny) == 0;
                        result.RdpStatus = result.RdpEnabled == true ? "enabled" : "disabled";
                        result.RdpSource = "Registry_fDenyTSConnections";
                        
                        // Also check if TermService is running
                        var serviceRunning = IsServiceRunning("TermService");
                        if (result.RdpEnabled == true && !serviceRunning)
                        {
                            result.RdpStatus = "enabled_service_stopped";
                        }
                        return;
                    }
                }
                
                // Fallback: Check service state
                if (IsServiceRunning("TermService"))
                {
                    result.RdpEnabled = true;
                    result.RdpStatus = "service_running";
                    result.RdpSource = "Service_TermService";
                }
                else
                {
                    result.RdpEnabled = false;
                    result.RdpStatus = "service_not_running";
                    result.RdpSource = "Service_TermService";
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] RDP check error: {ex.Message}");
                result.RdpStatus = "error";
                result.RdpSource = "exception";
            }
        }
        
        #endregion
        
        #region SMBv1 Detection
        
        private void CollectSmbV1(SecurityInfoResult result)
        {
            try
            {
                // Method 1: Registry check (most reliable)
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters");
                if (key != null)
                {
                    var smb1 = key.GetValue("SMB1");
                    if (smb1 != null)
                    {
                        result.SmbV1Enabled = Convert.ToInt32(smb1) != 0;
                        result.SmbV1Status = result.SmbV1Enabled == true ? "enabled" : "disabled";
                        result.SmbV1Source = "Registry_LanmanServer";
                        return;
                    }
                }
                
                // Method 2: Check Windows Feature (PowerShell alternative in C#)
                var featureResult = CheckSmbV1Feature();
                if (featureResult.HasValue)
                {
                    result.SmbV1Enabled = featureResult.Value;
                    result.SmbV1Status = featureResult.Value ? "feature_enabled" : "feature_disabled";
                    result.SmbV1Source = "WindowsFeature";
                    return;
                }
                
                // Method 3: Check if mrxsmb10 driver exists
                var driverExists = System.IO.File.Exists(@"C:\Windows\System32\drivers\mrxsmb10.sys");
                if (driverExists)
                {
                    result.SmbV1Enabled = true;
                    result.SmbV1Status = "driver_present";
                    result.SmbV1Source = "DriverFile";
                }
                else
                {
                    result.SmbV1Enabled = false;
                    result.SmbV1Status = "driver_absent";
                    result.SmbV1Source = "DriverFile";
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] SMBv1 check error: {ex.Message}");
                result.SmbV1Status = "error";
                result.SmbV1Source = "exception";
            }
        }
        
        private bool? CheckSmbV1Feature()
        {
            try
            {
                // Use DISM to check SMB1 feature state
                var psi = new ProcessStartInfo
                {
                    FileName = "dism",
                    Arguments = "/Online /Get-Features /Format:Table",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                if (process == null) return null;
                
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10000);
                
                // Look for SMB1Protocol line
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("SMB1Protocol"))
                    {
                        if (line.Contains("Enabled") || line.Contains("Activé"))
                            return true;
                        if (line.Contains("Disabled") || line.Contains("Désactivé"))
                            return false;
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] DISM SMB1 check failed: {ex.Message}");
            }
            return null;
        }
        
        #endregion
        
        #region Helpers
        
        private bool IsWindowsHomeEdition()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    var editionId = key.GetValue("EditionID")?.ToString() ?? "";
                    var productName = key.GetValue("ProductName")?.ToString() ?? "";
                    
                    // Windows Home editions don't have BitLocker
                    return editionId.Contains("Home", StringComparison.OrdinalIgnoreCase) ||
                           editionId.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                           productName.Contains("Home", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }
        
        private bool IsServiceRunning(string serviceName)
        {
            try
            {
                // Use WMI to check service status instead of ServiceController
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT State FROM Win32_Service WHERE Name = '{serviceName}'");
                
                foreach (ManagementObject service in searcher.Get())
                {
                    var state = service["State"]?.ToString();
                    return state?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] Service check failed for {serviceName}: {ex.Message}");
            }
            return false;
        }
        
        #endregion
    }
}
