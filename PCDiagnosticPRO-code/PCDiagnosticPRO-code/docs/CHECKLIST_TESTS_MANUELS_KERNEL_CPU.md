# Checklist des tests manuels (Kernel Power, Température CPU, Throttling)

## A) Kernel Power ID 1 et fenêtre (i)

### Déclencher ou simuler l'affichage du bouton (i)

1. **Avec événement réel** : Après un redémarrage ou une mise en veille / réveil, Windows enregistre souvent un événement Kernel Power ID 1. Lancer un scan complet, ouvrir la section **Stabilité système**, vérifier que la ligne « Kernel-Power » affiche un second (i) à gauche (icône bleue avec tooltip « Explication Kernel Power ID 1 vs 41 »).
2. **Mode dev / sample data** : Pour tester sans dépendre d’un vrai EventID 1 sur la machine, forcer temporairement `HasKernelPowerId1 = true` dans le code (ex. dans `HealthReportBuilder` après `section.HasKernelPowerId1 = ComprehensiveEvidenceExtractor.HasKernelPowerId1Present(root);` en mettant `section.HasKernelPowerId1 = true;`) puis lancer un scan et ouvrir Stabilité système.

### Vérifications

- Le (i) n’apparaît qu’à côté de la ligne **Kernel-Power** dans la section **Stabilité système** (pas dans les autres sections).
- Au clic sur ce (i), la fenêtre **KernelPowerInfoWindow** s’ouvre en modal.
- La fenêtre affiche : titre « Kernel Power (ID 1) vs (ID 41) », texte explicatif, tableau (ID 1 = Information, ID 41 = Critique), conclusion « Donc si tu vois ID 1, ce n’est normalement pas un crash », bouton **Fermer**.
- Style cyberpunk : fond bleu marin noir (#060A12), bordures bleu froid (#2AA7FF), au survol du bouton Fermer une lueur rouge (#FF2B2B) est visible.
- Le bouton Fermer ferme la fenêtre.

---

## B) Température CPU et méthode utilisée

### Vérifications

1. Lancer un scan, ouvrir la section **Processeur** dans l’écran résultats : la ligne **Température CPU** doit afficher la valeur et, entre parenthèses, la méthode utilisée (ex. « WMI MSAcpi_ThermalZone » ou « Non disponible »).
2. Ouvrir le **Rapport intégral** (bouton dédié), section **CPU** : vérifier la présence de **Source température** (méthode) et de la ligne **Méthodes de collecte température** avec le texte sur la lecture passive (aucun stress test, aucun benchmark, aucun code ne provoque de charge CPU).

---

## C) Throttling CPU dans le rapport

### Vérifications

1. Ouvrir le **Rapport intégral**, section **CPU**.
2. Vérifier la présence des lignes :
   - **Throttling détecté** : Oui / Non / Non disponible
   - **Type** : Thermique / Power limit / Indéterminé (ou combinaison)
   - **Preuves** : résumé (événements throttle, fréquence % max, etc.) ou « Aucun événement throttle récent »

Avec des événements Kernel-Processor-Power (ID 34/37) sur la machine, les comptages et le type doivent refléter les données collectées.

---

## Récapitulatif des fichiers modifiés (référence)

| Fichier | Raison |
|--------|--------|
| `EventLogDetailedCollector.cs` | Collecte Kernel Power EventID 1 (30 j) |
| `DriverStabilityCollector.cs` | QueryKernelPower1, KernelPower1Count30d dans le signal |
| `HealthReport.cs` | Propriété HasKernelPowerId1 sur HealthSection |
| `ComprehensiveEvidenceExtractor.cs` | HasKernelPowerId1Present(root) |
| `HealthReportBuilder.cs` | Remplissage HasKernelPowerId1 pour Stabilité système |
| `MainWindow.xaml` | Bouton (i) Kernel Power + converter KernelPowerInfoButtonVisibility |
| `MainWindow.xaml.cs` | OpenKernelPowerInfoWindow() |
| `KernelPowerInfoWindow.xaml` / `.xaml.cs` | Fenêtre explicative style cyberpunk |
| `Converters.cs` | KernelPowerInfoButtonVisibilityConverter |
| `FullReportBuilder.cs` | Méthodes de collecte température (lecture passive) ; Throttling détecté, Type, Preuves |
