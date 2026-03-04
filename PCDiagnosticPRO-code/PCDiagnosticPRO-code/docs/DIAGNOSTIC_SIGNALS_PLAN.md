# DIAGNOSTIC SIGNALS — GOD TIER IMPLEMENTATION PLAN
## PC DIAGNOSTIC PRO — 10 MESURES DIAGNOSTIQUES

---

## PHASE 0 — ANALYSE OBLIGATOIRE

### A) CARTOGRAPHIE PIPELINE ACTUEL

#### Génération des fichiers
| Fichier | Source | Chemin |
|---------|--------|--------|
| JSON PowerShell | `Total_PS_PC_Scan_v7.0.ps1` | `%LocalAppData%\PCDiagnosticPro\Rapports\Scan_{guid}_{date}.json` |
| TXT PowerShell | `Total_PS_PC_Scan_v7.0.ps1` | `%LocalAppData%\PCDiagnosticPro\Rapports\Scan_{guid}_{date}.txt` |
| JSON Combiné | `MainViewModel.WriteCombinedResultAsync()` | `%LocalAppData%\PCDiagnosticPro\Rapports\scan_result_combined.json` |
| TXT Unifié | `UnifiedReportBuilder.BuildUnifiedReportAsync()` | `%LocalAppData%\PCDiagnosticPro\Rapports\Rapport_Unifie_{date}.txt` |

#### Fusion C# + PS
- **Fichier**: `ViewModels/MainViewModel.cs`
- **Méthode**: `WriteCombinedResultAsync()` (lignes 1922-1953)
- **Structure**: `{ scan_powershell: {...}, sensors_csharp: {...} }`

#### Lecture UI
- **Fichier**: `ViewModels/MainViewModel.cs`
- **Méthode**: `LoadJsonResultAsync()` (lignes 1506-1604)
- **Builder**: `HealthReportBuilder.Build(jsonContent, _lastSensorsResult)`

### B) ÉTAT ACTUEL DES 10 MESURES

| # | Mesure | Présent? | Où? | Insuffisant parce que... |
|---|--------|----------|-----|--------------------------|
| 1 | **WHEA Ingestion** | ❌ Non | - | Aucune collecte EventLog WHEA-Logger |
| 2 | **GPU PerfCap + TDR** | ⚠️ Partiel | EventLogs (erreurs générales) | Pas de filtrage TDR spécifique (nvlddmkm, amdkmdag) |
| 3 | **IO Latency P95/P99** | ❌ Non | - | Seulement moyenne via PerfCounters, pas percentiles |
| 4 | **CPU Throttle Flags** | ❌ Non | - | Pas d'EventLog Kernel-Processor-Power ID 37 |
| 5 | **DPC/ISR Latency** | ❌ Non | - | Aucun collecteur ETW DPC/ISR |
| 6 | **Packet Loss + Jitter** | ⚠️ Partiel | NetworkLatency (skipped par défaut) | Pas de jitter, pas de multi-target, pas actif |
| 7 | **Hard Faults Sustained** | ⚠️ Partiel | DynamicSignals.memory.pageFaultsPerSec | Pas de percentile, pas de soutenu |
| 8 | **Power Limit Events** | ❌ Non | - | Aucune corrélation CPU/GPU power limit |
| 9 | **Driver Reset Signals** | ⚠️ Partiel | EventLogs + DevicesDrivers | Pas de TDR count, pas de Kernel-Power 41, pas BugCheck |
| 10 | **Boot Performance** | ⚠️ Partiel | BootTimeHealthAnalyzer.cs | Pas d'EventLog Diagnostics-Performance ID 100/101 |

### C) RISQUES IDENTIFIÉS

| Risque | Mesures concernées | Mitigation |
|--------|-------------------|------------|
| **Requiert Admin** | 1,2,3,4,5,8,9 | Déjà admin par défaut; vérifier `Metadata.IsAdmin` |
| **ETW lourd** | 3,5 | Capture courte (10s), timeout strict, async |
| **Antivirus block** | 5 (ETW kernel) | Fallback gracieux, marquer `unavailable` |
| **NVML/NVAPI absent** | 2,8 | Mode dégradé: déduction par corrélation |
| **Pas de GPU dédié** | 2,8 | Retourner `available=false, reason="no_dedicated_gpu"` |
| **UI trop lourde** | tous | Sections collapsibles, lazy loading |

---

## PHASE 1 — ARCHITECTURE DiagnosticsSignals

### Structure Proposée

```
PCDiagnosticPRO-code/
├── DiagnosticsSignals/
│   ├── ISignalCollector.cs           # Interface commune
│   ├── SignalResult.cs               # Modèle résultat standard
│   ├── SignalsOrchestrator.cs        # Orchestrateur avec timeout
│   ├── SignalsLogger.cs              # Log unique %TEMP%\PCDiagnosticPro_signals.log
│   ├── Collectors/
│   │   ├── WheaCollector.cs          # 1. WHEA ingestion
│   │   ├── GpuRootCauseCollector.cs  # 2. GPU PerfCap + TDR
│   │   ├── IoLatencyCollector.cs     # 3. IO latency percentile
│   │   ├── CpuThrottleCollector.cs   # 4. CPU throttle flags
│   │   ├── DpcIsrCollector.cs        # 5. DPC/ISR latency
│   │   ├── NetworkQualityCollector.cs # 6. Packet loss + jitter
│   │   ├── MemoryPressureCollector.cs # 7. Hard faults sustained
│   │   ├── PowerLimitsCollector.cs   # 8. Power limit events
│   │   ├── DriverStabilityCollector.cs # 9. Driver reset signals
│   │   └── BootPerformanceCollector.cs # 10. Boot performance réel
│   └── Models/
│       └── SignalModels.cs           # Tous les modèles de sortie
```

### Interface ISignalCollector

```csharp
public interface ISignalCollector
{
    string Name { get; }
    TimeSpan DefaultTimeout { get; }
    Task<SignalResult> CollectAsync(CancellationToken ct);
}
```

### Modèle SignalResult Standard

```csharp
public class SignalResult
{
    public string Name { get; set; } = "";
    public object? Value { get; set; }
    public bool Available { get; set; } = true;
    public string Source { get; set; } = "";
    public string? Reason { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Quality { get; set; } = "ok"; // ok, partial, suspect
    public string? Notes { get; set; }
}
```

### Intégration dans le Pipeline

1. **Après** `HardwareSensorsCollector.CollectAsync()`
2. **Avant** `WriteCombinedResultAsync()`
3. Résultats ajoutés au JSON combiné sous `diagnostic_signals: {...}`

---

## PHASE 2 — PLAN D'IMPLÉMENTATION DÉTAILLÉ

### Mesure 1: WHEA Ingestion
```
Source: EventLog System, Provider "Microsoft-Windows-WHEA-Logger"
Fenêtre: 7 jours + 30 jours
Sortie JSON:
{
  "whea": {
    "last7dCount": int,
    "last30dCount": int,
    "correctedCount": int,
    "fatalCount": int,
    "lastEvents": [{ "id": int, "time": string, "message": string }]
  }
}
Dépendances: System.Diagnostics.EventLog
Admin requis: Oui (déjà vérifié)
```

### Mesure 2: GPU PerfCap + TDR
```
TDR Source: EventLog System, Source contenant "nvlddmkm" ou "amdkmdag" ou "Display"
Event types: 4101 (TDR), 14 (display driver stopped)
PerfCap: Non disponible sans NVML → déduction par corrélation
Sortie JSON:
{
  "gpuRootCause": {
    "tdrCount7d": int,
    "tdrCount30d": int,
    "perfcapAvailable": false,
    "perfcapReason": null,
    "throttlingSuspected": bool,
    "evidence": string[]
  }
}
```

### Mesure 3: IO Latency Percentile
```
Plan A: ETW Microsoft-Windows-Kernel-Disk (Microsoft.Diagnostics.Tracing.TraceEvent)
Plan B: Fallback PerfCounters (moyennes seulement)
Sortie JSON:
{
  "storageLatency": {
    "readMsP50": double,
    "readMsP95": double,
    "readMsP99": double,
    "writeMsP50": double,
    "writeMsP95": double,
    "writeMsP99": double,
    "source": "ETW" | "PerfCounter",
    "windowSeconds": int
  }
}
Note: ETW nécessite package NuGet Microsoft.Diagnostics.Tracing.TraceEvent
```

### Mesure 4: CPU Throttle Flags
```
Source A: EventLog System, Source "Kernel-Processor-Power", Event ID 37
Source B: PerfCounter "% Processor Performance"
Sortie JSON:
{
  "cpuThrottle": {
    "firmwareThrottleEvents7d": int,
    "firmwareThrottleEvents30d": int,
    "perfPercentAvg": double,
    "freqMhzAvg": int,
    "throttleSuspected": bool,
    "evidence": string[]
  }
}
```

### Mesure 5: DPC/ISR Latency
```
Plan A: ETW Microsoft-Windows-Kernel-DPC (capture 10s)
Plan B: Marquer unavailable si ETW échoue
Sortie JSON:
{
  "dpcIsr": {
    "dpcUsP95": double,
    "dpcUsMax": double,
    "isrUsP95": double,
    "isrUsMax": double,
    "windowSeconds": int,
    "source": "ETW"
  }
}
Note: Critique pour stabilité gaming/audio
```

### Mesure 6: Packet Loss + Jitter
```
Source A: Ping ICMP vers 1.1.1.1, 8.8.8.8, gateway local (20 paquets)
Source B: DNS latency (10 résolutions)
Source C: TCP retransmit via PerfCounter
Sortie JSON:
{
  "networkQuality": {
    "targets": [{ "ip": "1.1.1.1", "lossPercent": 0, "rttAvg": 12, "jitterMs": 2 }],
    "overallLossPercent": double,
    "overallJitterMs": double,
    "dnsMsP95": double,
    "tcpRetransPerSec": double
  }
}
```

### Mesure 7: Hard Faults Sustained
```
Source: PerfCounter "Memory\Page Faults/sec" (30s, 1 sample/sec)
Sortie JSON:
{
  "memoryPressure": {
    "hardFaultsAvg": double,
    "hardFaultsP95": double,
    "hardFaultsMax": double,
    "sustainedSeconds": int,
    "windowSeconds": int
  }
}
```

### Mesure 8: Power Limit Events
```
CPU: EventLog Kernel-Processor-Power + corrélation perf drop
GPU: Corrélation GPU load élevé + clocks drop + temp élevé
Sortie JSON:
{
  "powerLimits": {
    "cpuPowerLimitSuspected": bool,
    "gpuPowerLimitSuspected": bool,
    "evidence": string[]
  }
}
```

### Mesure 9: Driver Reset Signals
```
Sources:
- TDR counts (mesure 2)
- Kernel-Power 41 (unexpected shutdown)
- BugCheck events
- WER crash reports
Sortie JSON:
{
  "driverStability": {
    "tdrCount30d": int,
    "kernelPower41Count30d": int,
    "bugcheckCount30d": int,
    "appCrashGpuRelated": int,
    "lastEvents": [{ "type": string, "time": string, "details": string }]
  }
}
```

### Mesure 10: Boot Performance Réel
```
Source: EventLog Microsoft-Windows-Diagnostics-Performance
Event ID 100 (boot complete), 101-110 (degradation)
Sortie JSON:
{
  "bootPerformance": {
    "lastBootMs": int,
    "avgBootMs": double,
    "degradationEvents30d": int,
    "lastEventTime": string
  }
}
```

---

## PHASE 3 — SCORING INTÉGRATION

### UnifiedDiagnosticScoreEngine Ajouts

Les 10 signaux alimentent `MachineHealthScore` avec pondération par criticité:

| Signal | Poids | Seuil Critique |
|--------|-------|----------------|
| WHEA fatal | 15 | >0 = -30 points |
| TDR répétés | 10 | >3/7d = -20 points |
| IO P99 élevé | 8 | >50ms = -15 points |
| DPC spikes | 8 | max >1000μs = -10 points |
| CPU throttle firmware | 6 | >5 events/7d = -10 points |
| Packet loss | 5 | >5% = -10 points |
| Hard faults sustained | 5 | >1000/s sustained = -8 points |
| Boot degradation | 4 | avgBootMs >120s = -5 points |
| Power limit | 4 | suspected = -5 points |
| Driver reset | 4 | Kernel-Power 41 = -10 points |

---

## PHASE 4 — ORDRE D'IMPLÉMENTATION

1. **Infrastructure** (SignalsOrchestrator, ISignalCollector, SignalResult)
2. **Mesures simples EventLog** (1-WHEA, 4-CPU Throttle, 9-Driver Stability, 10-Boot)
3. **Mesures réseau** (6-Network Quality)
4. **Mesures PerfCounter** (7-Memory Pressure)
5. **Mesures complexes** (2-GPU Root Cause, 8-Power Limits)
6. **Mesures ETW** (3-IO Latency, 5-DPC/ISR) - optionnelles si NuGet autorisé

---

---

## IMPLÉMENTATION COMPLÉTÉE

### Fichiers Créés

| Fichier | Description |
|---------|-------------|
| `DiagnosticsSignals/ISignalCollector.cs` | Interface pour tous les collecteurs |
| `DiagnosticsSignals/SignalResult.cs` | Modèle de résultat standard |
| `DiagnosticsSignals/SignalsLogger.cs` | Logger centralisé → `%TEMP%\PCDiagnosticPro_signals.log` |
| `DiagnosticsSignals/SignalsOrchestrator.cs` | Orchestrateur avec timeouts individuels |
| `DiagnosticsSignals/Collectors/WheaCollector.cs` | 1. WHEA ingestion (EventLog) |
| `DiagnosticsSignals/Collectors/GpuRootCauseCollector.cs` | 2. GPU TDR + root cause |
| `DiagnosticsSignals/Collectors/CpuThrottleCollector.cs` | 4. CPU throttle flags |
| `DiagnosticsSignals/Collectors/NetworkQualityCollector.cs` | 6. Packet loss, jitter, DNS latency |
| `DiagnosticsSignals/Collectors/MemoryPressureCollector.cs` | 7. Hard faults sustained |
| `DiagnosticsSignals/Collectors/DriverStabilityCollector.cs` | 9. Driver reset signals |
| `DiagnosticsSignals/Collectors/BootPerformanceCollector.cs` | 10. Boot performance réel |
| `DiagnosticsSignals/Collectors/PowerLimitsCollector.cs` | 8. Power limit events |

### Fichiers Modifiés

| Fichier | Modification |
|---------|--------------|
| `Models/CombinedScanResult.cs` | Ajout `diagnostic_signals` Dictionary |
| `ViewModels/MainViewModel.cs` | Intégration SignalsOrchestrator dans pipeline |

### Collecteurs Non Implémentés (nécessitent ETW TraceEvent)

| # | Mesure | Raison |
|---|--------|--------|
| 3 | IO Latency P95/P99 | Nécessite NuGet Microsoft.Diagnostics.Tracing.TraceEvent |
| 5 | DPC/ISR Latency | Nécessite ETW kernel DPC/ISR events |

Ces mesures peuvent être ajoutées ultérieurement via le package NuGet TraceEvent.

### Build Status

```
✅ 0 Erreurs
⚠️ 18 Warnings (pré-existants)
```

### JSON Output Structure

```json
{
  "scan_powershell": { ... },
  "sensors_csharp": { ... },
  "diagnostic_signals": {
    "whea": {
      "name": "whea",
      "value": { "last7dCount": 0, "last30dCount": 0, "correctedCount": 0, "fatalCount": 0, "lastEvents": [] },
      "available": true,
      "source": "EventLog_System_WHEA-Logger",
      "quality": "ok",
      "timestamp": "2026-01-30T17:30:00Z",
      "durationMs": 150
    },
    "gpuRootCause": { ... },
    "cpuThrottle": { ... },
    "networkQuality": { ... },
    "memoryPressure": { ... },
    "driverStability": { ... },
    "bootPerformance": { ... },
    "powerLimits": { ... }
  }
}
```

### Log Output Example

```
=== PC DIAGNOSTIC PRO — SIGNALS LOG ===
Started: 2026-01-30 17:30:00
========================================
17:30:00.001 [Orchestrator] INFO: Starting 8 collectors
17:30:00.002 [whea] START
17:30:00.150 [whea] SUCCESS (148ms) — 7d=0, 30d=0, fatal=0
17:30:00.151 [gpuRootCause] START
17:30:00.320 [gpuRootCause] SUCCESS (169ms) — TDR: 7d=0, 30d=0
...
17:30:35.500 [Orchestrator] INFO: Completed: 8 success, 0 fail, 35500ms total
```
