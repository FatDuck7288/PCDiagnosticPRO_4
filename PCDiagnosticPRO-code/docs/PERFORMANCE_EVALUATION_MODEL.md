# Performance Evaluation Model

The Performance Evaluation Engine is **offline and deterministic**. No external API; all logic is in code with documented thresholds.

## 1. Hardware tiers

| Component | Entry | Mid-range | Upper Mid | High-end |
|-----------|--------|-----------|-----------|----------|
| **CPU** | &lt;4 cores/threads | 6–8 cores | 10–16 threads | 12+ cores / 24+ threads (or Ryzen 9 / i9 / Xeon) |
| **GPU** | &lt;2 GB VRAM, iGPU | 2–4 GB VRAM | 4–8 GB VRAM | 8+ GB VRAM (or RTX 40 / RX 7) |
| **RAM** | 8 GB | 16 GB | — | 32 GB+ |
| **Storage** | HDD | SATA SSD | — | NVMe |

Tier resolution: `PerformanceTierTable.Resolve*Tier(...)` in `Services/PerformanceTierTable.cs`. Name-based overrides (e.g. Ryzen 7 → Upper Mid) apply when model string is available.

## 2. Usage scenario scoring (0–100)

Eight scenarios, each with a **score** and **classification**:

- **Not Recommended**: score &lt; 40  
- **Acceptable**: 40–55  
- **Good**: 56–70  
- **Excellent**: &gt; 70  

Formulas are in `UsageScenarioScorer.cs` (e.g. 1080p Gaming: base 40 + GPU tier + VRAM ≥6 GB + RAM ≥16 GB). Scores are clamped 0–100.

## 3. Bottleneck rules

- **HDD** with strong CPU/GPU → Storage primary; upgrade 1 = Storage.  
- **Strong GPU + 8 GB RAM** → RAM primary.  
- **NVMe + strong CPU + weak GPU** → GPU primary.  
- **Weak GPU** with decent CPU/RAM → GPU primary.  
- **Weak CPU** with decent GPU/RAM → CPU primary.  
- **Weak RAM** → RAM primary.  

Upgrade priority 1–3 is a short ordered list with a reason per component. See `BottleneckAnalyzer.cs`.

## 4. System category (verdict)

- **Entry-Level**: mostly Entry tiers.  
- **Mid-Range**: at least one Mid or mixed Entry/Mid.  
- **Upper Mid**: Mid/Upper Mid mix (e.g. CPU Upper Mid + GPU Mid).  
- **High-End**: High-end CPU+GPU+RAM or equivalent.  
- **Workstation Grade**: 12+ threads, 32 GB+ RAM, strong GPU.  

Realistic summary text is built from category + scenario classifications + primary limiting factor. See `PerformanceVerdictBuilder.cs`.

## 5. Single score (backward compatibility)

The 0–100 “Score performance” is the **average of the eight scenario scores**. Used for section border color and legacy UI.

## 6. Data sources

- **CPU / RAM / GPU / Storage**: `scan_powershell.sections` (CPU, Memory, GPU, Storage) and/or `diagnostic_snapshot.machine` + `diagnostic_snapshot.metrics`, and/or `HardwareSensorsResult` (C# sensors for GPU name/VRAM).  
- **Storage kind** (HDD / SATA SSD / NVMe): from `Storage.data.physicalDisks[].type` and `interface`.  
- **RAM speed / dual channel**: from `Memory.data.modules[]` (speedMHz, count ≥ 2).  

All of this is read in `HardwareProfileBuilder.Build(...)`.
