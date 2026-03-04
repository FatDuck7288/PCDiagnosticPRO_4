# Safety Policy - PowerShell AutoFix (Strict)

Blocked patterns (must fail):
- Invoke-Expression / IEX
- EncodedCommand / -enc
- Download + execute chains (Invoke-WebRequest|IEX, DownloadString+IEX)
- Remove-Item -Recurse on system roots
- Disable Defender realtime monitoring
- Add broad Defender exclusions
- Disable firewall profiles
- Create local users / add admin users

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
