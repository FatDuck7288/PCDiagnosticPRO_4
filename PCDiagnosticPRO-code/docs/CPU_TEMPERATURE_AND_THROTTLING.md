# Température CPU et Throttling — Méthodes et sources

## A) Température CPU

### Méthodes utilisées dans l’application

| Méthode | Fichier / composant | Déclenche un signal (Defender / pilote) ? | Disponibilité typique |
|--------|----------------------|--------------------------------------------|------------------------|
| **LibreHardwareMonitor (LHM)** | `HardwareSensorsCollector.cs` | **Oui** — pilote WinRing0 / accès bas niveau ; peut déclencher des alertes sécurité | Très bonne sur PC de bureau / gaming si « Surveillance matérielle » activée |
| **WMI MSAcpi_ThermalZoneTemperature** | `WmiThermalZoneFallback.cs` | **Non** — API Windows native, pas de pilote tiers | Souvent vide sur cartes gaming / certains BIOS |
| **WMI Win32_TemperatureProbe** | `WmiThermalZoneFallback.cs` | **Non** — idem | Peu de machines exposent ce capteur |
| **WMI Win32_PerfFormattedData_Counters_ThermalZoneInformation** | `WmiThermalZoneFallback.cs` | **Non** — Windows 10+ | Variable selon matériel |
| **HWiNFO Shared Memory** | `WmiThermalZoneFallback.cs` | **Non** — lecture seule d’un segment partagé si HWiNFO64 tourne (Sensors Only) | Si l’utilisateur lance HWiNFO64 en parallèle |
| **PowerShell / Get-Counter** | Script PS | **Non** — mais Windows **n’expose pas** la température CPU dans les compteurs de performance | N/A (pas de compteur CPU temp sous Windows) |

### Méthodes qui ne déclenchent aucun signal

- **WMI uniquement** : `MSAcpi_ThermalZoneTemperature`, `Win32_TemperatureProbe`, `ThermalZoneInformation`. Aucun pilote tiers, aucune alerte Defender.
- **HWiNFO Shared Memory** : lecture seule ; pas d’injection de code ni de pilote par notre app.

### Ordre de priorité dans le code (mode sécurisé — pas de LHM/WinRing0)

1. **HWiNFO Shared Memory** (en premier) : si HWiNFO64 est lancé avec « Shared Memory Support », lecture de la température CPU depuis le segment partagé. **Aucune alerte Defender** (notre app ne charge aucun pilote).
2. **WMI** : MSAcpi_ThermalZoneTemperature, Win32_TemperatureProbe, ThermalZoneInformation — souvent vide sur PC de bureau.
3. Si tout échoue : message invitant l’utilisateur à lancer HWiNFO64 (Sensors) avec Shared Memory pour obtenir la température sans alerte.

### Si C# et PowerShell ne peuvent pas fournir la température

- **Option 1** : L’utilisateur active « Surveillance matérielle » (LHM) dans les paramètres (peut déclencher une alerte selon la politique).
- **Option 2** : Lancer **HWiNFO64** en mode « Sensors Only » avec « Shared Memory » activé ; notre app lit la température depuis le segment partagé (aucun signal).
- **Option 3** : Vérifier le BIOS : activer **ACPI Thermal Zone** si disponible (améliore les chances que WMI expose une zone thermique).

---

## B) Throttling processeur

### Sources des informations

| Source | Fichier / composant | Données |
|--------|----------------------|--------|
| **Diagnostic signals (C#)** | `CpuThrottleCollector.cs` | EventLog Kernel-Processor-Power (ID 37 = limite firmware, ID 34 = thermique), compteur « % of Maximum Frequency », fréquence actuelle |
| **Event logs détaillés (fallback)** | `EventLogDetailedCollector.cs` + `ComprehensiveEvidenceExtractor.cs` | Comptage des événements Kernel-Processor-Power 37/34 dans `event_logs_detailed` lorsque `diagnostic_signals` est absent |

### Collecte C# (CpuThrottleCollector)

- **EventLog** : `Microsoft-Windows-Kernel-Processor-Power`, EventID **37** (firmware limit), **34** (thermal throttle).
- **Compteurs de performance** : « Processor Information » → « % of Maximum Frequency », « Processor Frequency ».
- Métriques exposées : `ThrottlingEventCount7d`, `ThrottlingEventCount30d`, `ThermalThrottleCount`, `PowerLimitCount`, `PercentOfMaxFreqAvg`, `PercentOfMaxFreqMin`, `ThrottleSuspected`.

### Fallback event_logs_detailed

- `EventLogDetailedCollector` inclut désormais les événements **Kernel-Processor-Power (ID 37 et 34)** dans la collecte « stabilité » (30 jours).
- Si `diagnostic_signals` est absent (échec des signaux ou rapport sans C#), la section CPU utilise `event_logs_detailed` pour afficher « Throttling : Oui (N événement(s) - event_logs) » ou « Non détecté », au lieu de « Inconnu ».
