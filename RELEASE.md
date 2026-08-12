# Release checklist

The order below is not advisory. Each rule exists because violating it has
already cost a release cycle or nearly shipped a defect.

## Order of operations

1. **Freeze the code.** No fixes land after this point without restarting at
   step 2.
2. **Adversarial review of the complete diff** — independent reviewers per
   dimension (delivery paths, protocol handling, UI truthfulness vs. code,
   docs claims vs. code), each finding verified by skeptics instructed to
   refute it. This happens **before** version stamping, not before tagging:
   the 6.2.15 review ran after the installer was built and both blockers it
   found (a BDAT truncation defect and a fix that converted transient file
   locks into permanent mail loss) forced rebuilds and a re-run of the full
   suite. Same review, an hour cheaper, if run first.
3. **Fix what survives review; each defect fix gets a negative-control test.**
   Build the pre-fix binary and confirm the new test *fails* against it. A
   test that passes on both builds proves nothing — one of the 6.2.15 abort
   tests did exactly that until it was strengthened with session-count
   assertions.
4. **Pre-flight the bench**: `build\preflight-tests.ps1` (add `-Clean` to
   remove a stale ERROR log). Never skip this after an aborted run — an
   aborted run leaves a deliberate scanner error in the ERROR log and the
   next run fails 100% of tests in fixture setup, and it also leaves the
   TLS fixtures' twelve extra ports behind, which the pre-flight checks.
5. **Check the roadmap against itself**: `build\check-roadmap.ps1`. It
   reconciles every tick box in `Roadmap.md` against the per-section counts
   and the contents table, because three hand-edited restatements of the same
   750 numbers drift — nine sections had drifted when the check was written.
6. **Version stamp**: `Version.h` (version, numeric, build),
   `section_setup_64.iss`, all seven `.csproj` `<Version>` values. Verify
   nothing else still carries the old version: `git grep <old-version>`.
7. **Build everything at the stamped version**: `build.ps1 -Configuration
   Release`, `build-tools.ps1 -Configuration Release`, ControlPanel
   `dotnet publish` to its `publish\` folder (build-tools does not cover it),
   `build-tests.ps1`. Confirm the stamped `FileVersion` on
   `hMailServer.exe` and `publish\hMailCP.dll`.
8. **Full regression suite on the stamped binary** — every test, nothing
   skipped. If *anything* changes after this run, the run is void: rebuild
   and re-run. Never abort a run; if one must be stopped, expect step 4 to
   fail and clean up before trusting any result.
9. **README release notes** — every claim checked against the diff. "Fixed"
   means reproduced-then-fixed or negative-control-tested; anything else is
   described as hardening or diagnostics. Unfixed known issues are named as
   unfixed.
10. **Installer**: ISCC on `hMailServer64.iss`. Never run the installer on
   the dev machine — validation is the CI smoke-test workflow
   (`installer-smoke.yml`), which installs it on a throwaway runner.
11. **Commit** (as chrisholloway5, no co-author trailers), **tag**
    `vX.Y.Z`, push branch then tag, `gh release create` with the installer.
    The SBOM workflow attaches SPDX/CycloneDX automatically.
12. **Close the loop**: answer every issue the release resolves (and close
    them), update the ones it does not resolve saying so plainly.

## Standing rules

- **Prefer two small releases over one large one.** 6.2.15 carried IMAP
  semantics, delivery changes, a connection-layer rewrite and a UI expansion
  in one tag; its worst defect lived in the *interaction* between two of
  those themes.
- **A fix to a shared layer needs a dependents audit.** Before changing
  behaviour in something like `TCPConnection`, enumerate who depends on the
  old behaviour (the BDAT reads depended on "any error ends the session").
- **The public record never overstates.** If it is not reproduced and
  negative-control-tested, it is not "fixed" — say "hardened", "instrumented"
  or "still open". Credibility with reporters is the project's scarcest
  resource.
