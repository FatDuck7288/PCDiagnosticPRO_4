# Analyse du Live Feed (Scan Health page) + Prompt prêt pour Cursor

## 1) Ce qui défile actuellement dans le Live Feed

### Structure UI du feed
- Le bloc **Live Feed** est en haut de la page Healthcheck (ligne dédiée, hauteur 150).
- Il est organisé en 3 zones:
  1. **En-tête**: `📡 {LiveFeedLabel}` + **section courante** (`CurrentSectionDisplay`) animée en pulsation pendant le scan.
  2. **Barre de progression linéaire**: progression globale du scan.
  3. **Liste défilante structurée**: une `ListBox` alimentée par `FilteredLiveFeedItems`.

### Colonnes affichées dans chaque ligne
Chaque entrée de feed est rendue avec les colonnes suivantes:
1. **Timestamp** (HH:mm:ss)
2. **Badge** (RUN / OK / ERR / WARN / SEC / INFO)
3. **Section**
4. **Séparateur visuel**
5. **Détail message**
6. **Pourcentage** (visible uniquement pour les entrées de type PROGRESS)

### Types de messages et mapping visuel
Le parsing reconnaît le format:
- `[PROGRESS] Section | current/total | pct%`
- `[STATUS] Section | message`
- `[DONE] Section | message`
- `[ERROR] Section | message`
- `[WARN] Section | message`
- `[INFO] Section | message`
- `[SECTION] Section | message`

Le badge/état associé:
- `PROGRESS` / `STATUS` → `RUN`
- `DONE` → `OK`
- `ERROR` → `ERR`
- `WARN` → `WARN`
- `SECTION` → `SEC`
- sinon → `INFO`

### Emojis et événements actuellement visibles
Le feed reçoit un mix de:
- Messages localisés de phases globales:
  - `▶` démarrage PowerShell
  - `🔧` capteurs matériels
  - `📊` compteurs de performance
  - `📡` signaux diagnostics
  - `📈` télémétrie processus
  - `🌐` diagnostic réseau
  - `📄` génération rapport
  - `✅` fins de phases
- Messages fallback/non structurés:
  - `📍` changement d’étape
  - `✅`, `❌`, `⚠️`, `⏹`, `↑` (notamment speed test réseau)

### Ordre/flux réel
- Les nouvelles entrées sont insérées en haut (`Insert(0, entry)`), donc on voit les plus récentes en premier.
- Limites de conservation:
  - 100 lignes legacy string
  - 200 lignes enrichies (`LiveFeedEntries`)
- Filtrage possible côté VM (`Tout`, `Erreurs`, `Avertissements`, `Important`, `Progression`) mais options exposées UI actuellement limitées à la liste prévue.

---

## 2) Ce qui peut être ajouté pour un défilement plus pertinent (au-delà du %)

### A. Ajouter de la valeur diagnostic (pas juste du “progress”)
1. **Durées par section**
   - Exemple: `Inventaire système terminé en 12.4s`.
   - Permet de détecter les ralentissements anormaux.

2. **Compteurs de résultat par section**
   - Exemple: `Drivers: 2 warnings, 0 errors, 14 OK`.
   - Donne une lecture actionnable immédiate.

3. **Top anomalies en direct**
   - Exemple: `CPU throttle détecté (p95=18%)` ou `DPC élevé (p95=2500µs)`.
   - Afficher un niveau de sévérité + mini recommandation.

4. **Source de donnée + confiance**
   - Exemple: `Température CPU: WMI fallback (confiance moyenne)`.
   - Évite les faux signaux lorsque certaines APIs tombent en fallback.

5. **Événements de qualité de collecte**
   - Exemple: `Donnée indisponible: SMART NVMe (driver bloqué)`.
   - Très utile pour expliquer les “trous” de rapport.

### B. Améliorer la lisibilité du défilement
6. **Regrouper/collapser les répétitions**
   - Remplacer 15 lignes semblables par `Capteurs: 15 lectures en cours…` puis update incrémentale.

7. **Pins “Important”**
   - Garder en haut une mini-zone sticky pour 3 à 5 événements critiques/warnings.

8. **Tag de catégorie**
   - Ajouter une catégorie explicite (`CPU`, `GPU`, `Disk`, `Network`, `Security`, `Drivers`).

9. **Actions recommandées inline**
   - Exemple: `Driver audio obsolète → proposer “Ouvrir détails drivers”`.

10. **Résumé dynamique toutes les N secondes**
    - Exemple: `Résumé (t+45s): 3 sections finies, 2 warnings, 0 erreurs`.

### C. Instrumentation dev/ops
11. **Event IDs stables** pour éviter les doublons.
12. **Niveaux de verbosité** (`Normal`, `Expert`) pour adapter le bruit.
13. **Export du feed brut structuré** (JSON lines) pour debug et support.

---

## 3) Prompt prêt à coller dans Cursor

```text
Contexte:
Tu travailles sur la page Scan Health de PCDiagnosticPro (WPF, MainWindow.xaml + MainViewModel.cs).
Le live feed actuel affiche déjà timestamps, badges, section, détail et % pour PROGRESS.
Je veux enrichir le défilement pour qu’il soit plus pertinent diagnostic (pas seulement progression).

Objectif:
Améliorer le live feed sans casser l’UI existante.

Demandes:
1) Analyse l’existant et conserve le rendering actuel (colonnes + badges + couleurs).
2) Ajoute des événements structurés de type:
   - [METRIC] Section | key=value | severity
   - [SUMMARY] Section | ok/warn/error counts
   - [ACTION] Section | recommandation courte
   - [TIMING] Section | durationMs=...
3) Implémente un système anti-spam:
   - déduplication sur une fenêtre glissante (ex: 5s)
   - agrégation des messages répétitifs (compteur xN)
4) Ajoute une catégorie explicite dans LiveFeedEntry (CPU/GPU/Disk/Network/Security/Drivers/System)
   + mapping visuel léger (couleur secondaire ou tag).
5) Ajoute un mode de verbosité:
   - Normal: erreurs/warnings/résumés/actions
   - Expert: inclut métriques détaillées
6) Ajoute un “sticky summary” en haut du feed:
   - sections complétées / total
   - warnings
   - erreurs
   - dernier événement critique
7) Garde compatibilité avec les messages existants:
   - parsing actuel [PROGRESS]/[STATUS]/[DONE]/[ERROR]/[WARN]/[INFO]/[SECTION]
   - fallback emojis existants (📍 🌐 ✅ ❌ ⚠️ ⏹ ↑)
8) Ajoute tests unitaires ciblés pour le parsing et la déduplication.
9) Propose un mini plan de migration en 3 étapes (safe rollout).

Contraintes:
- Ne pas dégrader les performances UI.
- Ne pas changer brutalement la structure visuelle du feed.
- Garder les labels localisés existants.

Livrables attendus:
- Patch complet (ViewModel + éventuels helpers + XAML minimal).
- Explication concise des choix.
- Checklist de validation manuelle.
```
