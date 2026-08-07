# Contributing to hMailServer

Thanks for your interest in contributing!

This project ships a [Code of Conduct](CODE_OF_CONDUCT.md) — by taking part you
agree to abide by it.

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
runs against a live local server instance over SMTP/IMAP/POP3. The full
environment recipe (SQL CE, ClamAV, SpamAssassin, INI settings) is not
published — open an issue and we will walk you through it.

## Pull Requests

- Branch from `master` (development branch). Version branches are bug-fix only.
- Keep changes focused; one logical change per PR.
- Add or update regression tests for behavior changes.
- Use parameterised SQL exclusively — never build SQL strings manually.
- New server-wide optional features should follow the INI-settings pattern:
  an `IniFileSettings` getter plus a control in the Server features dialog.

## Architecture

The layering is BO → Persistence → SQL, with Cache in front of the hot reads.
All configuration and management goes through the COM API in `Server/COM/` —
that is the seam the GUI, the test suite and external scripts all use.
Networking is Boost.Asio, wrapped by `Server/Common/TCPIP/`.

## License

By contributing you agree that your contributions are licensed under the
[AGPLv3](../LICENSE).
