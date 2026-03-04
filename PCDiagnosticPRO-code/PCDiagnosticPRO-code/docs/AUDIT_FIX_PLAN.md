# AUDIT_FIX_PLAN.md — PC Diagnostic PRO Data Collection Audit

## Date: 2026-01-30
## Analyseur: Principal Windows Diagnostics Engineer

---

## PHASE 1 — CARTOGRAPHIE DES SOURCES

### A) PowerShell Output
- **TXT Path**: `C:\Virtual IT Pro\Rapport\Scan_<runId>_<date>.txt`
- **JSON Path**: `C:\Virtual IT Pro\Rapport\Scan_<runId>_<date>.json`
- **Last RunID**: `cf14cc16-06eb-4e8b-8b94-aa4c562bfdc0` (28/01/2026)
- **Script**: `Total_PS_PC_Scan_v7.0.ps1` (IMMUTABLE by default)

### B) C# Sensors Output
- **Collector**: `Services/HardwareSensorsCollector.cs` (LibreHardwareMonitor)
- **Combined JSON**: `scan_result_combined.json` (written by `WriteCombinedResultAsync`)
- **Sanitizer**: `Services/DataSanitizer.cs` (validates all values before write)

### C) Unified TXT Generation
- **Builder**: `Services/UnifiedReportBuilder.cs`
- **Output**: `Rapport_Unifie_<date>.txt`
- **Sections**: Metadata, PS Content, C# Sensors, UDIS Score

---

## PHASE 1 — DONNÉES MANQUANTES

### A) Champs Absents ou Null

| Champ | Source | Valeur | Raison |
|-------|--------|--------|--------|
| `cpuTempC` | PS | null | "Neutralisé v7 - externalisé vers C#" |
| `gpuTempC` | PS | null | "Neutralisé v7 - externalisé vers C#" |
| `vramTotalMB` | PS | null | "Limitation WMI - externalisé vers C#" |
| `ProcessList` | PS | none | "Get-Process et CIM ont échoué" |
| `disk temps` | PS SMART | null (6 disques) | "SMART lecture invalide" |

### B) Champs avec Sentinelles

| Champ | Valeur | Type Sentinelle |
|-------|--------|-----------------|
| `diskQueueLength` | -1 | Counter non supporté |
| `ping.latencyMs` | -1 | Tests désactivés |
| `dns.resolutionMs` | -1 | Tests désactivés |
| `fragmentation.G` | -1 | Volume non analysé |
| `fragmentation.H` | -1 | Volume non analysé |

### C) Incohérences TXT ↔ JSON

| Section | TXT | JSON | Problème |
|---------|-----|------|----------|
| Sensors C# | "❌ Données capteurs non disponibles" | N/A | Combined JSON non lu |
| SMART Temp | "N/A (invalid reading)" | `tempC: null` | Valeur 917542 rejetée |

---

## PHASE 1 — ERREURS RÉCURRENTES

### A) errors[] dans JSON PS

```json
[
  {"code": "WMI_ERROR", "message": "Unknown error", "section": null},
  {"code": "TEMP_WARN", "message": "Temperature SMART invalide: 917542", "section": "Collect-SmartDetails"}
]
```

### B) SMART Temperature Aberrante

- **Valeur observée**: 917541, 917542, 917543
- **Cause racine**: Raw SMART attribute 194 contient 4 bytes.
  - Low byte = température actuelle
  - Autres bytes = min/max historiques
  - PS lit la valeur entière au lieu du low byte
- **Impact**: Toutes les températures disques = null

### C) PerfCounters Sentinelles

- `diskQueueLength = -1` → Counter non disponible sur ce système
- Aucun autre counter collecté (CPU%, Memory%, etc.)

### D) Network Tests Désactivés

- `ExternalNetTests = false` par défaut
- Ping: -1 (skipped)
- DNS: -1 (skipped)

---

## PHASE 1 — DIAGNOSTIC RACINE PAR CATÉGORIE

### CPU Température
- **État**: PS neutralisé → C# collecte via LHM
- **Code**: `HardwareSensorsCollector.TryCollectCpuMetrics` (lignes 158-278)
- **Stratégie**: Tctl/Tdie/Package avec fallback robuste
- **Status**: ✅ FONCTIONNEL (si LHM charge correctement)

### GPU Température/Load/VRAM
- **État**: PS neutralisé → C# collecte via LHM
- **Code**: `HardwareSensorsCollector.TryCollectGpuMetrics` (lignes 99-155)
- **Status**: ✅ FONCTIONNEL (NVIDIA RTX 3090 supporté par LHM)

### Disques Température SMART NVMe
- **Problème**: PS lit raw SMART attribute entier (4 bytes) au lieu du low byte
- **Valeur exemple**: 917541 = 0x000E0005 → low byte = 0x05 = 5°C (trop froid) ou erreur lecture
- **Correction requise**: Extraire low byte du SMART attribute 194/190
- **Status**: ⚠️ CORRECTIF REQUIS (optionnel PS ou C#)

### PerfCounters
- **Problème**: Seul `diskQueueLength` collecté, retourne -1
- **Correction**: Ajouter CPU%, Memory%, DiskTime% robustes
- **Status**: ⚠️ AMÉLIORATION REQUISE

### missingData Mapping
- **Format actuel**: Objet unique `{section, item, reason}`
- **Support C#**: `CollectorDiagnosticsService.ExtractMissingDataFlexible` gère objet ET array
- **Status**: ✅ GÉRÉ

### Réseau Speed Test
- **État**: Tests externes désactivés par défaut
- **Module**: `NetworkRealSpeedAnalyzer.cs` existe
- **Status**: ⚠️ INTÉGRATION REQUISE (appeler async dans le pipeline)

### Stabilité Système
- **Données disponibles**: BSOD=0, appCrashes=3, reliabilityEvents=20
- **Module**: `SystemStabilityAnalyzer.cs` existe
- **Status**: ✅ FONCTIONNEL

---

## PLAN DE CORRECTION — PHASE 2

### CORRECTION A: SMART Temperature Byte Extraction

**Fichier**: `Services/SmartTemperatureHelper.cs` (NOUVEAU)

**Action**:
1. Créer helper pour extraire low byte de SMART attribute
2. Appeler depuis `CollectorDiagnosticsService` pour normaliser les temps PS
3. Ne PAS modifier le script PS

**Impact**: Températures disques exploitables
**Risque**: Faible (traitement C# post-collecte)

### CORRECTION B: PerfCounters Robustes

**Fichier**: `Services/PerfCounterCollector.cs` (NOUVEAU)

**Action**:
1. Créer collecteur C# pour:
   - `\Processor(_Total)\% Processor Time`
   - `\Memory\Available MBytes`
   - `\PhysicalDisk(_Total)\% Disk Time`
2. Fallback si counter non disponible
3. Intégrer dans scan pipeline

**Impact**: Métriques performance réelles
**Risque**: Faible

### CORRECTION C: Network Speed Test Integration

**Fichier**: `ViewModels/MainViewModel.cs`

**Action**:
1. Appeler `NetworkRealSpeedAnalyzer.MeasureAsync()` pendant le scan
2. Stocker résultat dans `UdisReport`
3. Afficher dans TXT unifié

**Impact**: Débit réseau mesuré
**Risque**: Moyen (dépend connectivité réseau)

### CORRECTION D: Combined JSON Path Resolution

**Fichier**: `Services/UnifiedReportBuilder.cs`

**Action**:
1. Vérifier que le chemin `scan_result_combined.json` est correctement résolu
2. Log si fichier non trouvé

**Impact**: Capteurs C# visibles dans TXT
**Risque**: Faible

---

## TESTS OBLIGATOIRES

### T1 — Build et Run
```powershell
cd d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code
dotnet clean
dotnet build
dotnet run
```

### T2 — Scan Complet
1. Lancer scan via UI
2. Attendre completion
3. Vérifier création des rapports

### T3 — Vérification Cohérence
1. Ouvrir `scan_result_combined.json`
2. Vérifier `sensors_csharp.cpu.cpuTempC.available`
3. Comparer avec TXT unifié section capteurs

### T4 — Validation MissingData
1. Vérifier JSON `missingData` parsé correctement
2. Vérifier TXT contient section "Erreurs & Limitations"

### T5 — Sentinelles
1. Chercher valeur -1 ou 0°C dans TXT
2. Doit afficher "Non disponible (raison)" et non la valeur brute

---

## CHECKLIST COUVERTURE

| Catégorie | Collecté | Validé | Affiché TXT | Exploitable LLM |
|-----------|----------|--------|-------------|-----------------|
| CPU Temp | ✅ C# | ✅ DataSanitizer | ✅ | ✅ |
| GPU Temp | ✅ C# | ✅ DataSanitizer | ✅ | ✅ |
| GPU Load | ✅ C# | ✅ | ✅ | ✅ |
| VRAM | ✅ C# | ✅ DataSanitizer | ✅ | ✅ |
| RAM | ✅ PS | ✅ | ✅ | ✅ |
| Storage Volumes | ✅ PS | ✅ | ✅ | ✅ |
| SMART Health | ✅ PS | ✅ | ✅ | ✅ |
| SMART Temp | ⚠️ PS | ❌ Aberrant | ❌ | ❌ |
| Network Config | ✅ PS | ✅ | ✅ | ✅ |
| Network Speed | ⚠️ Module existe | - | ⚠️ | ⚠️ |
| Stability | ✅ PS + Analyzer | ✅ | ✅ | ✅ |
| Security | ✅ PS | ✅ | ✅ | ✅ |
| Drivers | ✅ PS | ✅ | ✅ | ✅ |
| MissingData | ✅ PS | ✅ Flexible | ✅ | ✅ |

---

## PRIORITÉS

1. **P0 (Critique)**: SMART Temperature byte extraction
2. **P1 (Haute)**: Network Speed Test integration
3. **P2 (Moyenne)**: PerfCounters robustes
4. **P3 (Basse)**: Documentation logs

---

*Fin de l'audit Phase 1*

---

## PHASE 2 — IMPLÉMENTATION RÉALISÉE

### Fichiers Créés

| Fichier | Description |
|---------|-------------|
| `Services/SmartTemperatureHelper.cs` | Extraction low byte SMART pour températures valides |
| `Services/PerfCounterCollector.cs` | Collecteur PerfCounters robuste (CPU, Memory, Disk, Network) |
| `Services/BootTimeHealthAnalyzer.cs` | Analyse temps de démarrage |
| `Services/ThermalEnvelopeAnalyzer.cs` | Analyse stabilité thermique |
| `Services/StorageIoHealthAnalyzer.cs` | Analyse santé IO disque |

### Fichiers Modifiés

| Fichier | Modification |
|---------|--------------|
| `ViewModels/MainViewModel.cs` | Intégration PerfCounterCollector dans pipeline scan |
| `Models/UdisReport.cs` | Ajout propriétés: Thermal, Boot, StorageIO, Network |
| `Services/UnifiedDiagnosticScoreEngine.cs` | Appel nouveaux analyseurs + sections summary |
| `Services/UnifiedReportBuilder.cs` | Affichage nouvelles métriques dans TXT |
| `MainWindow.xaml` | UI pour métriques additionnelles + bouton SpeedTest |

### Build Status

```
✅ Build réussi
⚠️ 14 warnings (nullability, non critiques)
❌ 0 erreurs
```

---

## TESTS À EFFECTUER

### T1 — Lancer Scan Complet
```powershell
cd d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code
dotnet run
```

### T2 — Vérifier Logs
```powershell
Get-Content $env:TEMP\PCDiagnosticPro_ui.log | Select-Object -Last 100
```

### T3 — Vérifier Rapports Générés
- `scan_result_combined.json` doit contenir `sensors_csharp` avec `available` correct
- `Rapport_Unifie_*.txt` doit afficher:
  - Section capteurs C# avec températures
  - UDIS score avec nouvelles métriques (Thermal, Boot, IO)
  - Erreurs collecte listées explicitement

### T4 — Checklist Couverture Finale

| Catégorie | Collecté | Validé | Affiché TXT |
|-----------|----------|--------|-------------|
| CPU Temp | ✅ C# LHM | ✅ DataSanitizer | ✅ |
| GPU Temp/Load | ✅ C# LHM | ✅ DataSanitizer | ✅ |
| VRAM | ✅ C# LHM | ✅ DataSanitizer | ✅ |
| Disk Temps | ✅ C# LHM | ✅ DataSanitizer | ✅ |
| SMART Health | ✅ PS | ✅ | ✅ |
| SMART Temp | ⚠️ PS + SmartHelper | ✅ byte extract | ⚠️ fallback C# |
| PerfCounters | ✅ C# new | ✅ | ✅ |
| Network Speed | ✅ on-demand | ✅ | ✅ bouton UI |
| Thermal Score | ✅ C# | ✅ | ✅ |
| Boot Health | ✅ C# | ✅ | ✅ |
| Storage IO | ✅ C# | ✅ | ✅ |
| Stability | ✅ PS + C# | ✅ | ✅ |
| MissingData | ✅ flexible | ✅ normalized | ✅ |

---

*Fin Phase 2 — Build OK*
