Administering the server from a command line
============================================

Two clients, one vocabulary, one API: `hmctl` for any shell, and the
`HmailServer` PowerShell module for PowerShell. Both speak the REST API, so both
work against a Windows server, a Linux server, or a server on another machine,
and neither needs COM or the Control Panel.

This is not a replacement for either administration program. The Windows Control
Panel is the place to configure a server, and the browser Control Deck is that
place on Linux. A command line is for the things a command line is better at:
making fifty accounts from a spreadsheet, reading one setting in a script,
watching the delivery queue, asking what a rule criterion would decide, or doing
the same thing to twenty servers.

| | |
|---|---|
| `hmailserver/source/Tools/Cli/hmctl` | One Python 3 file, no dependencies beyond the standard library. The Linux packages install it as `/usr/bin/hmctl`. |
| `hmailserver/source/Tools/Cli/HmailServer.psm1` | The PowerShell module: `Get-HmDomain`, `New-HmAccount`, `Set-HmSetting` and the rest, answering objects rather than text. |
| `build/check-cli.py` | Runs both against a recorded server and asserts what each one sent, so the two cannot drift apart in what they ask for. In CI. |

`build/hmconfig.ps1` is a different tool and stays: it exports the whole
configuration as one reviewable JSON document and applies it to another machine,
over COM, on Windows. These two are per-object verbs over HTTP.

Credentials
-----------

Either the administrator password, sent as HTTP Basic with the user name
`Administrator`, or an API key, sent as a bearer token. An API key is the better
answer for anything unattended: it can be read-only, it can be restricted to one
domain, and it can be revoked without changing the administrator password.

```bash
export HMAIL_URL=https://mail.example.com:8045
export HMAIL_API_KEY="$(cat /etc/hmailserver/api.key)"
hmctl domain list
```

```powershell
Connect-HmServer -Url https://mail.example.com:8045 -ApiKey (Get-Content .\api.key)
Get-HmDomain
```

`hmctl` reads `HMAIL_URL`, `HMAIL_API_KEY`, `HMAIL_PASSWORD` and
`HMAIL_PASSWORD_FILE`, and takes `--url`, `--api-key`, `--password` and
`--password-file` wherever you type them - before the verb or after it. With no
credential at all it asks for one, but only when there is a console to answer:
in a scheduled task or a pipeline it says what is missing and stops, rather than
waiting at a prompt nobody can see.

**`--insecure` is refused for anything but the loopback.** A self-signed
certificate on `localhost` is the ordinary case and nothing is on the wire; an
administrator password sent to a remote host over a connection nobody verified
is not a convenience, and this will not make it one. Give the server a
certificate the client trusts.

hmctl
-----

```
hmctl [--url URL] [--api-key KEY | --password PASSWORD | --password-file FILE]
      [--insecure] [--json] <resource> <verb> [arguments]
```

| Resource | Verbs | Notes |
|---|---|---|
| `domain` | list, get, create, update, delete | |
| `account` | list, get, create, update, delete | Listed and created under `--domain`, read and written at the address: which is how the API is shaped. |
| `alias` | list, create, delete | Under `--domain`. |
| `list` | list, create, update, delete | Distribution lists, under `--domain`. |
| `group` | list, get, create, update, delete | Account groups; `member` handles the accounts in one. |
| `rule`, `route`, `port`, `iprange`, `certificate` | list, create, update, delete | Server-wide. |
| `fetchaccount` | list, get, create, update, delete | Under `--address`. |
| `apikey` | list, create, delete | Administrator password only, never an API key. |
| `queue` | list, retry, delete | |
| `quarantine` | list, release, delete | |
| `settings` | get, set | A group whole, or one setting: `settings`, `antispam`, `antivirus`, `logging`, `backup`, `cache`, `indexing`, `scripting`, `directories`. |
| `ini` | list, get, set, delete | The `hMailServer.ini` keys the server exposes. |
| `log` | list, tail | |
| `backup` | status, start | The server's own backup. |
| `app-password` | list, create, delete | Under an account's address. |
| `match` | *(a criterion and a value)* | What a rule criterion would decide, without a rule. |
| `accounts` | import, export | A CSV file, with a dry run. |
| `report` | *(a section)* | What happened on this server, per domain and per day. `sections` first: it says what can be reported and what cannot. |
| `api` | *(method and path)* | The escape hatch, for a route newer than this file. |

A few whole commands:

```bash
hmctl domain create --set name=example.com --set active=true
hmctl account create --domain example.com --set address=ann@example.com --set password=...
hmctl account list --domain example.com
hmctl settings get antispam use_spf
hmctl settings set antispam --set use_spf=true --set spam_mark_threshold=5
hmctl queue list --json | jq '.[] | select(.retry_count > 3)'
hmctl match contains viagra "cheap viagra here"
hmctl report sections
hmctl report traffic --from 2026-09-01 --to 2026-09-15 --domain example.com
hmctl report mailboxes --top 25 --csv mailboxes.csv
hmctl api POST /api/v1/server/reinitialize
```

`--set name=value` reads `true`, `false`, `null`, numbers and anything starting
with `{` or `[` as JSON and everything else as a string; `--set-string` keeps a
value that looks like one of those a string. `--from-json FILE` takes a whole
object, and `-` reads it from standard input.

Output is a table by default and the server's own JSON with `--json`. The exit
code is 0 for success, 1 for an error, 2 for a usage mistake, 3 when the server
refused the credential and 4 when what you named does not exist - so a script
can tell "no such account" from "wrong password" without reading the text.

Reports
-------

```bash
hmctl report sections
hmctl report summary --from 2026-09-01 --to 2026-09-15
hmctl report senders --domain example.com --top 25
hmctl report traffic --csv traffic.csv
```

The sections are `traffic`, `failures`, `senders`, `recipients`, `mailboxes`,
`volume`, `storage` and `summary`. The window is `--from` and `--to` as
`YYYY-MM-DD`, both inclusive, defaulting to the last thirty days ending today on
the server; `--domain` narrows it to one domain the server hosts; `--top` is how
many rows the sender, recipient and mailbox sections return.

`--csv FILE` writes the table, and `--csv -` writes it to standard output. It is
the **server's** CSV, fetched with `format=csv`, rather than one built here out of
the JSON: two writers of the same table drift, and the file that ends up in a
spreadsheet should be the one the route promises.

Without `--csv`, the section's own note is printed above the table. Read it. The
four sections counted from the message trace are empty when the trace is switched
off - which is the default - and "the message trace is switched off" is not the
same statement as "nothing happened". `hmctl report sections` says which sources
are recording, how long each is kept, and the two questions this server cannot
answer at all: spam and virus counts per domain, and storage growth per domain.
[Reports.md](Reports.md) is why.

Accounts from a spreadsheet
---------------------------

```bash
hmctl accounts import new-people.csv --dry-run
hmctl accounts import new-people.csv
hmctl accounts export example.com accounts.csv
```

The header row names the fields and `address` is the only one required; anything
the account resource takes may be a column, so the file does not need changing
when the account gains a field.

```csv
address,password,name,active,max_size_mb
ann@example.com,Sup3rSecret!,Ann Adams,true,500
bob@example.com,An0therOne!,Bob Brown,true,
```

An empty cell is not sent, so the server's own default applies. **An address
that already exists is left alone and counted**, never overwritten: an import
that silently reset a password would lock a hundred people out with one command.
`--dry-run` makes no request that changes anything and prints what would happen,
line by line.

The export writes no password column, because the server does not have the
passwords to give. A file exported and re-imported makes no accounts; it is for
an inventory, not a round trip.

The configuration as one file
-----------------------------

```bash
hmctl config export server.json          # the whole configuration, as a document
hmctl config diff server.json            # what would change; exit 1 if anything would
hmctl config apply server.json           # the plan, and nothing else
hmctl config apply server.json --force   # the plan, applied
```

The document holds the settings groups, the `hMailServer.ini` keys the server
exposes, the domains and their accounts, aliases and distribution lists, the
groups, the rules, the routes, the listeners and the IP ranges. It is sorted
throughout and carries nothing the server allocated - no identifier, no count,
no time - so **two exports of an unchanged server are the same bytes**. That is
what makes it usable in a repository: a diff means a change, and nothing churns.

`apply` is a plan unless you add `--force`, and a **deletion needs `--allow-delete`
on top of that**. Without it, an entry the server has and the document does not
is left alone and counted, because the common case is a document written from
one server and applied to another that has things of its own.

What it does not carry, and why:

* **Passwords.** The server does not give them out, so they are not in the
  document. An account in the document that does not exist on the server is
  refused with that reason rather than created with a password nobody chose;
  add a `password` field for the accounts you mean to create.
* **Certificates and the directories.** A certificate is files on the server's
  own disk, and the directories are that machine's paths; copying either between
  machines would describe a server that does not exist.
* **What lives under an account** - application passwords, fetch accounts,
  folder permissions - which are the account holder's rather than the
  configuration's.

### A repository that is the configuration

There is no daemon; the loop is two commands and whatever runs them.

```yaml
# On a pull request: does the repository still match the server?
- run: hmctl config diff server.json        # exit 1 fails the job

# On merge: make the server match the repository.
- run: hmctl config apply server.json --force
```

Give the job an API key rather than the administrator password, keep the key in
the runner's secret store, and let the diff run on a schedule as well: a server
that has drifted from its repository is worth knowing about before somebody
needs the repository to be true.

The PowerShell module
---------------------

```powershell
Import-Module .\hmailserver\source\Tools\Cli\HmailServer.psm1
Connect-HmServer -Url https://localhost:8045 -Insecure

Get-HmDomain | Where-Object active -eq $false
Get-HmAccount -Domain example.com | Sort-Object max_size_mb -Descending | Select-Object -First 10
New-HmAccount -Domain example.com -Address ann@example.com -Password (Read-Host -AsSecureString)
Set-HmAccount -Address ann@example.com -Property @{ max_size_mb = 500 }
Set-HmSetting -Group antispam -Property @{ use_spf = $true }
Get-HmQueue | Where-Object retry_count -gt 3 | Start-HmQueueDelivery
Import-HmAccount -Path .\new-people.csv -WhatIf
```

Every function answers objects, so `Where-Object`, `Sort-Object`, `Export-Csv`
and the rest work on them; nothing in the module formats anything. Everything
that changes the server supports `-WhatIf` and `-Confirm`, and deleting a domain
or an account asks before it acts unless told not to. A domain name or an
address completes from the server itself once `Connect-HmServer` has run.

`Invoke-HmApi -Method GET -Path /api/v1/...` is the escape hatch, and is what
every other function is built on.

What neither client does
------------------------

* **It does not read or write the message store.** Mail is IMAP's business.
* **It is not a backup tool.** `hmctl backup start` asks the server to run its
  own backup, and that is all.
* **It sets no administrator password.** `hmailserver --set-admin-password` on
  the server does that, and the Control Panel does it on Windows.
* **Neither invents a field.** A field you did not give is one the server
  decides; the two clients were made to send the same request for the same
  command, and `build/check-cli.py` fails if they stop doing so.
