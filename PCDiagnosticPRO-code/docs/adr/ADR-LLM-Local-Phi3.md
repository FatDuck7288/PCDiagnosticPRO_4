# ADR - LLM Local Phi-3 (runtime unique)

Date: 2026-02-25
Status: Accepted

## Contexte
Le produit doit rester offline pour la stack IA, sans cloud ni API payante, avec reponses stables en EN/FR/ES, et un pipeline script AutoFix controlable.

## Decision
1. Runtime unique: `llama.cpp` via `LLamaSharp`.
2. Modele unique autorise: `Phi-3-mini-4k-instruct-q4.gguf`.
3. Toute logique multi-modeles/templates/fallback est retiree du client runtime.
4. `modelPath` est configure via `config/ai_settings.json` (pas de hardcode machine dans le repo).

## Pourquoi ce choix
1. GGUF natif + ecosysteme stable local pour Windows.
2. Controle explicite des hyperparametres en local (`contextWindow`, `maxTokens`, `temperature`, `topP`, `topK`, `repeatPenalty`, `threads`, `gpuLayers`, `enableStreaming`, `timeoutSeconds`).
3. Simplicite operationnelle: une seule combinaison supportee = moins d'erreurs de prompt/template.
4. Cohesion avec exigence offline et zero cloud.

## Consequences
## Positives
- Comportement prompt plus deterministe (format phi3 unique).
- Moins de code mort et de branches runtime.
- Diagnostic plus clair via logs unifies pipeline/runtime.

## Trade-offs
- Pas de tolerance pour d'autres fichiers GGUF: tout modele non conforme est refuse.
- Maintenance demandera de remplacer explicitement le modele si une nouvelle variante est adoptee.

## Risques et mitigations
1. Risque: chemin modele invalide.
   - Mitigation: validation stricte `ValidateModelPath` + message UI explicite.
2. Risque: rechargement runtime inutile.
   - Mitigation: `LlmRuntimeHost` singleton lazy + skip reload si meme modele.
3. Risque: script dangereux.
   - Mitigation: `SafetyPolicyEngine` hard-block + AutoFix activable uniquement apres pipeline 3 agents + confirmation utilisateur.
4. Risque: reponse chat hors contexte run.
   - Mitigation: run/context obligatoire avant chat contextualise; logs pipeline obligatoires.

## Implementation reference
- Runtime/validation: `PCDiagnosticPRO-code/AI/LocalLlamaCppClient.cs`
- Host singleton: `PCDiagnosticPRO-code/AI/LlmRuntimeHost.cs`
- Settings: `PCDiagnosticPRO-code/AI/AiSettings.cs`
- Config canonique: `config/ai_settings.json`