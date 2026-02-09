# Collecte hardware 100 % user mode (sans ring0)

## Règle absolue

**Aucun driver kernel (ring0) n’est utilisé.** Le code C# ne charge pas WinRing0 ni aucun pilote vulnérable.  
`HardwareSensorsCollector.ForceUnsafeMode` est **toujours** à `false` ; le chemin LibreHardwareMonitor + WinRing0 n’est jamais exécuté.

## Backends utilisés (user mode uniquement)

| Backend | Rôle | Fichier principal |
|--------|------|--------------------|
| **WMI / CIM** | Température CPU (MSAcpi_ThermalZoneTemperature, Win32_TemperatureProbe), temp disques (MSStorageDriver_*), infos GPU | `SafeHardwareSensorsCollector`, `WmiThermalZoneFallback`, `WmiQueryRunner` |
| **PDH / Performance Counters** | Charge CPU, disque, réseau | `SafeHardwareSensorsCollector`, `PerfCounterCollector` |
| **NVML (NVIDIA)** | Temp GPU, VRAM, load GPU (user mode, pas de kernel driver) | `SafeHardwareSensorsCollector` (NvmlGpuReader) |
| **Storage API Windows** | SMART / disques via WMI (MSStorageDriver_*) | `SafeHardwareSensorsCollector.TryCollectDiskMetricsWmi` |

## Métriques par source

| Métrique | Source | Ring0 ? | Note |
|----------|--------|---------|------|
| Température CPU | WMI ThermalZone / TemperatureProbe | Non | Souvent limité ou absent selon firmware |
| Température GPU | WMI + NVML (NVIDIA) | Non | AMD/Intel : WMI ou non disponible |
| Température disques | WMI MSStorageDriver_* | Non | SMART détaillé peut être partiel → "Non disponible" si absent |
| Charge CPU | Performance Counter "Processor, % Processor Time" | Non | Toujours disponible |
| Charge GPU | Performance Counter / WMI | Non | Selon pilote |
| VRAM | NVML (NVIDIA) / WMI | Non | Partiel sur autres constructeurs |

## Comportement en cas d’indisponibilité

- Toute métrique non lisible en user mode est marquée **Unavailable** avec une raison (ex. "Température CPU non accessible sans outils tiers", "Non disponible (mode sécurisé)").
- Les valeurs sentinelles (0°C, >150°C, -1) sont neutralisées dans `HealthReportBuilder.NeutralizeSentinelValues` et dans les collecteurs ; elles ne sont pas utilisées pour pénaliser le score.
- Aucune pénalité de score n’est appliquée pour une donnée "Non disponible".

## Références code

- `Services/SafeHardwareSensorsCollector.cs` : collecte CPU/GPU/disques en user mode.
- `Services/HardwareSensorsCollector.cs` : délègue toujours à SafeHardwareSensorsCollector (`ForceUnsafeMode = false`).
- `Services/WmiThermalZoneFallback.cs` : température CPU via WMI.
- `Services/DataSanitizer.cs` : normalisation des valeurs invalides avant écriture JSON.
