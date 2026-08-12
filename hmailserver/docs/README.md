Documentation
=============

Operator-facing documentation for this fork of hMailServer. Everything here is
written for someone running the server, not for someone reading the code — the
codebase map and contribution notes live at the repository root
([CONTRIBUTING.md](../../CONTRIBUTING.md), [RELEASE.md](../../RELEASE.md)).

| Document | Read it when |
|---|---|
| [DiagnosingStalledMail.md](DiagnosingStalledMail.md) | Mail is not moving and the server looks healthy — no crash, no error, and the log seems to stop mid-transaction. Start here rather than opening an issue; the answer is usually one line of the log. Covers both halves separately: the sender timing out *after* sending a message, and a message accepted but never delivered. |
| [HighAvailabilityRunbook.md](HighAvailabilityRunbook.md) | Planning a warm-standby or shared-database topology, or working out what this server does and does not support for multi-node operation. |
| [Upgrading.md](Upgrading.md) | Moving an existing installation to a new release, or from the original upstream project. What the schema upgrade touches, what to back up, and the one sharp edge — there is no downgrade path, so rollback is only as good as the snapshot you took first. |
| [RegulatoryScope.md](RegulatoryScope.md) | Asking whether the EU Cyber Resilience Act or the revised Product Liability Directive place obligations on this project. Dated determination plus the triggers that would change it. |
| [grafana-dashboard.json](grafana-dashboard.json) | Monitoring the server with Prometheus and Grafana. Import it as-is; it is built against the metric names the exporter actually publishes, and every metric it references is verified to exist. 19 panels covering health, mail flow, authentication, TLS, real p50/p95/p99 latency quantiles from histogram buckets, delivery-queue depth *and* oldest-message age, and certificate expiry. Series colours are validated for colour-blind separation and contrast against both light and dark chart surfaces, and every multi-series panel carries a legend so identity is never colour alone. |
| [Licenses/](Licenses/) | Third-party licence texts for the bundled dependencies. |

Elsewhere in the repository
---------------------------

| | |
|---|---|
| [README.md](../../README.md) | What the server does, how to build it, how to install it |
| [build/hmconfig.ps1](../../build/hmconfig.ps1) | Putting the server configuration in version control: export it as JSON, review a change as a diff, apply it to another machine. `apply` is a dry run unless you pass `-Force`, and deletions need `-AllowDelete` on top of that. |
| [ARCHITECTURE.md](../../ARCHITECTURE.md) | Changing the code: the module map, where a given change belongs, and the constraints that have caused real bugs here |
| [Roadmap.md](../../Roadmap.md) | The full capability matrix — what exists, what is planned, what is deliberately not, and every known defect |
| [.github/SUPPORT.md](../../.github/SUPPORT.md) | Where to ask what: issues, discussions, or the hMailServer forum |
| [.github/SECURITY.md](../../.github/SECURITY.md) | Reporting a security issue privately |
| [RELEASE.md](../../RELEASE.md) | The release process, including the pre-flight and regression gates |

If something you needed was not here, that is worth an
[issue](https://github.com/Progressiverobot/hmailserver/issues) on its own — a
missing runbook is a real defect, and the two documents above both exist because
a specific problem took far longer to diagnose than it should have.
