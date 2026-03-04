# Diagnostic Governance Improvements

Schema version: 2.2.0 | .NET 8 WPF

## 1. PS Normalization and psCoverage

**Files modified:**
- `Models/DiagnosticSnapshot.cs` — Added `PsCoverage` class and `PsCoverage?` property on `DiagnosticSnapshot`
- `Services/DiagnosticSnapshotBuilder.cs` — Added section tracking and sentinel metric creation

**What changed:**

The 10 mandatory metric domains (`os`, `memory`, `security`, `stability`, `storage`, `network`, `updates`, `startup`, `devices`, `boot`) are now explicitly tracked during PS section mapping.

- `TryMapSection()` records each section name to `_mappedPsSections` on success or `_unmappedPsSections` on failure.
- At `Build()` time, any mandatory domain missing from `Metrics` receives a **sentinel metric** with `available=false` and an explicit `reason` (e.g., `ps_section_os_not_collected`).
- A `PsCoverage` object is populated on the snapshot with:
  - `TotalExpectedSections` (always 10)
  - `MappedSections` / `MissingSections` counts
  - `CoveragePercent` (mapped / total * 100)
  - `MappedSectionNames` / `MissingSectionNames` lists
  - `UnmappedPsSections` — PS sections present in the JSON but with no metric mapping

**Contract:** Every mandatory domain will always have at least one metric entry. No implicit nulls.

---

## 2. Coverage Score in UI

**Files modified:**
- `ViewModels/MainViewModel.cs` — Added `_lastDiagnosticSnapshot` field and 5 coverage properties
- `MainWindow.xaml` — Added coverage display and warning TextBlocks

**New ViewModel properties:**

| Property | Type | Description |
|---|---|---|
| `CoveragePercent` | `double` | From `CollectionQuality.CoveragePercent` |
| `CoverageQualityLabel` | `string` | `"FULL"` (>=90), `"PARTIAL"` (>=50), `"LOW"` (<50) |
| `CoverageDisplay` | `string` | e.g., `"Collecte: 72% / qualite: PARTIAL"` |
| `IsCoverageLow` | `bool` | True when `CoveragePercent < 70` |
| `CoverageLowWarning` | `string` | Warning text shown when coverage is low |

**UI display:** Coverage label appears below the confidence badge in the score panel. A warning in orange appears when `IsCoverageLow` is true.

---

## 3. IT-Only Policy Gate

**Files created:**
- `Services/ItPolicyGate.cs` — Static service for IT-only policy validation

**Files modified:**
- `Models/UdisReport.cs` — Added `EvidencePaths` to `DiagnosticFinding`, added `PolicyResult` class
- `Services/DiagnosticFindingsBuilder.cs` — Calls `ItPolicyGate.ApplyPolicy()` after generating findings

**How it works:**

`ItPolicyGate` enforces two rules on every `DiagnosticFinding`:

1. **Domain whitelist:** `IssueType` must match one of the allowed IT domains (OS, Memory, Security, Stability, Storage, Network, Updates, Startup, Devices, Boot, CPU, GPU, Drivers, Performance, Temperature, CollectorError, WindowsUpdate). Any finding outside this whitelist is **rejected**.

2. **Evidence requirement:** If `AutoFixPossible=true` but `EvidencePaths` is empty, the finding is **downgraded** — `AutoFixPossible` is set to `false`, `RiskLevel` is set to `"Low"`, and `SuggestedAction` is prefixed with `[SUGGEST-ONLY]`.

**Integration:** `ItPolicyGate.ApplyPolicy()` is called at the end of `DiagnosticFindingsBuilder.Build()`, after all findings have been generated from HealthReport, PowerShell JSON, and collector errors.

**Public API:**
```csharp
// Validate a single finding
PolicyResult result = ItPolicyGate.ValidateFindingWithPolicy(finding);

// Apply policy to a list (filter + downgrade)
List<DiagnosticFinding> validated = ItPolicyGate.ApplyPolicy(findings);
```

---

## 4. AutoFix Evidence Requirements

**Files modified:**
- `Services/AutoFixReadinessService.cs` — Added `EvidencePaths` to `RemediationItem`, added evidence sweep in `Evaluate()`

**What changed:**

- `RemediationItem` now has a `List<string> EvidencePaths` field, populated from the source finding's evidence paths.
- After `ClassifyIssues()` runs, an **evidence sweep** iterates all `Fixable` items:
  - If `EvidencePaths` is empty, the item is moved from `Fixable` to `SuggestOnly`
  - `Actionability` is changed to `SuggestOnly`
  - `SafetyNote` is set to `"Pas de preuve explicite — suggestion uniquement"`
- The sweep is logged for traceability.

**Consequence:** No finding can reach `Fixable` (auto-fix) status without explicit evidence paths linking it to source data.

---

## Files Summary

| File | Action | Description |
|---|---|---|
| `Models/DiagnosticSnapshot.cs` | Modified | Added `PsCoverage` class and property |
| `Models/UdisReport.cs` | Modified | Added `EvidencePaths` to `DiagnosticFinding`, added `PolicyResult` |
| `Services/DiagnosticSnapshotBuilder.cs` | Modified | PS coverage tracking, sentinel metrics |
| `Services/ItPolicyGate.cs` | Created | IT-only policy validation service |
| `Services/DiagnosticFindingsBuilder.cs` | Modified | Integrated ItPolicyGate |
| `Services/AutoFixReadinessService.cs` | Modified | Evidence gate on RemediationItem |
| `ViewModels/MainViewModel.cs` | Modified | Coverage properties + snapshot reference |
| `MainWindow.xaml` | Modified | Coverage display in score panel |
