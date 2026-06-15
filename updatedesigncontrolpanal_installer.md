# Design & UI/UX Deep-Dive — hMailServer Control Panel + Installer

**Author:** Design audit (GitHub Copilot) · **Date:** 2026-06-15 · **Target build:** 6.2.5-B7
**Scope:** Visual design, colour system, theming, typography, layout, density, navigation,
component consistency, state handling, accessibility — for **(A)** the WPF Control Panel
(`hMailCP.exe`) and **(B)** the InnoSetup installer wizard.

> **How this was assessed.** Every Control Panel page (28 of them) was screenshotted from
> the live app in **both light and dark themes** at the default 1280×800 window, the full XAML
> + code-behind was read, and the InnoSetup scripts were reviewed end-to-end. Recommendations
> are grounded in Microsoft's Fluent/Windows design guidance and WCAG 2.1 contrast rules
> (see *References*).

---

## 0. Executive summary

The Control Panel is **structurally strong** — a modern WPF-UI (Fluent) shell with Mica
backdrop, a sensible navigation tree that mirrors the classic Administrator, full settings
parity, live charts, and a command palette. The problems the user is reacting to
("colours look bad") are **not** the overall layout — they are a handful of **specific,
fixable colour and theming defects** plus **density/proportion** issues that make otherwise
clean pages feel unbalanced.

The five highest-impact problems:

| # | Problem | Where | Severity |
|---|---------|-------|----------|
| 1 | **Nav selection paints a raw system-blue block** (default `TreeViewItem` chrome) that clashes with the Fluent look in both themes | Sidebar, every page | **P0** |
| 2 | **Hardcoded colours that don't follow the theme** — log severity colours are built for dark and become *invisible* on light (DEBUG ≈ 1.3:1 contrast); validation/strength use named WPF brushes (`IndianRed`, `Goldenrod`…) | Live logs, all dialogs, password fields | **P0** |
| 3 | **Misleading semantic colour** — "0 VIRUSES REMOVED" is painted **red**, and "Delete" buttons are loud saturated red, so *good/neutral* states read as *alarms* | Dashboard KPIs, Domains, Ports | **P1** |
| 4 | **Density/proportion** — single numeric inputs stretch the full ~1400 px width; data-grid columns are wildly unbalanced; settings cards float in empty space; content max-width is inconsistent page-to-page | Most settings + grid pages | **P1** |
| 5 | **No high-contrast / no design-token layer** — colours are scattered as literals across XAML and C#, so there is no single place to theme, and high-contrast mode is unsupported | App-wide | **P1** |

The installer is **functional but dated**: it is built with **InnoSetup 5's classic
("Windows 2000-era") wizard**, its custom pages use **fixed pixel positions with no DPI
scaling**, it carries **dead legacy dependency code** (MDAC, IE6, JET, .NET 2.0), and uses a
**hand-rolled blue hyperlink**.

None of this is a rewrite. It is roughly **2–3 days** of focused work to land a centralised
colour-token system, fix the five defects above, and modernise the installer wizard.

---

## Implementation status (2026-06-15)

The P0 and P1 roadmap below has been **implemented and validated** (built 0/0, screenshot-checked
in light + dark), and the P2 installer items shipped (ISCC compiles clean):

- **P0 (done):** central token system [ThemeTokens.cs](hmailserver/source/Tools/ControlPanel/Services/ThemeTokens.cs)
  (success/warning/danger/info + log palette, recomputed per theme incl. high-contrast); themed
  `TreeViewItem` template — the system-blue nav block is gone (rounded fill + brand pill + chevrons);
  Live-logs colours tokenised (DEBUG now legible on light) + tighter line spacing; all theme-blind
  named brushes (`IndianRed`/`MediumSeaGreen`/`Goldenrod`) replaced with tokens.
- **P1 (done):** global DataGrid styling (hairline row dividers, hover/selection, low-key headers)
  + balanced TCP/IP-port columns with right-aligned Port; numeric setting inputs constrained
  (180 px); KPI values neutral (no misleading red "0 viruses"), queue turns amber only when backing
  up; destructive list/action-bar buttons softened to secondary + danger-tinted text; dashboard
  charts show "no activity" placeholders; Welcome/About widened to a consistent 880 px.
- **P2 (done):** custom "database type" wizard page DPI-scaled (`ScaleX`/`ScaleY`), missing-semicolon
  bug fixed, copy modernised (built-in marked recommended, no EOL "SQL Compact" framing), and the
  dead legacy dependency `[Run]` entries (MSI/IE6/MDAC/JET/.NET 2.0) removed.
- **P3 (partial):** high-contrast is handled structurally by the token layer (maps to `SystemColors`);
  the existing shared text styles already form a type ramp. A deeper inline-literal/type-ramp refactor
  is deferred as low-value (remaining literals are benign — `Transparent`, saturated severity chips).
- **Polish:** the Welcome page is now a grid of clickable, icon-led quick-action tiles (navigating via a
  new `MainWindow.NavigateTo`); the Account/Domain tabbed editors gained `MinWidth`/`MinHeight`; and the
  sidebar now draws a distinct **brand keyboard-focus ring** (the dotted default focus is suppressed),
  closing the A7 focus-visual gap.
- **Inno 6 / `WizardStyle=modern`** remains a recommendation only (the repo builds with Inno Setup 5,
  where that directive does not exist); not applied to avoid breaking the current installer build.

The detailed analysis below is retained as the rationale and reference.

---

# Part A — Control Panel

## A1. Architecture & technology (context)

| Aspect | Current |
|---|---|
| Framework | .NET 8 WPF, `WinExe`, x64 ([ControlPanel.csproj](hmailserver/source/Tools/ControlPanel/ControlPanel.csproj)) |
| UI library | **WPF-UI 3.0.5** (lepo.co Fluent), `FluentWindow` + Mica backdrop |
| Charts | LiveChartsCore.SkiaSharpView 2.0-rc2 |
| Shell | [MainWindow.xaml](hmailserver/source/Tools/ControlPanel/MainWindow.xaml) — `TitleBar`, 200–260 px sidebar `TreeView`, `ContentControl` host |
| Theme engine | [MainWindow.xaml.cs](hmailserver/source/Tools/ControlPanel/MainWindow.xaml.cs) `ApplySavedTheme()` — follows OS theme, persists Light/Dark to `HKCU\Software\hMailServer\ControlPanel` |
| Styles | Centralised app styles in [App.xaml](hmailserver/source/Tools/ControlPanel/App.xaml) (`Card`, `PageTitle`, `KpiValue`, custom `TabItem`/`TabControl`, `NavItem`) |
| Pages | 28 views registered in `RegisterPages()`; XAML views for data-dense pages, **code-behind-built** views/dialogs for editors |

**What's already good and should be preserved:**

- The **custom `TabItem`/`TabControl`** pivot strip (accent underline, hover fill) is genuinely
  nice and consistent — see Protocols/Anti-spam.
- A **global `TextBlock` default foreground** guarantees text is never dark-on-dark.
- The **theme system** (follow-OS by default, manual override) is correct.
- The **sticky footer action bar** ("Values read from the server." + Reload / Save changes) is
  a good settings pattern.
- The **brand mark** (gradient "hM" tile) and `TitleBar` mail icon are tasteful.

---

## A2. Colour system & theming — the core of "colours look bad"

### A2.1 The brand palette

Defined once in [App.xaml](hmailserver/source/Tools/ControlPanel/App.xaml):

```text
BrandColor   #2F81F7  (blue)      ← GitHub "accent blue"
Brand2Color  #A371F7  (purple)    ← GitHub "purple"
BrandGradient = blue → purple
```

This is a fine, contemporary brand pair. The problem is **not** the brand colours — it's
that (a) the brand accent is **not wired into the controls** that still fall back to the raw
**Windows system accent** (the nav selection block), and (b) a **second, ad-hoc palette of
literal colours** is sprinkled across the codebase with no theme awareness.

### A2.2 Inventory of hardcoded colours (the actual offenders)

| Colour | Meaning | Location | Problem |
|---|---|---|---|
| System `Highlight` (≈`#0078D4`) | nav selected | default `TreeViewItem` template (not overridden) | **Clashes** with brand; raw blue block in both themes |
| `#A371F7` purple | "Spam blocked" KPI | [DashboardView.xaml](hmailserver/source/Tools/ControlPanel/Views/DashboardView.xaml) | Arbitrary; purple ≠ "spam" semantically |
| `#F85149` red | "Viruses removed" KPI = 0 | [DashboardView.xaml](hmailserver/source/Tools/ControlPanel/Views/DashboardView.xaml) | **Red for a good/zero value** reads as an error |
| `#C9D1D9 / #3FB950 / #A371F7 / #D29922 / #F85149 / #2F81F7` | log severity colours | [LogsView.xaml.cs](hmailserver/source/Tools/ControlPanel/Views/LogsView.xaml.cs) (L29–34) | **Fixed for dark theme**; `#C9D1D9` DEBUG ≈ **1.3:1** on white = invisible |
| `Brushes.IndianRed` | validation error text | [AccountDialog.cs](hmailserver/source/Tools/ControlPanel/Views/AccountDialog.cs), [DomainDialog.cs](hmailserver/source/Tools/ControlPanel/Views/DomainDialog.cs), [RouteDialog.cs](hmailserver/source/Tools/ControlPanel/Views/RouteDialog.cs), [ServerSettingsView.xaml.cs](hmailserver/source/Tools/ControlPanel/Views/ServerSettingsView.xaml.cs) | Named brush; **doesn't adapt** to theme/high-contrast; not the brand's error colour |
| `Brushes.MediumSeaGreen / Goldenrod / IndianRed / Gray` | password strength meter | [AccountDialog.cs](hmailserver/source/Tools/ControlPanel/Views/AccountDialog.cs) (L554–557) | Same; green/red pair is **colourblind-hostile** with no text/icon |
| `#3FB950` green | connection badge dot | [MainWindow.xaml](hmailserver/source/Tools/ControlPanel/MainWindow.xaml) (L76) | OK-ish but still a literal |
| `#F85149` red | connect error text | [ConnectView.xaml](hmailserver/source/Tools/ControlPanel/Views/ConnectView.xaml) (L36) | Literal |
| `FromRgb(0xC4,0x18,0x1E)` … severity chips | Server status warnings | [StatusView.xaml.cs](hmailserver/source/Tools/ControlPanel/Views/StatusView.xaml.cs) (L198–201) | Literals; fine values but not tokenised |
| `Brushes.White` foreground on chips/badges | StatusView, CollectionEditor | several | Assumes a dark chip behind it; breaks if chip is light |

**Root cause:** there is **no semantic colour-token layer**. WPF-UI ships a full set of
theme-aware brushes (`TextFillColorPrimaryBrush`, `SystemFillColorCriticalBrush`,
`SystemFillColorSuccessBrush`, `AccentTextFillColorPrimaryBrush`, etc.) that automatically
flip for light/dark/high-contrast. The app uses them for *text* but falls back to **literal
hex / named brushes for every state colour**, which is exactly why colours "look off" when
the theme changes.

> **Microsoft guidance (grounding):** *"When creating templates for custom controls, use
> theme brushes rather than hardcoded color values… common controls automatically use theme
> brushes to adjust contrast for light and dark themes."* — [Theming in Windows apps](https://learn.microsoft.com/windows/apps/develop/ui/theming).
> And on meaning: *"Use color meaningfully… use color to indicate interactivity… make sure
> elements have sufficient contrast regardless of theme."* — [Color in Windows](https://learn.microsoft.com/windows/apps/design/signature-experiences/color).

### A2.3 Recommended fix — a single semantic token dictionary

Create **`Themes/Tokens.xaml`** merged in `App.xaml`, defining semantic brushes **per theme**
using `ResourceDictionary.ThemeDictionaries` (`Light` / `Default` (dark) / `HighContrast`).
Everything else references these by `DynamicResource` only:

```xml
<ResourceDictionary>
  <ResourceDictionary.ThemeDictionaries>
    <!-- DARK (Default) -->
    <ResourceDictionary x:Key="Default">
      <SolidColorBrush x:Key="AppBrandBrush"        Color="#4C8DFF"/>
      <SolidColorBrush x:Key="AppSuccessBrush"      Color="#3FB950"/>
      <SolidColorBrush x:Key="AppWarningBrush"      Color="#D29922"/>
      <SolidColorBrush x:Key="AppDangerBrush"       Color="#F85149"/>
      <SolidColorBrush x:Key="AppInfoBrush"         Color="#A371F7"/>
      <SolidColorBrush x:Key="LogDebugBrush"        Color="#8B949E"/>
      <SolidColorBrush x:Key="LogSmtpBrush"         Color="#3FB950"/>
      <!-- … imap/pop3/app/error … -->
    </ResourceDictionary>
    <!-- LIGHT -->
    <ResourceDictionary x:Key="Light">
      <SolidColorBrush x:Key="AppBrandBrush"        Color="#2F6FE0"/>
      <SolidColorBrush x:Key="AppSuccessBrush"      Color="#1A7F37"/>
      <SolidColorBrush x:Key="AppWarningBrush"      Color="#9A6700"/>
      <SolidColorBrush x:Key="AppDangerBrush"       Color="#CF222E"/>
      <SolidColorBrush x:Key="AppInfoBrush"         Color="#6639BA"/>
      <SolidColorBrush x:Key="LogDebugBrush"        Color="#57606A"/>  <!-- ≥4.5:1 on white -->
      <SolidColorBrush x:Key="LogSmtpBrush"         Color="#1A7F37"/>
      <!-- … -->
    </ResourceDictionary>
    <!-- HIGH CONTRAST -->
    <ResourceDictionary x:Key="HighContrast">
      <SolidColorBrush x:Key="AppBrandBrush"   Color="{x:Static SystemColors.HighlightColor}"/>
      <SolidColorBrush x:Key="AppDangerBrush"  Color="{x:Static SystemColors.WindowTextColor}"/>
      <!-- map all tokens to SystemColors.* -->
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
```

Then:
- `LogsView` reads `LogDebugBrush` etc. via `TryFindResource` instead of `Color.FromRgb(…)`.
- Dialogs set validation text `Foreground="{DynamicResource AppDangerBrush}"`.
- Password strength uses `AppDanger/AppWarning/AppSuccess` **plus a text label** ("Weak/Fair/Strong")
  so it isn't colour-only.
- KPI accents come from tokens (and the virus/zero case is **neutral**, not red — see A4).

This is the single most valuable change: it turns "colours look bad on theme switch" into a
non-issue and unlocks high-contrast support for free.

---

## A3. Navigation selection (P0 visual bug)

The sidebar `TreeView` only overrides the **foreground** in its `ItemContainerStyle`; it never
replaces the **control template**, so the default WPF selection rectangle paints the **system
highlight** — a bright blue block when the tree is focused (see Dashboard/Domains/Welcome/About
screenshots) and a flat grey when unfocused (Server status). The app **already contains** the
right visual language for this — the `NavItem` `RadioButton` style in
[App.xaml](hmailserver/source/Tools/ControlPanel/App.xaml) (rounded pill, brand accent bar,
subtle hover) — it just isn't applied to the tree.

**Fix:** give `TreeViewItem` a custom `ControlTemplate` that mirrors `NavItem`:
rounded `CornerRadius="6"` selection fill = `SubtleFillColorSecondaryBrush`, a 3 px brand
accent pill on the left when `IsSelected`, transparent on hover→`SubtleFillColorTertiary`, and
**no system highlight**. Set `HighContrastAdjustment="None"` on items so token styling flows in
contrast mode. This one change removes the most obvious "looks bad" element on every page.

---

## A4. Semantic colour misuse (P1)

- **Dashboard KPIs:** "SPAM BLOCKED" is purple and "VIRUSES REMOVED" is **red**, regardless of
  value. A **red `0`** for *viruses removed* signals danger for what is actually the *healthy*
  state. Recommendation: KPI **values** should be **neutral** (`TextFillColorPrimaryBrush`);
  use a small **coloured icon or label chip** for category identity, and only switch the value
  to `AppDanger`/`AppWarning` when the metric is *actually* in a bad range (e.g., queue depth
  over a threshold). Colour should encode **state, not category**.
- **Destructive buttons:** "Delete" (Domains) and "Delete selected" (Ports) are filled
  saturated red sitting next to neutral outline buttons — visually shouting on an otherwise
  calm page. Recommendation: make destructive actions a **subtle/secondary** style by default
  (neutral with a danger-tinted label or a trash glyph) and reserve the **filled danger**
  treatment for the **confirmation dialog's** primary button. This matches Fluent's
  "emphasise the safe path" convention and reduces visual noise.
- **Password strength** green/amber/red is colour-only → fails colourblind users. Add the
  textual level (already computed in [PasswordStrength.cs](hmailserver/source/Tools/ControlPanel/Services/PasswordStrength.cs)) next to the bar.

---

## A5. Layout, spacing & density (P1)

Observed from the screenshots:

1. **Full-width single inputs.** On Anti-spam / settings pages a value like `1024` or `10000`
   sits in a textbox that spans the entire ~1400 px content width. It looks unbalanced and
   makes the eye travel. **Fix:** cap input width by semantic type — numerics/ports
   `MaxWidth≈160`, short strings `≈320`, hostnames/paths `≈480`, free text/areas full width.
   Provide shared input styles (`NumericInput`, `ShortInput`, `WideInput`) so this is
   consistent. Consider a **two-column form** layout on wide windows to use the horizontal
   space and cut vertical scrolling.
2. **Cards floating in emptiness.** Protocols shows 3 checkboxes in a giant card; the card
   stretches full width with a huge blank lower half. **Fix:** let single-purpose cards size to
   content (`VerticalAlignment=Top`, `HorizontalAlignment=Left` with a sensible `MaxWidth`),
   and/or group related settings into the same card so pages don't look half-empty.
3. **Inconsistent content max-width.** Welcome/About cap content at ~60 % width (big empty
   right third) while Domains/Status/Ports use the full width. **Fix:** adopt **one** content
   container with a consistent `MaxWidth` (e.g. 1100–1200) centred, *or* full-bleed — but apply
   it uniformly via a shared `PageRoot` style.
4. **Data-grid column proportions.** TCP/IP ports gives the `Address` column ~50 % of the grid
   for "0.0.0.0" while `Port`/`Security` are cramped; rows have **no separators, no hover, no
   zebra**, so the eye can't track a row from "SMTP" across the gap to "25". **Fix:** set
   sensible star/auto widths (Protocol `120`, Address `*`-capped, Port `90`, Security `140`,
   Certificate `*`), add a 1 px row divider (`ControlElevationBorderBrush`), a hover/selected
   row state, and right-align numeric `Port`.
5. **Live-logs density.** Each line has a large vertical gap → only ~13 lines visible, plus a
   horizontal scrollbar from oversized inter-column gaps. **Fix:** tighten line height
   (monospace, ~1.25 line spacing), use fixed/elastic columns, and wrap or elide the message
   column instead of horizontal scroll.

---

## A6. Component consistency

- **Buttons.** Three styles can appear in one row (filled primary, outline, filled danger).
  Define a clear hierarchy: **one** primary (filled accent) per view, **secondary** (outline)
  for the rest, **subtle** for tertiary, **danger** only where destructive — and use it
  everywhere. The dialogs already use `Wpf.Ui.Controls.Button` with `Appearance=Primary`
  correctly; bring the list/grid pages in line.
- **Inputs.** Many code-behind editors set `Background = Brushes.Transparent` on `TextBox`/
  `ListView` to sit on cards; standardise on a shared input style instead of per-control literals.
- **Inline "pill" buttons.** The "Properties" pill next to a domain name and the inline
  Edit/Delete on the account row read inconsistently — adopt a consistent **row-action**
  pattern (trailing icon buttons that appear on row hover).
- **Empty/zero/loading states.** Dashboard charts render as a flat 0-line that looks broken;
  account/alias lists show bare "No … for this domain." Add proper **empty-state** treatments
  (muted icon + one line + optional CTA) and a **loading** shimmer/spinner, and have charts show
  a "no data yet" placeholder instead of an empty axis.

---

## A7. Accessibility

| Check | Status | Note |
|---|---|---|
| Light/dark text contrast (chrome) | ✅ | WPF-UI theme brushes used for text |
| **Log text contrast (light)** | ❌ | DEBUG `#C9D1D9` on white ≈ **1.3:1** (needs ≥4.5:1) — fix via tokens (A2.3) |
| **Colour-only meaning** | ❌ | Password strength, KPI semantics — add text/icons |
| **High-contrast theme** | ❌ | Only a `Dark` themes dictionary is loaded; literals won't map to `SystemColors` |
| Focus visuals | ✅ | Nav now draws a distinct brand keyboard-focus ring (the dotted default is suppressed); WPF-UI controls keep their own focus visuals |
| Keyboard nav | ✅ | Tree + `Ctrl+K` palette; tab order mostly sound |
| AutomationProperties | ✅ | Nav items/host have names + automation IDs (good) |

A `wpf_dev_accessibility` lint pass and an explicit **HighContrast** dictionary should be added.
Set `HighContrastAdjustment="None"` where custom brushes are intentional.

> Microsoft maintains a **≥7:1** (often 14:1) ratio for contrast themes and recommends mapping
> custom resources to `SystemColors.*` under a `HighContrast` dictionary — see
> [Contrast themes](https://learn.microsoft.com/windows/apps/design/accessibility/high-contrast-themes).

---

## A8. Typography

Centralised styles exist (`PageTitle` 24/SemiBold, `PageSubtitle` 13, `KpiValue` 28/Bold,
`KpiLabel` 11, `StatusLabel`/`StatusValue`). This is good. Gaps:
- Many code-behind controls set `FontSize = 13` inline rather than referencing a style → drift.
- No defined **type ramp** doc (e.g., Display/Title/Subtitle/Body/Caption). Recommend codifying
  4–5 named `TextBlock` styles and banning inline `FontSize` in code-behind.
- `KpiValue` uppercase labels with `FontSize 11` + letter-spacing read as "caps lock"; fine, but
  ensure tertiary contrast holds in light mode.

---

## A9. Per-page audit

Grades: 🟢 good · 🟡 needs polish · 🔴 has a clear defect.

| Page | Grade | Key findings |
|---|---|---|
| Welcome | 🟡 | Content only ~60 % width; plain text cards — add quick-action tiles/icons |
| Dashboard | 🔴 | Red "0 viruses" semantics; empty charts look broken; KPI category-vs-state colour |
| Server status | 🟢 | Clean card grid; good. (Tokenise the severity chip colours.) |
| Delivery queue | 🟡 | Grid styling (separators/hover) — verify column widths |
| Live logs | 🔴 | **Log colours invisible on light**; loose line height; horizontal scroll |
| Domains & accounts | 🟡 | Loud red Delete; inline "Properties" pill; lots of empty list space |
| Rules | 🟡 | Verify list density + action button hierarchy |
| Protocols | 🟡 | 3 checkboxes in an oversized empty card |
| Delivery of e-mail | 🟡 | Full-width numeric inputs likely |
| Routes | 🟡 | Code-behind dialog uses `IndianRed` validation |
| Anti-spam (5 tabs) | 🟡 | Full-width inputs for scores; otherwise tidy tabs |
| Anti-virus / Blocked attach. | 🟡 | Same input-width + grid patterns |
| Logging | 🟡 | Form density |
| Auto-ban & SSL/TLS | 🟡 | Form density |
| IP ranges | 🟡 | Grid styling + dialog validation colour |
| SSL certificates | 🟡 | Grid styling |
| Transport security | 🟡 | Form density |
| Advanced hardening | 🟡 | Form density |
| TCP/IP ports | 🔴 | Grid column proportions; red "Delete selected"; no row separators |
| Incoming relays | 🟡 | Grid styling |
| API & monitoring | 🟡 | Form density |
| Performance | 🟡 | Full-width numeric inputs |
| Advanced & scripting | 🟡 | Form density |
| Event scripts | 🟡 | Editor styling |
| Server messages | 🟡 | Collection editor density |
| Groups | 🟡 | Collection editor density |
| Backup & restore | 🟡 | Verify layout |
| MX query / Sendout / Diagnostics | 🟡 | Output area styling; ensure mono/contrast |
| About | 🟢 | Well-written; only the ~60 % content-width inconsistency |

## A10. Dialogs (tabbed editors)

`DomainDialog`, `AccountDialog`, `RouteDialog`, `IPRangeDialog`, `RuleCriteriaDialog`,
`RuleActionDialog`, `TcpIpPortDialog`, `DistributionListDialog`, `FolderPermissionsDialog`,
`TotpSetupDialog` etc. are **hand-built in code-behind** (e.g.
[DomainDialog.cs](hmailserver/source/Tools/ControlPanel/Views/DomainDialog.cs)):

- ✅ Use the shared custom `TabControl` style and `Wpf.Ui` primary button — consistent.
- 🟡 **Fixed sizes** (`640×680`) with no `MinWidth`/resize affordance for long signatures/keys.
- 🔴 Validation status uses `Brushes.IndianRed` (theme-blind) → switch to `AppDangerBrush`.
- 🟡 Lots of `FontSize = 13` / `Brushes.Transparent` literals → move to shared styles.
- 🟡 Inputs are full-width inside the dialog regardless of content (DKIM selector vs. signature).

---

# Part B — Installer (InnoSetup)

## B1. Current state

Built with **InnoSetup 5** (`C:\Program Files (x86)\Inno Setup 5\ISCC.exe`) from
[hMailServer64.iss](hmailserver/installation/hMailServer64.iss), which includes the section
files and [hMailServerInnoExtension.iss](hmailserver/installation/hMailServerInnoExtension.iss).

Wizard flow: Welcome → License → Dir → **Components** → *custom: Database type* → Tasks →
*custom: admin password* → Ready → Install (.NET 8 runtime if missing) → Finish (optionally
launches `hMailCP.exe`).

Visual config ([section_setup.iss](hmailserver/installation/section_setup.iss)):
`WizardImageFile=setup.bmp`, `WizardSmallImageFile=setup-small.bmp`, `license.rtf`, `{pf}`.

## B2. Findings

| # | Finding | Evidence | Severity |
|---|---|---|---|
| 1 | **Classic (legacy) wizard look.** Inno 5 has no `WizardStyle=modern`; the wizard renders in the old gray Win2000-era layout with the tall left image on Welcome/Finish. | Inno 5 toolchain; no `WizardStyle` directive | **P1** |
| 2 | **Custom pages use fixed pixels, no DPI scaling.** Radio buttons/labels positioned with `Left:=32; Top:=40; Width:=…` and **no `ScaleX()`/`ScaleY()`** → mispositioned/clipped at 125–200 % DPI. | [hMailServerInnoExtension.iss](hmailserver/installation/hMailServerInnoExtension.iss) L383–432 | **P1** |
| 3 | **Hand-rolled blue hyperlink.** "More information…" uses `clBlue` + manual underline `TFont` instead of a proper link control. | same file, L420–432 | P2 |
| 4 | **Dead legacy dependency code.** `[Run]`/`[CustomMessages]` still install **MSI 2.0, IE6, MDAC, JET, .NET 2.0**, with **hardcoded `download.microsoft.com` URLs** for MDAC/.NET 2.0 (dead links). | [section_run.iss](hmailserver/installation/section_run.iss) L1–6; InnoExtension L73–75 | P2 (cruft/confusing) |
| 5 | **SQL CE branding.** The DB-type page advertises "Microsoft SQL Compact" — an **end-of-life** engine (mainstream support ended) — as the built-in option, with no note. | InnoExtension L390 | P2 |
| 6 | **Password page has no reveal/strength/match feedback** beyond a final equality check; uses a plain `InputQueryPage`. | InnoExtension L436–440 | P2 |
| 7 | **Deprecated constant `{pf}`** (use `{commonpf}`/`{autopf}`); harmless today but flagged by Inno 6. | section_setup.iss L4 | P3 |
| 8 | **Welcome/Finish imagery** is the legacy `setup.bmp` (not viewed — BMP) — likely the old hMailServer art; should be refreshed to the new brand. | section_setup.iss L8–9 | P2 |

## B3. Recommendations

1. **Upgrade to InnoSetup 6 + `WizardStyle=modern`.** Biggest visual win for the least code:
   modern fonts, spacing, larger header, optional `WizardSizePercent`/`WizardResizable=yes`.
   Inno 6 keeps the same script language; main migration tasks are `{pf}`→`{autopf}` and the
   deprecated bits below. (Confirm minimum-Windows target — Inno 6 drops < Win7.)
2. **DPI-scale every custom control:** wrap all `Left/Top/Width/Height` in `ScaleX()/ScaleY()`
   so the DB-type and password pages render correctly on HiDPI.
3. **Replace the `clBlue` label with `TNewLinkLabel`** (Inno 6) for a real, accessible link.
4. **Delete the legacy dependency block** (MSI/IE6/MDAC/JET/.NET 2.0 `[Run]` + custom messages +
   dead URLs). Keep only the **.NET 8 Desktop Runtime** check (already correct via
   `DotNetDesktopMissing`).
5. **Modernise the DB-type page:** label the built-in engine accurately, add a one-line helper
   ("Recommended for most installs; no separate database server required"), and consider a
   visual two-card chooser instead of bare radio buttons.
6. **Improve the password page:** add a reveal toggle and live "passwords match"/strength hint
   (custom page with `TNewEdit` + `PasswordChar`), matching the Control Panel's own strength
   meter language.
7. **Refresh `setup.bmp`/`setup-small.bmp`** to the new brand (the gradient "hM" mark / wordmark)
   at 2× for HiDPI; verify against Inno 6's recommended sizes.
8. **Set `SetupIconFile`** to the app icon and ensure `VersionInfo*`/`AppPublisher` reflect
   "Progressive Robot Ltd / Christopher Holloway" consistently.

---

# Part C — Prioritised roadmap

### P0 — "Colours look bad" (do first; ~1 day)
1. **Token dictionary** `Themes/Tokens.xaml` with `Light`/`Default`/`HighContrast` (A2.3).
2. **Themed `TreeViewItem` template** → kill the system-blue nav block (A3).
3. **Tokenise log colours** so DEBUG is legible on light (A2.2/A7).
4. Replace `IndianRed`/named brushes in dialogs + status with `AppDangerBrush` (A2/A10).

### P1 — Density & semantics (~1 day)
5. Input-width styles (`NumericInput`/`ShortInput`/`WideInput`) + two-column forms (A5).
6. Data-grid styling: column widths, row dividers, hover/zebra, right-aligned numerics (A5).
7. KPI semantics neutral + state-based; subtle destructive buttons; password-strength labels (A4).
8. Consistent content `MaxWidth`/`PageRoot`; right-size single-setting cards (A5).
9. Empty/zero/loading states (charts, lists) (A6).

### P2 — Installer modernisation (~0.5–1 day)
10. Inno 6 + `WizardStyle=modern`; `ScaleX/Y`; `TNewLinkLabel`; delete legacy deps; refresh
    imagery; improve DB + password pages (B3).

### P3 — Polish
11. High-contrast pass + `wpf_dev_accessibility` lint; codify the type ramp; remove inline
    `FontSize`/`Brushes.*` literals across code-behind.

---

## References

- [Theming in Windows apps](https://learn.microsoft.com/windows/apps/develop/ui/theming) — use theme brushes, not hardcoded colours.
- [Color in Windows](https://learn.microsoft.com/windows/apps/design/signature-experiences/color) — colour principles, meaning, contrast, colourblindness.
- [XAML theme resources](https://learn.microsoft.com/windows/apps/develop/platform/xaml/xaml-theme-resources) — the colour ramp & theme-dependent brushes.
- [Contrast themes](https://learn.microsoft.com/windows/apps/design/accessibility/high-contrast-themes) — `ThemeDictionaries`, mapping to `SystemColors`, `HighContrastAdjustment`.
- WCAG 2.1 SC 1.4.3 (contrast ≥ 4.5:1) and SC 1.4.1 (don't rely on colour alone).
- [WPF-UI (lepo.co)](https://github.com/lepoco/wpfui) — theme brush keys (`*FillColor*Brush`, `SystemFillColor*Brush`, `AccentTextFillColor*Brush`).

*Appendix code snippets above are illustrative skeletons; exact brush keys should be verified
against the installed WPF-UI 3.0.5 resource set before implementation.*
