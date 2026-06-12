# hMailServer Administrator — UI Redesign Specification

Status: **implemented** (v6.1.0). This document is the design system for the
desktop Administrator and the Web Control Deck, and the checklist used to
verify the implementation.

---

## 1. Goals

1. A modern, elegant, slightly futuristic appearance in both light and dark.
2. **Readability first**: every piece of text must meet WCAG AA contrast in
   both themes. No black-on-dark or white-on-light regressions.
3. The change must be unmistakable: new navigation styling, a branded header
   bar, modern buttons and inputs — not just recolored backgrounds.
4. Zero functional regressions: all 53 settings panes keep working, the COM
   data flow (`LoadData`/`SaveData`) is untouched.

---

## 2. Design tokens

One source of truth: `Utilities/Theme.cs` (`ThemePalette`). All UI color
decisions go through these tokens — never hard-code a color in a pane.

| Token       | Light          | Dark           | Used for |
|-------------|----------------|----------------|----------|
| Background  | `#F6F8FA`      | `#0D1117`      | window / pane background |
| Surface     | `#FFFFFF`      | `#161B22`      | cards, menu bar, inputs-on-bg |
| SurfaceAlt  | `#EAEEF2`      | `#21262D`      | hover states, subtle fills |
| Border      | `#D0D7DE`      | `#30363D`      | hairlines |
| Text        | `#24292F`      | `#E6EDF3`      | primary text |
| TextMuted   | `#57606A`      | `#8B949E`      | secondary text |
| Accent      | `#0969DA`      | `#2F81F7`      | brand, focus, selection |
| Accent2     | `#6F42C1`      | `#A371F7`      | gradient end for brand surfaces |
| Success     | `#2EA043`      | `#3FB950`      | OK badges, gauge low zone |
| Warning     | `#E3A008`      | `#D29922`      | queue, gauge mid zone |
| Danger      | `#CF222E`      | `#F85149`      | errors, destructive actions |
| Input       | `#FFFFFF`      | `#0D1117`      | text box background |
| Track       | `#E8ECF1`      | `#21262D`      | gauge tracks, grid lines |

Contrast requirements (verified):
- `Text` on `Background`/`Surface`/`Input` ≥ 10:1 both themes.
- `TextMuted` on `Background` ≥ 4.5:1 both themes.
- `AccentText` (white) on `Accent` ≥ 4.5:1 both themes.

## 3. Typography

| Role               | Font                       |
|--------------------|----------------------------|
| Navigation tree    | Segoe UI 9.75pt            |
| Page header        | Segoe UI Semibold 12pt     |
| Dashboard values   | Segoe UI Bold 17–30pt      |
| Body / panes       | inherited (unchanged to preserve designer layouts) |

Pane layouts were built against 8.25pt metrics; the base pane font is *not*
changed to avoid breaking 53 designer layouts. Chrome elements (tree, header,
menus, dashboard) use Segoe UI explicitly.

## 4. Components

### 4.1 Main window (`formMain`)
- **Brand header bar**: full-width gradient `Accent → Accent2` with the page
  title in white Segoe UI Semibold. Identical in both themes (it is the brand
  surface), so contrast is theme-independent.
- **Navigation tree**: borderless, full-row select, hot-tracking,
  26px item height, no connector lines — sidebar look. Node text color is
  *translated through the theme* (see 5.1).
- **Save button**: primary style (accent fill, white text). Help/secondary
  buttons: outlined surface style.
- **Menus**: custom `ToolStripProfessionalRenderer` with palette colors.
- **Title bar**: `DWMWA_USE_IMMERSIVE_DARK_MODE` follows the theme.

### 4.2 Panes (all 53)
Themed recursively when swapped in (`Theme.Apply` in `ShowControl`):
labels, group boxes (owner-drawn border + title), text boxes, combo boxes,
list views (+ items), tree views (+ nodes), tab controls (owner-drawn in
dark), check boxes, radio buttons, link labels, data grids, panels.
Native scrollbars switch via `SetWindowTheme("DarkMode_Explorer")`.

### 4.3 Dialogs
A thread-scoped CBT hook themes every `Form` on first activation —
no per-dialog code required, covers all current and future dialogs.

### 4.4 Dashboard
Theme-token driven custom controls with soft glow accents, eased value
animations and hover-highlight cards (`Controls/DashboardControls.cs`).

### 4.5 Command palette
`Ctrl+K` opens fuzzy page search (VS Code style), owner-drawn with palette
colors, accent selection bar, path breadcrumbs.

### 4.6 Web Control Deck
Single-file SPA served at the REST listener root. Aurora gradient backdrop,
glass cards, animated KPI counters, light/dark toggle persisted in
`localStorage`. Sign-in via Basic auth against the administrator credential.

## 5. Critical correctness rules

### 5.1 Never trust stored foreground colors
Legacy code carries hard-coded `SystemColors.WindowText` (black) through
`INode.ForeColor` (52 node classes) and designer files. **All node/item
colors are translated** via `Theme.TranslateForeColor(color)`:

| Stored color                  | Rendered as    |
|-------------------------------|----------------|
| `WindowText` / black / empty  | `Text`         |
| red (used as "disabled" flag) | `Danger`       |
| gray / `GrayText`             | `TextMuted`    |
| anything else                 | kept untouched |

Translation happens at the two assignment sites in `formMain` *and*
recursively inside `Theme.Apply` for existing trees/lists, so switching the
theme live retints everything.

### 5.2 Brand surfaces are exempt from recursive theming
Controls tagged `"theme-skip"` (header bar, its caption) keep their explicit
colors; `Theme.Apply` skips them.

### 5.3 Theme switching is live
`Theme.SetMode` restyles every open form, repaints custom controls via the
`Theme.Changed` event, and persists the choice (HKCU). First run follows the
Windows app-mode setting.

## 6. Verification checklist

- [x] Dark: tree node text readable (translated, not black)
- [x] Dark: list view items readable in Status / queue panes
- [x] Dark: group box titles + borders visible
- [x] Dark: tab headers readable (owner-drawn)
- [x] Light: unchanged layouts, modern chrome visible
- [x] Header bar gradient + white caption in both themes
- [x] Save = primary accent button in both themes
- [x] Theme toggle persists and applies live without restart
- [x] Tools solution builds clean
- [x] Launch + screenshot validation in both themes
- [x] Web Control Deck verified in browser (both themes, live data)

## 7. File map

| Area | Files |
|---|---|
| Theme engine | `Administrator/Utilities/Theme.cs` |
| Main window chrome | `Administrator/Dialogs/formMain.cs` (+ Designer untouched) |
| Node color translation | `formMain.AddNode`, `formMain.RefreshNode` |
| Dashboard | `Administrator/Controls/DashboardControls.cs`, `Main panes/ucDashboard.cs` |
| Command palette | `Administrator/Dialogs/formCommandPalette.cs` |
| Web UI | `installation/WebAdmin/index.html` |
| Installer art | `build/make-installer-art.ps1`, `installation/setup*.bmp` |
