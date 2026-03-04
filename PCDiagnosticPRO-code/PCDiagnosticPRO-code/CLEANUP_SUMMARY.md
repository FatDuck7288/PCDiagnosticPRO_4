# Résumé nettoyage et optimisation — PC Diagnostic Pro

## 1. Liste d’actions par impact

### High
| Action | Fichiers | Preuve / justification |
|--------|----------|------------------------|
| **Suppression code mort** | `Services/PerformanceScoreCalculator.cs`, `Services/AutoFixReadinessService.cs`, `Services/SecurityTransparencyHelper.cs` | Aucune référence dans la codebase (grep sur noms de classes et méthodes publiques). Score/readiness/transparence sécurité gérés par `UnifiedDiagnosticScoreEngine`, `DiagnosticFindingsBuilder.EvaluateSafetyGate` et `UnifiedReportBuilder.BuildSection10_Securite`. |

### Med
| Action | Fichiers | Preuve / justification |
|--------|----------|------------------------|
| **Éviter double lecture JSON** | `Services/UnifiedReportBuilder.cs` | `BuildUnifiedReportAsync` lisait `combinedJsonPath` une fois pour construire le rapport puis une seconde fois dans `ValidateReportCompletenessAsync`. On passe maintenant le `JsonElement? combinedRoot` déjà chargé (clone) pour la validation, et on garde `combinedJsonPath` pour `ValidateUnifiedReportNonBlocking`. |

### Low
| Action | Fichiers | Preuve / justification |
|--------|----------|------------------------|
| **Mise à jour ARCH_MAP** | `ARCH_MAP.txt` | Mention des 3 services supprimés pour cohérence doc. |

---

## 2. Changements appliqués (par “commit” logique)

### A) Dead code removal
- **Supprimé** : `Services/PerformanceScoreCalculator.cs`  
  - Aucun appel à `PerformanceScoreCalculator.Calculate` ni à `TableVersion` (la version utilisée partout est `PerformanceEvaluationEngine.TableVersion`).
- **Supprimé** : `Services/AutoFixReadinessService.cs`  
  - Aucun appel à `Evaluate` ni à `WriteTxtSection`. La gate AutoFix est gérée par `DiagnosticFindingsBuilder.EvaluateSafetyGate` et `UnifiedDiagnosticScoreEngine`.
- **Supprimé** : `Services/SecurityTransparencyHelper.cs`  
  - Aucun appel à `ParseSecurityData` ni `WriteSecurityTransparencySection`. La section Sécurité du rapport TXT est construite dans `UnifiedReportBuilder.BuildSection10_Securite` sans ce helper.

### B) Optimisation perf
- **UnifiedReportBuilder.cs**
  - `ValidateReportCompletenessAsync` ne lit plus le fichier JSON une seconde fois.
  - Signature : `ValidateReportCompletenessAsync(string unifiedContent, string? psTxtPath, JsonElement? combinedRoot, string combinedJsonPath)`.
  - Appel : `await ValidateReportCompletenessAsync(sb.ToString(), originalTxtPath, combinedRoot, combinedJsonPath)`.
  - Utilisation de `combinedRoot` (déjà cloné) pour la validation ; `combinedJsonPath` conservé pour `SelfTestRunner.ValidateUnifiedReportNonBlocking`.
  - Retour : `Task` (plus `async`), avec `return Task.CompletedTask` en fin de méthode.

### C) Cleanup / doc
- **ARCH_MAP.txt** : ajout d’une note listant les 3 services supprimés et renvoi à ce document.

---

## 3. Avant / après

| Métrique | Avant | Après |
|----------|--------|--------|
| Fichiers .cs (source, hors obj) | +3 | −3 (PerformanceScoreCalculator, AutoFixReadinessService, SecurityTransparencyHelper) |
| Lectures disque pour génération rapport unifié | 2× le JSON combiné | 1× le JSON combiné |
| API publiques | Inchangées | Inchangées (BuildUnifiedReportAsync, routes, schémas, formats de réponses identiques) |

---

## 4. Risques potentiels

- **Réintégration future** : Si un besoin “AutoFix Readiness” détaillé ou “Security Transparency” dédié revient, il faudra réimplémenter ou réintroduire depuis l’historique Git (les fichiers ont été supprimés, pas commentés).
- **Build** : Aucune référence aux types supprimés ; la solution compile. En revanche, si `PCDiagnosticPro.exe` est en cours d’exécution, la build peut échouer (fichier verrouillé) : fermer l’application avant de builder.

---

## 5. Comment vérifier

1. **Build**  
   Fermer toute instance de PCDiagnosticPro, puis :  
   `dotnet build` (ou build depuis l’IDE). Aucune erreur de compilation attendue.

2. **Tests existants**  
   Exécuter les tests du projet (ex. `dotnet test` ou tests dans `Tests/`) pour s’assurer qu’aucune régression sur le flux Score / Rapport / Validation.

3. **Comportement observable**  
   - Lancer un scan complet, générer le rapport unifié TXT.  
   - Vérifier que le score global, le grade, la section Sécurité et la validation (log “[VALIDATION]”) sont identiques à avant.  
   - Vérifier que le rapport TXT contient bien les 15+ sections attendues.

4. **Perf (optionnel)**  
   Comparer le temps de génération du rapport unifié avant/après sur un même `scan_result_combined.json` : on attend une légère réduction (une lecture en moins).

---

## 6. Check-list de validation

- [ ] `dotnet build` réussit (après fermeture de l’exe si besoin).
- [ ] Tests unitaires / intégration existants passent.
- [ ] Génération du rapport unifié TXT : contenu et sections identiques.
- [ ] Logs de validation ([VALIDATION] ✅ / ⚠️) cohérents.
- [ ] Aucune régression sur l’UI (score, grade, AutoFixAllowed, thème, etc.).
