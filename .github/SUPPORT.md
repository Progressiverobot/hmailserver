# Getting help

**Something is broken** — open an [issue](https://github.com/Progressiverobot/hmailserver/issues).

The most useful report contains, in order of value:

1. **The relevant log, not a description of it.** Turn on debug logging (Settings → Logging → Debug messages in the Control Panel) and paste the window around the problem. The single most useful bug report this project has received was a log that stopped at a particular line — where it stopped was the whole diagnosis.
2. **The `ERROR_hmailserver_<date>.log`** for the same window, if there is one. Even one entry is often decisive.
3. **Version** (`hMailServer.exe` reports it, as does the Control Panel), **Windows version**, and **database backend**.
4. **What you expected instead**, and whether it is reproducible.

If mail is being accepted but not delivered, or a session appears to hang, say which of those it is — they have different causes and the logs differ.

**Something might be a security problem** — do not open a public issue. See [SECURITY.md](SECURITY.md).

**A question rather than a defect** — use [Discussions](https://github.com/Progressiverobot/hmailserver/discussions). Configuration questions, "is this supposed to work like this", and "has anyone got X working" all belong there.

**General hMailServer questions** not specific to this fork are often better answered on the long-running [hMailServer forum](https://www.hmailserver.com/forum/), which has years of accumulated configuration knowledge.

## What to expect

This is a maintained fork, not a commercial product with a support contract. Reports are read and taken seriously — several releases this year exist because someone took the time to report something carefully — but there is no response-time guarantee.

Two commitments in return:

- **You will get a straight answer.** If something is not fixed, it will be said plainly rather than implied to be fixed. An open problem that is honestly labelled stays findable; one that is quietly closed does not.
- **A fix is not claimed without evidence.** "Fixed" means reproduced and covered by a test that fails against the previous build. Anything less is described as what it is — a mitigation, a bound, or added diagnostics.

## Before reporting

- Check the [Releases page](https://github.com/Progressiverobot/hmailserver/releases) — the newest version may already address it.
- Check [Roadmap.md](../Roadmap.md) for known gaps and work already planned.
- Search existing [issues](https://github.com/Progressiverobot/hmailserver/issues?q=is%3Aissue) and [discussions](https://github.com/Progressiverobot/hmailserver/discussions).
