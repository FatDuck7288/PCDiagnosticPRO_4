Objectif : produire une review exhaustive orientée production, pas juste des micro détails.

Portée : considérer l’ensemble du repo, mais prioriser les zones critiques listées ci-dessous.

Format attendu : sections claires, liste des risques, et recommandations actionnables.

1) Priorités absolues (bloquants release)

Bugs logiques ou états impossibles

Exceptions non gérées / crash potentiel

Concurrence / deadlocks / UI freeze

Gestion des timers, cancellation tokens, async await

Accès disque réseau base de données : timeouts, retries, fallback

Sécurité : injections, chemins, permissions, secrets, logs sensibles

Données : incohérences, mapping incomplet, champs vides dans le rapport final

2) WPF C# .NET 8 (si applicable dans le repo)

MVVM : séparation View ViewModel Model, pas de logique métier dans la View

Binding : erreurs de binding, modes TwoWay, UpdateSourceTrigger

Performance UI : opérations lourdes sur thread UI, progress reporting

Gestion des ressources : IDisposable, streams, process handles

Robustesse : guards sur null, validation, états d’erreur

3) PowerShell collector (si applicable)

Compatibilité PowerShell 5.1 Desktop

Best effort : aucune section ne doit stopper le scan

JSON : cohérence des clés, types stables, valeurs par défaut si collecte échoue

Commandes : erreurs silencieuses contrôlées, timeouts, permissions

Vérifier les métriques critiques : VRAM RAM CPU DISK réseau (cohérence)

4) Exactitude des chiffres collectés

Identifier les points où une métrique peut être fausse (ex VRAM)

Proposer la source Windows la plus fiable (API, WMI, PerfCounters)

Mentionner toute divergence possible entre outils (Task Manager vs API)

5) Sortie attendue de la review

Donner :

Liste des 10 risques majeurs classés par gravité

Suggestions de correctifs (sans refactor massif)

Endroits précis (fichiers classes méthodes) à modifier

Tests recommandés (unitaires ou manuels) pour valider les correctifs

6) Règles

Ne pas recommander de réécriture globale

Préférer des corrections ciblées

Si une recommandation change le comportement, le signaler clairement
