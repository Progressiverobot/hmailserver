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

This is a small project — a single maintainer, not a staffed security team.
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
  [ThirdPartyBinaries.md](../hmailserver/docs/ThirdPartyBinaries.md) for what
  we ship and where it came from.

## Supply chain

Every release ships an SBOM (SPDX and CycloneDX) covering both the .NET and
the native dependencies. Third-party binaries committed to this repository are
inventoried, checksummed and verified on every build — see
[hmailserver/docs/ThirdPartyBinaries.md](../hmailserver/docs/ThirdPartyBinaries.md).

Two things on a release can be verified independently of GitHub:

- **The release tag** (from `v6.2.23-alpha2` on) is an annotated tag signed
  with the maintainer's SSH key. The allow list is in the repository, so a
  clone can check it without trusting anything else:

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
read either signature, and the installer is not Authenticode-signed today.
