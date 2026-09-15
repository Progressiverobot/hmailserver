# Control Panel design system

The desktop Control Panel (`hmailserver/source/Tools/ControlPanel`, WPF on .NET 10 with WPF-UI 4.3) is being redesigned to read as one administration console - calm, dense enough for an administrator, the same on every page, and right in the light theme, the dark theme and High Contrast. This document is the contract the page waves build against: it names every token and every component, with its exact name and its public surface, and it says what the shell does around a page. It was written on 15 September 2026 with the first wave, which delivered the frame and migrated two pages - the sign-in page and the dashboard - as the pattern; a test (`ControlPanel.Tests/Services/DesignTokensTests.cs`) holds this document to the tokens file and the components, so a token or a component that is not here does not build.

Three files carry the design. `Services/DesignTokens.cs` holds the numbers and the colours as plain C#, with no WPF, so the tests can hold them; `Views/Scaffold/Tokens.xaml` declares the same values as resources for XAML; `Views/Scaffold/Scaffold.xaml` holds the components' templates, as implicit styles keyed by type, and merges the tokens. `App.xaml` merges `Scaffold.xaml` after WPF-UI's own dictionaries. `Services/ThemeTokens.cs` turns the colours into brushes on every theme change, and `Services/Typography.cs` still holds the type ramp the sizes below repeat. The three status colours themselves stay in `Services/StatusPalette.cs`, where `ColourVisionTests` holds every pair apart for colour-blind eyes.

## The frame

`MainWindow.xaml` is a WPF-UI `FluentWindow` with the Mica backdrop, in three rows: the title bar, the command bar, then the sidebar beside the content.

The **command bar** (`command-bar`) is `AppCommandBarHeight` tall and holds, left to right: the sidebar toggle (`sidebar-toggle`, its tool tip and accessible name saying what a click will do), the brand, the search (`nav-search` - a button drawn as a field, because what it opens is the Ctrl+K palette and that is where the typing happens; the shortcut still works), the server status pill (`server-status`, a `StatusPill` fed by `ServerSession.LinkStateChanged` and by sign-in: *Not connected* while the sign-in page shows, *Connected*, *Reconnecting…* while the session heals, *Connection lost* when it cannot; its accessible name is "Connected to host as user"), the theme toggle (`theme-toggle`) and the administrator's menu (`admin-menu`, showing the account's name once signed in and holding who is signed in where, the server version, the language, the theme and the About page). Nothing that was in the sidebar's footer is lost: the connection badge became the pill, the language and theme buttons moved into the bar.

The **sidebar** is the `TreeView` built from `Services/NavigationMap.cs` - nothing in the map changed, no page title changed, every automation id (`nav-<key>`, `navgroup-<slug>`) is what it was. Rows are `AppNavRowHeight` tall with the control radius, the hover fill and the brand pill on the selected row; pages are indented under their group's label rather than under its glyph, so the two text columns line up. Below `DesignTokens.Shell.CollapseSidebarBelow` (1000 pixels of window width) the sidebar collapses to the **rail** (`nav-rail`): one glyph per top-level group (`navrail-<slug>`), the group of the page on screen marked, a click expanding the sidebar on that group. The toggle collapses and expands it by choice above that width, and the choice is kept in the registry beside the window bounds (`SidebarCollapsed` under `HKCU\Software\hMailServer\ControlPanel`). The footer under the tree carries the server's version and hides with the rail.

The **content area** keeps the breadcrumb bar (`breadcrumb-bar`, the trail and the *Related:* signposts) above the page. A page owns its own `ScrollViewer` and pads itself with `AppPagePadding`; the host adds nothing. The window remembers its size, position and maximised state as before.

## Tokens

The numbers first. Every margin, padding and gap in a page is one of the six steps; a layout that needs a seventh has gone wrong. In C# the same values are `DesignTokens.Space.Xs` … `Xxl`, `DesignTokens.Radius.Control` / `Card` / `Pill`, `DesignTokens.Shell.CommandBarHeight` / `RailWidth` / `NavRowHeight` / `CollapseSidebarBelow` / `SidebarMinWidth` / `SidebarMaxWidth`, and `DesignTokens.Dialog.MinWidth` / `DefaultWidth` / `MaxWidth` / `MaxHeightFraction` (360, 520, 900, 0.9).

| Token | Value | What it is for |
|---|---|---|
| `AppSpaceXs` | 4 | The gap between a mark and its word, a label and its editor. |
| `AppSpaceSm` | 8 | The gap between buttons in a row, between a pill and a title. |
| `AppSpaceMd` | 12 | The gap between cards in a row, under a toolbar, between the rows of a form. |
| `AppSpaceLg` | 16 | Under a page header, the inset of a compact card. |
| `AppSpaceXl` | 24 | The page's side padding, between the sections of a settings page, a dialog's inset. |
| `AppSpaceXxl` | 32 | The inset of the sign-in card and of an empty or loading state. |
| `AppPagePadding` | 24,20,24,24 | The padding of a page's `ScrollViewer`. Every page uses it; a page with a padding of its own is a page that will look wrong beside the others. |
| `AppCardPadding` | 20,16 | A card's inset - the `Card` component's default and the old `Card` Border style's. |
| `AppHeaderGap` | 0,0,0,16 | The margin under a `PageHeader`. |
| `AppCardGap` | 0,0,0,12 | The margin under a card, a toolbar or a notice that another follows. |
| `AppFieldGap` | 0,0,0,12 | The margin under a `FieldRow`. |
| `AppSectionGap` | 0,0,0,24 | The margin under a `SettingsSection`. |
| `AppControlCornerRadius` | 4 | Controls, navigation rows, notices. |
| `AppCardCornerRadius` | 8 | Cards and dialogs. |
| `AppPillCornerRadius` | 999 | A capsule: the status pill. |
| `AppCommandBarHeight` | 48 | The command bar. |
| `AppRailWidth` | 48 | The sidebar collapsed to icons. |
| `AppNavRowHeight` | 34 | One navigation row, and the minimum height of a grid row. |

## Type

The ramp is Windows 11's: Caption 12, Body 14 (Body Strong is 14 SemiBold), Subtitle 20, Title 28. The sizes are resources and the roles are keyed `TextBlock` styles. They are keyed on purpose and must stay so: an implicit TextBlock style reaches into every control template and overrides the on-accent button text, which is how the forum's one look complaint arose (the note in `App.xaml` tells the story). The default text colour comes from property inheritance - the window and `FluentDialogWindow` set their Foreground to the theme's primary text brush - so a bare TextBlock needs no style to be readable.

| Token | Value | What it is for |
|---|---|---|
| `AppFontSizeCaption` | 12 | Captions, hints, field labels, the breadcrumb, the pill. |
| `AppFontSizeBody` | 14 | Body text, controls, navigation rows, card titles. |
| `AppFontSizeSubtitle` | 20 | A dialog's heading, the sign-in card's heading. |
| `AppFontSizeTitle` | 28 | The page title. |
| `TextTitle` | 28 SemiBold, primary, trimmed with an ellipsis | The page title; the `PageHeader` draws it. The older `PageTitle` style stays for the unmigrated pages and adds the level-1 heading and a margin. |
| `TextSubtitle` | 20 SemiBold, primary, wraps | A dialog's or a card-page's heading. |
| `TextBodyStrong` | 14 SemiBold, primary, wraps | Card titles, section headings, the summary line of a notice card. |
| `TextBody` | 14, primary, wraps | Body text. |
| `TextSecondary` | 14, secondary, wraps | The page subtitle, an empty state's sentence. |
| `TextCaption` | 12, secondary, wraps | Hints, descriptions, field labels, notes. |
| `TextCaptionTertiary` | 12, tertiary, wraps | The quietest text: a keyboard hint, a footer line. |

## Colour

Five status colours and a neutral, as brushes that `ThemeTokens.Refresh` recomputes on every theme change and republishes on the application's own dictionary, which shadows the dark values `Tokens.xaml` declares. Reference them with `DynamicResource`, never `StaticResource`, and in code with `SetResourceReference` by key (`StatusSemantics.For(level).BrushKey` gives the key for a level) rather than by holding a brush - a resolved brush keeps the colour of the theme it was resolved under. The light values each clear 4.5:1 on white; the dark values clear it on #1B1B1B. Under High Contrast every colour comes from the system palette and none is invented: brand and information are the highlight, the three status colours are the window text - their shape and word tell them apart, which is the rule below - neutral is the disabled-text grey, a card is the window colour with a window-text outline, and the notice tint goes to zero.

| Token | Light | Dark | High Contrast | What it is for |
|---|---|---|---|---|
| `AppBrandBrush` | #2F6FE0 | #4C8DFF | Highlight | The selection pill, the focused row's outline, the accent that is not a status. |
| `AppSuccessBrush` | #1A7F37 | #3FB950 | WindowText | Good: connected, done, passed. Square. |
| `AppWarningBrush` | #9A6700 | #D29922 | WindowText | Warning: a backlog, a certificate near expiry, a reconnect in progress. Triangle. |
| `AppDangerBrush` | #CF222E | #F85149 | WindowText | Critical: a failure, a lost link, a validation message. Cross. |
| `AppInfoBrush` | #6639BA | #A371F7 | Highlight | Information: a fact worth a notice. Diamond. Held at least ΔE 8 from the three above under protanopia and deuteranopia. |
| `AppNeutralBrush` | #57606A | #9DA7B0 | GrayText | A status that says nothing: not connected, a count of zero. |
| `AppCardBackgroundBrush` | #B3FFFFFF | #0FFFFFFF | Window | The card surface. Translucent, so it sits on Mica and on the page alike. |
| `AppCardBorderBrush` | #0F000000 | #14FFFFFF | WindowText | The card's outline. |
| `AppCardHoverBrush` | #80F9F9F9 | #15FFFFFF | Window | A card that can be pointed at - a tile, a rail item - under the pointer. |
| `AppDividerBrush` | #0F000000 | #14FFFFFF | WindowText | The hairline between a card's content and its footer, under the command bar, around the breadcrumb. |
| `AppNoticeTintOpacity` | 0.12 | 0.12 | 0 | The opacity of the status colour behind an `InlineNotice`. |

The rule that goes with the palette: **a status is never carried by colour alone.** `StatusSemantics.For(level)` gives every level a colour key, a shape (`ShapeMarkVisuals` draws it) and a word, and the components below draw all three and put the word first in their accessible name. A page that paints a value in a status colour adds the shape and the word itself, as the dashboard's queue badge does.

## Components

All in `hMailServer.ControlPanel.Views.Scaffold`; import it with `xmlns:scaffold="clr-namespace:hMailServer.ControlPanel.Views.Scaffold"` in XAML or `using hMailServer.ControlPanel.Views.Scaffold;` in code (a file that also imports `Wpf.Ui.Controls` will find `Card` ambiguous; qualify it). Each is a control with a template in `Scaffold.xaml`, so it works in XAML and in code alike; each is never a tab stop itself; an empty string set on a text slot is the same as null and collapses the slot; and each is an element of its own in the automation tree with the name given below - a bare WPF `Control` has no automation peer, and a card built from one would reach a screen reader as loose text.

| Component | What it is | Public surface |
|---|---|---|
| `PageHeader` | The top of every page: the title, an optional subtitle under it, an optional status pill beside the title, the page's actions on the right. It is the page's level-1 heading. | `Title`, `Subtitle` (string); `Status`, `Actions` (object slots). Automation: Text, HeadingLevel1, Name = Title, HelpText = Subtitle. |
| `Card` | The surface a page groups things on: an optional title and description above the content, an optional footer under a hairline, the card tokens for surface, border, radius and padding. Without a title it is the plain surface the old `Card` Border style gives. | `Title`, `Description` (string); `Footer` (object); `Content`; `Padding` (default `AppCardPadding`). Automation: Group, Name = Title. |
| `Toolbar` | The row above a grid: a search box, a slot for filters beside it, a slot for the actions on the right. | `SearchText` (string, two-way), `SearchPlaceholder` (string; also the box's accessible name), `ShowSearch` (bool); `Filters`, `Actions` (object slots); event `SearchTextChanged`; `FocusSearch()`. Automation: ToolBar. |
| `EmptyState` | What a grid shows with nothing in it: a glyph, one sentence saying so and what to do, the action that does it. Shown in the grid's place. | `Icon` (`SymbolRegular`, `Empty` for none), `Text` (string); `Action` (object). Automation: Text, Name = Text. |
| `LoadingState` | An indeterminate ring and a line, centred where the data will be; a polite live region. | `Text` (string, default *Loading…*). Automation: Text, Name = Text. |
| `InlineNotice` | A notice in the flow of a page at one of the status levels: the mark, the severity word, the sentence, room for an action; the level's colour as a tint behind and an outline around. Polite live region; set `AutomationProperties.LiveSetting="Assertive"` on one that must interrupt. | `Level` (`StatusLevel`, default Information), `Text` (string); `Content` (the action). Automation: Text, Name = "Word: text". |
| `StatusPill` | A status as a capsule: the level's mark and colour and a short word. | `Level` (`StatusLevel`), `Text` (string). Automation: Text, Name = "Word: text". |
| `FieldRow` | One field of a form: the caption (with its Alt key, wired to the editor through `Mnemonic`), the editor, an optional hint under it, an optional validation message under that, drawn with the danger colour, a cross and the words. The editor takes its accessible name from the caption and its help text from the hint and the message. | `Label`, `Hint`, `Error` (string; null clears the message); `Content` (the editor). Automation: Group, Name = the caption. |
| `SettingsSection` | A section of a settings page: a heading, a description, the fields. A level-2 heading under the page's level-1. Named SettingsSection because System.Windows.Documents has a Section and a dozen views import that namespace. | `Heading`, `Description` (string); `Content`. Automation: Group, HeadingLevel2, Name = Heading. |
| `DialogFrame` | The shape of a dialog's content: heading and description, a body that scrolls past the dialog's maximum height, a footer with a note on the left and the primary button before the secondary. | `Heading`, `Description` (string); `Body`, `Footer`, `PrimaryButton`, `SecondaryButton` (object). Automation: Pane, Name = Heading. |

The dialogs' frame is reached through `FluentDialogWindow`, the base class of every dialog: `UseFrame(heading, body, primary, secondary, description = null, width = 520)` builds a `DialogFrame`, makes it the window's content, gives Enter to the primary button and Escape to the secondary (or to closing the dialog when there is none), sizes the window to the content at that width within `DesignTokens.Dialog` (the height capped at 0.9 of the work area, the body scrolling past it) and puts the keyboard in the first field of the body on load. `SizeToContentWithin(width)` and `FocusFirstFieldOnLoad(root)` are its two halves, for a dialog that keeps its own layout. The eighteen dialogs that predate the frame keep their layouts and their typed-in widths until they are migrated; `Views/Dialogs.cs`, the message-box set, is untouched.

Grids are styled implicitly in `App.xaml`: the `DataGrid` chains to WPF-UI's own and has no grid lines, column headers only, rows at least `AppNavRowHeight` tall and a full-row selection; the row, cell and header styles are the ones the grids already had (a hairline under each row, a hover and selection fill, semibold low-key headers). A number column sets `ElementStyle="{StaticResource GridNumberText}"` and `HeaderStyle="{StaticResource GridNumberHeader}"`, which right-align its cells and its header.

## Rules

- **No new text without its seventeen translations.** Every caption is looked up by its English text (`Services/Loc.cs`), and `build/check-localisation.py`, `build/check-mnemonics.py` and `build/check-catalogues.py` fail the build on a caption missing from any catalogue, on a caption without an Alt key or with a colliding one, and on a translation that drops a token the reader types back. The cheapest redesign changes no text; a component's defaults are the only texts the scaffold adds.
- **No implicit TextBlock style**, for the reason under *Type*.
- **Brushes by DynamicResource or by key**, never by a held instance, for the reason under *Colour*.
- **Colour, shape and word** for every status, and the word first in the accessible name.
- **Every element keeps its automation name.** A `PageHeader` names the page, a `Card` its group, a `FieldRow` its editor; the ids the shell and the pages carry (`nav-<key>`, `breadcrumb-*`, `dashboard-*`) are what `build/capture-cp.ps1` and any automation hold on to and do not change.
- **One page, one `ScrollViewer`, padded with `AppPagePadding`.**

## Migrating a page

The dashboard (`Views/DashboardView.xaml`) is the pattern for a XAML page and the sign-in page (`Views/ConnectView.xaml`) for a form. A page changes in this order: the `ScrollViewer` takes `AppPagePadding`; the title and subtitle become a `PageHeader` (its `Subtitle` is set from code where the page updates it - the dashboard writes the time of the last refresh there); the page's buttons move into the header's `Actions`; every `Border` with the `Card` style becomes a `Card`, with its heading as the `Title`; a state shown as a mark and a word becomes a `StatusPill`; an error line becomes an `InlineNotice`; a label above an editor becomes a `FieldRow`, and the editor loses its placeholder where the caption says the same. A code-built page does the same with `new Card { Title = L("..."), Content = ... }`. Nothing about what the page reads or writes changes, and the tests that read the page's source for its settings (`SettingClaimsTests`, `SettingsSearchIndexTests`) keep passing because the setting keys and labels are not what moves.

## What the first wave left for the page waves

The thirty-five code-built pages and the twelve other XAML pages still use the `PageTitle`/`PageSubtitle` styles, the `Card` Border style and their own paddings (26,20 in most); the two long settings views (`ServerSettingsView.xaml.cs`, `FeatureSettingsView.xaml.cs`) build their cards and fields imperatively and are the natural home of `SettingsSection` and `FieldRow`; the list pages' hand-built search boxes are `Toolbar`s waiting to happen, and their `StatusText` overlays are `EmptyState`s; the eighteen dialogs have their own widths and footers and take `UseFrame`; the grids have their number columns still left-aligned until each names `GridNumberText`. The `LogsView`'s severity palette (`Log*Brush`) is the log viewer's own and is not a design token.
