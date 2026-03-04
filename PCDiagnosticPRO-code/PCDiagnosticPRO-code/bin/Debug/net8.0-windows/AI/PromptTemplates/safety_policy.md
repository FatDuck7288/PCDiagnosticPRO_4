# Safety Policy - PowerShell AutoFix (Strict)

Blocked patterns (must fail):
- Invoke-Expression / IEX
- EncodedCommand / -enc
- Download + execute chains (Invoke-WebRequest|IEX, DownloadString+IEX)
- Remove-Item -Recurse on system roots (C:\, %SystemDrive%\)
- Disable Defender realtime monitoring (Set-MpPreference -DisableRealtimeMonitoring)
- Add broad Defender exclusions (Add-MpPreference -ExclusionPath)
- Disable firewall profiles (netsh firewall disable, Set-NetFirewallProfile -Enabled False)
- Create local users / escalate privileges (New-LocalUser, net user /add, Add-LocalGroupMember Administrator)
- Disk destruction commands: diskpart, Clear-Disk, Format-Volume
- Shadow copy deletion: vssadmin delete shadows
- Boot configuration tampering: bcdedit /set

Allowed capability families:
- read-only diagnostics
- safe cleanup optional
- export logs
- query windows update status
- restart approved services

Mandatory script header lines:
- #Requires -Version 5.1
- # SUMMARY:
- # DOES_NOT:
- # RISKS:
- # ROLLBACK:
- # REQUIRES_ADMIN:
- # CAPABILITIES:

Mandatory reliability rules:
- try/catch blocks are required for mutating or external-call sections
- web requests must include -TimeoutSec
- process launches must include explicit wait/timeout handling
- script must be compatible with Windows PowerShell 5.1 and PowerShell 7+
