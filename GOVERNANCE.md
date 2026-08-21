Governance
==========

This document describes how the hMailServer fork at
[Progressiverobot/hmailserver](https://github.com/Progressiverobot/hmailserver)
is run: who decides what, how decisions are made, and what each role is
expected to do. It describes the project as it actually is today, not as an
aspirational structure.

The short version
-----------------

This is a **single-maintainer project** stewarded by Progressive Robot Ltd.
Christopher Holloway is the maintainer and currently holds every role below.
Decisions are made by the maintainer, in the open, on the issue tracker.

That is a real constraint rather than an embarrassment, and it is stated
plainly here for the same reason it is stated in
[SECURITY.md](.github/SECURITY.md): people relying on a mail server are
entitled to know how much of it rests on one person. The
[Continuity](#continuity) section below says what is and is not currently
true about that.

Roles and responsibilities
--------------------------

### Maintainer

**Currently: Christopher Holloway ([@chrisholloway5](https://github.com/chrisholloway5)).**

The maintainer is accountable for the project. In practice this means:

- **Accepting changes.** Reviewing and merging pull requests, and deciding
  what does and does not belong in the fork.
- **Releasing.** Producing releases according to [RELEASE.md](RELEASE.md),
  including running the full regression suite against the exact binary being
  shipped, and signing every release asset.
- **Security response.** Receiving private vulnerability reports, triaging
  them, and meeting the timetable published in
  [SECURITY.md](.github/SECURITY.md).
- **Direction.** Keeping [Roadmap.md](Roadmap.md) current, and deciding what
  the project will deliberately *not* do.
- **Infrastructure.** Holding and protecting the credentials listed under
  [Critical assets](#critical-assets).

The maintainer holds admin rights on the GitHub repository and the signing
and release path.

### Contributor

Anyone who opens a pull request. Contributors are expected to follow
[CONTRIBUTING.md](.github/CONTRIBUTING.md): keep changes focused, keep the
regression suite green, add or update regression tests for behaviour changes,
use parameterised SQL exclusively, and accept that contributions are licensed
under the AGPLv3.

Contributors do not need any prior status. There is no CLA to sign.

### Reporter

Anyone who files an issue or a security report. Reporters are expected to
follow [SUPPORT.md](.github/SUPPORT.md) for defects, and
[SECURITY.md](.github/SECURITY.md) for anything security-relevant — in
particular, **not** to open a public issue for a security problem.

Reporters are credited for security findings unless they ask not to be.

How decisions are made
----------------------

**Ordinary changes.** The maintainer decides, and the reasoning is recorded
where the decision happens — in the pull request, in the issue, or in the
release notes. The project's habit is to write down *why* rather than just
*what*; the release notes are the clearest example of this and are the
intended place to look for the reasoning behind a behavioural change.

**Disagreements.** Raise them on the issue in question. The maintainer will
respond with a reason, not just a verdict. A closed issue is not a closed
conversation — reopening it with new information (especially a reproduction)
is welcome, and has changed outcomes before.

**Scope.** Whether something belongs in the fork is decided against one
question: does it serve people running this as a mail server in production?
Compatibility with existing hMailServer deployments is a standing constraint,
not a preference — an upgrade must preserve configuration and mail.

**Security.** Security decisions are the maintainer's alone, and are made
under the disclosure timetable in [SECURITY.md](.github/SECURITY.md) rather
than by consensus.

Becoming a maintainer
---------------------

There is no committee and no fixed number of contributions. The path is
demonstrated, sustained judgement: a track record of changes that are correct,
tested, and considerate of existing deployments, plus a willingness to take on
the release and security duties above rather than only the coding.

If you want to help at that level, say so on an issue. The project would
benefit from it — see [Continuity](#continuity).

Critical assets
---------------

These are the things whose loss would stop the project, and they are listed
so that a successor knows what to look for:

| Asset | What it is for |
|---|---|
| GitHub repository admin | Accepting changes, managing issues, publishing releases |
| Release signing path | Sigstore cosign keyless signing of release assets (no long-lived private key is held) |
| Code-signing material | Signing the Windows installer |
| Build environment recipe | Visual Studio 2026 / v145 plus the external libraries under `hMailServerLibs` |
| Test environment recipe | The database, ClamAV, SpamAssassin and INI configuration the full regression suite needs |

Everything needed to *build, test, release and verify* the product is
committed to this repository and documented — in
[README.md](README.md), [ARCHITECTURE.md](ARCHITECTURE.md),
[RELEASE.md](RELEASE.md), the scripts under `build/`, and the workflows under
`.github/workflows/`. Deliberately, nothing about producing a release depends
on knowledge that exists only in the maintainer's head.

Note that release signing is **keyless** (Sigstore, via GitHub OIDC). There is
no private signing key to inherit or to lose, which removes the single most
common continuity failure for a small project.

Continuity
----------

**Current status: the bus factor is 1.** One person can accept changes,
publish a release, and respond to a security report. If that person were
unavailable, the project would stall until repository access were recovered
through GitHub's own account-recovery or organisation-ownership processes.

This is an open, known gap rather than a solved problem, and it is recorded
here rather than papered over. The mitigations that *are* in place:

- Every build, test and release step is scripted and committed, so the work is
  reproducible by someone else without tacit knowledge.
- Release signing requires no inherited private key.
- The repository is public, and the licence (AGPLv3) permits anyone to
  continue the work — a fork is always available as a last resort, which is
  precisely how this project itself came to exist.

Closing the gap properly requires a second person with repository admin and
release rights. The project is open to that; see
[Becoming a maintainer](#becoming-a-maintainer).

Changing this document
----------------------

Propose changes the same way as any other change: open a pull request. The
maintainer decides, and, as everywhere else in this project, will give a
reason.
