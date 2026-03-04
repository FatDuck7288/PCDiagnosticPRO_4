# ADR-Themes-Branding

- Date: 2026-02-24
- Status: Implemented (MCP extraction pending Figma frame URL)

## Context

Le projet WPF devait:
- supprimer l'ancien skin Nova Lumina,
- garder "Dark Futuriste" identique au rendu actuel,
- ajouter et maintenir "PC X-RAY",
- finaliser le branding UX "PC X-Ray",
- préserver la compatibilite des rapports existants (legacy + nouveau dossier).

## Decisions

1. Theme system
- Theming centralise dans `Themes/DarkFuturiste.xaml` et `Themes/PCXRAY.xaml`.
- `ThemeManager` ne gere que les dictionnaires declares dans `ThemeDefinitions.All`.
- Persistance du theme inchangée, application immediate runtime inchangée.

2. Theme catalog
- Themes actifs: `DarkFuturiste`, `PCXRAY`.
- Codes legacy Nova Lumina retires du catalogue actif.
- Fallback robuste vers `DarkFuturiste` conserve.

3. Nova Lumina
- Fichier retire: `PCDiagnosticPRO-code/Styles/Themes/NovaLumina.xaml`.
- Aucune reference source restante (hors artefacts build).

4. Branding UX
- Branding utilisateur conserve/normalise sur `PC X-Ray`.
- Ligne de titre du rapport unifie mise a jour: `PC X-Ray - Rapport Unifié v2.0`.
- Noms techniques internes (`namespace`, `AssemblyName`) conserves pour faible risque.

5. Storage compatibility
- Dossier par defaut: `%LocalAppData%\\PCXRay\\Reports`.
- Fallback lecture legacy conserve:
  - `%LocalAppData%\\PCDiagnosticPro\\Rapports`
  - `%LocalAppData%\\PCDiagnosticPro\\Reports`
- Aucune migration destructive, aucune suppression/copie automatique.

## Implementation notes

### Modified files
- `PCDiagnosticPRO-code/Themes/ThemeDefinitions.cs`
- `PCDiagnosticPRO-code/Themes/ThemeManager.cs`
- `PCDiagnosticPRO-code/Themes/DarkFuturiste.xaml`
- `PCDiagnosticPRO-code/Themes/PCXRAY.xaml`
- `PCDiagnosticPRO-code/Styles/FuturisticStyles.xaml`
- `PCDiagnosticPRO-code/Controls/ThemedSpinner.xaml`
- `PCDiagnosticPRO-code/Controls/ScanAnimationOverlay.xaml`
- `PCDiagnosticPRO-code/MainWindow.xaml`
- `PCDiagnosticPRO-code/ViewModels/MainViewModel.cs`
- `PCDiagnosticPRO-code/ViewModels/ChatSupportViewModel.cs`
- `PCDiagnosticPRO-code/Services/UnifiedReportBuilder.cs`
- `PCDiagnosticPRO-code/Services/SelfTestRunner.cs`
- removed: `PCDiagnosticPRO-code/Styles/Themes/NovaLumina.xaml`

### Figma MCP mapping (structure)

> Etat: mapping structure en place, extraction MCP reelle en attente du lien Figma frame/node (`fileKey` + `node-id`).

Color tokens:
- `figma.color.background` -> `Color.Background`
- `figma.color.surface.default` -> `Color.Surface`
- `figma.color.surface.elevated` -> `Color.SurfaceElevated`
- `figma.color.text.primary` -> `Color.TextPrimary`
- `figma.color.text.secondary` -> `Color.TextSecondary`
- `figma.color.accent.primary` -> `Color.Accent`
- `figma.color.status.success` -> `Color.Success`
- `figma.color.status.warning` -> `Color.Warning`
- `figma.color.status.danger` -> `Color.Danger`
- `figma.color.status.info` -> `Color.Info`
- `figma.color.border.default` -> `Color.Border`

Design tokens:
- `figma.radius.card` -> `Radius.Card`
- `figma.spacing.s` -> `Spacing.S`
- `figma.spacing.m` -> `Spacing.M`
- `figma.spacing.l` -> `Spacing.L`
- `figma.font.title` -> `Font.Title`
- `figma.font.body` -> `Font.Body`
- `figma.font.mono` -> `Font.Mono`
- `figma.shadow.card` -> `Shadow.Card`

Component mapping:
- Card -> `CardStyle`
- Button -> `FuturisticButton`, `AccentButton`
- Tabs -> `TabControl`, `TabItem`
- Badges -> `BadgeOK`, `BadgeWarn`, `BadgeFail`
- Tables -> `DataGrid` styles
- Progress -> `LiveFeedSlimProgressBar`, `CircularProgressBar`
- Spinner -> `ThemedSpinner`
- Alerts/Banners -> `PanelBorder`, `HudPanel`, `HudPanelInner`

## Risks and mitigations

1. Dark theme visual drift
- Mitigation: tokens Dark conserves, changements structurels limites.

2. Legacy report access regression
- Mitigation: fallback multi-dossiers dans `MainViewModel`, `ChatSupportViewModel`, `SelfTestRunner`.

3. Figma parity gap
- Mitigation: extraction MCP a finaliser des reception du lien frame/node.

## Validation checklist (manual)

- Home / landing
- Scan & Fix
- Chat & Support IA
- Parametres (switch theme + persistance)
- Tables (rapport integral)
- Boutons primaires/secondaires
- Spinner / progressbar
- Alerts / banners

