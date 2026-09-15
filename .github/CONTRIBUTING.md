# Contributing to hMailServer

Thanks for your interest in contributing!

This project ships a [Code of Conduct](CODE_OF_CONDUCT.md) — by taking part you
agree to abide by it.

## Building

See [README.md](../README.md) for full build instructions. In short:

- Visual Studio 2026 (platform toolset v145), 64-bit Windows
- External libs (OpenSSL 4.0.x, Boost 1.92, PostgreSQL 18 libpq) built
  under a directory pointed to by the `hMailServerLibs` environment variable
  (`libraries\build-openssl.ps1`, `build-boost.ps1` and `build-pgsql.ps1` do this)
- Server solution: `hmailserver/source/Server/hMailServer/hMailServer.sln`
- Tools solution: `hmailserver/source/Tools/hMailServer Tools.sln`
- Helper scripts live in `build/` (`build.ps1`, `build-tests.ps1`, `run-tests.ps1`)

The compiler runs with `/WX` — code must build warning-free.

## Testing

All changes must keep the regression suite green. The suite runs against a
live local server instance over SMTP/IMAP/POP3. Setting a machine up for it -
the service, SQL Server Compact and the bench database, ClamAV, SpamAssassin,
the standard ports and the traps that have broken runs before - is written up
in [hmailserver/docs/RegressionEnvironment.md](../hmailserver/docs/RegressionEnvironment.md);
`build/preflight-tests.ps1` is the machine-readable version of the same recipe
and tells you exactly which step is missing.

## Small tasks

If you want to contribute and do not know where to start, the issues labelled
[`good first issue`](https://github.com/Progressiverobot/hmailserver/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22)
are real, bounded pieces of work, each of which closes a gap recorded in
`Roadmap.md` — none is make-work. Each says what it is for, where to look, how
to check it, and what "done" means. Comment on one to claim it; if it turns out
to be larger than it looked, say so on the issue rather than growing the change.

## Sign-off

Commit with `git commit -s`. That adds a `Signed-off-by: Your Name <you@example.com>`
trailer, which is your statement that you wrote the change (or have the right
to submit it) and that it may be distributed under this project's licence -
the [Developer Certificate of Origin](https://developercertificate.org/), the
whole text of which is eleven lines. There is no CLA. A pull request whose
commits lack the trailer fails the `DCO` check, which names the commit; the
fix is `git commit --amend -s` and a push.

## Pull Requests

- Branch from `master` (development branch). Version branches are bug-fix only.
- Keep changes focused; one logical change per PR.
- Add or update regression tests for behavior changes.
- Use parameterised SQL exclusively — never build SQL strings manually.
- New server-wide optional features should follow the INI-settings pattern:
  an `IniFileSettings` getter plus a control in the Server features dialog.
- Every pull request must pass the checks that run on it: the C# builds with
  `-warnaserror`, the hosted C++ server build, CodeQL (C#), dependency review,
  binary provenance, and the coding-style jobs - editorconfig (`.editorconfig`:
  spaces, width 3 for `.h`/`.cpp`/`.cs`), `dotnet format --verify-no-changes`, and
  `python3 build/add-license-headers.py --check` (every source file opens with the
  project's three-line header - the site, the copyright line and
  `SPDX-License-Identifier: AGPL-3.0-or-later` - and the script's other mode
  puts it there).
- Control Panel changes are held to five more checks in the same job: every static
  caption carries an Alt-key mnemonic (`build/check-mnemonics.py`); folder-access
  decisions stay in `ACLManager` (`build/check-authz-choke-point.py`); and, in the
  tree, a new caption is marked for translation (`L("_Save changes")`
  in C#, `{loc:L '_Save changes'}` in XAML), the English catalogue is regenerated
  with `python3 build/check-localisation.py --write`, and all 17 complete languages
  get a translation - an unmarked or untranslated caption fails CI
  (`build/check-localisation.py`, `build/check-catalogues.py`); every INI setting
  the server reads has a Control Panel editor (`build/check-ini-coverage.py`).

## A new setting goes in the database

The rule, from 15 September 2026: **a setting belongs in the database, not in `hMailServer.ini`.**

Put it in `hm_settings` - the store the COM `Settings` object and the Control Panel's classic pages have
always used, reached through `Property` and `PropertySet` - and give it a Control Panel editor, which
`build/check-ini-coverage.py` already requires of every key in the file. Do not add a key to the
`[Settings]` section.

The file keeps only what is needed to reach the database, or to be let in without one:
`[Directories]`, `[Database]`, and `[Security]`'s administrator password and second-factor secret. If
something genuinely has to be readable before the database is open, that is the exception - say so in the
commit message, and expect to be asked.

Why, in one line each:

* Two nodes cannot share a file, and shared settings are what an active-active pair needs.
* A configuration backup that does not include the file is not a backup of the configuration.
* A Control Panel or a Control Deck on another machine has no share to that file.
* A change to a file setting is saved now and applied at the next service start; a change in the
  database can be published to the running server.

The 238 keys already in the file are being migrated - `Roadmap2.md`, section 13 - and `hm_inisettings`
(schema 6011) already mirrors them, so the work is flipping which store is the truth rather than moving
them one at a time. `IniFileSettings` is the seam that makes that possible, and
`build/check-ini-coverage.py` proves on every pull request that every section is read through it.

## Looking at the two browser pages

The webmail (`/portal`) and the browser Control Deck (`/`) are pages a person uses, and a page is judged by looking at it. `.mcp.json` in the repository root declares one Model Context Protocol server for that: **Playwright** (`@playwright/mcp`, pinned), which drives a real Chromium against a running server so an agent - or a contributor pairing with one - can open a view, sign in, click through a flow, take a screenshot and read the accessibility tree rather than guess from the source. It is optional: nothing in the build, the tests or CI uses it, and a checkout that never starts it behaves exactly as before.

Point it at a server you are already running (`https://localhost:8045/portal` on the bench). Nothing else is declared, deliberately:

* **A design-tool server** (Figma and the like) would need designs in that tool, and this project has none - the desktop console's design system is `hmailserver/docs/ControlPanelDesign.md` and the pages are hand-written.
* **Component-library servers** (shadcn, Tailwind and friends) describe a stack this project does not use: the webmail and the Deck are plain markup, one stylesheet and one script each, embedded in the binary, and the Control Panel is WPF.
* **A third-party accessibility server** would put an unvetted npm package inside the repository with an agent driving it. Accessibility belongs in a check this project owns and runs in CI, which is a roadmap row rather than a convenience.

The repository's own harnesses stay the authority on behaviour: `build/check-portal-script.py` and `build/check-deck-script.py` run each page's script against a small DOM and a recorded server, with no browser and no network, and they run on every pull request. A browser is for what a harness cannot see - how it looks, and whether it can be used.

## Architecture

The layering is BO → Persistence → SQL, with Cache in front of the hot reads.
All configuration and management goes through the COM API in `Server/COM/` —
that is the seam the GUI, the test suite and external scripts all use.
Networking is Boost.Asio, wrapped by `Server/Common/TCPIP/`.

## License

By contributing you agree that your contributions are licensed under the
[AGPLv3](../LICENSE).
