# Reports

*What happened on this server, per domain and per day — and, just as plainly, what this
server cannot tell you.*

Built 15 September 2026. The route is `GET /api/v1/reports`, the page is **Reports** in the
Control Deck, and the shell command is `hmctl report`.

A report that is wrong is worse than a report that is missing, because nobody checks a number
that looks plausible. So this document starts with where each number comes from, and what
that source cannot answer. Everything else follows from it.

---

## The three sources

| Source | Table | What it carries | What it cannot answer |
|---|---|---|---|
| **The message trace** | `hm_messagetrace` (schema 6020) | One row per recipient per delivery attempt: when, the event, the sender, the recipient, the source address, the SMTP status code. | It is **off by default** (`MessageTraceEnabled`), because it is a record of who corresponds with whom, retained. It records only `delivered` and `failed` — there is no spam event, no virus event, and a message still in the queue is in neither. It is kept for `MessageTraceRetentionDays` (30 by default). |
| **The metric history** | `hm_metricsamples` (schema 6028) | One sample per metric per minute, **server-wide**: processed, delivered, deferred, bounced, spam detected, viruses removed, and — since this feature — `store_bytes` and `store_messages`. | No domain. Not one of these counters has ever carried one, which is why spam and virus counts cannot be given per domain. Kept for `MetricsHistoryDays` (7 by default). |
| **The message store** | `hm_messages` joined to `hm_accounts` | The size and the message count of every mailbox, **as it is now**. | It is a point in time, not a history. Nothing has ever recorded what a mailbox held yesterday, so "this mailbox grew by 40 MB last week" is not answerable. |

`GET /api/v1/reports` answers all of this at run time: which sources are recording, how long
each is kept, and the oldest row each still holds. **Read it before reading a section.** A
report drawn while its source was switched off is empty, and that is not the same statement
as "nothing happened" — the index, and each section's own `note`, say which it is.

## What cannot be answered, and what it would take

Two of the seven numbers the roadmap row asked for are only available server-wide:

* **Spam and virus counts per domain.** The counters are server-wide and the trace has no
  spam or virus event. Attributing either to a domain would mean inventing the attribution.
* **Storage growth per domain.** The store-size sample is server-wide. A domain's size *now*
  is in the `mailboxes` section; its size last Tuesday was never recorded.

Both would need a per-domain sample taken daily — a new table and a schema step. That is a
roadmap row, not something a page can do by extrapolating from the size now.

One further limit, inherited from the trace itself: it correlates by queue id, so a message
followed across a forward, a distribution list or a rule that re-sends appears as a separate
delivery rather than as a continuation.

---

## The route

```
GET /api/v1/reports                  the index: the sections, the sources, what cannot be answered
GET /api/v1/reports/<section>        one section
```

`<section>` is one of:

| Section | Scope | Counted from | What it says |
|---|---|---|---|
| `traffic` | domain | the trace | Per day and per local domain: `incoming`, `outgoing`, `failed_incoming`, `failed_outgoing`, and the totals. |
| `failures` | domain | the trace | Per local domain and SMTP status code: the reason in words, and how many inbound and outbound. |
| `senders` | domain | the trace | The local addresses that sent the most, with how many of their deliveries failed. |
| `recipients` | domain | the trace | The local addresses that received the most, with how many deliveries to them failed. |
| `mailboxes` | domain | the store | The largest mailboxes now, and the same totalled per domain. |
| `volume` | **server** | the metric history | Per day: processed, delivered, deferred, bounced, spam, viruses. |
| `storage` | **server** | the metric history | Per day: the size of the message store, and `growth_bytes` over the window. |
| `summary` | mixed | all three | Every section the credential may see, in one document, with an `omitted` list naming the ones it may not and why. |

### Parameters

| Name | Meaning |
|---|---|
| `from`, `to` | `YYYY-MM-DD`, both inclusive. The default is the last 30 days ending today on the server. The window may not exceed 366 days. The last day is a **whole** day. |
| `domain` | One domain this server hosts. The default is every domain the credential may see. A domain that is not hosted here is a 404, not an empty report. |
| `top` | How many rows `senders`, `recipients` and `mailboxes` return: 1 to 200, default 10. |
| `format` | `json` (default) or `csv`. `csv` answers `text/csv` with the same columns and rows and a `Content-Disposition` naming the section and the window. Refused for `summary`, which is not one table. |

### The answer

Every section answers the same shape — `columns` and `rows` — so a reader that gains a column
gains it without a change:

```json
{
  "from": "2026-09-01", "to": "2026-09-15", "domain": "example.com", "top": 10,
  "section": "traffic", "source": "hm_messagetrace",
  "enabled": true, "truncated": false,
  "note": "Counted from the message trace: one row per recipient per delivery attempt ...",
  "columns": ["day", "domain", "incoming", "outgoing", "failed_incoming", "failed_outgoing"],
  "rows": [{"day": "2026-09-14", "domain": "example.com", "incoming": 12, "outgoing": 3,
            "failed_incoming": 1, "failed_outgoing": 0}],
  "totals": {"incoming": 12, "outgoing": 3, "failed_incoming": 1, "failed_outgoing": 0}
}
```

`enabled` is whether the source is recording at all. `truncated` is whether the window held
more grouped rows than one report reads (see *the cost*, below) — a partial answer that says
so, rather than an unbounded read.

The CSV is written by the server from those very columns and rows, which is why `hmctl` asks
the server for it rather than building one out of the JSON: two writers of the same table
drift, and the file an administrator opens in a spreadsheet should be the one the route
promises. Cells beginning with an equals sign, a plus, a minus or an at sign are prefixed with
an apostrophe — a report of who sent the most mail must not be a way to run a formula on the
administrator's machine.

### What incoming and outgoing mean

Only domains **this server hosts** are counted. The other end of every conversation is
somebody else's domain, and folding it in would tell you that `gmail.com` received four
hundred messages here.

* `incoming` — deliveries whose **recipient** is in that local domain.
* `outgoing` — deliveries whose **sender** is in that local domain.

A local sender writing to a local recipient counts once in each, which is what the server
did with it: it carried the message both ways.

---

## Authorisation

The administrator password reads everything. An API key:

* may be **read-only** — every section is a read, so a monitoring credential needs nothing more;
* if **restricted to named domains**, sees those domains and no others. A request that names a
  domain it may not see is refused 403 by name; a request that names none is *filtered* to its
  own domains rather than refused, which is the treatment `GET /api/v1/domains` already gives
  such a key;
* is refused `volume` and `storage` **outright** — those are counted from server-wide counters
  that carry no domain, so there is no honest way to narrow them. The delivery queue and the
  quarantine are refused to such a key for the same reason. In a `summary`, they appear in the
  `omitted` list with the reason, rather than the document being quietly smaller.

The decision is made in `RestApiServer::Authorize_` and nowhere else, from the section name
and the `domain` parameter that `ParseRoute_` puts into the route.

---

## The cost

An administrator asking for a report must not be a way to take the server down. One report
makes at most three grouped queries, and grouping happens **in the database**:

* **the trace** — one query over `hm_messagetrace` in the window, grouped by
  `(day, event, status, sender, recipient)`. What comes back is distinct tuples, not
  deliveries: a hundred thousand messages between the same fifty correspondents is a few
  hundred rows. The read is capped at 100 000 grouped rows, and the answer sets `truncated`
  when the cap is reached.
* **the mailboxes** — one grouped pass over `hm_messages` by its index on `messageaccountid`,
  returning one row per mailbox that holds anything.
* **the metrics** — one query per metric asked for, grouped by `(day, hour)`: at most 24 rows
  a day.

Nothing scans the message store per request, and nothing reads a message file.

Day and hour are extracted in the dialect of the configured backend (`EXTRACT` on MySQL and
PostgreSQL, `DATEPART` on SQL Server and its Compact Edition); nothing is ordered in SQL,
because a bounded vector is sorted for free and an `ORDER BY` over an aggregate is exactly
the sort of expression the four backends spell differently.

### The arithmetic of a counter

`volume` is the increase of a counter across a day. A counter only climbs until the server
restarts, when it returns to zero. So the increase inside an hour is its maximum less its
minimum, and the increase *between* two hours is the next minimum less the previous maximum —
unless that is negative, which is a restart, and then the whole of the next minimum was
earned since it. Read any other way, a restart shows up as a large negative number, and the
report claims that minus nine thousand messages were delivered on Tuesday.

`storage` is a gauge, not a counter: a day's figure is its **last** sample of that day, so a
store that shrank on the 3rd shows as smaller on the 3rd. `growth_bytes` is the last day's
size less the first day's, and is negative when mail was deleted.

---

## From the Control Deck

**Reports** in the sidebar. A date range with *Last 7 / 30 / 90 days*, a domain picker, a row
count, a bar chart of messages per day and of the store per day, a table per section, and a
**CSV** link on every table. The whole page is one request — `GET /api/v1/reports/summary` —
because seven requests for seven tables of the same window is seven chances for them to
disagree about what the window was.

The sources table at the top is the index, drawn as it came: which source is recording, how
long it is kept, how far back it goes, and the list of what cannot be answered. A section
whose source is switched off is marked *Not recorded* and carries the server's own sentence
saying so.

## From a shell

```
hmctl report sections                     what can be reported, and what cannot
hmctl report traffic --from 2026-09-01 --to 2026-09-15 --domain example.com
hmctl report senders --top 25
hmctl report summary
hmctl report mailboxes --csv mailboxes.csv
hmctl report traffic --csv -              to standard output
```

`--csv` asks the server for `format=csv` and writes what it answers. Without `--csv` the
section's `note` is printed above the table, because "the message trace is switched off" is
not the same statement as an empty table.

---

## Where the code is

| | |
|---|---|
| The aggregates and the SQL | `hmailserver/source/Server/Common/Util/Reports.h` and `Reports.cpp` |
| The route, the sections, the CSV | `hmailserver/source/Server/Common/Util/RestApiReports.cpp` |
| The routing and the authorisation | `RestApiServer.cpp` — `ParseRoute_`, `Authorize_` |
| The two store gauges | `Common/Application/MetricsHistoryTask.cpp`, `Common/Util/MetricsServer.cpp` |
| The page | `hmailserver/installation/WebAdmin/index.html` — `renderReports` |
| The shell command | `hmailserver/source/Tools/Cli/hmctl` — `report_command` |
| The tests | `hmailserver/test/RegressionTests/API/RestApiReports.cs` |
| The checks | `build/check-deck-script.py`, `build/check-deck-languages.py`, `build/check-cli.py` |
