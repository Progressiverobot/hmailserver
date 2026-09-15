# Control Panel design system

The desktop Control Panel (`hmailserver/source/Tools/ControlPanel`, WPF on .NET 10 with WPF-UI 4.3) is being redesigned to read as one administration console - calm, dense enough for an administrator, the same on every page, and right in the light theme, the dark theme and High Contrast. This document is the contract the page waves build against: it names every token and every component, with its exact name and its public surface, and it says what the shell does around a page. It was written on 15 September 2026 with the first wave, which delivered the frame and migrated two pages - the sign-in page and the dashboard - as the pattern, and the other three waves followed it the same day: every page, both long settings views and all eighteen dialogs are on it now; a test (`ControlPanel.Tests/Services/DesignTokensTests.cs`) holds this document to the tokens file and the components, so a token or a component that is not here does not build.

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
| `AppFormWidth` | 760 | The widest a form is laid out, whatever the window does: a caption and its editor at opposite ends of a maximised window are unreadable. `DesignTokens.FormWidth`. |
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
| `PageHeader` | The top of every page: the title, an optional subtitle under it, an optional status pill beside the title, the page's help button and the page's actions on the right. It is the page's level-1 heading. | `Title`, `Subtitle` (string); `Status`, `Actions` (object slots); `TourId` (string, null for no help button - the shell sets it as it opens a page, from `TourCatalog`); event `ShowTour`, which bubbles to the shell. Automation: Text, HeadingLevel1, Name = Title, HelpText = Subtitle; the help button is `page-help`. |
| `Card` | The surface a page groups things on: an optional title and description above the content, an optional footer under a hairline, the card tokens for surface, border, radius and padding. Without a title it is the plain surface the old `Card` Border style gives. | `Title`, `Description` (string); `Footer` (object); `Content`; `Padding` (default `AppCardPadding`). Automation: Group, Name = Title. |
| `Toolbar` | The row above a grid: a search box, a slot for filters beside it, a slot for the actions on the right. | `SearchText` (string, two-way), `SearchPlaceholder` (string; also the box's accessible name), `ShowSearch` (bool); `Filters`, `Actions` (object slots); event `SearchTextChanged`; `FocusSearch()`. Automation: ToolBar. |
| `EmptyState` | What a grid shows with nothing in it: a glyph, one sentence saying so and what to do, the action that does it. Shown in the grid's place. | `Icon` (`SymbolRegular`, `Empty` for none), `Text` (string); `Action` (object). Automation: Text, Name = Text. |
| `LoadingState` | An indeterminate ring and a line, centred where the data will be; a polite live region. | `Text` (string, default *Loading…*). Automation: Text, Name = Text. |
| `InlineNotice` | A notice in the flow of a page at one of the status levels: the mark, the severity word (collapsed at `Normal`, where the level says nothing and the word would read as a status that is not one), the sentence, room for an action; the level's colour as a tint behind and an outline around. Polite live region; set `AutomationProperties.LiveSetting="Assertive"` on one that must interrupt. | `Level` (`StatusLevel`, default Information), `Text` (string); `Content` (the action). Automation: Text, Name = "Word: text". |
| `StatusPill` | A status as a capsule: the level's mark and colour and a short word. | `Level` (`StatusLevel`), `Text` (string). Automation: Text, Name = "Word: text". |
| `FieldRow` | One field of a form: the caption (with its Alt key, wired to the editor through `Mnemonic`), the editor, an optional hint under it, an optional validation message under that, drawn with the danger colour, a cross and the words. The editor takes its accessible name from the caption and its help text from the hint and the message. | `Label`, `Hint`, `Error` (string; null clears the message); `Content` (the editor). Automation: Group, Name = the caption. |
| `SettingsSection` | A section of a settings page: a heading, a description, the fields. A level-2 heading under the page's level-1. Named SettingsSection because System.Windows.Documents has a Section and a dozen views import that namespace. | `Heading`, `Description` (string); `Content`. Automation: Group, HeadingLevel2, Name = Heading. |
| `TourOverlay` | The ring a tour draws round the control a step points at, and the card beside it with the sentence, *Step n of m*, Back, Next (or Finish on the last stop) and Skip. Built in code rather than templated, because it is not laid out in a page but floats over one; it is a `Canvas` with no background, so it is hit-testable only where the card is. | `ShowStep(target, tourName, position, sentence, canGoBack, lastStep)`, `Hide()`, `Reposition()`, `FocusCard()`; `Showing`, `HasFocusWithin`; events `Next`, `Back`, `Skip`. Automation: the card is a polite live region named "tour. position. sentence". |
| `DialogFrame` | The shape of a dialog's content: heading and description, a body that scrolls past the dialog's maximum height, a footer with a note on the left and the primary button before the secondary. | `Heading`, `Description` (string); `Body`, `Footer`, `PrimaryButton`, `SecondaryButton` (object). Automation: Pane, Name = Heading. |

The dialogs' frame is reached through `FluentDialogWindow`, the base class of every dialog: `UseFrame(heading, body, primary, secondary, description = null, width = 520)` builds a `DialogFrame`, makes it the window's content, gives Enter to the primary button and Escape to the secondary (or to closing the dialog when there is none), sizes the window to the content at that width within `DesignTokens.Dialog` (the height capped at 0.9 of the work area, the body scrolling past it) and puts the keyboard in the first field of the body on load. `SizeToContentWithin(width)` and `FocusFirstFieldOnLoad(root)` are its two halves, for a dialog that keeps its own layout. All eighteen dialogs are on the frame since 15 September 2026, and none of them names a width any more: each is sized to its content within `DesignTokens.Dialog`. `Views/Dialogs.cs`, the themed message boxes, and `Views/NavigationPalette.cs` are on it too. Two helpers live in `Views/DialogFields.cs` beside them rather than in the base class, which is where they belong and where they should move: `FitTabs` sizes a tabbed dialog to its tallest tab and `FillTabs` gives every tab one height, both measuring each tab's content on its own - selecting a tab and measuring the `TabControl` reports the tab before it, because a control outside a visual tree reaches its content presenter a pass later, and that under-measurement hid the whole add-row of one dialog's address tab until a render caught it.

Grids are styled implicitly in `App.xaml`: the `DataGrid` chains to WPF-UI's own and has no grid lines, column headers only, rows at least `AppNavRowHeight` tall and a full-row selection; the row, cell and header styles are the ones the grids already had (a hairline under each row, a hover and selection fill, semibold low-key headers). A number column sets `ElementStyle="{StaticResource GridNumberText}"` and `HeaderStyle="{StaticResource GridNumberHeader}"`, which right-align its cells and its header.

## Rules

- **No new text without its seventeen translations.** Every caption is looked up by its English text (`Services/Loc.cs`), and `build/check-localisation.py`, `build/check-mnemonics.py` and `build/check-catalogues.py` fail the build on a caption missing from any catalogue, on a caption without an Alt key or with a colliding one, and on a translation that drops a token the reader types back. The cheapest redesign changes no text; a component's defaults are the only texts the scaffold adds.
- **No implicit TextBlock style**, for the reason under *Type*.
- **Brushes by DynamicResource or by key**, never by a held instance, for the reason under *Colour*.
- **Colour, shape and word** for every status, and the word first in the accessible name.
- **Every element keeps its automation name.** A `PageHeader` names the page, a `Card` its group, a `FieldRow` its editor; the ids the shell and the pages carry (`nav-<key>`, `breadcrumb-*`, `dashboard-*`) are what `build/capture-cp.ps1` and any automation hold on to and do not change.
- **One page, one `ScrollViewer`, padded with `AppPagePadding`.**
- **A tour is never the only way to do anything, and never blocks the product while it runs.** See *Tours* below.

## Tours

A tour teaches a page while somebody uses it. One mechanism, the same shape in all three surfaces - this
Control Panel, the browser Control Deck (`hmailserver/installation/WebAdmin/index.html`) and the webmail
(`Server/Common/Util/Portal.html` and `Portal.js`) - so that the three can be reasoned about, and read in
the store, with one explanation.

**A step** is an element, one sentence, an optional condition that ends the step when the thing has
happened, and what to do when the element is not there. The element is named by its automation id in the
Control Panel and by its element id (or, in the Deck, a stable selector) in the two pages - never by a
position, because a step pinned to "the third button" survives nothing. A step that cannot find its element
is **skipped**, unless it is marked as one the rest of the tour is meaningless without, in which case the
tour ends *saying so*. A step never acts: it does not press the button it points at and it does not fill
the field. The only thing it does besides pointing is open the page or view its element lives on, because
an element on a page nobody has opened is in no visual tree to be found in.

**A tour** is an ordered list of steps with an id, a name and a version. The id is what the finished record
is kept under, so renaming one abandons every reader's record of having done it; the version is raised when
the steps change enough that somebody who finished the old tour has not seen the new one.

**Where it is kept.** Which tours this person has finished, and where they stopped in one they left, as one
line: `firstrun=1|firstrun@3v1` - the finished tours with the version each was finished at, then the resume
points as *step* `v` *version*. The Control Panel keeps it in `HKCU\Software\hMailServer\ControlPanel`
under `TourProgress`, beside the window bounds, the palette's history and the sidebar's collapsed state; the
Deck in `localStorage` under `hmsTours`; the webmail in the account's preferences under `tours`
(`/api/v1/me/preferences`), so that a reader who starts the walk on a laptop finishes it on a phone. Every
kind of damage to the stored line is survivable and silent in all three: the worst it can cost is being
offered a tour twice.

**The rules**, which are the feature rather than decoration:

- **Never modal, and never in the way.** The ring takes no pointer events and the card is the only thing on
  its layer that does, so every control underneath - the very one being pointed at - stays usable while a
  tour runs. A tour is never the only way to do anything.
- **Never takes the keyboard.** Nothing is focused when a step opens, and the focus the tour found is put
  back when it ends. The card's buttons are ordinary tab stops; `F6` reaches them from anywhere, and
  `Escape` ends the tour **only** while the keyboard is already inside the card - everywhere else Escape
  still belongs to the page.
- **No Alt keys on the card.** The one place in this application where a caption carries no access key. The
  card floats over whichever page the step is on, so any key it claimed would sooner or later be a key that
  page already owns, and an access key that does the wrong thing on one page in forty-seven is worse than
  none.
- **The highlight is not colour alone.** A solid ring in the accent and a dashed one inside it in the text
  colour, so the highlight survives greyscale, colour blindness and High Contrast - where the accent becomes
  the system highlight and the dashes are what still separate the ring from an ordinary focus rectangle.
- **The sentence is announced.** The card is a polite live region and the Control Panel raises a UI
  Automation notification with the sentence as each step opens, so the tour is usable without sight and
  without the focus being taken to make that happen.
- **It is the change that ends a step, not the state.** A step whose condition was already true when the
  reader arrived stays until they press Next; somebody who already has a domain should read the sentence
  about domains rather than watch the step vanish before they have read it.
- **Every ending says something.** Finished, left, or stopped because a step it needed was not on the
  screen - each is a sentence. Silence after a tour that stopped on its own is how somebody decides the
  feature is broken.
- **Every sentence goes through the catalogues**, like every other text: seventeen languages here, twenty in
  the Deck, twenty-three in the webmail.

**Where the logic lives.** In the Control Panel, everything that can be decided without a window is in
`Services/Tour.cs` (the steps and the catalogue), `Services/TourSession.cs` (the stepper - which step is
showing, what Next and Back do, when and why the tour ended) and `Services/TourProgress.cs` (the record),
all three compiled into `ControlPanel.Core` and tested without WPF, exactly as `ConnectionStatus` and
`SettingsPageStatus` are. `MainWindow.Tour.cs` is the half that genuinely needs a window: opening the page a
step is on, finding the control by its automation id, keeping the ring on it while the page moves
underneath, and answering the conditions from the server. The two browser pages keep the same division by
convention rather than by compilation, and `build/deck-script-test.js` and `build/portal-script-test.js`
execute theirs.

**The ways in.** A tour is offered on the Welcome page (a button in the header and, until the walk has been
finished, a notice above the intents), in the Ctrl+K palette as a row of its own above the page list, and by
a help button in the header of any page a tour visits - which starts that tour at its first stop *on that
page*, so the button teaches the page it is on. The Deck's is the `?` button in its header and the
webmail's is *Show me around* in the account menu; both behave the same way.

## Migrating a page

The dashboard (`Views/DashboardView.xaml`) is the pattern for a XAML page and the sign-in page (`Views/ConnectView.xaml`) for a form. A page changes in this order: the `ScrollViewer` takes `AppPagePadding`; the title and subtitle become a `PageHeader` (its `Subtitle` is set from code where the page updates it - the dashboard writes the time of the last refresh there); the page's buttons move into the header's `Actions`; every `Border` with the `Card` style becomes a `Card`, with its heading as the `Title`; a state shown as a mark and a word becomes a `StatusPill`; an error line becomes an `InlineNotice`; a label above an editor becomes a `FieldRow`, and the editor loses its placeholder where the caption says the same. A code-built page does the same with `new Card { Title = L("..."), Content = ... }`. Nothing about what the page reads or writes changes, and the tests that read the page's source for its settings (`SettingClaimsTests`, `SettingsSearchIndexTests`) keep passing because the setting keys and labels are not what moves.

## Where the migration stands

Every page is on the frame since 15 September 2026, in three waves after the first: the twelve remaining XAML pages and the code-built pages (wave 2), `ServerSettingsView` with its 31 tabs and 53 cards (wave 3), and `FeatureSettingsView` with all eighteen dialogs, the message boxes and the palette (wave 4). No page title, no tab header, no setting caption and no automation id moved in any of them, which is what kept the search index, the Ctrl+K palette and `SettingClaimsTests`/`SettingsSearchIndexTests` passing throughout.

What the waves added beside the components, because a page needed it and a component did not offer it:

* `Services/StatusText.cs` and `Services/GridStyles.cs` - the second because the keyed grid styles are XAML resources and a third of the pages build their columns in C#; `GridStyles.Number(column)` is the C# way to say `GridNumberText`. Both are candidates for the scaffold.
* `Services/SettingsPageStatus.cs` and `Services/ServerSettingsPageStatus.cs` - the word and the severity of a settings page's pill, per state. Two types because there are two lifecycles: an hMailServer.INI page is read, written and then waits on a service restart (*Editing, Unavailable, Saved, Restarting, RestartFailed, NotConnected, Applied*), and the server settings page is read and written over COM and takes effect as it saves (*Clean, Unsaved, Saving, Saved, PartlySaved*). Both are WPF-free and both are held to a distinct word per state by a test.
* `Views/DialogFields.cs` - `Field`, the editors, and the two tab-sizing helpers described above.
* `Services/DialogLayout.cs` - the arithmetic that sizes a tabbed dialog within the work area.

What the waves asked for and did not get, in the order they are worth doing:

1. **`FitTabs`, `FillTabs` and the framed-prompt helper belong in `FluentDialogWindow`.** They are in `DialogFields` only because `UseFrame` is protected and the static prompts build a bare window.
2. **`Card` has no status slot and no heading level.** `PageHeader` has a status slot; a card that needs one puts the pill in the first row of its content. A settings card *is* a section, so it is a title-less `Card` wrapping a `SettingsSection` to get the level-2 heading - one unnamed group around one named one.
3. **`SettingsSection` and `FieldRow` carry a default margin** (`AppSectionGap`, `AppFieldGap`) that a caller inside a card has to zero. A default most callers clear is in the wrong place.
4. **`FieldRow` cannot say "the check box is its own caption".** Passing `Label = null` leaves the row's automation group unnamed.

Two things are deliberately not migrated and are not defects: `AccessibleChartCard`, `AccountTwoFactorPanel` and `AppPasswordsPanel` still use the old `Card` Border style, which is why `App.xaml` keeps it - a title-less `Card` draws the same surface, so they are identical on screen; and the `LogsView`'s severity palette (`Log*Brush`) is the log viewer's own and is not a design token.
