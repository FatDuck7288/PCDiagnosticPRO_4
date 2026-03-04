# Audit complet PCDiagnosticPRO — Plan d'implémentation

## PHASE 0 — Cartographie (déjà faite)
Exploration complète: AI/, Services/, ViewModels/, Tests/, Scripts/, Themes/, config/

## 17 Faiblesses identifiées → 3 Vagues

### Vague 1 — Critiques (sécurité/stabilité) — 5 fixes
| Fix | Fichier | Problème | Effort |
|-----|---------|----------|--------|
| F1 | RestorePointService.cs:114 | Injection PS: `description` au lieu de `safeDescription` | S |
| F2 | SafetyPolicyEngine.cs:31 | Regex MASS_DELETE `C:\\\\` ne matche pas `C:\` | S |
| F3 | SafetyPolicyEngine.cs:53 | `\|\|` matche dans commentaires PS → faux REFUSE | S |
| F4 | chat_support_base.md:8 | "Reponds uniquement en francais" hardcodé | S |
| F5 | ChatSupportViewModel.cs:1082 | `const langCode = "fr"` ignore App.CurrentLanguage | S |

### Vague 2 — Stabilité/Robustesse — 7 fixes
| Fix | Fichier | Problème | Effort |
|-----|---------|----------|--------|
| F6 | LocalLlamaCppClient.cs | Race: Unload() null _model pendant StreamAsync | M |
| F7 | PowerShellExecutor.cs:225 | WaitForExit() sans timeout → hang infini | M |
| F8 | PowerShellService.cs:47 | Champs non-volatile accédés multi-thread | S |
| F9 | WmiQueryRunner.cs:18 | Liste `_errors` statique illimitée | S |
| F10 | ContextPackBuilder.cs:209 | Hash intégrité cassé (re-sérialisation JSON) | M |
| F11 | LlmRuntimeHost.cs:26 | Singleton ignore changement AiSettings | M |
| F12 | PromptLoader.cs:36 | Erreur de load cachée indéfiniment | S |

### Vague 3 — Qualité/Maintenabilité — 5 fixes
| Fix | Fichier | Problème | Effort |
|-----|---------|----------|--------|
| F13 | safety_policy.md | 5 patterns manquants vs C# engine | S |
| F14 | MainViewModel.ScanExecution.cs | Process non-disposé si Start() échoue | S |
| F15 | LlmOutputSanitizer.cs:190 | IsPlainTextLine drop `_#=[]\@` | S |
| F16 | LlmOutputSanitizer.cs:142 | Fallback FR affiché pour EN/ES | M |
| F17 | ai_settings.json:25 | contextWindow=8192 vs max 2048 TinyLlama | S |

## Critères d'acceptation
- Build 0 erreurs 0 warnings après chaque vague
- Chaque fix est isolé et réversible
- Pas de MessageBox ajoutée
- Pas de hardcode de chemins bin/Debug
