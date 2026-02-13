# Performance dataset schema and configuration

This document describes the JSON schema expected for the **external performance dataset** (remote URL or API). Any site or service can expose a compatible "database" so the app uses it as the single source of truth for scenario requirements (min / recommended / ultra).

## 1. Where to configure the app

- **Config file path**: `%LocalAppData%\PCDiagnosticPro\config.json`
- **Properties**:
  - **`PerformanceDatasetUrl`** (string): HTTPS URL of the dataset JSON (or an API that returns this JSON). Required for remote loading. Example: `"https://example.com/api/performance-dataset.json"`.
  - **`PerformanceDatasetMode`** (string, optional):
    - **`RequireExternal`** (default): If the URL is configured but the dataset cannot be loaded or validated, scoring is marked unavailable (no silent fallback).
    - **`AllowFallbackEmbedded`**: If the remote dataset fails, the app uses an embedded fallback dataset so scores remain visible, with a clear label indicating fallback mode.

Example `config.json`:

```json
{
  "PerformanceDatasetUrl": "https://example.com/performance-dataset.json",
  "PerformanceDatasetMode": "AllowFallbackEmbedded"
}
```

- **Cache**: The app caches the dataset under `%LocalAppData%\PCDiagnosticPro\cache\` (TTL 7 days, grace period 30 days for RequireExternal). ETag / If-None-Match is supported for conditional requests.
- **Update without recompilation**: When you update the JSON (or the API response) on the server, the app retrieves the new version on the next load (after TTL expiry or after calling `PerformanceDatasetLoader.Invalidate()` and re-evaluating). No app recompile is required.

## 2. Required JSON schema for the dataset

The root object must be valid JSON and contain at least:

| Property           | Type   | Description |
|--------------------|--------|-------------|
| **DatasetVersion** | string | Semantic version of the dataset (e.g. `"1.0.0"`). |
| **PublishedAt**    | string | ISO 8601 publication timestamp (e.g. `"2025-02-12T00:00:00Z"`). |

### 2.1 MarketBenchmarks (required for scenario scoring from requirements)

**`MarketBenchmarks`** must be an object whose keys are the **scenario IDs** and whose values are **MarketBenchmark** objects. When present and non-empty, the app uses **only** these requirements for scoring (no hardcoded thresholds).

**Scenario IDs** (exactly these strings):

| ID                | Display name (example)     |
|-------------------|----------------------------|
| `office`          | Office / Browsing          |
| `multitasking`    | Multitasking               |
| `gaming_1080p`    | Gaming (1080p)             |
| `gaming_1440p`    | Gaming (1440p)             |
| `gaming_4k`       | Gaming (4K)                |
| `4k_editing`      | 4K Video Editing           |
| `streaming_gaming`| Streaming + Gaming         |
| `vms`             | Virtual Machines           |
| `ai_inference`    | AI (basic inference)       |

Each **MarketBenchmark** object:

| Property        | Type   | Description |
|-----------------|--------|-------------|
| **Label**       | string | Localized display name for the scenario. |
| **Description** | string | What the scenario measures (e.g. "Jeux AAA 1440p 60 FPS ultra"). |
| **Requirements** | object | **ScenarioRequirements** (see below). |

**ScenarioRequirements** (min / recommended / ultra + weights):

| Property                   | Type   | Description |
|----------------------------|--------|-------------|
| MinCpuCores, MinCpuThreads | int    | Minimum CPU (below = score 0–39). |
| MinRamGb                   | double | Minimum RAM (GB). |
| MinGpuVramMb               | double | Minimum GPU VRAM (MB). |
| MinGpuTierOrder            | int    | GPU tier order (1=Entry … 5=Workstation). |
| MinStorageTier             | int    | Storage tier (1=HDD, 2=SATA SSD, 4=NVMe). |
| RecommendedCpuCores, …     | int/double | Same names with "Recommended" prefix (score ~70). |
| UltraCpuCores, …           | int/double | Same names with "Ultra" prefix (score 100). |
| WeightCpu, WeightGpu, WeightRam, WeightStorage | double | Component weights (should sum to ~1.0). Defaults: 0.25, 0.35, 0.25, 0.15. |

Scoring interpolates: below min → 0–39, at min → 40, at recommended → 70, at/above ultra → 100. Final score = weighted average of component scores.

### 2.2 Optional root properties

- **ScenarioRules**: Per-scenario base + bonus rules (used when a scenario has no MarketBenchmark entry).
- **ClassificationThresholds**: `NotRecommendedBelow`, `AcceptableBelow`, `GoodBelow` (defaults 40, 55, 70).
- **Floors**: `HighEndCondition` + `ScenarioFloors` for minimum scores on high-end configs.
- **CpuPatterns**, **GpuPatterns**: Name-to-tier rules for tier resolution.
- **CpuHeuristicRules**, **GpuVramThresholds**, **RamTierRules**, **StorageTierRules**: Tier thresholds when patterns do not match.

See `PCDiagnosticPro.Models.PerformanceDataset` and related types in the codebase for the full C# contract.

## 3. Example minimal dataset (MarketBenchmarks only)

```json
{
  "DatasetVersion": "1.0.0",
  "PublishedAt": "2025-02-12T00:00:00Z",
  "MarketBenchmarks": {
    "office": {
      "Label": "Office / Browsing",
      "Description": "Bureautique et navigation",
      "Requirements": {
        "MinCpuCores": 2,
        "MinCpuThreads": 4,
        "MinRamGb": 4,
        "MinGpuVramMb": 0,
        "MinGpuTierOrder": 0,
        "MinStorageTier": 1,
        "RecommendedCpuCores": 4,
        "RecommendedCpuThreads": 8,
        "RecommendedRamGb": 8,
        "RecommendedGpuVramMb": 0,
        "RecommendedGpuTierOrder": 1,
        "RecommendedStorageTier": 2,
        "UltraCpuCores": 6,
        "UltraCpuThreads": 12,
        "UltraRamGb": 16,
        "UltraGpuVramMb": 0,
        "UltraGpuTierOrder": 2,
        "UltraStorageTier": 4,
        "WeightCpu": 0.3,
        "WeightGpu": 0.1,
        "WeightRam": 0.4,
        "WeightStorage": 0.2
      }
    }
  }
}
```

(Add the other seven scenario IDs with their own `Label`, `Description`, and `Requirements` for full coverage.)

## 4. Serving the dataset

- Expose the JSON over **HTTPS** (the app rejects non-HTTPS URLs).
- Either serve a static JSON file or an API endpoint that returns the same schema.
- When requirements change (e.g. new games, heavier 4K editing), update the file or API response; the app will use the new version after cache expiry or manual refresh.
