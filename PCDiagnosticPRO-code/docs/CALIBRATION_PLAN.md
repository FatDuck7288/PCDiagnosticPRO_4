# Plan de calibration – Scores de performance

## Problème

Une machine très haut de gamme (ex. 95 Go RAM, 24 Go VRAM, Ryzen 9) obtient des scores globaux ~56–61 % et Bureau ~61 %, ce qui est incohérent : ce type de configuration doit obtenir **100 % pour le bureau** et des scores élevés pour les autres tâches. Les points ne doivent être enlevés **que** lorsqu’il manque quelque chose par rapport à ce qu’exige la tâche.

## Principes de calibration

### 1. Logique « capacité à la tâche »

- Pour chaque tâche, on définit ce qu’il **faut au minimum** pour faire la tâche (min) et ce qui est **confortable / recommandé** (reco).
- **Si la machine atteint ou dépasse le niveau recommandé pour la tâche → 100 % pour cette tâche.** Aucune pénalité pour « avoir trop ».
- Les points sont retirés **uniquement** quand un composant est **en dessous** du minimum ou du recommandé (proportionnellement).
- Donc : Bureau avec 95 Go RAM, 24 Go VRAM, CPU correct → **100 % Bureau**, sans discussion.

### 2. Grille par tâche (seuils à respecter)

Les exigences ci‑dessous sont des **références pour la comparaison « est‑ce que j’ai ce qu’il faut ? »**. Une machine qui les dépasse obtient 100 % pour cette tâche.

| Tâche | Minimum (en dessous = pas recommandé) | Recommandé (atteint ou au‑dessus = 100 %) |
|-------|----------------------------------------|--------------------------------------------|
| **Bureau / Navigation** | 4 Go RAM, 2 cœurs, stockage quelconque | 8 Go RAM, 4 cœurs, SSD ou mieux. GPU non exigeant. |
| **Multitâche** | 8 Go RAM, 4 cœurs | 16 Go RAM, 8 cœurs / 16 threads. |
| **Jeu 1080p** | 8 Go RAM, 4 cœurs, 4 Go VRAM, GPU milieu de gamme | 16 Go RAM, 6 cœurs, 8 Go VRAM, GPU bon tier. |
| **Jeu 1440p** | 16 Go RAM, 6 cœurs, 8 Go VRAM | 16 Go RAM, 8 cœurs, 12 Go VRAM, GPU haut de gamme. |
| **Jeu 4K** | 16 Go RAM, 8 cœurs, 12 Go VRAM | 32 Go RAM, 8+ cœurs, 16 Go VRAM, GPU très haut de gamme. |
| **Montage 4K** | 16 Go RAM, 6 cœurs, 4 Go VRAM, SSD | 32 Go RAM, 8 cœurs, 8 Go VRAM, NVMe. |
| **Streaming + Jeu** | 16 Go RAM, 6 cœurs, 6 Go VRAM | 32 Go RAM, 8 cœurs, 8 Go VRAM. |
| **VMs** | 16 Go RAM, 4 cœurs | 32 Go RAM, 8 cœurs / 16 threads. |
| **IA (inférence)** | 16 Go RAM, 6 Go VRAM | 32 Go RAM, 12 Go VRAM. |

- **Jeu 1080p, 1440p et 4K** doivent être **trois scénarios distincts** avec des exigences croissantes et des scores différenciés (1080p ≥ 1440p ≥ 4K sur une même machine).
- Le scénario **Jeu 4K** doit être ajouté s’il n’existe pas encore.

### 3. Deux tableaux en sortie

- **Tableau 1 – Performance par tâche**  
  « Est‑ce que mon PC peut faire la tâche ? »  
  Une note 0–100 par scénario selon la grille ci‑dessus.  
  Au‑dessus du recommandé = 100 %. En dessous = score proportionnel (avec possibilité de détailler par composant).

- **Tableau 2 – Position par rapport au marché**  
  « Où se situe mon PC par rapport aux autres ? »  
  Comparaison à la consommation des utilisateurs moyens et aux machines les plus puissantes (percentiles, tier « marché », etc.).  
  Ce deuxième tableau est **distinct** du premier : il ne remplace pas la note « capacité à la tâche », il apporte un éclairage complémentaire.

### 4. Règles de cohérence des scores

- Sur une même machine, on conserve l’ordre logique :  
  **Note(Bureau) ≥ Note(Multitâche) ≥ Note(Jeu 1080p) ≥ Note(Jeu 1440p) ≥ Note(Jeu 4K) ≥ …**  
  (les tâches plus exigeantes ne doivent pas avoir une note plus haute que les tâches plus légères).

### 5. Implémentation technique (recommandations)

- **Dataset (distant ou embarqué)**  
  - Bureau : seuils « ultra » ou « recommandé » **bas** (ex. 8 Go RAM, 4 cœurs) pour qu’une machine correcte dépasse et obtienne 100 %.  
  - Jeu 1080p / 1440p / 4K : trois entrées distinctes avec exigences croissantes (4K > 1440p > 1080p).  
  - Ajouter le scénario **gaming_4k** (Jeu 4K) avec des exigences supérieures à 1440p.

- **Formule de score**  
  - Soit on garde la courbe actuelle (min → 40, reco → 70, ultra → 100) et on **abaisse les seuils « ultra » du bureau** pour que 8 Go RAM / 4 cœurs = ultra → 100 %.  
  - Soit on introduit une règle **« à partir du recommandé = 100 % »** : dès que chaque composant atteint le niveau recommandé (ou au‑dessus), le score de la tâche est plafonné à 100 %.  
  - L’extraction du profil (CPU, RAM, VRAM, stockage) doit être fiable pour que la comparaison aux seuils soit correcte (déjà visée par les corrections HardwareProfileBuilder).

- **UI / rapport**  
  - Afficher clairement :  
    - **Tableau 1** : scores par tâche (capacité à la tâche).  
    - **Tableau 2** : comparaison au marché (optionnel, « second chapitre »).

---

## Résumé des actions

1. **Recalibrer** les seuils du dataset (embarqué et schéma distant) pour que Bureau donne 100 % dès qu’on a au moins 8 Go RAM, 4 cœurs, configuration « correcte ».
2. **Différencier** Jeu 1080p, 1440p et 4K (exigences et scores distincts).
3. **Ajouter** le scénario **Jeu 4K** (gaming_4k) avec des exigences supérieures à 1440p.
4. **Séparer** dans l’affichage : tableau « performance par tâche » vs tableau « votre PC vs marché » (ce dernier en second chapitre).

Ce document sert de **référence de calibration** : les seuils et la logique « capacité à la tâche » doivent être alignés avec cette grille dans le code et dans le dataset (embarqué ou distant).
