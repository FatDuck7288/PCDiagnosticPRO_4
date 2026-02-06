# Trim et optimisation — Liste des améliorations et code superflu

## 1. Code superflu à retirer ou alléger

### 1.1 Moteurs de score archivés (Obsolete, jamais appelés en production)

| Fichier | Action recommandée | Impact |
|---------|--------------------|--------|
| `Services/GradeEngine.cs` | **Supprimer** ou garder une version squelettique (~50 lignes) avec uniquement les constantes utilisées nulle part (aucun appel à `GradeEngine.ComputeGrade` dans le pipeline). | ~600 lignes en moins. Le pipeline utilise uniquement UDIS. |
| `Services/FinalScoreCalculator.cs` | **Supprimer** : `ApplyToReport` et `Calculate` ne sont jamais appelés. `HealthReportBuilder` remplit `Divergence` avec les valeurs UDIS directement. | ~225 lignes en moins. |

**Précision** : `report.Divergence.GradeEngineScore` est rempli par `HealthReportBuilder` avec `udis.UdisScore`, pas par `GradeEngine` ni `FinalScoreCalculator`. Les deux classes sont mortes pour le flux principal.

### 1.2 Documentation obsolète ou redondante

| Fichier | Action | Raison |
|---------|--------|--------|
| `ARCH_MAP.txt` | Mettre à jour ou déprécier | Décrit encore GradeEngine/FinalScoreCalculator comme partie du pipeline (sections C, D, E). La source de vérité est désormais UDIS. |
| `AUDIT_FIX_PLAN.md` | Archiver ou fusionner dans README_FIX | Plan historique ; README_FIX.md contient déjà le résumé 2.2.0. |
| `AUDIT_FIX_REPORT.md` | Idem | Rapport d’audit passé. |
| `AUDIT_GOD_TIER_FIX.md` | Idem | Correctifs déjà appliqués. |
| `TESTS_T1_T4.md` | Mettre à jour | Mentionne encore « FinalScore (PS + C# avec GradeEngine) » ; aligner sur UDIS. |
| `DIAGNOSTIC_SIGNALS_PLAN.md` / `DRIVER_INVENTORY.md` | Garder ou déplacer dans `/docs` | Utiles pour la maintenance ; optionnel de les regrouper. |

### 1.3 Commentaires et blocs verbeux

- **DiagnosticSnapshotBuilder.cs** : Le bloc de contrat (l.233–259) est utile ; à garder. Les nombreux `LogBuild(...)` en debug peuvent rester, mais on peut les conditionner à `#if DEBUG` ou un flag pour réduire le bruit en release.
- **HealthReportBuilder.cs** : Plusieurs `#region` et commentaires « P0/P1 » ; garder les régions, supprimer les commentaires redondants qui répètent le nom de la région.
- **MainViewModel.cs** : Beaucoup de `OnPropertyChanged` dupliqués dans les setters ; pas de code vraiment superflu, mais des régions bien nommées aident (déjà en place).

### 1.4 TODOs laissés en place

| Fichier | Ligne | TODO | Suggestion |
|---------|--------|------|------------|
| HealthRulesEngine.cs | 688 | Extract driver issues from PS | Soit implémenter (lecture section DevicesDrivers), soit remplacer par « Non implémenté : section PS non mappée ». |
| HealthRulesEngine.cs | 981 | Parse SmartDetails section | Idem : implémenter `ExtractSmartIssuesFromPs` ou documenter « non mappé ». |
| HealthRulesEngine.cs | 1026 | Check SecurityCenter for third-party AV | Idem : implémenter ou documenter. |

Réduire les TODOs vides : soit une ligne d’implémentation minimale, soit un commentaire clair « Non implémenté : … ».

---

## 2. Améliorations possibles (sans tout casser)

### 2.1 Alignement TryMapSection ↔ noms de sections PS

- **Problème** : `TryMapSection(sections, "Updates", ...)` ne tente qu’une clé. Le JSON PS peut exposer `"WindowsUpdate"`. Les mappers internes utilisent déjà plusieurs alias (`TryGetSectionData(..., "WindowsUpdate", "Updates", "WindowsUpdateInfo")`).
- **Amélioration** : Soit appeler `TryMapSection(sections, "WindowsUpdate", AddUpdatesMetricsFromPs)` (si le script écrit `WindowsUpdate`), soit étendre `TryMapSection` pour accepter `params string[] sectionNames` et tester chaque alias jusqu’à un succès.

### 2.2 Logging conditionnel

- **DiagnosticSnapshotBuilder** : 41 appels à `LogBuild`. En release, cela peut alourdir les logs.
- **Amélioration** : `LogBuild` qui ne fait rien si `!Debugger.IsAttached` ou un flag `EnableBuildLog`, ou wrapper dans `#if DEBUG`.

### 2.3 Constantes et chaînes dupliquées

- Répétition de `"2.2.0"` en commentaires : déjà centralisé dans `DiagnosticSnapshot.CURRENT_SCHEMA_VERSION` ; les commentaires peuvent référencer « CURRENT_SCHEMA_VERSION ».
- Chaînes UI répétées (ex. « Collecte insuffisante », « Score plafonné ») : les garder en dur est acceptable ; pour une vraie i18n, un fichier de ressources serait préférable (hors périmètre trim).

### 2.4 MainViewModel

- Fichier très long (~4580 lignes). Amélioration structurelle : extraire des sous-ViewModels ou des « handlers » (ex. ScanCommandHandler, SettingsHandler, ReportHistoryHandler) pour alléger la classe. Ce n’est pas du trim direct mais améliore la maintenabilité.

### 2.5 Tests et build

- Vérifier que les tests (`ContractTests`, etc.) passent après suppression de `GradeEngine` / `FinalScoreCalculator` : aucun test ne doit instancier ou mocker ces classes (déjà le cas d’après la recherche).
- Après suppression : mettre à jour `ARCH_MAP.txt` (ou le supprimer) pour refléter UDIS comme seule source de score.

---

## 3. Résumé des actions « trim » recommandées

| Priorité | Action | Gain estimé |
|----------|--------|-------------|
| Haute | Supprimer `GradeEngine.cs` (ou squelettiser) | ~600 lignes |
| Haute | Supprimer `FinalScoreCalculator.cs` | ~225 lignes |
| Moyenne | Mettre à jour ou retirer `ARCH_MAP.txt` | Clarté doc |
| Moyenne | Archiver / fusionner AUDIT_*.md dans un seul doc ou /docs | Moins de fichiers à la racine |
| Basse | Remplacer les TODOs vides par un commentaire ou une implémentation minimale | Moins de bruit |
| Basse | LogBuild conditionnel (DEBUG ou flag) dans DiagnosticSnapshotBuilder | Logs release plus légers |

**Item annulé** : aucun dans cette liste.

---

## 4. Ce qu’il ne faut pas toucher (pour l’instant)

- **ScoreV2** (HealthReport.ScoreV2, ExtractScoreV2, etc.) : encore utilisé pour le rapport (breakdown, topPenalties, BuildHealthSections). Ne pas retirer sans migrer ces usages.
- **Divergence** (GradeEngineScore, PowerShellScore, etc.) : rempli par HealthReportBuilder avec UDIS/ScoreV2 ; le modèle reste utile pour l’affichage et les logs.
- **UDIS, HealthReportBuilder, DiagnosticSnapshotBuilder, TryMapSection, validation Build()** : cœur du correctif récent ; pas de trim ici.
