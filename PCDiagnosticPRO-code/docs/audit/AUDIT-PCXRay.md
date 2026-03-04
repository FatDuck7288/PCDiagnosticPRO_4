# AUDIT PC X-Ray - Etat actuel vs cible Scan & Fix + Chat & Support

Date: 2026-02-25
Scope: stack IA locale, ChatSupport, pipeline agents, AutoFix, config/runtime.

## 1) Etat actuel vs objectifs produit

| Domaine | Etat avant | Etat apres implementation |
|---|---|---|
| Modele LLM | multi-modeles possibles (chatml/mistral/tinyllama) | verrouille a `Phi-3-mini-4k-instruct-q4.gguf` |
| Runtime | llama.cpp + branches templates multiples | runtime unique llama.cpp (LLamaSharp), prompt phi3 unique |
| Offline IA | telechargement cloud integre (HF) | telechargement cloud retire, selection locale uniquement |
| Chat/context | possible reponse generique sans run | run requis, message explicite si contexte absent |
| Agents | pipeline 3 agents lance dans l'analyse run | pipeline 3 agents declenche uniquement via `Generate AutoFix Script` |
| AutoFix | pouvait etre active via script extrait du chat | active seulement si script final vient du pipeline 3 agents |
| Logs pipeline | partiels | logs structures `ContextPipeline` + `PipelineMetrics` |
| Config | `modelPath` hardcode local possible | `modelPath` vide par defaut, settings offline propres |

## 2) Top bugs/corrections priorisees

### P0 (bloquants)
1. Runtime multi-templates -> supprime: `LocalLlamaCppClient` n'accepte plus que Phi-3 q4.
2. Fallbacks cloud/offline incoherents -> retire: UI et VM n'exposent plus telechargement modele.
3. Chat hors contexte -> corrige: chat refuse proprement sans run charge.
4. Preuve pipeline -> ajoute: `AiPipelineMetrics` + logs `ContextPipeline`/`PipelineMetrics`.
5. Rechargements runtime -> corrige: `LlmRuntimeHost` singleton lazy.

### P1 (agents + AutoFix)
1. AutoFix via script chat libre -> supprime (`TryActivateAutoFixFromChatResponse` retire).
2. Separation UX -> `Analyze selected run` (contexte) + `Generate AutoFix Script` (pipeline 3 agents).
3. Gate execution -> `CanAutoFix` exige script genere par pipeline + policy approve.
4. Execution controlee -> logs enrichis (`runId`, `scriptHash`, `exitCode`, chemins logs/transcript).

### P2 (qualite/tests/optim)
1. Cache contexte par hash/mtime -> ajoute dans `ChatSupportViewModel`.
2. Parsing JSON stream + mesures parse -> `ContextPackBuilder.LoadFromFile(..., out bytes, out parseMs)`.
3. Tests IA etendus -> `ContextPackBuilderTests`, `LlmClientPipelineTests`, safety tests renforces.

## 3) Dette technique restante

1. Warnings nullability hors scope IA (WMI/platform tests, HealthRules) toujours presents.
2. `ModelDownloaderService.cs` ne peut pas etre supprime physiquement (ACL verrouillee) mais son contenu est neutralise et non reference.
3. Selftest IA en `dotnet run` peut echouer si WPF obj est verrouille; execution binaire directe est stable.

## 4) Mesures / preuves

## Build
- `dotnet build PCDiagnosticPro.sln -c Debug --no-restore` -> OK (warnings non-bloquants).

## Selftests IA
- `PCDiagnosticPRO-code/bin/Debug/net8.0-windows/PCDiagnosticPro.exe --selftest-ai` -> PASSED
  - AI SETTINGS TESTS: 2/2
  - AI SAFETY TESTS: 4/4
  - CONTEXT PACK TESTS: 3/3
  - CHAT SUPPORT VM TESTS: 1/1
  - LLM CLIENT PIPELINE TESTS: 2/2

## Logs `%TEMP%\PCDiagnosticPro_ui.log`
- Presence des traces:
  - `[AI][ContextPipeline] ... parseMs=... contextBuildMs=...`
  - `[AI][PipelineMetrics] stage=context_load ... contextTokens=... cacheHit=...`
  - `[LLM] Inference done | tokens=... | elapsed=... | tok/s`

## 5) Fichiers majeurs modifies

- `PCDiagnosticPRO-code/AI/AiSettings.cs`
- `PCDiagnosticPRO-code/AI/LocalLlamaCppClient.cs`
- `PCDiagnosticPRO-code/AI/LlmRuntimeHost.cs`
- `PCDiagnosticPRO-code/AI/Models/AiPipelineMetrics.cs`
- `PCDiagnosticPRO-code/ViewModels/ChatSupportViewModel.cs`
- `PCDiagnosticPRO-code/Views/ChatSupportView.xaml`
- `PCDiagnosticPRO-code/AI/PowerShellExecutor.cs`
- `PCDiagnosticPRO-code/Services/SelfTestRunner.cs`
- `PCDiagnosticPRO-code/config/ai_settings.json`
- `config/ai_settings.json`

## 6) Checklist technique finale

- [x] Aucun chemin hardcode vers `modelPath`
- [x] Modele charge une fois (singleton runtime)
- [x] Streaming/cancel gere (runtime + tests mock cancellation)
- [x] Chat utilise ContextPack (logs `ContextPipeline`/`PipelineMetrics`)
- [x] Agents executes uniquement pour generation script
- [x] AutoFix = confirmation + logs + exit code + option reboot
- [x] Code LLM non-Phi3 retire du runtime client
- [x] Build OK + selftests IA OK