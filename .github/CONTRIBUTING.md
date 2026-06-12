# Contributing to hMailServer

Thanks for your interest in contributing!

## Building

See [README.md](../README.md) for full build instructions. In short:

- Visual Studio 2026 (platform toolset v145), 64-bit Windows
- External libs (OpenSSL 4.0.x, Boost 1.91, PostgreSQL 18 libpq) built
  under a directory pointed to by the `hMailServerLibs` environment variable
- Server solution: `hmailserver/source/Server/hMailServer/hMailServer.sln`
- Tools solution: `hmailserver/source/Tools/hMailServer Tools.sln`
- Helper scripts live in `build/` (`build.ps1`, `build-tests.ps1`, `run-tests.ps1`)

The compiler runs with `/WX` — code must build warning-free.

## Testing

All changes must keep the regression suite green (898 tests). The suite
runs against a live local server instance over SMTP/IMAP/POP3 — see
[IMPLEMENTATION-NOTES.md](../IMPLEMENTATION-NOTES.md) for the full test
environment recipe (SQLCE, ClamAV, SpamAssassin, INI settings).

## Pull Requests

- Branch from `master` (development branch). Version branches are bug-fix only.
- Keep changes focused; one logical change per PR.
- Add or update regression tests for behavior changes.
- Use parameterised SQL exclusively — never build SQL strings manually.
- New server-wide optional features should follow the INI-settings pattern
  (`IniFileSettings` getter + Server features dialog) described in
  [AGENTS.md](../AGENTS.md).

## Architecture

Read [AGENTS.md](../AGENTS.md) for the codebase guide: layering
(BO → Persistence → SQL, Cache in front), the COM API seam, Boost.Asio
networking, and the directory map.

## License

By contributing you agree that your contributions are licensed under the
[AGPLv3](../LICENSE).
