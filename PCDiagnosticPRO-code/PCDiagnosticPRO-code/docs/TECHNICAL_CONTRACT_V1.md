# Technical Contract V1 (`technical_contract`)

## Version
- `version`: `1.0.0`
- Node location: root of `scan_result_combined.json`

## Required fields
- `technical_contract.version`
- `technical_contract.score.sourceOfTruth`
- `technical_contract.score.isAvailable`
- `technical_contract.gpuCompleteness.state`
- `technical_contract.criticalRows[]`
- `technical_contract.legacyCompatibility`

## Semantics
- `score.sourceOfTruth` is always `UDIS`.
- `score.isAvailable=false` means no fallback UI score must be shown.
- `score.failClose` explains fail-close with:
  - reason code
  - user message
  - impact
  - action
- `criticalRows[]` stores per critical field:
  - `sectionId`, `fieldId`, `jsonPath`
  - `provenanceType` (`collecte`, `calcule`, `deduit`, `indisponible`)
  - status triplet (`label`, `reason`, `confidence`)
  - `missingDataExplained`

## Backward compatibility
- Contract is additive only.
- Existing legacy fields remain untouched.
- Legacy aliases are documented in `technical_contract.legacyCompatibility.aliases`.
- Consumers can migrate progressively by reading `technical_contract` first, then legacy fields.
