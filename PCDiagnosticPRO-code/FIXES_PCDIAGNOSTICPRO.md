# CORRECTIFS À IMPLÉMENTER

## 1. Speed Test - Icône Terre
- Fichier: MainWindow.xaml
- Ligne ~700: Ajuster Width/Height et VerticalAlignment

## 2. Live Feed - Sections
- Fichier: MainViewModel.cs  
- Vérifier que les messages de phase sont bien injectés dans le flux

## 3. Barre de Progression
- Fichier: MainViewModel.cs
- Ligne ~6700: Ajuster les poids de progression

## 4. Points de Restauration
- Fichier: RestorePointsWindow.xaml.cs
- Charger les données temps réel au lieu du cache JSON

## 5. Boutons (i) Kernel Power
- Fichier: MainWindow.xaml.cs
- Ajouter un paramètre pour router vers la bonne fenêtre

## 6. Tableaux Rapport Intégral
- Fichier: Views/FullReportView.xaml
- Ajouter HorizontalScrollBarVisibility et copier-coller
