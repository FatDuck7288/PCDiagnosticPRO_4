# Analyse complète – Scores Performance et extraction CPU

## 1. Symptôme

- **Onglet Performance** : scores 51–69 pour une machine haut de gamme (96 Go RAM, Ryzen 9 5900X, RTX 3090 24 Go).
- **Attendu** : 85–100 pour Bureau, Jeu 1080p/1440p, Multitâche, etc.
- **Cause identifiée** : le `HardwareProfile` utilisé par le moteur de scoring a **CpuModel = (null), CpuCores = 0, CpuThreads = 0**. Le CPU n’est pas extrait, donc le scoring utilise des fallbacks (tiers “Upper Mid” sans modèle) et les formules produisent des scores sous-estimés.

---

## 2. Flux de données jusqu’au scoring

### 2.1 Qui appelle le moteur de performance

| Appelant | Fichier | Argument `combinedRoot` passé à `Evaluate` |
|----------|---------|--------------------------------------------|
| **HealthReportBuilder** | `HealthReportBuilder.cs` → `InjectPerformanceScore` | **Racine complète** du JSON (fichier combiné lu en mémoire). Contient `scan_powershell`, `diagnostic_snapshot`, `sections`, etc. |
| **FullReportBuilder** | `FullReportBuilder.cs` → `BuildPerformanceSection` | **Uniquement** `combined.ScanPowershell` (objet `scan_powershell` déjà désérialisé). |

### 2.2 D’où vient le JSON combiné

1. Le script PowerShell **Total_PS_PC_Scan_v7.0.ps1** produit un JSON via `Build-JsonSnapshot` :
   - Clés racine : `metadata`, `paths`, **`sections`**, `errors`, `findings`, `missingData`, `scoreV2`.
   - **`sections`** est un objet dont les clés sont les noms de sections (`"CPU"`, `"Memory"`, `"GPU"`, `"Storage"`, …).
   - Chaque section a la forme : `{ "status": "...", "summary": null, "data": <données> }`.
   - Pour **CPU** : `SectionData['CPU'] = $data` avec `$data = [ordered]@{ cpus = $cpuList; cpuCount = ... }`.
   - Chaque élément de `$cpuList` est : `[ordered]@{ name = ...; cores = ...; threads = ...; maxClockSpeed = ...; currentLoad = ... }` (clés **minuscules**).

2. **MainViewModel** :
   - Charge le JSON PS dans `doc.RootElement`.
   - Construit `CombinedScanResult` avec **`ScanPowershell = doc.RootElement.Clone()`** (donc tout le snapshot PS, incluant `sections`).
   - Sérialise avec `JsonSerializer.Serialize(combined, ...)` → le fichier **scan_result_combined.json** a une clé **`scan_powershell`** (grâce à `[JsonPropertyName("scan_powershell")]`) dont la valeur est le JSON PS complet (structure préservée car `JsonElement` est resérialisé tel quel).
   - La racine du fichier combiné contient aussi **`sections`** (liste de noms de sections, type `List<string>`) et les autres propriétés C# (`sensors_csharp`, `diagnostic_snapshot`, etc.).

3. Lors de la **lecture** du rapport :
   - **HealthReportBuilder** reçoit le **contenu brut** du JSON combiné et parse la racine.
   - **FullReportBuilder** désérialise en `CombinedScanResult` ; `combined.ScanPowershell` est donc l’objet **scan_powershell** (metadata, paths, **sections**, errors, …).

### 2.3 Résolution de `sections` dans `HardwareProfileBuilder.Build`

- **Entrée** : `combinedRoot` = soit la racine du JSON combiné (HealthReportBuilder), soit uniquement l’objet `scan_powershell` (FullReportBuilder).
- **Règles** :
  1. Si `root.TryGetProperty("scan_powershell", out var ps)` → `sections = ps.sections` si `ps.sections` est un **Object** ; sinon `sections = ps`.
  2. Si `sections` pas encore défini et `root.TryGetProperty("sections", out var sec2)` avec `sec2.ValueKind == Object` → `sections = sec2`.

**Point important** :  
- En **FullReportBuilder**, `root` = `scan_powershell` → pas de clé `scan_powershell` dans `root` → on prend `root.sections`. Dans le JSON PS, `sections` est bien un **objet** (CPU, Memory, …), donc `sections` est correctement résolu.  
- En **HealthReportBuilder**, `root` = racine du combiné → `root.scan_powershell` existe → `sections = root.scan_powershell.sections`. La racine a aussi une clé `sections` mais c’est une **liste** (tableau de noms), pas un objet → on ne l’utilise pas pour `sections` ici.

Les logs **H1** et **H5** confirment : `sectionsResolved: true`, `hasCpuKey: true`, `cpuSectionKeys = ["summary","status","data"]`, `cpuDataKeys = ["cpus","cpuCount"]`. La structure **sections.CPU.data.cpus** est donc bien celle utilisée.

---

## 3. Où l’extraction CPU échoue

### 3.1 Sources utilisées dans `ExtractCpu` (ordre)

1. **Source 1 – diagnostic_snapshot (JSON)**  
   - `diagnostic_snapshot.machine` : les logs montrent **machineKeys sans `cpuName`** (hostname, os, totalRamGB, architecture, …).  
   - `diagnostic_snapshot.metrics.cpu` : **Object** ; le code actuel (AddCpuMetrics, etc.) remplit surtout température / utilisation, pas model/cores/threads depuis cette structure.  
   → Aucune donnée CPU (nom, cœurs, threads) n’est lue ici.

2. **Source 2 – DiagnosticSnapshot C#**  
   - `snapshot` est **null** dans les logs (`snapshotNull: true`).  
   → Pas utilisé.

3. **Source 3 – sections (PowerShell)**  
   - On a bien `sections.CPU.data` avec **cpus** et **cpuCount**.  
   - Le code appelle `TryCpuFromSections(sections.Value, profile)` qui :
     - Trouve la section "CPU",
     - Récupère `data` (objet avec `cpus`, `cpuCount`),
     - Cherche un tableau sous les noms `cpus`, `Cpus`, …,
     - Prend le **premier élément** du tableau : `var first = cpuArray.EnumerateArray().FirstOrDefault();`
     - Si `first.ValueKind == Object`, appelle `ExtractCpuFromJsonObject(first, profile)`.

4. **Source 4 – cpuList (diagnostic_snapshot)**  
   - Utilisée seulement si le modèle ou les cœurs sont encore vides ; pas de `cpuList` dans la structure actuelle du diagnostic_snapshot.

### 3.2 Hypothèses pour l’échec Source 3

- **H3a – Tableau `cpus` vide**  
  Si le script PS n’a pas rempli `$cpuList` (ex. Get-WmiObject Win32_Processor échoue ou retourne rien), alors `cpus` dans le JSON est `[]`.  
  → `EnumerateArray().FirstOrDefault()` renvoie `default(JsonElement)` (ValueKind souvent `Undefined`/non Object).  
  → La condition `if (first.ValueKind != JsonValueKind.Object) continue;` fait qu’on ne appelle jamais `ExtractCpuFromJsonObject`, donc aucun remplissage CPU.

- **H3b – Casse ou noms de propriétés différents**  
  Le script PS produit des clés **minuscules** (`name`, `cores`, `threads`). Le code utilise `EnumerateObject()` + `MatchesAny(prop.Name, "name", "model", ...)` (insensible à la casse). En théorie, ça devrait matcher. Si toutefois la sérialisation (PS ou C#) change les noms (ex. PascalCase ailleurs), les clés pourraient ne pas correspondre.

- **H3c – Type de la valeur "name"**  
  Si pour une raison quelconque `name` n’est pas une chaîne (ex. nombre ou objet mal formé), `GetStringFromJsonValue` retourne null et on ne remplit pas `CpuModel`.

- **H3d – Exception silencieuse**  
  Un `try/catch` autour de la Source 3 pourrait avaler une exception (ex. accès à une propriété sur un élément inattendu). Les logs ne montrent pas d’erreur explicite, mais une exception avant d’atteindre `ExtractCpuFromJsonObject` expliquerait un profil vide.

Aucune modification de logique n’a été faite dans cette analyse ; la section suivante vise uniquement à **obtenir une preuve runtime** pour trancher entre ces hypothèses.

---

## 4. Preuve runtime à recueillir

Pour savoir pourquoi **sections.CPU.data.cpus[0]** ne remplit pas le profil, il faut dans les logs :

1. **Longueur du tableau `cpus`** (ex. `cpusArrayLength`) :  
   - 0 → H3a (tableau vide).  
   - ≥ 1 → le problème est sur le premier élément (H3b, H3c ou autre).

2. **Si longueur ≥ 1** :  
   - **Noms des propriétés du premier élément** (ex. `firstElementKeys`).  
   - **Valeur brute** de la propriété “name” (ou “Name”) pour ce premier élément (type + contenu).  

Un **seul** ajout de log ciblé dans `TryCpuFromSections` (après avoir obtenu `cpuArray` et `first`) permet d’enregistrer ces trois informations dans `.cursor/debug.log` sans changer le comportement du code. Une fois ces lignes présentes dans le log, on pourra :

- Soit corriger l’extraction (ex. clé différente, type différent),
- Soit investiguer pourquoi `cpus` est vide (côté script ou données WMI).

---

## 5. Résumé

| Élément | État |
|--------|------|
| Flux HealthReportBuilder vs FullReportBuilder | Compris ; les deux chemins résolvent bien `sections` (objet CPU/Memory/…). |
| Structure JSON PS (sections.CPU.data.cpus) | Confirmée par les logs H5. |
| diagnostic_snapshot / snapshot C# | Pas de cpuName dans machine ; snapshot null → pas de source CPU côté snapshot. |
| Extraction depuis sections.CPU.data.cpus[0] | Code présent et insensible à la casse ; soit le tableau est vide, soit le premier élément n’est pas lu correctement (clés/valeur "name"). |
| Prochaine étape | Ajouter un log minimal (longueur de `cpus`, clés de `cpus[0]`, valeur brute de "name"), relancer un scan, puis adapter le correctif en fonction du log. |

Aucune modification des formules de scoring ni des cas “données absentes” n’est recommandée avant d’avoir cette preuve et d’avoir corrigé l’extraction CPU.
