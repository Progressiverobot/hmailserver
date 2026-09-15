Alerts, and the audit trail
===========================

Two things an administrator asks for on the first day and, until 15 September
2026, could not have.

**Nothing told anybody anything.** Six conditions in this server already notice
that something is wrong — the disk filling, a work queue stalling, a certificate
a week from expiry, a run of auto-bans, a minidump written, a backup that failed
— and each of them wrote a line in a log file and stopped. A log file nobody is
reading is not a notification.

**Nothing recorded who changed what.** A setting was different from yesterday
and there was no way to find out who had changed it, from where, or what it had
been. The first thing a regulated customer asks for is the second thing on this
page.

---

The audit trail
---------------

### What is recorded

Every **administrative change**: a configuration row created, changed or
deleted, and every server setting written. One row per change, in `hm_audit`:

| Column | What it holds |
|---|---|
| `audittime` | When, in seconds since the epoch, UTC. |
| `auditactor` | Who. `Administrator` over COM; `administrator` or `key:<id>` over REST — the same identity string the REST log line uses. |
| `auditactortype` | `administrator`, `account` or `apikey`. |
| `auditinterface` | `COM`, `REST` or `Deck`. |
| `auditaddress` | Where from. The peer address for REST and the Deck; empty for COM (see below). |
| `auditobjecttype` | `domain`, `account`, `alias`, `rule`, `setting`, and so on. |
| `auditobjectname` | The name a person would recognise — the domain name, the address, the setting's name. |
| `auditaction` | `created`, `updated` or `deleted`. |
| `auditdetail` | The columns the change carried, or, for a setting, the value it had and the value it was given. |
| `auditprevhash`, `audithash` | The chain. See *Verifying the chain*. |

### What is never recorded

A password, a key, a token, a secret, a hash, a salt or a passphrase is recorded
as `(changed)` and never as a value. The decision is made by the NAME of the
column or the setting (`AuditTrail::IsSecretName`), matched as a substring, so a
column added later called `accountnewpassword` is covered without anybody
remembering to add it.

An audit trail that copies every password an administrator sets into a table the
Deck renders is worse than no audit trail at all.

The server's own writes are not recorded either. Delivery, retention,
greylisting, the message index and the auto-ban expiry pass all change rows
every minute and none of them is an administrative change; a trail that said
"the mail server changed something" every minute would be one nobody reads.

### Why a COM row has no address

A COM call arrives through DCOM with no peer address this process can see.
Inventing one would be worse than saying nothing, so the field is empty and the
`auditinterface` column carries what can be known: the change was made by
something holding the administrator credential — the Control Panel,
`hmconfig.ps1`, DBSetup, or a script.

### Where it is written from

Two places in the whole server, and a check under `build/` that keeps it that
way:

* **Objects** — `DatabaseConnectionManager::Execute`, the single statement
  chokepoint every write in the server already runs through. The audit row is
  taken there, after the pooled connection has been released.
* **Settings** — `PropertySet::SetLong` / `SetBool` / `SetString`, which is the
  only place where the value the setting HAD is still readable. `hm_settings` is
  deliberately not one of the audited tables, so a setting is recorded once,
  with its old value and its new one, and not twice.

Both sit BELOW the interfaces, so COM and REST are covered by construction
rather than by two parallel implementations that can drift.

Who made the change is a thread-local actor, installed at one place per
interface:

* **REST and the Deck** — `RestApiServer::ProcessRequest_`, once, for the length
  of one authorised request.
* **COM and the Control Panel** — `COMAuthentication::GetIsServerAdmin` and
  `::GetIsDomainAdmin`, the authorisation questions a COM write asks before it
  writes. They run on the thread that is about to do the writing, which matters:
  hMailServer's COM server is an out-of-process MTA and RPC dispatches each call
  on whichever pool thread is free, so an actor installed when the client
  authenticated would be sitting on a thread that never writes anything. The COM
  methods that write without asking — the settings property setters — carry an
  `AuditScope` of their own.

`build/check-audit-choke-point.py` fails the build if a third caller appears, if
either chokepoint goes away, if a COM method that persists something does
neither of those two things, or if anything but `AuditTrail` writes `hm_audit`.

### Verifying the chain

Each row carries the SHA-256 of the row before it and its own, computed over the
previous hash followed by this row's fields, newline-separated. Editing a row or
deleting one therefore breaks the chain at that point:

    GET /api/v1/audit/verify

    {"intact":false,"rows_checked":41,"first_broken_id":42,
     "reason":"the row's contents do not produce the hash stored with it, so the row has been edited"}

The two failures it distinguishes:

* **edited** — the row's own contents no longer produce the hash stored with it.
* **removed** — the row does not carry the hash of the row before it, so a row
  between them is gone.

This does not make the table tamper-**proof**. Anyone who can write the table
can recompute the whole chain. It makes tampering **detectable** without keeping
a second copy, which is what the question "has this record been edited" actually
needs.

A note on retention: `AuditRetentionDays` deletes rows older than the number of
days it names, and the oldest surviving row then still points back at a row that
is gone. It is left that way on purpose — rewriting its back-hash would mean
recomputing every hash after it, which is exactly the operation the chain exists
to make detectable. `verify` knows the difference: with retention in force, a
trail that does not begin at the empty hash is the administrator's own policy;
with retention off (the default, `0`, keep everything) it is a report of
tampering.

### Reading it

    GET /api/v1/audit?limit=100&offset=0&actor=&object_type=&action=&since=&until=

`actor` is a substring, so `key:` finds every change made with an API key.
`since` and `until` are seconds since the epoch. The answer is newest first,
with `total` for the whole filtered set.

The Deck has an **Audit trail** page that reads it, with the filter, paging and
a *Verify the chain* button.

**Administrator credential only.** An API key of any scope is refused, and
refused with a 401 rather than a 403 so that it learns nothing from asking. A
key that could read who changed what could watch for its own footprints.

Switching recording off (`audit_trail_enabled`) is itself recorded, while
recording is off. Otherwise the way to change something unobserved would be to
turn the observer off first.

---

Alerts
------

### The shape

* A **condition** is a row in `hm_alertrules`: its name, whether it is on, its
  threshold, what to do (mail, a webhook, or both), how long to wait before
  saying it again, and whether it goes out at once or waits for the digest. The
  name is a free string, so a new condition is a new ROW and never a schema
  change.
* An **event** is a row in `hm_alertevents`: this condition became true (or
  stopped being true) at this time, with this summary.
* **Delivery** is `AlertTask`, once a minute. It is the only thing that sends.

That last point is not tidiness. Whatever noticed the condition writes a row and
returns — so a notification about the delivery queue is never submitted INTO the
delivery queue by the thread that is stuck on it. It is the same deferral the
TLS-RPT reporter makes, for the same reason.

### What ships

| Condition | On by default | What raises it |
|---|---|---|
| `backup.failed` | **yes** | `BackupManager::OnBackupFailed`, and a scheduled backup that could not run. |
| `certificate.expiring` | **yes** | `AlertTask`, which reads the notAfter of every configured certificate once a minute and compares it with the rule's threshold in days (7 by default). |
| `disk.low` | no | `DiskSpace::ReportBand_`, at the warning band and again below the floor. |
| `queue.stalled` | no | `WorkQueueHealthTask`, when every worker thread of a monitored queue has been in the same task past `AsyncQueueStallThreshold`. |
| `autoban.storm` | no | `AlertTask`, when auto-bans in the last hour reach the rule's threshold (25 by default). |
| `minidump.written` | no | `AlertTask`, when a dump newer than the last one recorded appears in the log directory. |

Four are off because they fire on a healthy server often enough to be noise
until somebody has decided they want them. The two that are on are the two whose
whole value is arriving unasked: a backup that failed is not noticed until the
day it is needed, which is the worst possible day to find out.

Nothing is mailed until `AlertRecipient` and `AlertSenderAddress` are set, which
is what makes "on by default" safe in a stock install.

### Nothing may flap

An alerting system that mails every minute is switched off by its administrator
on the first day, and then nothing tells them anything. Four things stop that:

1. A **state** condition that is already raised is not raised again. The disk is
   not "low" once a minute for an hour; it became low once, and it will become
   not-low once. Clearing is recorded too, and only when the raise was sent.
2. An **occurrence** — a backup that failed, a minidump — has no "still true", so
   it is bounded by the rule's **cool-down** instead: another inside the
   cool-down is recorded but held for the digest rather than sent.
3. A **ceiling per hour** across every condition (`AlertMaxPerHour`, 20). Past
   it, everything goes to the digest.
4. The **digest** is the quiet default: one message at `AlertDigestHour` (07:00
   UTC) listing what fired, rather than one message per event. A digest with
   nothing in it is not sent — a message that says "nothing happened" every
   morning is the first thing an administrator filters into a folder they never
   open.

### Webhooks

A rule with the webhook action POSTs one JSON object per event:

```json
{"condition":"disk.low","state":"raised","severity":"high","time":1789000000,
 "time_utc":"2026-09-15 08:26:40Z","summary":"Free disk space is down to 412 MB.",
 "detail":"Mail is still being accepted. It will be refused below 100 MB.",
 "server":"mail.example.com"}
```

with three headers:

    X-hMailServer-Timestamp: 1789000042
    X-hMailServer-Signature: sha256=<hex>
    X-hMailServer-Event: disk.low

#### Verifying the signature

The signature is **HMAC-SHA256 over the timestamp, a full stop, and the raw
request body**, under the rule's shared secret, as lower-case hex. Verify it
against the bytes you received, before parsing them:

```python
import hmac, hashlib, time

def verify(secret, headers, raw_body):
    timestamp = headers["X-hMailServer-Timestamp"]
    signature = headers["X-hMailServer-Signature"]

    # Refuse anything older than five minutes, or the signature can be replayed.
    if abs(time.time() - int(timestamp)) > 300:
        return False

    expected = hmac.new(secret.encode(), (timestamp + ".").encode() + raw_body,
                        hashlib.sha256).hexdigest()

    return hmac.compare_digest("sha256=" + expected, signature)
```

```javascript
const crypto = require("crypto");

function verify(secret, headers, rawBody) {
   const timestamp = headers["x-hmailserver-timestamp"];
   const signature = headers["x-hmailserver-signature"];

   if (Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return false;

   const expected = "sha256=" + crypto.createHmac("sha256", secret)
      .update(timestamp + ".").update(rawBody).digest("hex");

   return crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(signature));
}
```

Compare with a constant-time comparison, and check the timestamp: a signature
with no freshness check can be replayed for ever.

A rule that calls a webhook is refused without an address and refused without a
secret. An unsigned body arriving at a URL anybody can read out of a
configuration file is an alert anybody can forge.

#### Retries and the dead letter

An attempt that does not get a 2xx is retried on the next pass, then after 1, 5,
15 and 60 minutes, up to `AlertWebhookMaxAttempts` (5). After the last one the
event is **dead-lettered**: it stops being retried, the row stays with
everything it said, and HM6543 is recorded once so that an administrator learns
their endpoint is not answering. The events route shows the state:

    GET /api/v1/alerts/events
    ... "webhook":"dead-lettered","webhook_attempts":5 ...

Only https, or plain http to a loopback address — the rule `HttpsClient` applies
to every outbound request in this server.

### The routes

| Route | What it does |
|---|---|
| `GET /api/v1/alerts/rules` | Every condition and its rule. A webhook secret is never returned; `webhook_secret_set` says whether one is configured. |
| `PUT /api/v1/alerts/rules/<condition>` | Change one, or create one for a condition nothing has shipped. A field the body leaves out keeps the value it has. `webhook_secret` is write-only; `""` clears it. |
| `GET /api/v1/alerts/events` | What has fired, newest first, with the webhook delivery state. |
| `POST /api/v1/alerts/test` | Raise a condition on request, through exactly the path a real one takes. `{"condition":"disk.low","summary":"...","occurrence":false}`. |
| `POST /api/v1/alerts/run` | Run one alerting pass now, instead of waiting for the scheduled one. |

All five are administrator-only, for the reason the audit routes are: the rules
hold the webhook secrets.

### The settings

All nine are in `hm_settings` — the store the COM `Settings` object and the
Control Panel's classic pages use — and none of them is an `hMailServer.ini`
key. They are readable and writable at `GET`/`PUT /api/v1/settings`, and the
Deck's Settings page draws them from the server's own OpenAPI document, so they
appear there without the page being changed.

| Setting | Default | What it does |
|---|---|---|
| `audit_trail_enabled` | on | Whether administrative changes are recorded. |
| `audit_retention_days` | 0 | Days of trail to keep. 0 keeps everything. |
| `alerts_enabled` | on | The master switch. With no recipient, nothing is sent. |
| `alert_recipient` | empty | Where alerts and the digest go. |
| `alert_sender_address` | empty | What they are sent from. |
| `alert_digest_enabled` | on | Whether conditions marked for the digest wait for it. |
| `alert_digest_hour` | 7 | The hour, UTC, the digest goes out. |
| `alert_max_per_hour` | 20 | The ceiling before everything goes into the digest. |
| `alert_webhook_max_attempts` | 5 | Attempts before an event is dead-lettered. |

---

What this does not do
---------------------

* **There is no Control Panel page for the alert rules.** The nine settings
  appear on the Deck's Settings page and are writable over COM through
  `Settings`, so the desktop program can reach them; the rules themselves are
  reached over REST, the Deck, or `hmctl`. A Control Panel page is the next
  piece of work on this row.
* **A COM row records no address**, for the reason given above.
* **The audit trail records administrative changes, not reads.** Who looked at
  a mailbox is a different question and a much larger one.
* **One logical change can be more than one row** where saving an object writes
  its children as separate statements — a rule with its criteria, a distribution
  list with its recipients. Each row says what it was.
* **`hmctl` and the PowerShell module have no `audit` or `alerts` verbs yet.**
