Third-party binaries
====================

This repository contains 40 committed binary files — DLLs, two MSIs, an EXE and
two type libraries. A committed binary is a file nobody can review. It arrives
in a pull request as `Binary files differ`, and from that point on the only
thing anyone knows about it is its name.

This document is the record of what each one is, where it came from, why it is
here, and whether it should be. The machine-readable half is
[third-party-binaries.json](third-party-binaries.json), and the
`Binary provenance` workflow
([.github/workflows/verify-binary-provenance.yml](../../.github/workflows/verify-binary-provenance.yml))
fails the build if the two ever disagree with the tree.

The division of labour between the two files is deliberate. The JSON holds the
SHA-256 hashes and is the only place they appear; this document explains and
never repeats them. Two copies of forty hashes would diverge, and the copy
people read would be the wrong one.

The short version
-----------------

| | |
|---|---|
| Committed binaries | 40 |
| Should be deleted outright — nothing references them | **3** |
| Byte-identical duplicates of another committed file | **10** |
| Genuinely required in the tree | 13 |
| Required today, but a removal or refresh candidate | 13 |
| Generated from this repository's own source | 1 |

Thirteen of the forty — a third — are there by accident rather than by need.
None of the three unused ones is loaded by anything: they are the residue of
an x86 installer branch that was deleted, a downloader that is declared and
never called, and a copy of a Windows 2000 system DLL.

What the build already does right
---------------------------------

The three largest and most security-relevant native dependencies — OpenSSL,
Boost and PostgreSQL's libpq — are **not** in this repository at all. They live
outside the tree under `%hMailServerLibs%`, and
[post-build.bat](../source/Server/hMailServer/post-build.bat) copies the built
DLLs into the output directory. That is why Scorecard does not flag them, and
it is the model the rest of this list should be judged against.

It is worth being precise about one thing, because the file name invites the
opposite assumption: `libraries/build-dependencies.ps1` **downloads nothing**.
It builds Boost from a directory the developer has already populated. No script
in this repository fetches any of the 40 files below. Every one of them is
committed, and every one of them is used directly from the tree.

So the answer to "which of these could simply be fetched instead of committed"
is: mechanically, none are today. The interesting question is which *should*
move to the out-of-tree model, and that is answered per component below.

Policy
------

**1. A binary may be committed only if it is listed in
`third-party-binaries.json`.** CI enforces this. An entry must give the
SHA-256, the component and version, the publisher, the licence, where upstream
it came from, what uses it, and why it cannot be obtained another way.

**2. Changing a committed binary means changing its manifest entry in the same
commit.** The hash check turns an unreviewable binary diff into a reviewable
text diff. That text diff is the review.

**3. Prefer, in this order:**

1. Not committing it — take it from the toolchain or from `%hMailServerLibs%`
   at build time, as OpenSSL, Boost and libpq already are.
2. Committing the vendor's *signed* original and extracting at build time, so
   the signature is the provenance rather than a hash we wrote down ourselves.
3. Committing the file with a recorded hash and upstream URL — this document.

**4. A binary with no publisher, no version resource and no known upstream is
not acceptable long-term.** There is exactly one of those, and it is named
below rather than quietly tolerated.

**5. Record what you verified, not what you assume.** Every version, publisher
and signature status in the manifest was read out of the files themselves.
Where an upstream URL could not be confirmed, the manifest says where to look
rather than inventing a link.

Authenticode: what is actually signed
-------------------------------------

Twelve of the forty carry a valid Authenticode signature. Twenty-eight do not.

| Signed by | Files |
|---|---|
| `CN=Microsoft Windows Software Compatibility Publisher` | 10 (the MSVC runtime) |
| `CN=Microsoft Corporation` | 2 (the SQL CE MSIs) |
| *unsigned* | 28 |

The twenty MariaDB Connector/C DLLs are unsigned, which is why the recorded
hash matters most for exactly those files: there is no other way to tell that
the `libmysql.dll` in this tree is the one MariaDB shipped. The two `.tlb`
files cannot carry an Authenticode signature at all — a type library is not a
signable image format — so their entry is a hash and nothing else by necessity.

For the twelve that *are* signed, a signature check is stronger than a hash pin
and survives a legitimate vendor patch bump. The CI job here does not do it,
because it runs on Linux in seconds and `Get-AuthenticodeSignature` is
Windows-only; see "Not yet done" below.

The inventory
-------------

Grouped by component. Per-file hashes are in
[third-party-binaries.json](third-party-binaries.json).

### Delete these — nothing loads them

| Component | Files | Finding |
|---|---|---|
| `installation/System Files/dnsapi.dll` | 1 | Windows 2000 DNS Client API, file version 5.00.2195.6680. No `.iss` file references it and no build step copies it. `RMSPF.cpp` line 415 calls `LoadLibrary("DNSAPI.DLL")`, which resolves against System32 and never loads this copy. A 23-year-old OS DLL, committed, shipped nowhere — and a DLL-planting hazard the day someone "helpfully" copies it next to the executable. |
| `installation/isxdl.dll` | 1 | ISX Download DLL 5.1.5.0. `hMailServerInnoExtension.iss` lines 97–119 declare all eight of its exports; **not one is called**. It is still packed into the installer. Deleting it removes an unsigned, unmaintained HTTP downloader from a payload that runs with installer privileges. |
| `installation/SQLCE/SSCERuntime_x86-ENU.msi` | 1 | Microsoft SQL Server Compact 4.0 SP1 (x86). The installer is x64-only, and the comment at `hMailServerInnoExtension.iss` line 839 records that the x86 branch was removed. Never staged, never executed. 8.7 MB. |

### Byte-identical duplicates

| Component | Files | Finding |
|---|---|---|
| `installation/Extras/libmysql.dll` and `Extras/plugin/*.dll` | 10 | Verified byte-for-byte identical to the ten files under `libraries/mariadb-connector-c-3.4.9/`. The build copies from `libraries/`; the installer ships from `Extras/`. One of the two copies is redundant, and `libraries/` is the right home for a vendored dependency. Repointing `section_files_common.iss` at `libraries/` removes ten of these forty alerts and, more importantly, removes the possibility of the two copies drifting apart unnoticed. |

### Required in the tree

| Component | Files | Why it stays |
|---|---|---|
| MariaDB Connector/C 3.4.9 (`libraries/mariadb-connector-c-3.4.9/`) | 10 | The MySQL/MariaDB client. `MySQLInterface.cpp` builds a path beside the running executable and loads `libmysql.dll` from there at runtime, together with the nine authentication plugins found through `MYSQL_PLUGIN_DIR`. Without the full plugin set, common MySQL 8 and MariaDB account types cannot authenticate. This *could* be fetched from the vendor at build time with a hash check; the argument against is that it would put a network dependency in the path of every build for a file that changes once a year. Recorded provenance is the better trade. |
| ADO 2.8 type libraries (`libraries/msado28/`) | 2 | `#import`ed at compile time by `stdafx.h` lines 47 and 51 to generate the ADO wrappers the server compiles against. Importing the host's copy instead would make the generated wrappers vary with the build machine's Windows version — pinning them is the point. Both are PE images despite the `.tlb` extension, which is why an extension-only scan misses them. |
| SQL Server Compact 4.0 SP1 x64 (`installation/SQLCE/`) | 1 | The built-in database backend. Identity read from the MSI itself: "Microsoft SQL Server Compact 4.0 SP1 x64 ENU", Microsoft Corporation, built 2012-04-06, validly signed. **Microsoft has withdrawn the download**, so this copy is the archive of record — there is no upstream left to fetch it from. This is the clearest case in the list for a committed binary. |

### Required today, but review them

| Component | Files | Why it is on notice |
|---|---|---|
| MSVC v145 runtime (`installation/Microsoft.VC145.CRT/`) | 10 | Version 14.51.36231.0, validly signed by Microsoft. Shipped beside the server so a target machine needs no separate redistributable. **This is the strongest candidate for moving out of the tree**: every machine that can build this project already has these files at `%VCToolsRedistDir%\x64\Microsoft.VC*.CRT`, so the installer could copy them at build time and ten binaries would leave the repository. The cost is that the shipped runtime then tracks the build machine's Visual Studio patch level — which is arguably more correct, not less. |
| `installation/Extras/7za.exe` | 1 | 7-Zip standalone console **19.00**, from 2019. Upstream is 26.02. Run from `{app}\Bin` by `Compression::GetExecutableFullPath_()`. Nothing here says 19.00 is exploitable, but a seven-year-old unsigned executable that the server invokes is the committed binary most overdue a refresh. Update it, re-record the hash, and note that the upstream URL is now a GitHub release (`ip7z/7zip`) rather than 7-zip.org. |
| `installation/System Files/ATL/atl70.dll` | 1 | ATL 7.0 runtime from Visual Studio .NET 2003, installed to `{sys}`. Nothing in this tree links ATL 7.0, and `section_files_common.iss` line 11 already says it "looks vestigial on a v145 build". It is still referenced by the installer, so removing it needs an install test on a clean machine rather than a deletion. |
| `installation/ISC.dll` | 1 | **The weakest provenance in the repository.** A 45 KB unsigned native DLL with no version resource, no company string, no product string and no known upstream, loaded by the installer to call `CheckPorts()` (`hMailServerInnoExtension.iss` line 94, called at line 695). It is genuinely used, so it cannot simply be deleted — but "an unattributed binary that runs during installation" is the one entry on this list that should not survive to the next release. Replace it with Inno Setup Pascal code, or rebuild it from source that lives in this repository. |

### Ours, not third-party

| Component | Files | Note |
|---|---|---|
| `source/Tools/Interop/Interop.hMailServer.dll` | 1 | TlbImp output from *this project's* own `hMailServer.idl`. Committed deliberately so the .NET tools build with a plain `dotnet build` and no registered typelib; the reasoning and the regeneration command are in [Interop/README.md](../source/Tools/Interop/README.md). It is in the manifest because Scorecard cannot tell our binary from anyone else's, and because "it's ours" is a claim that deserves to be written down and hashed like any other. |

What CI checks
--------------

The `Binary provenance` workflow runs on every push and pull request to master
and answers two questions:

1. **Has a listed binary changed or gone missing?** Hard failure. If the change
   was intentional, the manifest entry is updated in the same commit — which is
   precisely the review moment a binary diff otherwise denies you.
2. **Has a binary appeared that nobody wrote down?** Hard failure. Adding a DLL
   to a Windows project takes two seconds and is invisible in review.

Files still carrying a `remove-*` disposition are reported as warnings, not
failures, so the list above stays visible in CI without blocking anyone.

The detector matches Scorecard's: the extension set from Scorecard's
`checks/raw/binary_artifact.go`, plus a content sniff for PE, ELF and OLE
headers. That second half is not belt-and-braces — an extension-only scan over
this tree finds 38 files, the magic-byte scan finds 40, and Scorecard reports
40. The two extras are the `.tlb` type libraries.

Updating the manifest
---------------------

When you add, update or remove a committed binary:

1. Make the change to the file.
2. Update its entry in `third-party-binaries.json` — `sha256`, `size`,
   `version`, `upstream`, and `authenticode` if the signing status changed.
   Get the hash with
   `(Get-FileHash <path> -Algorithm SHA256).Hash.ToLower()`.
3. Update the relevant row in this document if the reasoning changed.
4. Push. The workflow will tell you if the three disagree.

Do not "fix" a failure by copying the hash CI printed into the manifest without
knowing why it changed. That is the one move that turns this whole mechanism
into decoration.

Relationship to OpenSSF Scorecard
---------------------------------

Scorecard's `Binary-Artifacts` check reports one alert per committed binary —
40 of them here — and has no per-file suppression. It does, however, support
maintainer annotations: a `scorecard.yml` file (also honoured at
`.github/scorecard.yml`) whose `remediated` reason exists for exactly this
case, described upstream as "a binary is needed but it is signed or has
provenance". An annotated check is skipped when Scorecard writes its SARIF, so
the alerts stop being reported.

**That annotation has deliberately not been added yet.** Thirteen of the forty
are duplicates or dead files. Annotating the check today would say "assessed,
provenance recorded" about thirteen files whose correct disposition is
deletion, and the tooling would stop mentioning them. The annotation becomes
true — and should be added — once the `remove-unused` and `remove-duplicate`
entries above are gone.

Not yet done
------------

Honest list of what this document does not cover.

- **No LGPL text for MariaDB Connector/C.** Twenty committed files and the
  shipped installer include it, `hmailserver/docs/Licenses/` has texts for
  7-Zip, SQL CE, OpenSSL, Boost and others — and nothing for Connector/C, whose
  licence requires the text to accompany the distribution. This is a licence
  compliance gap, not a security one, and it needs the real LGPL-2.1 text
  added rather than a summary.
- **`msado28*.tlb` redistribution terms are not established.** They are
  Windows operating-system components copied out of `%CommonProgramFiles%`.
  Committing them is normal industry practice and has been done here since the
  upstream project; whether it is *licensed* has not been checked.
- **The MariaDB Connector/C 3.4.9 upstream archive URL is not pinned.** The
  vendor's browse page is recorded; the exact Windows archive filename and its
  hash were not confirmed. Until they are, the chain of custody starts at "the
  files in this tree" rather than at "the archive MariaDB published".
- **CI does not re-verify Authenticode signatures**, only hashes. Doing it
  needs a Windows runner and belongs in the release workflow.
