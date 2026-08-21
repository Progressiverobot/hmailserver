Diagnosing slow or stalled mail
===============================

This guide covers the case where mail is not moving and the server appears
healthy: no crash, no error, the service is running, and the logs seem to stop
mid-transaction. It exists because that exact shape of problem took three
releases to track down
([discussion #18](https://github.com/Progressiverobot/hmailserver/discussions/18)),
almost entirely because the server used to be silent about it.

It is no longer silent. Read this before opening an issue — the answer is
usually in one line of the log.

First: which half is stuck?
---------------------------

The two halves fail differently and have different causes. Work out which one
you have before anything else.

**Accepting** — the sending server connects, sends the message, and then waits.
From its side you will see a timeout *after* the message body was transmitted.
Postfix reports this as `timed out while sending end of data`. The message never
appears in your queue.

**Delivering** — the message is accepted (the sender got a `250`), is visible in
the delivery queue, and never leaves.

Everything below is split along that line.

Turn on debug logging first
---------------------------

Control Panel → **Logging** → tick **Debug messages**, and make sure logging is
enabled. Reproduce the problem once. The lines this guide refers to are written
at debug level; the *slow* ones are also written at application level, so if you
only have application logging you will still see the important ones.

Remember to turn debug logging off afterwards on a busy server.

Accepting: the sender times out after sending the message
---------------------------------------------------------

Between the `354` and the `250` the server runs the accept pipeline: spam tests,
message modifications, archiving, the `OnAcceptMessage` script, and the database
save. All of it happens on a bounded pool of threads, and the reply is only sent
when it finishes.

The log now times each stage:

```
SMTPConnection - accept: start spam-protection.
Spam test: SpamTestDNSBlackLists, Score: 0, Time: 12 ms
Spam test: SpamTestSpamAssassin, Score: 0, Time: 120000 ms
SMTPConnection - accept: done spam-protection in 120047 ms (session 5).
SMTPConnection - accept: done message-modifications in 0 ms (session 5).
SMTPConnection - accept: start script/save.
SMTPConnection - accept: done script/save in 3 ms (session 5).
```

**How to read it.** Read it as a sequence and look for the last line written —
the stage that is stuck is the one after it. Two stages announce themselves with
a `start` line (`spam-protection` and `script/save`), so for those a `start` with
no matching `done` is the whole diagnosis. The middle stage does **not** have a
`start` line: message modifications run between `done spam-protection` and
`start script/save`, so a stall there shows up as `done spam-protection` followed
by silence. If every stage completes but one took a long time, the `Time:` or
`done ... in` value names the culprit directly. A stage taking ten seconds or
more is logged at application level even without debug logging, and so is any
individual spam test that takes ten seconds or more.

Common causes, in the order they actually occur:

| What the log shows | Cause |
|---|---|
| `SpamTestSpamAssassin` with a large `Time:` | spamd is unreachable, overloaded, or accepting connections without answering |
| `SpamTestDNSBlackLists`, `SpamTestSURBL` or `SpamTestSPF` slow | the resolver is not answering. Look in the TCP/IP log for `DNS - Query timed out` and `DNS - Query failure`. **Do not reach for `DNSServer` as the fix** — see the warning below |
| `done spam-protection`, then silence | message modifications: the spam headers, the signature, the List-\* headers, or the write of the modified message back to disk |
| `script/save` slow | an `OnAcceptMessage` event script, or the database |
| Nothing between `354` and silence | you are on a version older than 6.2.17; upgrade, because that is the version that added these lines |

> **`DNSServer` was broken in 6.2.17 and 6.2.18. It was fixed in 6.2.19 (issue
> #25).** On those two versions every lookup through a configured custom server
> failed: AAAA and CNAME queries returned `ERROR_TIMEOUT` in the same millisecond
> they were issued, and the A query returned a status the resolver treated as "no
> records". SpamAssassin was the visible symptom because it reports a failed
> lookup, but DNSBL, SURBL, SPF, DKIM and MX lookups failed the same way in
> silence — a server in that state had quietly stopped most of its spam filtering
> and was still accepting mail. **If you are on 6.2.17 or 6.2.18, upgrade, or
> leave `DNSServer` empty so the system resolvers are used.**
>
> The cause is worth knowing, because the fix looks like a bug. Moving to the
> asynchronous `DnsQueryEx` replaced a `PIP4_ARRAY`, which has no port field,
> with a `DNS_ADDR`, which carries a full `SOCKADDR` — and a destination port of
> **zero** is what that structure wants, because the DNS client supplies the port
> itself. Filling in 53 looks like the obvious correction and breaks every
> lookup: measured against a real server, port 53 returns status 87 and no
> records for all three record types, while port 0 returns the A record and a
> correct `DNS_INFO_NO_RECORDS` for a host with no AAAA. `DNSResolverWinApi.cpp`
> says *do not "fix" this line* directly above it, for this reason.
>
> One caveat survives the fix: a custom server list is only honoured together
> with `DNS_QUERY_BYPASS_CACHE`, so setting `DNSServer` still means every lookup
> pays full price. Leave it empty unless you need a specific resolver.

Since 6.2.17 acceptance is also bounded: if it exceeds `FinalizationTimeout`
(240 seconds by default) the server answers `451` and the sender retries, rather
than leaving it waiting for a reply that never comes. If you see `451 4.3.1`
responses, the accompanying error entry names the deadline that was hit, and the
stage timings above it name what consumed the time.

### If every message stalls, not just one

Look for this:

```
Task SMTP-accept session=42 ip=203.0.113.9 waited 8 seconds for a thread in
work queue Asynchronous task queue. 14 task(s) are still queued.
```

and, when it is severe:

```
All 15 threads in work queue Asynchronous task queue have been busy for at
least 120 seconds, so no further task on this queue can start. 14 task(s) are
queued. Running: SMTP-accept session=42 ip=203.0.113.9 (thread 4120, 168s), ...
```

That means every worker is occupied and messages are queuing behind them. One
slow dependency does this to the whole server, which is why a single wedged
scanner used to look like "the server stopped responding". The task names tell
you which sessions are stuck; the stage timings tell you what they are stuck on.

Delivering: the message is accepted but never leaves
----------------------------------------------------

Delivery runs on a separate, smaller pool. The usual causes:

* **A virus scanner that stops responding.** ClamAV is contacted after the
  message is accepted, so a wedged clamd shows up as "accepted but never
  delivered". Each socket operation is bounded by `ClamMinTimeout` /
  `ClamMaxTimeout` and a clamd that never answers is reported rather than held.
  Be precise about what that bound is, though: it is a deadline on *one* read or
  write, armed fresh each time, and the message is streamed to clamd in chunks —
  so a clamd that answers each chunk just before the deadline can make a single
  large message take considerably longer than `ClamMaxTimeout` in total. It cannot
  hold the thread forever; it can hold it for a while.
* **A remote server that answers extremely slowly.** The idle timeout is re-armed
  on every byte received, so a host that sends one byte occasionally used to hold
  a delivery thread indefinitely. Since 6.2.18 there is an absolute ceiling
  (`ClientSessionCeiling`, 30 minutes by default), armed once and never re-armed.
* **A custom virus scanner or external tool that hangs** — bounded by
  `ExternalProcessTimeout`.
* **The database.** Check for errors mentioning the connection pool.

Check the delivery queue in the Control Panel, and the SMTP log for the
destination in question.

Settings that bound each stage
------------------------------

All are in `hMailServer.ini` under `[Settings]` and all are in seconds. Defaults
are chosen to be well inside a typical sending server's timeout.

| Setting | Default | Bounds | `0` means |
|---|---|---|---|
| `FinalizationTimeout` | 240 | The whole accept pipeline, after which the sender gets a `451` | no bound |
| `SAMaxTimeout` | 90 | SpamAssassin: idle timeout, and a session ceiling of this plus 30s | **not** "no bound" — see below |
| `ClamMaxTimeout` | 90 | ClamAV: idle timeout per socket operation | **not** "no bound" — see below |
| `DNSQueryTimeout` | 10 | A single DNS query | no bound |
| `ScriptTimeout` | 60 | One event script invocation | no bound |
| `ExternalProcessTimeout` | 300 | An external scanner process | no bound |
| `ClientSessionCeiling` | 1800 | An entire outbound delivery session | no bound |
| `AsyncQueueStallThreshold` | 120 | How long every worker may be busy before the saturation report | reporting off |
| `DBConnectionAcquireTimeout` | 60 | Waiting for a pooled database connection | no bound |

**The two exceptions matter, because `0` tightens them instead of removing them.**
`SAMaxTimeout` and `ClamMaxTimeout` are not passed straight through: they go to
`TimeoutCalculator::Calculate(min, max)`, which returns the *minimum* whenever the
maximum is lower than it. So `SAMaxTimeout=0` gives a 30-second idle timeout
(`SAMinTimeout`) and a 30-second session ceiling — a *shorter* bound than the
default, not an absent one — and `ClamMaxTimeout=0` gives 15 seconds
(`ClamMinTimeout`). To lengthen either, raise the value; to lengthen it a long way,
raise the matching `...MinTimeout` too, or the calculator will pull it back under
load. There is no way to make either unbounded.

When the pool deadline expires, a recipient lookup answers `451 4.3.2` rather than
`550`: the server can tell "the database did not answer" from "no such address",
so a database locked by a backup defers the mail instead of bouncing it.

What to include in a report
---------------------------

If the above does not identify it, open an issue with:

1. The log from the `354` (or from the delivery attempt) onwards, including the
   stage timing lines.
2. The `ERROR_hmailserver_<date>.log` for the same window.
3. Which scanners and event scripts are enabled.
4. Whether the message eventually arrives, arrives twice, or never arrives.

See [SUPPORT.md](../../.github/SUPPORT.md).

Verified against the code
-------------------------

Every log line quoted here was copied from the source rather than from a running
server, and every default was read from the code that reads the ini file. Checked
13 August 2026 against: `SMTPConnection::LogFinalizationStage_` and
`FinalizationDeadlineExceeded_` (the stage lines, the ten-second escalation, the
`451 4.3.1` and error 5525); `SpamTestRunner::RunSpamTest` (the `Spam test:` line
and its own ten-second escalation); `WorkQueue::ExecuteTask` and
`WorkQueue::ReportStalledTasks`, plus `Application`'s `"Asynchronous task queue"`
(the two saturation lines, the second of which is error 5526);
`IniFileSettings::LoadSettings` (every default in the table);
`TimeoutCalculator::Calculate` (the `0` exceptions); `SpamAssassinClient`'s
`SetSessionCeiling(GetSAMaxTimeout() + 30)`; `SMTPConnection::ProtocolRCPT_`'s two
`DatabaseUnavailableMarker::Scope` blocks (`451 4.3.2` rather than `550`);
`DNSResolverWinApi::Query` (the `DNSServer` regression and the cache bypass); and
`Logger`'s `ERROR_hmailserver_%s.log`. If you change one of those, this page is
the second place to look.
