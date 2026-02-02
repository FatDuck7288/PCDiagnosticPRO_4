using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            public bool IsWindowsHome { get; set; }
            public bool? DeviceEncryptionEnabled { get; set; } // For Windows Home
            
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
        
        private void CollectBitLocker(SecurityInfoResult result)
        {
            try
            {
                if (result.IsWindowsHome)
                {
                    // Windows Home: Check Device Encryption instead of BitLocker
                    result.DeviceEncryptionEnabled = CheckDeviceEncryption();
                    result.BitLockerEnabled = result.DeviceEncryptionEnabled;
                    result.BitLockerStatus = result.DeviceEncryptionEnabled == true 
                        ? "device_encryption_on" 
                        : result.DeviceEncryptionEnabled == false 
                            ? "device_encryption_off" 
                            : "not_supported_home";
                    result.BitLockerSource = "Registry_DeviceEncryption";
                    return;
                }
                
                // Windows Pro/Enterprise: Try manage-bde first
                var manageBdeResult = TryManageBde();
                if (manageBdeResult.HasValue)
                {
                    result.BitLockerEnabled = manageBdeResult.Value;
                    result.BitLockerStatus = manageBdeResult.Value ? "encrypted" : "not_encrypted";
                    result.BitLockerSource = "manage-bde";
                    return;
                }
                
                // Fallback: WMI Win32_EncryptableVolume
                var wmiResult = TryWmiBitLocker();
                if (wmiResult.HasValue)
                {
                    result.BitLockerEnabled = wmiResult.Value;
                    result.BitLockerStatus = wmiResult.Value ? "encrypted" : "not_encrypted";
                    result.BitLockerSource = "WMI_Win32_EncryptableVolume";
                    return;
                }
                
                // Unable to determine
                result.BitLockerEnabled = null;
                result.BitLockerStatus = "unable_to_determine";
                result.BitLockerSource = "none";
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] BitLocker check error: {ex.Message}");
                result.BitLockerStatus = "error";
                result.BitLockerSource = "exception";
            }
        }
        
        private bool? CheckDeviceEncryption()
        {
            try
            {
                // Check Device Encryption status via registry
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\BitLocker\Status");
                if (key != null)
                {
                    var status = key.GetValue("EncryptionStatus");
                    if (status != null)
                    {
                        return Convert.ToInt32(status) > 0;
                    }
                }
                
                // Alternative: Check BitLocker volume info
                using var blKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\BitLocker");
                if (blKey != null)
                {
                    // If this key exists, device encryption might be enabled
                    return true;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] Device encryption check failed: {ex.Message}");
            }
            return null;
        }
        
        private bool? TryManageBde()
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
                process.WaitForExit(5000);
                
                if (output.Contains("Protection Status:") || output.Contains("État de la protection"))
                {
                    // Check for "Protection On" / "Protection activée"
                    if (output.Contains("Protection On") || output.Contains("Protection activée"))
                        return true;
                    if (output.Contains("Protection Off") || output.Contains("Protection désactivée"))
                        return false;
                }
                
                // Check for "Fully Encrypted" / "Entièrement chiffré"
                if (output.Contains("Percentage Encrypted:") || output.Contains("Pourcentage chiffré"))
                {
                    if (output.Contains("100") || output.Contains("Fully Encrypted") || output.Contains("Entièrement chiffré"))
                        return true;
                    if (output.Contains("0%"))
                        return false;
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] manage-bde failed: {ex.Message}");
            }
            return null;
        }
        
        private bool? TryWmiBitLocker()
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
                        // 0 = Unprotected, 1 = Protected, 2 = Unknown
                        return Convert.ToInt32(protectionStatus) == 1;
                    }
                }
            }
            catch (ManagementException ex)
            {
                App.LogMessage($"[SecurityInfoCollector] WMI BitLocker query failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityInfoCollector] WMI BitLocker exception: {ex.Message}");
            }
            return null;
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
