# Lunar design tokens

Owner: `src/Lunar.Desktop/Themes` (FE-01). Source of truth for values is PRD §3.5; this file documents keys and rules. Tokens are resource keys; views reference keys and never literal colors, sizes or fonts.

## Files and load order

Merge in this order in the application resources (FE-02 owns `App.xaml`; discovery `App.xaml` is not used):

1. one of `Colors.Dark.xaml`, `Colors.Light.xaml`, `Colors.HighContrast.xaml`
2. `Typography.xaml`
3. `Spacing.xaml`
4. `Components.xaml`

All three color files define the same keys. Switching theme means replacing only the color dictionary; brushes in `Components.xaml` use `DynamicResource` and follow. Choosing the theme (setting, OS theme, high-contrast detection) and reloading `Colors.HighContrast.xaml` when system colors change belong to FE-23/FE-24; FE-01 supplies only the dictionaries.

## Colors (`Lunar.Color.*`, with a matching `Lunar.Brush.*` in Components)

| Token | Dark (PRD) | Light | Use |
|---|---|---|---|
| Background | #0B0C10 | #F6F7FB | window/pill background |
| Surface | #181A22 | #FFFFFF | cards, expanded panel |
| TextPrimary | #F6F7FB | #0B0C10 | main text |
| TextSecondary | #A8ADBE | #4A4F63 | secondary text, captions |
| TextDisabled | #6F7488 | #8A8FA3 | disabled text (exempt from contrast) |
| Accent | #B6B2FF | #4B45C9 | interactive emphasis, selection |
| OnAccent | #0B0C10 | #FFFFFF | text/icons on an Accent fill |
| Focus | #B6B2FF | #4B45C9 | keyboard focus ring |
| Border | #3A3E52 | #D4D7E2 | decorative dividers only |
| Error | #FF737E | #B3261E | error text/icons |
| Success | #86D7B0 | #1B7F4B | success text/icons |

The light palette (including Border and TextDisabled) is not in the PRD; it was derived in FE-01 and approved by the user on 2026-11-03. High contrast maps every token to a Windows system color (`Window`, `WindowText`, `GrayText`, `Highlight`, `HighlightText`, `HotTrack`). Error and Success then share one color, so components must never convey status by hue alone (add an icon or text).

Contrast rules enforced by `ThemeTokenTests` (WCAG 2.x relative luminance), both Dark and Light:
- TextPrimary, TextSecondary, Accent, Error, Success on Background and on Surface: at least 4.5:1.
- OnAccent on Accent: at least 4.5:1.
- Focus on Background and on Surface: at least 3:1.
- Border is decorative; a control that needs a visible boundary must not rely on Border alone.

## Typography

`Lunar.Font.Family` = "Segoe UI Variable Text, Segoe UI"; `Lunar.Font.Family.Display` = "Segoe UI Variable Display, Segoe UI". Sizes in DIPs: Caption 12, Body 14, Subtitle 16, Title 20, Display 28 (`Lunar.Font.Size.*`). TextBlock styles: `Lunar.Text.Body`, `.Secondary`, `.Caption`, `.Subtitle`, `.Title`, `.Display`, `.Error`.

## Spacing and shape

`Lunar.Space.XS/S/M/L/XL` = 4/8/12/16/24 (double) and `Lunar.Padding.*` (uniform Thickness). `Lunar.Radius.Pill` = 999, `Lunar.Radius.Panel` = 24 (CornerRadius). Idle/expanded window sizes are layout (FE-03), not tokens.

## Focus

`Lunar.Style.FocusVisual` is a 2 DIP ring in `Lunar.Brush.Focus`. Controls opt in with `FocusVisualStyle="{StaticResource Lunar.Style.FocusVisual}"`; reusable controls (FE-12) apply it by default.

## Rules

- No literal color in view XAML. `ThemeTokenTests.ViewXamlUsesTokensInsteadOfLiteralColors` scans all non-theme XAML in Desktop (retained Sprint 0 discovery files excluded).
- Add a token to all three color files and Components at once; the tests fail otherwise.
- Verified so far: parsing, key parity, contrast math and WPF resource resolution. High contrast on screen: the pill and its edge were reported clearly visible by the user on 2026-11-03 (FE-02; no text or icons reviewed yet). Not verified: real rendering in the app, Windows high-contrast themes on screen, Narrator, DPI scaling. These need FE-02+ and manual evidence.
