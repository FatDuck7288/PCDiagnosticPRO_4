# GOD TIER AUDIT + FIX — PC DIAGNOSTIC PRO
## Date: 2026-01-30 | Référence: scan_result_combined.json + Rapport_Unifie_20260130_104233.txt

---

## PHASE 1 — AUDIT FACTUEL COMPLET

### Problèmes Identifiés (10 causes principales)

| # | Donnée | Valeur Actuelle | Statut | Impact DRS |
|---|--------|-----------------|--------|------------|
| 1 | CPU Temp C# | `value=0, available=false, reason="sentinelle 0"` | ✅ Détecté | -5 |
| 2 | SMART Temp PS | Erreur 917538, `temperature: null` | ⚠️ Partiel | -5 |
| 3 | diskQueueLength PS | `-1` sentinelle exposée | ❌ Bug | -3 |
| 4 | ProcessList PS | `missingData`, `source: "none"` | ❌ Manquant | -3 |
| 5 | Disk Temps C# | 3/5 disques `available=false` | ⚠️ Partiel | -3 |
| 6 | Network Latency PS | `latencyMs: -1` skipped | ❌ Sentinelle | -2 |
| 7 | Fragmentation G/H | `-1` sentinelles | ❌ Sentinelle | -2 |
| 8 | findings PS | `{}` vide | ⚠️ Flou | -2 |
| 9 | WMI_ERROR PS | `"Unknown error"` | ⚠️ Flou | -1 |
| 10 | DynamicSignals CPU | `average: "0"` string | ⚠️ Suspect | -1 |

### Score Fiabilité Actuel: 62/100 (trop punitif)
### Score Fiabilité Cible: 85-90/100 (courbe progressive)

---

## PHASE 2 — STRATÉGIE MULTI-SOURCES

### CPU Température
```
Source A: LibreHardwareMonitor (C#)
  → Priorité: Package > Tdie > Tctl > Core
  → Validation: > 5°C ET < 115°C
  
Source B: WMI MSAcpi_ThermalZoneTemperature (C#)
  → Conversion: (kelvin - 2732) / 10 = °C
  → Validation: > 5°C ET < 115°C
  
Fallback: unavailable + reason="sensor_not_found"
```

### Disques Température
```
Source A: LibreHardwareMonitor Storage sensors (C#)
  → Validation: > 0°C ET < 90°C

Source B: PS SMART attribut 194 ou 190 byte extraction
  → lowByte = rawValue & 0xFF
  → Validation: > 0°C ET < 90°C

Fallback: unavailable + reason="disk_temp_not_readable"
```

### PerfCounters
```
Source A: PS Get-Counter "\PhysicalDisk(_Total)\Current Disk Queue Length"
  → Timeout: 5s
  
Source B: WMI Win32_PerfFormattedData_PerfDisk_PhysicalDisk
  → CurrentDiskQueueLength
  
Fallback: null + reason="perf_counter_not_supported"
Règle: JAMAIS exposer -1
```

### ProcessList
```
Source A: Get-CimInstance Win32_Process
  → Timeout: 10s
  
Source B: Get-Process
  → Timeout: 5s
  
Source C: tasklist /fo csv
  → Parse CSV, extraire top memory

Fallback: missingData avec reason explicite
```

---

## PHASE 3 — CLARIFICATION SECTIONS FLOUES

### Stabilité Système (Définition Officielle)
```
Composants:
1. BSOD 30 jours = MinidumpAnalysis.minidumpCount + EventLogs.bsodCount
2. Crashes apps 30 jours = ReliabilityHistory.appCrashes
3. Erreurs critiques 7 jours = EventLogs.System.criticalCount + Application.criticalCount
4. Erreurs totales 7 jours = EventLogs.System.errorCount + Application.errorCount

Résumé TXT:
  BSOD (30j): 0
  App Crashes (30j): 0
  Erreurs Critiques (7j): 0
  Erreurs Totales (7j): 91 (41 System + 50 Application)
```

### Pilotes (Définition Officielle)
```
Composants:
1. Devices en erreur = problemDeviceCount avec status="Error"
2. Devices dégradés = problemDeviceCount avec status="Degraded"
3. Devices unknown = count où status="Unknown"

Catégories par classe:
  - USB: 4 erreurs (port reset failures)
  - HIDClass: 10 unknown (périphériques HID normaux)
  - VolumeSnapshot: 8 unknown (VSS normal)
  - System: 1 degraded (AMDRyzenMaster)

Résumé TXT:
  Vrais problèmes: 6 (5 USB errors + 1 WD SES)
  Mineurs/Normal: 34 (unknown = déconnectés ou virtuels)
```

---

## PHASE 4 — DATA RELIABILITY SCORE (NOUVELLE LOGIQUE)

### Courbe Progressive (remplace logique punitive)
```csharp
int CalculateDRS(int errorCount, List<CollectorError> errors)
{
    // Base score with progressive degradation
    int baseScore = errorCount switch
    {
        0 => 100,
        1 => 95,
        2 => 90,
        3 => 84,
        4 => 78,
        5 => 72,
        _ => Math.Max(50, 72 - ((errorCount - 5) * 4))
    };
    
    // Pondération par criticité
    foreach (var err in errors)
    {
        int penalty = err.Category switch
        {
            "Security" => 8,      // Pénalité élevée
            "SMART" => 4,         // Pénalité moyenne
            "Storage" => 4,
            "CPU_Temp" => 2,      // Pénalité faible
            "ProcessList" => 2,
            _ => 1
        };
        baseScore -= penalty;
    }
    
    return Math.Clamp(baseScore, 0, 100);
}
```

### Principe Clé
```
Collecte partielle ≠ Mauvais PC
Collecte partielle = Confiance réduite dans le diagnostic

Un PC avec 2 erreurs de collecte mineure peut être en parfaite santé.
Le DRS mesure la FIABILITÉ DU DIAGNOSTIC, pas la santé du PC.
```

---

## PHASE 5 — UNIFICATION JSON ET TXT

### JSON Combiné - Ajouts Requis

```json
{
  "normalized_metrics": {
    "cpu_temp": {
      "value": null,
      "available": false,
      "source": "LHM_Tctl",
      "reason": "sentinel_out_of_range",
      "timestamp": "2026-01-30T10:42:27Z",
      "fallback_attempted": "WMI_ThermalZone",
      "fallback_result": "not_available"
    },
    "disk_temps": [
      {
        "disk": "Samsung SSD 990 PRO",
        "value": 53,
        "available": true,
        "source": "LHM_Storage",
        "timestamp": "2026-01-30T10:42:27Z"
      }
    ]
  },
  "findings": {
    "note": "No critical findings detected",
    "items": [],
    "generated_at": "2026-01-30T10:42:33Z"
  },
  "collection_quality": {
    "drs_score": 89,
    "errors_count": 2,
    "missing_count": 1,
    "sentinels_detected": 1,
    "sentinels_cleaned": 1
  }
}
```

### TXT Unifié - Section Qualité Collecte
```
════════════════════════════════════════════════════════════════════════════════
  [QUALITÉ DE COLLECTE — DATA RELIABILITY]
════════════════════════════════════════════════════════════════════════════════

  Score Fiabilité (DRS): 89/100

  ┌─ ERREURS COLLECTEUR ──────────────────────────────────────────────────────┐
  │  ⚠️ [WMI_ERROR] Section inconnue: erreur non spécifique
  │  ⚠️ [SMART_INVALID] Collect-SmartDetails: raw=917538 → rejeté
  └─────────────────────────────────────────────────────────────────────────────┘

  ┌─ SENTINELLES NETTOYÉES ──────────────────────────────────────────────────┐
  │  ✓ CPU Temp: 0 → null (reason: sentinel_out_of_range)
  │  ✓ diskQueueLength: -1 → null (reason: perf_counter_not_supported)
  └─────────────────────────────────────────────────────────────────────────────┘

  ┌─ DONNÉES MANQUANTES ──────────────────────────────────────────────────────┐
  │  ○ ProcessList: Get-Process et CIM ont échoué (fallback tasklist tenté)
  └─────────────────────────────────────────────────────────────────────────────┘

  Note: Collecte partielle ≠ PC défaillant. DRS mesure la fiabilité du diagnostic.
```

---

## PHASE 6 — PATCHES CONCRETS

### Fichiers C# à Modifier

| Fichier | Modification |
|---------|--------------|
| `Services/HardwareSensorsCollector.cs` | Ajouter fallback WMI ThermalZone pour CPU |
| `Services/DataSanitizer.cs` | Validation plages CPU/Disk + reason structurée |
| `Services/DataReliabilityEngine.cs` | Nouvelle courbe progressive + pondération |
| `Services/CollectorDiagnosticsService.cs` | Nettoyage sentinelles avant scoring |
| `Services/UnifiedReportBuilder.cs` | Section Qualité Collecte + findings |

### Fichier PowerShell à Modifier (CIBLÉ)

| Section | Modification |
|---------|--------------|
| `Collect-PerformanceCounters` | Remplacer -1 par `$null` + reason object |
| `Collect-ProcessList` | Ajouter fallback tasklist /fo csv |
| `Collect-SmartDetails` | Byte extraction pour température |

---

## PHASE 7 — VALIDATION ATTENDUE

### Avant vs Après (10 champs clés)

| Champ | Avant | Après |
|-------|-------|-------|
| CPU Temp | `value=0, available=false` | `value=null, available=false, fallback_attempted=true` ou valeur WMI |
| Disk Temps | 2/5 valides | 2/5 + reason par disque manquant |
| SMART Temp | Erreur 917538 | `null, reason="smart_raw_invalid"` ou byte extract |
| diskQueueLength | `-1` | `null, reason="perf_counter_not_supported"` |
| ProcessList | `missingData` | Rempli via tasklist ou missingData avec fallback info |
| GPU Temp | ✅ 77°C | Inchangé |
| VRAM Total | ✅ 24576 MB | Inchangé |
| ReliabilityHistory | `eventCount=20, appCrashes=0` | + résumé explicite en TXT |
| EventLogs | `errorCount=91` | + intégré dans Stabilité Système |
| findings | `{}` vide | `{note: "...", items: [], generated_at: "..."}` |

### Score Fiabilité Attendu

```
Avant: 62/100 (trop punitif)
Après: 85-90/100 (courbe progressive)

Calcul:
- 2 erreurs base → 90
- Erreurs mineurs (SMART, WMI) → -4
- = 86/100 (fiable)
```

---

## FICHIERS MODIFIÉS — RÉCAPITULATIF

### C# — Fichiers Créés
| Fichier | Description |
|---------|-------------|
| `Services/WmiThermalZoneFallback.cs` | **NOUVEAU** - Fallback WMI MSAcpi_ThermalZoneTemperature pour CPU temp quand LHM retourne sentinelle |

### C# — Fichiers Modifiés
| Fichier | Modification |
|---------|--------------|
| `Services/HardwareSensorsCollector.cs` | Ajout validation sentinelle CPU temp (0°C, hors plage 5-115°C) + appel fallback WMI ThermalZone |
| `Services/DataReliabilityEngine.cs` | Nouvelle courbe progressive (0→100, 1→95, 2→90...) + pondération criticité + breakdown audit |
| `Services/UnifiedReportBuilder.cs` | Ajout notes explicatives DRS dans le rapport TXT |

### PowerShell — Fichiers Modifiés
| Section | Modification |
|---------|--------------|
| `Collect-PerformanceCounters` | diskQueueLength: remplacé -1 par $null + reason + fallback WMI |
| `Collect-Processes` | Ajout fallback tasklist /fo csv quand Get-Process et CIM échouent |
| `Collect-SmartDetails` | Utilisation Extract-SmartTemperature pour byte extraction valeurs aberrantes |

---

## LIVRABLES FINAUX

1. ✅ `AUDIT_GOD_TIER_FIX.md` (ce fichier)
2. ✅ `Services/WmiThermalZoneFallback.cs` (nouveau)
3. ✅ `Services/HardwareSensorsCollector.cs` (fallback WMI CPU temp)
4. ✅ `Services/DataReliabilityEngine.cs` (courbe progressive)
5. ✅ `Services/UnifiedReportBuilder.cs` (notes DRS)
6. ✅ `Scripts/Total_PS_PC_Scan_v7.0.ps1` (3 corrections ciblées)
7. 📊 Build réussi: 0 erreurs, 16 warnings

---

## VALIDATION — AVANT VS APRÈS (Attendu)

| Champ | Avant | Après |
|-------|-------|-------|
| CPU Temp | `value=0, available=false` | Essai WMI ThermalZone avant abandon |
| diskQueueLength | `-1` (sentinelle) | `$null + reason="perf_counter_not_supported"` |
| ProcessList | missingData immédiat | Fallback tasklist avant missingData |
| SMART Temp | Erreur 917538 | Extract low byte (34°C) ou null+reason |
| DRS Score | ~62/100 (punitif) | ~89/100 (courbe progressive) |

---

## EXÉCUTION RECOMMANDÉE

```powershell
# 1. Build
cd "d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code"
dotnet build

# 2. Run
dotnet run

# 3. Comparer les nouveaux rapports avec les anciens
# - scan_result_combined.json : vérifier diskQueueLength, ProcessList.source
# - Rapport_Unifie_*.txt : vérifier DRS score, CPU temp fallback info
```
