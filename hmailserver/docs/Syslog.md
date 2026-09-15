Sending the log to syslog
=========================

Every log entry this server writes can also be sent, as RFC 5424, to a syslog
collector — rsyslog, syslog-ng, journald, a SIEM — over UDP, TCP or TCP with TLS.
On Linux, a server running under systemd with no collector configured writes to
the journal instead, where `journalctl` reads it.

It is a **copy**, not a move. The log files under `LogFolder` are written exactly
as they were before, the SQL log device still does what it did, and the OTLP log
exporter is unaffected. Nothing you turn on here can stop a line reaching the
files, and nothing here can delay or lose a message: the sink hands entries to a
background thread over a bounded queue, and a collector that is down costs
dropped log lines and nothing else.

Turning it on
-------------

Control Panel → **Logging** → the *syslog (RFC 5424)* card. Everything below is
also on the COM `Settings.Logging` object, so a script can set it, and the values
live in the `hm_settings` table rather than in `hMailServer.ini` — which means a
Control Panel on another machine can read and write them, and a configuration
backup contains them.

**The settings take effect when the sink starts**, which is at the next service
start or when you press *Reinitialize*. Saving the page writes the values
immediately; the running sink keeps the ones it started with.

| Setting | `hm_settings` name | REST key, under `/api/v1/settings/logging` | Default | What it means |
|---|---|---|---|---|
| Send log entries to syslog | `SyslogEnabled` | `syslog_enabled` | off | The whole feature. With it off nothing below has any effect. |
| Collector host name or address | `SyslogHost` | `syslog_host` | *(empty)* | Where to send. **Empty is not "off"**: on Linux under systemd it selects the journal; on Windows it means nothing is sent, and the server says so once in the application log. |
| Collector port | `SyslogPort` | `syslog_port` | `514` | 514 is the registered port for UDP and for plain TCP; 6514 is the registered port for syslog over TLS. |
| How entries reach the collector | `SyslogTransport` | `syslog_transport` (`udp`, `tcp`, `tls`) | `0` (UDP) | `0` UDP, `1` TCP, `2` TCP over TLS (RFC 5425). |
| Facility | `SyslogFacility` | `syslog_facility` | `2` (mail) | The RFC 5424 facility, 0 to 23. 2 is `mail`; 16 to 23 are `local0` to `local7`. |
| Send entries at least this severe | `SyslogMinimumSeverity` | `syslog_minimum_severity` | `6` (informational) | Entries less severe than this are not sent. Numerically lower is more severe. |
| Which categories are sent | `SyslogLogTypes` | `syslog_log_smtp`, `syslog_log_pop3`, `syslog_log_imap`, `syslog_log_application`, `syslog_log_tcpip`, `syslog_log_debug` | `86` | One stored bit mask: SMTP 2, POP3 4, TCP/IP 8, application 16, debug 32, IMAP 64. The Control Panel and the REST API both show it as six switches; 86 is SMTP + POP3 + application + IMAP. |

On Linux there is no Control Panel, so the REST keys in the third column and the
Control Deck are how this is turned on there. `hmctl settings get logging` prints
them all.

Two things the mask does not control, on purpose:

* **Errors are always sent.** The error log is written even when logging is
  switched off entirely, and this follows it. An administrator who has narrowed
  the categories has narrowed the noise, not the alarms.
* **A category nobody has a switch for is sent.** If a future release adds a log
  category and forgets to add it here, the entries arrive rather than vanishing.

What a message looks like
-------------------------

```
<22>1 2026-09-15T14:21:09.284+01:00 mail.example.com hMailServer 4812 SMTPD [hmailserver@32473 thread="7412" session="41" client="203.0.113.9"] <BOM>RECEIVED: MAIL FROM:<sender@example.org>
```

Field by field, in the order RFC 5424 section 6 puts them:

* `<22>` — the priority: facility × 8 + severity. 2 × 8 + 6 is an informational
  entry on the `mail` facility. An error line from `ErrorManager` reporting
  severity *Medium* would be 2 × 8 + 4 = `<20>`.
* `1` — the syslog version. It is not this server's version and never changes.
* the timestamp — RFC 3339, with milliseconds and a real offset. It is the same
  instant the file log records, taken from the same entry rather than read from
  the clock a second time, so a line in the file and a line at the collector can
  be matched exactly.
* the hostname — the server's own `HostName` setting; the machine's name when
  that is empty; `-` when neither can be read.
* `hMailServer` — the application name.
* `4812` — the process id.
* `SMTPD` — the message id. It is the log category (`SMTPD`, `SMTPC`, `POP3D`,
  `POP3C`, `IMAPD`, `APPLICATION`, `TCPIP`, `DEBUG`, `ERROR`) — except on an
  error line carrying an hMailServer error number, where it is that number:
  `HM5015`. That makes `msgid == HM5165` a usable filter at the collector.
* the structured data — `thread`, and `session` and `client` where the entry
  belongs to a conversation. The SD-ID is `hmailserver@32473`; 32473 is the
  enterprise number IANA reserves for documentation and examples (RFC 5612),
  used because this project has none registered of its own. A collector keys on
  the whole SD-ID, so this is a name rather than a claim.
* the message — UTF-8, introduced by the byte-order mark RFC 5424 section 6.4
  asks for (shown above as `<BOM>`, three bytes: EF BB BF). rsyslog, syslog-ng and journald all strip
  it; a receiver that does not shows three extra bytes.

Severity is decided from the category: an `ERROR` entry carries the severity
`ErrorManager` recorded (Critical → `crit`, High → `err`, Medium → `warning`,
Low → `notice`), `DEBUG` and `TCPIP` are `debug`, and everything else is
`informational`.

Sizes and framing
-----------------

* **UDP** truncates the whole message to 1024 bytes, cutting the message text and
  never the header, on a UTF-8 character boundary. RFC 5426 requires a receiver
  to accept 480 octets and recommends 2048; 1024 clears the requirement and stays
  under every path MTU that matters once the IP and UDP headers are added.
* **TCP and TCP over TLS** frame each message with its own length — `MSG-LEN SP
  SYSLOG-MSG`, the octet counting of RFC 5425 section 4.3 and RFC 6587 — and
  truncate nothing below 65000 bytes.

When the collector is not there
-------------------------------

The sink is on the path every message takes, so its failure behaviour is part of
the feature rather than an afterthought:

* Entries are queued for a background thread. The queue holds 4096 entries and
  drops the **oldest** when it is full, so a collector that has stopped reading
  costs a fixed amount of memory and the newest lines — the ones describing what
  is happening now — are the ones kept.
* A stream transport that cannot connect backs off: one second, then two, four,
  and so on to a minute. Nothing is attempted while the back-off is in force, so
  a dead collector does not cost a connect timeout per log line.
* The application log gets **one** line when an outage starts and **one** when
  the collector comes back, with the number of entries dropped. It does not get
  one per failure: a collector that has been down for a day would otherwise write
  the log it was meant to be reading.
* Nothing is reported through `ErrorManager`. A log collector being unreachable is
  an operational fact, not a server fault, and an entry in the ERROR log would be
  read as one.

A working rsyslog configuration
-------------------------------

Drop this in `/etc/rsyslog.d/30-hmailserver.conf` and restart rsyslog. It listens
for RFC 5424 over UDP and TCP, and files anything whose application name is
`hMailServer` in its own file.

```rsyslog
# UDP, for the default transport.
module(load="imudp")
input(type="imudp" port="514")

# TCP, for SyslogTransport = 1. Octet counting is what rsyslog calls
# "octet-counted" framing and is what imtcp expects by default.
module(load="imtcp")
input(type="imtcp" port="514")

# One file for this server, and stop, so the lines are not duplicated into
# /var/log/mail.log as well. Drop the "stop" if you want both.
if ($app-name == "hMailServer") then {
   action(type="omfile" file="/var/log/hmailserver.log"
          template="RSYSLOG_SyslogProtocol23Format")
   stop
}
```

`RSYSLOG_SyslogProtocol23Format` writes the line back out as RFC 5424, structured
data included, so `thread`, `session` and `client` survive into the file. Without
it rsyslog reformats to the older RFC 3164 shape and the structured data is lost.

For **TLS** (`SyslogTransport = 2`), rsyslog needs a certificate whose chain and
name this server can verify against the machine's trust store — a self-signed
certificate has to be installed as trusted on the hMailServer machine, or the
handshake is refused and nothing is sent:

```rsyslog
module(load="imtcp"
       StreamDriver.Name="gtls"
       StreamDriver.Mode="1"
       StreamDriver.AuthMode="anon")
input(type="imtcp" port="6514")

global(DefaultNetstreamDriverCAFile="/etc/ssl/certs/ca.pem"
       DefaultNetstreamDriverCertFile="/etc/rsyslog.d/collector-cert.pem"
       DefaultNetstreamDriverKeyFile="/etc/rsyslog.d/collector-key.pem")
```

Set the server's **Collector host name or address** to the name that appears in
the collector's certificate, not to its IP address: the name is what is checked.

A working systemd-journal configuration
---------------------------------------

There is nothing to configure. On a machine where the server runs as the
`hmailserver.service` unit the packages install, switching **Send log entries to
syslog** on and leaving the collector host **empty** writes to the journal
directly, over `/run/systemd/journal/socket`. Nothing else is needed, and no
collector has to be installed:

```console
$ hmctl settings set logging --set syslog_enabled=true
$ sudo systemctl restart hmailserver
$ journalctl -u hmailserver -f
```

The journal gets the same fields, under names `journalctl` already understands
plus four of our own:

| Journal field | What it holds |
|---|---|
| `MESSAGE` | The log message. |
| `PRIORITY` | The same 0–7 severity the collector would get. |
| `SYSLOG_IDENTIFIER` | `hMailServer`. |
| `SYSLOG_FACILITY` | The configured facility. |
| `SYSLOG_PID` | The process id. |
| `HMAILSERVER_CATEGORY` | `SMTPD`, `IMAPD`, `APPLICATION`, … |
| `HMAILSERVER_MSGID` | The category, or `HM…` on an error line that carries a number. |
| `HMAILSERVER_THREAD` | The logging thread. |
| `HMAILSERVER_SESSION` | The session, on a conversation line. |
| `HMAILSERVER_CLIENT` | The peer address, on a conversation line. |

Which makes the questions an administrator actually asks answerable from the
journal:

```console
# everything the server logged for one SMTP conversation
$ journalctl -u hmailserver HMAILSERVER_SESSION=41

# everything one client did, across sessions, since midnight
$ journalctl -u hmailserver HMAILSERVER_CLIENT=203.0.113.9 --since today

# errors only, newest first
$ journalctl -u hmailserver -p err -r

# one particular error number, wherever it came from
$ journalctl -u hmailserver HMAILSERVER_MSGID=HM5165
```

If a collector host **is** configured the journal is not used: the server sends
to the collector instead. Point it at `127.0.0.1` if you want both — journald
itself will take the datagram when it is the machine's syslog listener.

What this does not do
---------------------

* **It does not replace the log files.** `LogFolder` is still written, still
  rotated by `LogDeleteDays` or by the packaged logrotate rule, and is still what
  the Live logs page reads.
* **It does not send the AWStats journal, the backup log or the event log.**
  Those three have external consumers that parse a fixed field list, and neither
  the log format setting nor the log device setting touches them either.
* **It does not retry a dropped entry.** The queue is the buffer; there is no
  disk spool. A collector that must not lose a line should be reached over TCP or
  TLS, which at least tells the server when it is not there.
* **It does not authenticate to the collector.** TLS verifies the collector's
  certificate; the collector is not offered one of ours. A collector that needs
  client certificates is a roadmap item rather than a setting.
