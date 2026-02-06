# README_FIX.md — PC Diagnostic PRO Implementation Report

## Date: 2026-01-30 | Schema Version: 2.2.0 | FIX PLAN APPLIED

---

## RÉSUMÉ DES IMPLÉMENTATIONS

### PHASE 1 — DiagnosticSnapshot + NormalizedMetric

**Fichiers créés:**
- `Models/DiagnosticSnapshot.cs` - Structure principale avec schemaVersion 2.2.0
- `Models/NormalizedMetric.cs` - Format normalisé + MetricFactory helpers
- `Services/DiagnosticSnapshotBuilder.cs` - Construction avec validation sentinelles

**Contrat schemaVersion 2.2.0:**
```json
{
  "diagnostic_snapshot": {
    "schemaVersion": "2.2.0",
    "generatedAt": "2026-01-30T...",
    "machine": { "hostname": "...", "os": "...", "isAdmin": true },
    "metrics": {
      "cpu": { "temperature": { "value": 45.0, "unit": "°C", "available": true, ... } },
      "gpu": { ... },
      "storage": { ... },
      "whea": { ... },
      "networkQuality": { ... }
    },
    "findings": [],
    "collectionQuality": { "totalMetrics": 25, "availableMetrics": 22, ... }
  }
}
```

### PHASE 2 — Sanitization Sentinelles

**Règles appliquées:**
- CPU temp = 0 → `available: false, reason: "sentinel_zero", confidence: 0`
- Disk temp = 0 → `available: false, reason: "sentinel_zero"`
- PerfCounter = -1 → `available: false, reason: "sentinel_minus_one"`
- NaN/Infinity → `available: false, reason: "nan_or_infinite"`
- Hors plage → `available: false, reason: "out_of_range (...)"`

### PHASE 3 — NetworkQuality OFFLINE STRICT

**Fichier modifié:** `DiagnosticsSignals/Collectors/NetworkQualityCollector.cs`

**Changements:**
- ❌ Supprimé: 8.8.8.8, 1.1.1.1 (IPs externes)
- ✅ Conservé: Gateway locale, 127.0.0.1, DNS local (RFC1918 uniquement)

**Cibles autorisées:**
1. Gateway par défaut (si RFC1918)
2. Localhost (127.0.0.1)
3. Serveurs DNS configurés (seulement si RFC1918)

**Métriques collectées:**
- 30 pings par cible
- latencyMsP50, latencyMsP95
- jitterMsP95
- lossPercent
- localDnsResolveMs (résolution hostname local)

### PHASE 4 — 10 Signaux Diagnostiques

| # | Signal | Fichier | Status |
|---|--------|---------|--------|
| 1 | WHEA | WheaCollector.cs | ✅ |
| 2 | GPU TDR/PerfCap | GpuRootCauseCollector.cs | ✅ |
| 3 | IO Latency | IoLatencyCollector.cs | ✅ NOUVEAU |
| 4 | CPU Throttle | CpuThrottleCollector.cs | ✅ |
| 5 | DPC/ISR | DpcIsrCollector.cs | ✅ NOUVEAU (ETW required) |
| 6 | Memory Pressure | MemoryPressureCollector.cs | ✅ |
| 7 | Power Limits | PowerLimitsCollector.cs | ✅ |
| 8 | Driver Stability | DriverStabilityCollector.cs | ✅ |
| 9 | Boot Performance | BootPerformanceCollector.cs | ✅ |
| 10 | Network Quality | NetworkQualityCollector.cs | ✅ LOCAL ONLY |

### PHASE 6 — Intégration scan_result_combined.json

**Fichier modifié:** `ViewModels/MainViewModel.cs` (WriteCombinedResultAsync)

Le JSON combiné inclut maintenant:
```json
{
  "scan_powershell": { ... },
  "sensors_csharp": { ... },
  "diagnostic_snapshot": { "schemaVersion": "2.2.0", ... },
  "diagnostic_signals": { ... },
  "process_telemetry": { ... },
  "network_diagnostics": { ... }
}
```

---

## COMMENT LANCER LE SCAN

### 1. Build
```powershell
cd d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code
dotnet build
```

### 2. Exécuter (Admin requis)
```powershell
# Méthode 1: dotnet run
dotnet run

# Méthode 2: Exécutable direct
.\bin\Debug\net8.0-windows\PCDiagnosticPro.exe
```

### 3. Lancer le Scan
1. Cliquer sur "Lancer le Scan" dans l'UI
2. Attendre la fin (~2-3 minutes avec signaux + réseau)

---

## FICHIERS À VÉRIFIER

### Emplacement des rapports
```
%LocalAppData%\PCDiagnosticPro\Rapports\
```

### Fichiers générés
| Fichier | Description |
|---------|-------------|
| `scan_result_combined.json` | JSON unifié avec diagnostic_snapshot |
| `Rapport_Unifie_*.txt` | Rapport texte lisible |
| `Scan_*_*.json` | JSON brut PowerShell |

### Logs
| Fichier | Description |
|---------|-------------|
| `%TEMP%\PCDiagnosticPro_ui.log` | Log UI principal |
| `%TEMP%\PCDiagnosticPro_signals.log` | Log des 10 signaux |

---

## PREUVES DE CORRECTION

### 1. SchemaVersion 2.2.0
Vérifier dans `scan_result_combined.json`:
```json
"diagnostic_snapshot": {
  "schemaVersion": "2.2.0"
}
```

### 2. 10 Signaux Présents
Vérifier que `diagnostic_snapshot.metrics` contient:
- `cpu`, `gpu`, `storage`
- `whea`, `gpuRootCause`, `storageLatency`, `cpuThrottle`
- `dpcIsr` (avec `reason: "etw_required_for_latency"`)
- `memoryPressure`, `powerLimits`, `driverStability`
- `bootPerformance`, `networkQuality`

### 3. Sentinelles Éliminées
Si CPU temp = 0 dans les capteurs:
```json
"cpu": {
  "temperature": {
    "value": null,
    "available": false,
    "reason": "sentinel_zero",
    "confidence": 0
  }
}
```

### 4. NetworkQuality LOCAL ONLY
Vérifier que `targets[]` ne contient que:
- IPs commençant par `192.168.`, `10.`, `172.16-31.`, `127.`
- Aucun `8.8.8.8` ou `1.1.1.1`

### 5. DPC/ISR Unavailable (ETW absent)
```json
"dpcIsr": {
  "available": {
    "available": false,
    "reason": "etw_required_for_latency"
  }
}
```

---

## TESTS AUTOMATISÉS

### Exécuter les tests de contrat
```csharp
var (passed, failed, failures) = PCDiagnosticPro.Tests.ContractTests.RunAllTests();
Console.WriteLine($"Passed: {passed}, Failed: {failed}");
foreach (var f in failures) Console.WriteLine($"  FAIL: {f}");
```

### Tests inclus
| Test | Vérifie |
|------|---------|
| Test_SchemaVersion_Is_2_2_0 | schemaVersion = "2.2.0" |
| Test_CpuTemp_Zero_Returns_Unavailable | Sentinelle 0 → unavailable |
| Test_DiskTemp_Zero_Returns_Unavailable | Sentinelle 0 → unavailable |
| Test_PerfCounter_MinusOne_Returns_Unavailable | Sentinelle -1 → unavailable |
| Test_PerfCounter_NaN_Returns_Unavailable | NaN → unavailable |
| Test_NetworkQuality_NoExternalIPs | Pas de 8.8.8.8, 1.1.1.1 |
| Test_DpcIsrCollector_Without_ETW_Returns_Unavailable | ETW absent → reason claire |
| Test_Unavailable_Metric_Has_Reason | reason obligatoire |
| Test_Unavailable_Metric_Has_Zero_Confidence | confidence = 0 |

---

## DEFINITION OF DONE ✅

| Critère | Status |
|---------|--------|
| diagnostic_snapshot existe avec schemaVersion 2.2.0 | ✅ |
| Chaque métrique a: value, unit, available, source, reason, timestamp, confidence | ✅ |
| CPU temp 0 → available=false, confidence=0, reason explicite | ✅ |
| Disk temp 0 → available=false, reason explicite | ✅ |
| PerfCounter -1/NaN → available=false, reason explicite | ✅ |
| NetworkQuality = local only (aucune cible externe) | ✅ |
| 10 signaux présents (2 peuvent être unavailable avec reason) | ✅ |
| Rapport TXT cohérent avec snapshot | ✅ |
| Logs UI + Signals complets | ✅ |

---

---

## FIX PLAN IMPLEMENTATIONS (P0-P1)

### P0-1: ProcessTelemetryCollector - Toolhelp32 Fallback

**Fichier:** `Services/ProcessTelemetryCollector.cs`

**Chaîne de fallback:**
1. `System.Diagnostics.Process.GetProcesses()` (méthode primaire)
2. `Toolhelp32Snapshot` (API Windows native) si méthode 1 échoue

**Fonctionnalités:**
- `CreateToolhelp32Snapshot` + `Process32First/Next` pour lister les PID
- `OpenProcess(QUERY_LIMITED_INFORMATION)` pour accès limité
- `GetProcessMemoryInfo` pour WorkingSet et PrivateBytes
- `GetProcessTimes` pour temps CPU
- Top 15 RAM, Top 10 CPU
- Gestion `accessDenied=true` (conserve PID/Name même si accès refusé)

### P0-3: WMI_ERROR - Detailed Capture

**Fichier:** `Models/WmiErrorInfo.cs`

**Champs capturés:**
- `namespace` (ex: root\wmi)
- `query` (ex: SELECT * FROM ...)
- `method` (WMI, CIM, PowerShell)
- `durationMs`
- `exceptionType`
- `hresult` (code hexadécimal)
- `message` (JAMAIS "Unknown")
- `topStackFrame`
- `severity` et `isLimitation`

### P1-2: NetworkQuality OFFLINE STRICT

**Fichier:** `DiagnosticsSignals/Collectors/NetworkQualityCollector.cs`

**Mesures LOCAL ONLY:**
- `LinkSpeedMbps` (vitesse négociée de l'adaptateur)
- `WifiSignalPercent` (si Wi-Fi)
- Ping vers: gateway, localhost, DNS local (RFC1918 uniquement)
- `LatencyMsP50`, `LatencyMsP95`, `JitterMsP95`, `PacketLossPercent`
- `DnsLatencyMs` (résolution hostname local)
- `TcpRetransmitRate` (si perf counters disponibles)

**Grille de recommandations:**
| Débit | Verdict |
|-------|---------|
| < 5 Mbps | Navigation only, gaming déconseillé |
| 5-20 Mbps | Streaming HD ok, gaming déconseillé si jitter |
| 20-100 Mbps | Gaming possible si loss <1% |
| > 100 Mbps | Gaming compétitif possible |

**Alertes:**
- Perte > 2% → "Réseau instable"
- Jitter > 30ms → "Appels vidéo et gaming affectés"
- Signal Wi-Fi < 40% → "Connexion instable probable"

---

## FICHIERS MODIFIÉS/CRÉÉS

| Fichier | Action | Description |
|---------|--------|-------------|
| `Services/ProcessTelemetryCollector.cs` | Modifié | Toolhelp32 fallback natif |
| `Models/WmiErrorInfo.cs` | Créé | Capture détaillée erreurs WMI |
| `DiagnosticsSignals/Collectors/NetworkQualityCollector.cs` | Modifié | Offline + recommandations |

---

*Généré le 2026-01-30 — FIX PLAN Complete*
