# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 6.2.x   | :white_check_mark: |
| < 6.2   | :x:                |

## Reporting a Vulnerability

Report security vulnerabilities privately through GitHub Security Advisories:

<https://github.com/Progressiverobot/hmailserver/security/advisories/new>

Do **not** open a public issue, discussion or pull request for a security
problem, and do not post it to the hMailServer forum. A public report starts
the clock for everyone running the server, not just for us.

If you cannot use GitHub Security Advisories, open a normal issue that says
only that you have a security report and asks for a private channel — no
technical detail — and we will open an advisory and invite you to it.

## What to expect

| Stage | Target |
| ----- | ------ |
| Acknowledgement that the report was received | 5 working days |
| Initial assessment: reproduced or more information requested | 10 working days |
| Fix released for a confirmed vulnerability | 90 days from the acknowledgement |

This is a small project, not a staffed security team: it has two maintainers,
and one of them currently receives private reports (see
[GOVERNANCE.md](https://github.com/Progressiverobot/hmailserver/blob/master/GOVERNANCE.md#maintainers)).
Those are the targets we hold ourselves to, not a contractual commitment. If a
deadline is going to slip, you will be told before it slips rather than after.

## Coordinated disclosure

We follow coordinated disclosure on a **90 day** timetable. The advisory is
published when a fix ships, or at 90 days, whichever comes first. If you intend
to disclose sooner than that, say so in the report so we can plan around it
rather than discover it.

You keep credit for the finding unless you ask us not to name you. We will not
ask you to sign anything, and we do not pay bounties.

Once a fix is available it ships as a new build and the advisory is published
with a CVE requested through GitHub.

## Scope

hMailServer is a network-facing mail server (SMTP/IMAP/POP3 plus optional REST
API, metrics and web-services listeners). Reports of particular interest:

- Remote code execution or memory corruption in protocol handlers
- Authentication or authorization bypass
- TLS/crypto weaknesses (DANE, MTA-STS, DKIM/ARC, certificate handling)
- SQL injection in the persistence layer
- Privilege escalation via the Windows service or COM API

### Out of scope

These are known properties of the design rather than vulnerabilities, and a
report about them will be closed with a pointer back here:

- Findings that require an account that is already a server administrator.
  Administrators can run scripts and change the configuration by design.
- Missing hardening on a listener the operator deliberately exposed without
  TLS. The server lets you do that; it warns you first.
- Output from an automated scanner with no demonstrated impact, including
  version-banner findings and TLS-configuration grades.
- Vulnerabilities in a third-party dependency with no path from hMailServer
  to the affected code. Report those upstream; tell us as well if we ship a
  vulnerable version, and see
  [ThirdPartyBinaries.md](https://github.com/Progressiverobot/hmailserver/blob/master/hmailserver/docs/ThirdPartyBinaries.md) for what
  we ship and where it came from.

## Supply chain

Every release ships an SBOM (SPDX and CycloneDX) covering both the .NET and
the native dependencies. The third-party executables and libraries the build
uses are no longer committed to this repository. They are inventoried in
[hmailserver/docs/ThirdPartyBinaries.md](https://github.com/Progressiverobot/hmailserver/blob/master/hmailserver/docs/ThirdPartyBinaries.md):
those fetched from the project's `build-inputs-<n>` releases are checked against a
recorded SHA-256 whenever `build/get-installer-binaries.ps1` places them, the
MSVC runtime is checked by version, and the ADO type libraries, which are
Windows' own files, are checked only for presence, with their hash reported.

Two things on a release can be verified independently of GitHub:

- **The release tag** (from `v6.2.23-alpha2` on) is an annotated tag signed
  with a maintainer's SSH key listed in `.github/allowed_signers`. That allow
  list is in the repository, so a clone can check it without trusting
  anything else:

  ```
  git -c gpg.ssh.allowedSignersFile=.github/allowed_signers verify-tag v6.2.23-alpha2
  ```

- **Every asset** carries a Sigstore bundle (`<asset>.cosign.bundle`), keyless,
  bound to this repository's workflow identity and recorded in the public
  Rekor log. With [cosign](https://github.com/sigstore/cosign) installed:

  ```
  cosign verify-blob \
    --bundle hMailServer-x.y.z-x64.exe.cosign.bundle \
    --certificate-identity-regexp '^https://github\.com/Progressiverobot/hmailserver/' \
    --certificate-oidc-issuer https://token.actions.githubusercontent.com \
    hMailServer-x.y.z-x64.exe
  ```

Neither replaces Authenticode: Windows SmartScreen and the UAC prompt do not
read either signature. Since 6.3.1 the Windows installer is also
Authenticode-signed; the Linux packages are not.

## Vulnerability management policy

What is checked, on what, and what happens when it finds something. The
checks are automatic and blocking; the thresholds below are the policy they
enforce, and a finding that is not fixed is suppressed only with a written
reason.

### Dependencies (software composition analysis)

- **What runs.** Every pull request against `master` runs GitHub's
  dependency review (`.github/workflows/dependency-review.yml`), which
  compares the dependency graph before and after the change against the
  GitHub Advisory Database, including its malware advisories. It is a
  required status check: a change that adds a dependency with a known
  vulnerability of severity **high or critical**, or a package flagged as
  malicious, cannot be merged. Dependabot opens a pull request for every
  advisory that touches a dependency already in use, and for every new
  version of a GitHub Action the workflows pin. The NuGet lock files and the
  SHA-pinned actions mean a dependency cannot change under a build without a
  reviewed commit.
- **Remediation thresholds.** A high or critical advisory in a dependency is
  fixed, or the dependency replaced, **before the next release** and within
  14 days of the advisory; moderate within 30 days; low within 90 days or
  with the next dependency refresh. A license finding (a dependency whose
  license is not compatible with AGPL-3.0-or-later) is treated as high.
- **Before a release.** A release is not cut while a dependency-review or
  Dependabot finding of high or critical severity is open. The release
  checklist in `RELEASE.md` says so, and the SBOMs attached to the release
  (`hmailserver.spdx.json`, `hmailserver.cyclonedx.json`) are what a reader
  checks the shipped versions against.
- **Not affected.** A finding in a component that does not affect this
  project - the vulnerable code is not reached, the feature is not built, the
  platform is not one the server runs on - is recorded as a statement in the
  project's VEX document, [`.github/hmailserver.openvex.json`](hmailserver.openvex.json)
  (OpenVEX), with the reason, when it is dismissed. An empty statement list
  means every advisory raised against a component has been fixed rather than
  dismissed.

### Source code (static analysis)

- **What runs.** CodeQL analyses the C#, C++, JavaScript and Python in the
  repository on every pull request and on every push to `master`
  (`.github/workflows/codeql.yml`). The `master` ruleset requires the CodeQL
  results: a pull request that introduces an alert of **error** level, or a
  security alert of **high or critical** severity, cannot be merged until it
  is fixed or dismissed with a reason. The C++ server is compiled with the
  toolchain's own warnings-as-errors set, and the regression suite runs the
  server under its crash oracle before every release, so a fault the analysis
  would not see still fails the gate.
- **Remediation thresholds.** An error-level or high/critical security alert
  is fixed before merge. A medium alert is fixed within 30 days; a low or
  note-level alert with the next change to that file, or dismissed with a
  reason written on the alert. The Code Quality dashboard and the Code
  Scanning page are kept at zero open CodeQL alerts between releases; a release is
  not cut with an open error-level alert.
- **Suppressions.** A finding is dismissed only on the alert itself, with a
  reason that says why it is a false positive or not exploitable, so the
  reason is on the record beside the finding.

### Secrets and credentials

- The repository holds no credential that opens anything: no token, no
  password, no signing key. The certificates and private keys under
  `hmailserver/test/SSL examples` are throwaway fixtures the regression suite
  uses to exercise TLS, trusted by nothing outside it. GitHub's secret scanning
  with push protection is on, so a push that carries a real secret is refused
  before it lands.
- Credentials the automation needs live only in GitHub Actions secrets and
  variables: the Azure identity that Authenticode-signs the installer, and
  nothing else. Release signatures are keyless (Sigstore, bound to the
  workflow's own identity), so there is no signing key to protect. Release
  tags are signed with a maintainer's SSH key, whose public half is in
  `.github/allowed_signers`.
- A secret is rotated when a maintainer leaves, when the workflow that uses
  it changes hands, or on any suspicion of exposure; the Azure identity's
  secret has an expiry and is replaced before it.
- Test environments use throwaway credentials that the suite creates and
  destroys; none of them is a real account.

### Branch names in the pipelines

No workflow interpolates a branch or tag name into a shell command. Where a
name is needed (`sign-release.yml`, `attest-release.yml`), it is passed
through an environment variable, and the release tag is checked for the
exact form `vX.Y.Z` and for a signature by an allowed signer before it is
used for anything.
