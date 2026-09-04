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
6. **Check the database scripts build a database**: `build\check-db-scripts.ps1`.
   It creates a throwaway SQL CE database from `CreateTablesMSSQL.sql` using the
   same splitting rules the server uses, because nothing else here does - the
   regression bench's database is upgraded out of band, so no local test takes
   the CREATE path a fresh install takes. 6.2.22-pre4 shipped an installer whose
   database could not be created at all, and the only symptom was a service that
   started and did not listen.

   Run `build\check-schema-versions.ps1` beside it (pwsh 7, not Windows
   PowerShell 5.1, where it dies in `Measure-Object -Property To`). It walks the
   registered upgrade chain and proves it is contiguous and forward-only, and that
   every step is present for every backend. That matters most on exactly the kind
   of release this checklist keeps being used for - one that moves the schema
   several steps past the last stable - and it was missing from this step until
   6.2.23.

7. **Version stamp**: `Version.h` (version, numeric, build),
   `section_setup_64.iss`, all seven `.csproj` `<Version>` values. Verify
   nothing else still carries the old version: `git grep <old-version>`.
   In `section_setup_64.iss` the `VersionInfoVersion` fourth component stays
   `0`: the build number lives in `Version.h` and the tag, and a build-only
   re-cut (the common case) must not have to touch the installer script or
   the seven `.csproj` files, whose versions are also `<version>.0`.
8. **Build everything at the stamped version**: `build.ps1 -Configuration
   Release`, `build-tools.ps1 -Configuration Release`, ControlPanel
   `dotnet publish` to its `publish\` folder (build-tools does not cover it),
   `build-tests.ps1`. Confirm the stamped `FileVersion` on
   `hMailServer.exe` and `publish\hMailCP.dll`.

   If `hMailServer.idl` changed in this range, regenerate the checked-in COM
   wrapper after the server build and before the tools build:
   `buildegenerate-interop.ps1`. It runs TlbImp AND rewrites the wrapper's
   SHA-256 and size in `hmailserver\docs	hird-party-binaries.json` - the
   binary-provenance workflow fails when those disagree, and regenerating by
   hand without the manifest did exactly that twice in one day. A stale wrapper
   still compiles, which is why this is easy to skip: the tools use a small
   stable subset of the API, so nothing fails, and the members added this
   release are simply invisible to them. Regenerate AFTER any interface-ordering
   fix, never before, or the old vtable layout is baked into the shipped wrapper
   permanently.

   Then prove the build is still reproducible: build Release a second time
   from clean (`build\build.ps1 -Configuration Release -Clean`) and compare
   the SHA-256 of `hMailServer.exe` with the first. They must be identical -
   /Brepro, /d1trimfile, /pdbaltpath and OPENSSL_NO_FILENAMES in the project
   make the executable a pure function of the source and the toolchain, and
   a mismatch means something has started embedding a timestamp or a path
   again. Put the hash in the release notes' verification section so anyone
   with the same toolchain (v145, Windows SDK 10.0.26100) and library layout
   can check the published binary came from the published source. Both
   builds restart the service, so do this before step 9, never after.

9. **Full regression suite on the stamped binary** — every test, nothing
   skipped. If *anything* changes after this run, the run is void: rebuild
   and re-run. Never abort a run; if one must be stopped, expect step 4 to
   fail and clean up before trusting any result.
10. **README release notes** — every claim checked against the diff. "Fixed"
   means reproduced-then-fixed or negative-control-tested; anything else is
   described as hardening or diagnostics. Unfixed known issues are named as
   unfixed.
11. **Installer**: ISCC on `hMailServer64.iss`. Never run the installer on
   the dev machine — validation is the CI smoke-test workflow
   (`installer-smoke.yml`), which installs it on a throwaway runner.
12. **Commit** (as chrisholloway5, no co-author trailers - history has been
    rewritten once to remove them, and will be again). `master` is protected:
    changes arrive by pull request, force-pushes and deletions are refused, and
    the maintainer's bypass exists for emergencies, not for the release flow.
    So push the working branch, open the PR and merge it with a rebase so the
    history stays linear and the commits keep their own messages:

    ```
    git push origin server-fixes-wave
    gh pr create --base master --head server-fixes-wave --fill
    gh pr merge --rebase            # self-merge is allowed; no reviewer is required
    git pull --ff-only origin master
    ```

    Then **tag** `vX.Y.Z` on master as an **annotated, signed** tag and push it
    (tags matching `v*` are protected too: no deletion, no rewrite - a tag is
    spent once):

    ```
    git tag -s vX.Y.Z -m "hMailServer X.Y.Z"
    git -c gpg.ssh.allowedSignersFile=.github/allowed_signers verify-tag vX.Y.Z
    git push origin vX.Y.Z
    ```

    Signing is SSH-based (`git config gpg.format ssh` and `user.signingkey`
    pointing at the public key, set once in the repository's config), and the
    key must be listed in `.github/allowed_signers` - the signing workflow
    below refuses to sign anything for a tag that is lightweight or that does
    not verify against that file, so an unsigned tag stops the release before
    it has any assets. Every tag up to `v6.2.23-alpha1` was lightweight; there
    was nothing to verify and nothing stopped a `v*` ref being moved.

    Then publish **as a draft first** - this repository has
    immutable releases enabled, so a published release refuses every further asset
    upload and would be stuck with whatever it was created with:

    ```
    gh release create vX.Y.Z <installer> --draft --prerelease \
       --title "..." --notes-file <notes>
    gh workflow run "SBOM" -f release_tag=vX.Y.Z          # SPDX + CycloneDX
    gh workflow run "Sign release artefacts" -f tag=vX.Y.Z  # LAST: signs what is attached
    gh release view vX.Y.Z --json assets                   # expect installer + 2 SBOMs + bundle
    gh release edit vX.Y.Z --draft=false                   # publish, now complete
    ```

    Order matters twice over: the SBOMs have to be on before signing, because
    cosign signs whatever is attached when it runs; and everything has to be on
    before publication, because nothing can be added afterwards. 6.2.22-pre4 was
    published straight away and ended up with an installer and no SBOM at all.

    **The two `gh workflow run` lines are required, not belt-and-braces.** Saving a
    draft does not start a workflow: GitHub does not deliver `release` events for
    draft releases, which was measured here rather than assumed - creating the pre5
    draft produced no run at all. The `release: created` trigger on both workflows
    therefore only covers a release published without a draft, which is the path
    that can no longer attach anything. Dispatching them by hand is the path that
    works.

    And a tag is spent once: a tag that has backed an immutable release cannot back
    another, even after that release is deleted. There is no re-cutting a broken
    release under the same version - it needs the next number.
13. **Close the loop**: answer every issue the release resolves (and close
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
