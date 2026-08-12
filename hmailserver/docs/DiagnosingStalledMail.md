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
SMTPConnection - accept: done script/save in 3 ms (session 5).
```

**How to read it.** A `start` line with no matching `done` line is the stage that
is stuck — that is the whole diagnosis. If every stage completes but one took a
long time, the `Time:` value names the culprit directly. A stage taking more than
ten seconds is logged at application level even without debug logging.

Common causes, in the order they actually occur:

| What the log shows | Cause |
|---|---|
| `SpamTestSpamAssassin` with a large `Time:` | spamd is unreachable, overloaded, or accepting connections without answering |
| `SpamTestDNSBlackLists` / `SURBL` / `SPF` slow | the resolver is not answering; check `DNSServer` in `hMailServer.ini`, and note that setting it bypasses the Windows DNS cache so every lookup pays full price |
| `script/save` slow | an `OnAcceptMessage` event script, or the database |
| Nothing between `354` and silence | you are on a version older than 6.2.15; upgrade, because that is the version that added these lines |

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
  delivered". Since 6.2.18 the connection is bounded and a timeout is reported
  rather than holding the thread forever.
* **A remote server that answers extremely slowly.** The idle timeout is re-armed
  on every byte received, so a host that sends one byte occasionally used to hold
  a delivery thread indefinitely. There is now an absolute ceiling
  (`ClientSessionCeiling`, 30 minutes by default).
* **A custom virus scanner or external tool that hangs** — bounded by
  `ExternalProcessTimeout`.
* **The database.** Check for errors mentioning the connection pool.

Check the delivery queue in the Control Panel, and the SMTP log for the
destination in question.

Settings that bound each stage
------------------------------

All are in `hMailServer.ini` under `[Settings]`, in seconds, and `0` disables the
bound. Defaults are chosen to be well inside a typical sending server's timeout.

| Setting | Default | Bounds |
|---|---|---|
| `FinalizationTimeout` | 240 | The whole accept pipeline, after which the sender gets a `451` |
| `SAMaxTimeout` | 90 | SpamAssassin (the session ceiling is this plus 30s) |
| `ClamMaxTimeout` | 90 | ClamAV |
| `DNSQueryTimeout` | 10 | A single DNS query |
| `ScriptTimeout` | 60 | One event script invocation |
| `ExternalProcessTimeout` | 300 | An external scanner process |
| `ClientSessionCeiling` | 1800 | An entire outbound delivery session |
| `AsyncQueueStallThreshold` | 120 | How long every worker may be busy before the saturation report |
| `DBConnectionAcquireTimeout` | 60 | Waiting for a pooled database connection |

When the pool deadline expires, a recipient lookup answers `451` rather than
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
