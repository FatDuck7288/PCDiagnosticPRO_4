# Tests obligatoires (T1–T4) — Prouver les correctifs

## Prérequis
- Lancer un scan complet (PowerShell + capteurs C#).
- Vérifier que les rapports sont générés : `Rapport_Unifie_*.txt` et `scan_result_combined.json` dans le dossier de sortie.

---

## T1 — Reproduire avec rapports main

**Objectif** : Confirmer que `collectorErrorsLogical` reflète `errors[]` et que le badge "Partiel/Limité" s’affiche.

1. Charger les 2 fichiers de la dernière exécution :
   - TXT : dernier `Rapport_Unifie_*.txt`
   - JSON : `scan_result_combined.json` (ou JSON PowerShell annoncé)

2. **Vérifications** :
   - Dans le TXT : section `[COLLECTE : ERREURS ET LIMITATIONS]` → ligne `Erreurs collecteur (logique): N` avec N = nombre d’entrées dans `errors[]` du JSON.
   - Dans le log `%TEMP%\PCDiagnosticPro_ui.log` :
     - `COLLECTOR_ERRORS_LOGICAL=N (from errors[]=N)`
     - `CollectionStatus=PARTIAL` ou `FAILED` si N > 0.
   - Dans l’UI : badge **"Collecte partielle / limitée"** ou **"Collecte échouée"** visible sous "Santé de votre PC" lorsque `collectorErrorsLogical > 0` ou `CollectionStatus != OK`.

**Commande log (PowerShell)** :
```powershell
Get-Content $env:TEMP\PCDiagnosticPro_ui.log -Tail 200 | Select-String "COLLECTOR_ERRORS_LOGICAL|CollectionStatus|collectorErrorsLogical"
```

---

## T2 — CPU temp 0

**Objectif** : Confirmer que la température CPU à 0°C est traitée comme invalide (available=false, "Non disponible (sentinelle 0)").

1. Si votre machine a une CPU temp à 0 (ou pour simuler) : après scan, ouvrir `scan_result_combined.json` et chercher `sensors_csharp` → `cpu` → `cpuTempC`.
2. **Vérifications** :
   - Si la valeur brute était 0 : après normalisation, `available` doit être `false` et `reason` doit contenir "sentinelle" ou "invalide".
   - Dans le TXT : section capteurs C# → CPU Temperature doit afficher **"Non disponible (capteur invalide: 0°C)"** ou équivalent.
   - Dans le log : `[SANITIZE] CPU Temp invalid (0) -> hidden: ...` ou `SANITIZE cpuTempC: 0 -> invalid`.
3. Impact : le score CPU et/ou ConfidenceScore doit être pénalisé (pas de 0°C considéré comme valide).

**Commande** :
```powershell
Get-Content $env:TEMP\PCDiagnosticPro_ui.log | Select-String "SANITIZE|cpuTempC"
```

---

## T3 — Score final

**Objectif** : Confirmer que le score affiché est le FinalScore (PS + C# avec confidence gating), pas uniquement GradeEngine.

1. Après un scan avec `errors[]` non vide ou missingData :
   - **Vérifications** :
     - `FinalScore` ≠ `ScoreCSharp` seul (sauf si confiance très élevée et pas de cap).
     - Si `collectorErrorsLogical > 0` : cap appliqué (FinalScore ≤ 70) et mention dans le TXT / logs.
     - Le grade affiché (UI et TXT) est calculé sur **FinalScore**, pas sur le score C# seul.
2. Dans le TXT : section `[SCORE ENGINE — FINAL SCORE PS + C#]` doit contenir :
   - `ScoreV2_PS`, `ScoreCSharp`, `FinalScore`, `ConfidenceScore`, `Source de vérité : FinalScore (moyenne pondérée + confidence gating)`.

**Commande log** :
```powershell
Get-Content $env:TEMP\PCDiagnosticPro_ui.log | Select-String "ScoreV2_PS|ScoreCSharp|FinalScore|FinalGrade|ConfidenceScore|caps applied"
```

---

## T4 — TXT/JSON alignés

**Objectif** : Même vérité côté TXT et JSON (erreurs, limitations, statut collecte).

1. **Vérifications** :
   - Les codes d’erreur listés dans le TXT (section "Erreurs collecteur") correspondent aux `errors[]` du JSON PowerShell (ou du `scan_powershell` dans le JSON combiné).
   - `missingData` : si le JSON a un objet (ex. `{"ProcessList": "disabled"}`), le TXT doit afficher une ligne du type "ProcessList: disabled" dans "Données manquantes".
   - `STATUT COLLECTE` dans le TXT = **ÉCHOUÉE** ou **PARTIELLE** lorsque `errors[]` non vide ou `collectorErrorsLogical > 0` ; **COMPLÈTE** sinon.
   - Capteurs : si le JSON combiné a `available: false` et `reason: "sentinelle 0"` pour `cpuTempC`, le TXT doit afficher "Non disponible (…)" pour la température CPU.

---

## Résumé des logs obligatoires

Le fichier `%TEMP%\PCDiagnosticPro_ui.log` doit contenir (après un scan) :

- `JSON path: ...`
- `CollectionStatus=... ; errors=... ; collectorErrorsLogical=... ; missingDataCount=...`
- `ScoreV2_PS=... ; ScoreCSharp=... ; FinalScore=... ; FinalGrade=... ; ConfidenceScore=...`
- `COLLECTOR_ERRORS_LOGICAL=... (from errors[]=...)`
- Événements `[SANITIZE] ... invalid -> hidden` pour les métriques normalisées.

---

## Exécution des tests unitaires DataNormalizer

Depuis un projet de test ou un runner :

```csharp
PCDiagnosticPro.Tests.DataNormalizerTests.RunAll();
```

Ou exécuter chaque test individuellement :
- `DataNormalizerTests.TestCpuTempZeroInvalid()`
- `DataNormalizerTests.TestCpuTempOutOfRange()`
- `DataNormalizerTests.TestCpuTempValid()`
- `DataNormalizerTests.TestGpuTempInvalid()`
- `DataNormalizerTests.TestSmartTempCorrupt()`
- `DataNormalizerTests.TestPerfCounterSentinel()`
- `DataNormalizerTests.TestVramInvalid()`
