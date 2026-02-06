# Driver Inventory & Updates (C#)

## Sources and APIs

This module uses **Windows-provided APIs only**:

- **WMI (Windows Management Instrumentation)**
  - `Win32_PnPSignedDriver` for driver metadata (class, version, date, provider, INF, signature).
  - `Win32_PnPEntity` for hardware IDs and device status.
- **Windows Update Agent (COM)**
  - `Microsoft.Update.Session` and `IUpdateSearcher` for available **driver updates**.
  - Criteria: `IsInstalled=0 and Type='Driver'`.

No third‑party code, no reverse engineering, and no proprietary databases are used.

## What is collected

Driver inventory:
- DeviceClass
- DeviceName / FriendlyName
- Provider / Manufacturer
- DriverVersion
- DriverDate (WMI date converted to ISO)
- INF name
- Hardware IDs (if available)
- Status (from Win32_PnPEntity)
- Signed state (IsSigned)

Driver update status (best‑effort):
- Uses Windows Update Agent search results
- Attempts to match by Hardware ID, then model + manufacturer, then class + manufacturer
- Status values: `UpToDate`, `Outdated`, `Unknown`

## Limitations

- Some drivers do not expose hardware IDs or signature status.
- Windows Update Agent may be unavailable, disabled, or offline.
- Matching between installed drivers and update candidates is **heuristic**.
- No driver download or installation is performed.

## Permissions / Licensing

- Uses **OS‑bundled components** only (WMI + Windows Update Agent).
- No external scraping or private endpoints.
- No proprietary or copied code.
