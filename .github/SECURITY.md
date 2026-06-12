# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 6.0.x   | :white_check_mark: |
| < 6.0   | :x:                |

## Reporting a Vulnerability

Please report security vulnerabilities privately via
[GitHub Security Advisories](../../security/advisories/new)
("Report a vulnerability"). Do **not** open a public issue for
security problems.

You can expect an initial response within a few days. Once a fix is
available it will be released as a new build and the advisory will be
published.

## Scope

hMailServer is a network-facing mail server (SMTP/IMAP/POP3 plus
optional REST API, metrics and web-services listeners). Reports of
particular interest:

- Remote code execution or memory corruption in protocol handlers
- Authentication or authorization bypass
- TLS/crypto weaknesses (DANE, MTA-STS, DKIM/ARC, certificate handling)
- SQL injection in the persistence layer
- Privilege escalation via the Windows service or COM API
