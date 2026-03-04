# ADR: Local LLM + Controlled AutoFix for Chat & Support

Date: 2026-02-24
Status: Accepted

## Context

PCDiagnosticPro needs an offline "Chat & Support IA" flow in the existing Chat navigation:
- local LLM (Phi-3 Mini Instruct GGUF) for run-aware chat and script drafting,
- deterministic + AI action plan split (manual vs AutoFix),
- one AutoFix button with strict safety gates,
- controlled PowerShell execution with full logs and reboot prompt.

No new navigation section is allowed.

## Decision

1. Runtime and model
- Runtime: llama.cpp through LLamaSharp (.NET 8, Windows).
- Default model: `Phi-3-mini-4k-instruct.gguf`.
- Model path is loaded from `config/ai_settings.json` (no hardcoded `C:\Models\...`).

2. Model installation UX
- Manual install is first-class:
  - banner "LLM not installed" in Chat view,
  - "Choose model .gguf",
  - "Open models folder",
  - "Install guide".
- Validation before save/load:
  - file exists,
  - `.gguf` extension,
  - size > 0,
  - readable,
  - optional checksum.
- App remains usable in degraded mode without model.

3. Safety and pipeline
- 3-agent pipeline:
  - ScriptBuilderAgent,
  - CodeReviewerAgent,
  - TesterJudgeAgent (static only, never executes).
- AutoFix gate is score-agnostic:
  - score remains informational only,
  - hard block on dangerous verdict (`REFUSE`), blocked commands, or unauthorized capabilities,
  - AutoFix availability depends on detected problems, not on score thresholds.

4. AutoFix execution model
- AutoFix runs only after explicit user click + confirmation popup.
- Working directory:
  `%LocalAppData%\PCDiagnosticPro\AiAutofix\{runId}\`
- Generated artifacts:
  - `Autofix.ps1`,
  - `ExecutionLog.txt`,
  - `Transcript.txt`,
  - `AiRunReport.json` (updated post-execution).
- PowerShell process:
  - `-NoProfile -ExecutionPolicy Bypass`.
  - rationale:
    - `-NoProfile` avoids user-profile side effects,
    - `Bypass` prevents local policy friction for this controlled run path.
- Elevation:
  - if script declares admin requirement, execution requests UAC (`Verb=runas`).

5. Reboot behavior
- Reboot requirement is detected from:
  - script/output patterns,
  - exit code markers,
  - pending reboot registry indicators.
- UI prompts:
  - "Restart now" / "Later".
- "Restart now" performs immediate reboot after final confirmation.

## Consequences

- Freemium and transparency are preserved:
  - no hidden execution,
  - no silent model download,
  - no "AI repairs everything" claim.
- Chat view is still useful without model (run loading + deterministic plan + install guidance).
- All critical automation is auditable through run-scoped logs and report files.
