Roadmap
=======

What is planned for this fork, what is deliberately not, and why. It is kept
honest rather than aspirational: work appears here when there is a concrete
reason for it, and anything already shipped moves to the
[Releases page](https://github.com/Progressiverobot/hmailserver/releases).

No dates are promised. This is a maintained fork, not a funded product, and the
order below is the order things will be looked at rather than a schedule.

How work is prioritised
-----------------------

In this order, and the order matters:

1. **Anything that loses, corrupts or silently drops mail.** A mail server that
   accepts a message has taken responsibility for it.
2. **Anything that stops the server serving** — a hang, a thread pool that can be
   exhausted, a crash reachable from the network.
3. **Security**, weighted by whether an unauthenticated remote party can reach it.
4. **Operability** — being able to tell *why* something went wrong without
   attaching a debugger. Most of the 2026 work has been here, because the hardest
   bug of the year ([#18](https://github.com/Progressiverobot/hmailserver/discussions/18))
   was hard entirely because the server was silent about it.
5. **Features.**

A fix does not ship without a regression test that fails against the build
before it, and the full suite must pass against the exact binary being released.
The process is in [RELEASE.md](RELEASE.md).

In progress
-----------

Bounding every wait that can block a shared thread pool. The server runs its
work on bounded pools; a dependency that stops responding consumes threads until
none are left, and then the server accepts mail and never replies. That was the
mechanism behind #18, and once found, the same shape turned up in several other
places:

* Outbound SMTP delivery, virus scanning, DNS lookups, event scripts and external
  processes all had waits with no ceiling.
* The idle timeouts that appeared to bound them are re-armed on every byte, so a
  peer that dribbles data holds a connection — and anything waiting on it — for
  as long as it likes.

Work in flight: absolute session ceilings distinct from idle timeouts, queue
depth and stall reporting that names the task holding each thread, per-stage
timing of message acceptance, and a cap on pre-authentication IMAP buffering.

Near term
---------

**Enable the database connection-pool deadline by default.** The mechanism exists
but ships disabled, because bounding the wait is only half the job: a caller has
to be able to tell "the database was busy" from the answer it would otherwise
have given. The recipient lookup behind `RCPT TO` currently reports a failed
lookup as *no such user*, so a deadline that expired during a backup would reject
a valid recipient with a permanent 550. The callers need to distinguish the two
first. Until then, blocking is the safer failure.

**Decide the virus-scanner timeout policy explicitly.** A scanner that is killed
for exceeding its bound currently fails open — the message is delivered unscanned,
which is what already happens when the scanner refuses a connection. That is
consistent, but it is a security posture that should be a deliberate, documented,
configurable choice rather than an inherited default.

**Finish the static-analysis backlog.** A first `/analyze` pass found a buffer
overrun in three path helpers, a data race on the virus-scanner counter, ignored
`ReadFile` results and several unchecked NULL dereferences. The reachable ones are
being fixed now; the rest need triage rather than blanket suppression.

**Publish the architecture guide.** There is a detailed map of the codebase, but
it is kept local and unpublished, so anyone arriving at the repository has
`CONTRIBUTING.md` and nothing else to orient them. A fork that invites
contributions should publish the map.

**Documentation gaps**, in rough order of usefulness:

* A troubleshooting guide for delivery and acceptance stalls, built around the
  per-stage timings — the log now identifies which scanner, lookup or script is
  slow, and that should be written down where an administrator will find it.
* `SUPPORT.md`: where to ask what (issues, discussions, the hMailServer forum).
* An upgrade/migration note covering the database upgrade chain.
* An index for `hmailserver/docs/`, which currently holds a single runbook.

Testing and CI
--------------

* **Abnormal-input coverage.** Aborted and malformed sessions are the class that
  produced the worst bugs of the year, and the suite only recently gained tests
  for it. SMTP is covered; IMAP and POP3 need the same treatment.
* **Static analysis in CI.** Currently a manual pass. It should run automatically
  so new findings are caught at the point they are introduced.
* **Broader database coverage.** The suite runs against one backend. MySQL,
  PostgreSQL and MS SQL are supported and should be exercised, at least
  periodically.
* **Installer verification on more Windows versions.** The smoke test installs
  each release on a clean machine and checks the service comes up; it runs on one
  image today.

Features
--------

Not scheduled, and honestly assessed rather than promised:

* **Backup scheduling in the GUI.** Frequently wanted. It needs a real scheduler
  with persistence and failure handling, which is more than a point release
  should absorb — which is precisely why it keeps being deferred.
* **IMAP FETCH byte fidelity.** `FETCH` reconstructs parts of a message rather
  than returning the original octets. The current behaviour is semantically
  correct and no client is known to be affected; a rework would be a large change
  to a heavily used path, so it waits for a real reported problem.
* **Message archiving improvements.** Archiving works and is now discoverable in
  the GUI, but it is coarse: no retention, no per-domain control.

Relationship with upstream
--------------------------

The original project is largely dormant but not dead, and real fixes do still
land there. A scheduled job compares this fork against upstream monthly and opens
a tracking issue when anything new appears; the baseline it compares from is in
[`.github/upstream-sync`](.github/upstream-sync), along with the reasoning for
the two upstream changes this fork deliberately does not take.

As of the last review, nothing upstream is missing here.

Not planned
-----------

Saying no is part of a roadmap:

* **A rewrite**, in any language. This is upstream hMailServer with a current
  toolchain and a set of additions — 936 of the 980 shared server source files
  are still byte-identical. That is the point of it.
* **Removing the COM API.** It is how the Control Panel and every third-party
  script talk to the server. It is not going anywhere.
* **Matching upstream's dependency downgrades.** This fork is deliberately ahead
  on OpenSSL, Boost and PostgreSQL.
* **32-bit builds.** 64-bit only.

Influencing this list
---------------------

A concrete report with a log beats a feature request. Bugs and enhancements go to
[Issues](https://github.com/Progressiverobot/hmailserver/issues), open-ended
questions to
[Discussions](https://github.com/Progressiverobot/hmailserver/discussions), and
anything security-sensitive privately via [SECURITY.md](.github/SECURITY.md).

Something being on this list does not mean it is being worked on now, and
something not being on it does not mean it will be refused — it usually means
nobody has asked.
