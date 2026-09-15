Remote domains: TLS, limits and recipient verification
=====================================================

A remote domain policy says what this server will do when it talks to one named
domain it does not host: a partner, a customer, a bank, a provider. It holds three
kinds of decision, in one record because they are one subject:

* **Transport security** - whether mail *to* the domain must be encrypted, and how
  strongly; whether mail *from* the domain must arrive encrypted.
* **What this server will attempt** - the largest message it will send there, how
  many connections it holds open to it at once, how many messages a minute it sends
  it, and whether an automatic reply or a forward may go there at all.
* **Recipient verification** - whether an address at the domain is checked with the
  domain's own server before this server accepts mail for it. This is for a domain
  this server is a backup MX for, and for nothing else.

Policies live in the database (`hm_remotedomainpolicies`, schema 6047), take effect
for the next message without a restart, and are part of the configuration backup.
Nothing about them is in `hMailServer.ini`.

They are edited four ways, which all write the same records:

| Where | How |
|---|---|
| Control Panel | **Mail flow & delivery > Remote domains**, beside Routes |
| Control Deck | **Remote domains** in the navigation |
| REST API | `GET`/`POST /api/v1/remote-domains`, `PUT`/`DELETE /api/v1/remote-domains/{id}`, `GET /api/v1/remote-domains/effective?domain=...`, `POST /api/v1/remote-domains/verification-cache/clear` |
| COM | `Application.Settings.RemoteDomainPolicies` |

All of them are server-administrator only. An API key restricted to named domains
is refused, because a policy names somebody else's domain and decides this server's
posture rather than one hosted domain's.


A policy is not a route
-----------------------

A **route** says *where* mail for a domain goes: a target host and port, a
credential, and it replaces the MX lookup. A **policy** says what this server will
and will not do when it gets there, however the server was found. A domain may have
both, and the policy applies to a routed delivery exactly as it applies to an MX
one - a policy that a route quietly exempted from its TLS requirement would be the
silent downgrade this feature exists to prevent.


Which policy applies
--------------------

A policy names a domain (`bank.example`) or a wildcard pattern (`*.example`, `*`).
For each delivery the **most specific active** policy governs:

1. an exact name beats any pattern, however long the pattern;
2. between two patterns, the one with more literal characters wins;
3. an inactive policy is skipped, and the next most specific active one governs in
   its place. Switching off the entry for one domain beneath a `*` that requires TLS
   removes an exception; it does not exempt that domain from the rule every other
   domain still obeys.

`GET /api/v1/remote-domains/effective?domain=mail.bank.example` (and
`PolicyForDomain` over COM) answers which record a delivery will use, so this can be
checked before a message finds out.

When one connection carries recipients of several domains - several domains routed
to the same smart host, for instance - the strictest combination applies: the
highest TLS requirement, and the smallest of each non-zero limit.


Outbound TLS
------------

| Value | What delivery requires |
|---|---|
| `none` | Nothing beyond what the server would do anyway: the SMTP connection security setting, the route's own security, and any MTA-STS or DANE policy the domain publishes. |
| `encrypted` | STARTTLS must be offered and the handshake must succeed. The certificate is not judged - the level for a partner with a self-signed or internal certificate. |
| `verified` | Encrypted, and the certificate must chain to a trusted root and name the host being connected to - what MTA-STS `enforce` requires. |
| `dane` | Encrypted, and the certificate must match a DNSSEC-validated TLSA record published for the host (RFC 7672). A host with no usable validated record is skipped. The TLSA lookup is made for such a policy even when `DaneEnabled` is off and even for a routed delivery; with `DnssecValidationEnabled` off no record counts as validated, so every delivery under the policy is deferred. |

**How this composes with MTA-STS and DANE.** Those two already require TLS for a
domain that publishes a policy. A remote domain policy is the administrator saying
so for a domain that does not. The three combine as the *strongest* of them, never
the newest: MTA-STS `enforce` and a usable TLSA record each raise the requirement on
their own, and a policy here can only raise it further. A domain that publishes an
MTA-STS enforce policy and is named here at `encrypted` still gets certificate
verification, because MTA-STS asked for it. There is no value on this page that
weakens what a domain published for itself.

**What a refusal looks like.** A delivery a policy cannot satisfy is **deferred**,
never sent in the clear and never silently downgraded. The recipient's status is
`4.7.0`, the message stays in the queue and is retried on the usual schedule, and
the text an administrator reads - in the delivery log and, if the retries run out,
in the bounce - names the rule:

    Delivery deferred: the remote domain policy for bank.example requires TLS with a
    certificate that verifies to this destination, and it could not be established.
    The message has NOT been sent in the clear.

It is a deferral rather than a bounce on purpose. The remote is a third party, and
its STARTTLS - or its certificate - may be fixed before the queue gives up; RFC
8461 section 5 reasons the same way about MTA-STS. A DANE requirement no host can
meet is deferred with `4.7.0` and a sentence naming the policy in the same way.

**Inbound TLS.** With **Require TLS on mail from this domain** set, a sender whose
envelope address is at the domain is answered `530 5.7.0 Must issue STARTTLS first`
at `MAIL FROM` on an unencrypted session. It is not applied to an authenticated
submission (one of this server's own users), and not to a connection from a
configured **incoming relay**: behind a front-end filter or gateway, the hop the
policy is about happened before that machine, and inbound TLS has to be required
there. Mind that the envelope sender is whatever the connecting server claims; this
is a transport rule for honest partners, not an anti-spoofing control.


Limits
------

| Setting | 0 means | Beyond it |
|---|---|---|
| Largest message (KB) | no limit of ours | The message is **not attempted**: no connection is opened, and it is bounced at once with `5.3.4`, naming the policy and both sizes. This is the one refusal that is permanent - a message will not be smaller on the next attempt. |
| Simultaneous connections | unlimited | The delivery is deferred with `4.4.5` and retried on the usual schedule. Delivery threads never wait for a slot, because a thread waiting is a thread not delivering anybody else's mail. The count covers every domain the policy's pattern matches. |
| Messages a minute | unlimited | Deferred with `4.4.5`, as the server-wide `MaxOutboundPerDestinationPerMinute` does; the two are independent and both apply. |

A deferral waits for the retry interval of the route, or of the server's delivery
settings. A tight connection ceiling on a busy
destination can therefore delay mail by that interval; set the retry interval with
that in mind, or prefer the messages-a-minute limit, which spreads the load rather
than parking it.

**Automatic replies and forwarding.** With **Automatic replies may go to this
domain** cleared, no out-of-office reply - an account's own or the domain-wide one -
is sent to an address at the domain. With **Mail may be forwarded to this domain**
cleared, an account's forward to an address there is not made (the message is still
delivered to the mailbox), and neither is a Sieve `redirect`. Both are logged at
debug level.


Recipient verification (backup MX)
----------------------------------

A backup MX accepts mail for every address at the domain it backs up, because the
list of addresses is on the primary. While the primary is down, a message for an
address that does not exist is accepted, queued, refused by the primary when it
returns, and bounced - to the envelope sender, who in a dictionary attack is
somebody else. The backup has become a backscatter source. With **Verify a
recipient with the domain's own server** on, this server asks the primary at `RCPT
TO` time and refuses the address there, to the machine that is actually sending.

**How it asks.** An SMTP session: `EHLO`, `MAIL FROM:<>`, `RCPT TO:<address>`,
`QUIT`. No message is sent. The null sender is what RFC 5321 reserves for
non-delivery traffic, and it cannot be bounced to. The server asked is **The server
to ask**, or when that is empty the domain's MX hosts in preference order, skipping
any address and port this server itself listens on - which is the backup's own MX
record. At most two hosts are tried.

**What it decides.**

| The primary answers | This server |
|---|---|
| `2xx` to `RCPT TO` | accepts the recipient |
| `5xx` to `RCPT TO` | refuses it: `550 5.1.1 Recipient address rejected` |
| anything else - `4xx`, a refused null sender, no greeting, a timeout, no host to ask | **accepts**, exactly as it would have without the policy |

A verification that cannot be made never becomes a refusal. The primary's own
wording is written to this server's log and is not repeated to the sender.

**When it asks.** Only for a session that has not authenticated, only after the
relay and authentication decisions, the DNS blacklists and greylisting have had
their say, and never for a domain this server hosts. An address this server already
knows the answer to never reaches a verification.

**Safety settings.**

| Setting | Default | Why |
|---|---|---|
| How long to wait | 10 s, at most 60 | The wait is inside the sending server's SMTP session. It is one deadline over the whole conversation, the same on Windows and Linux. |
| How long an answer is remembered | 60 min | A flood of messages to one address costs the primary one session. A verification that gave no verdict is remembered for one minute at most, so a primary that comes back is asked again promptly. |
| How often one domain is asked, per minute | 10 | A ceiling on sessions to the primary. Beyond it recipients are accepted without asking - our own throttle never refuses somebody else's mail. |

**Two warnings.**

* *A callout can be used to enumerate a third party's addresses*, and to make this
  server a nuisance to someone else's. Turn it on only for a domain this server is a
  backup MX for, by agreement with whoever runs the primary. The rate limit and the
  cache bound what an abuser gets; they do not make the feature appropriate for a
  domain you do not serve.
* *Some primaries refuse the null sender* or answer every `RCPT TO` with `250` until
  `DATA`. Against those, verification gives no verdict and changes nothing - check
  the log for `gave no verdict` after turning it on.

When the primary has been fixed after refusing addresses it should have accepted,
**forget the remembered verdicts**: the button on the Deck view,
`POST /api/v1/remote-domains/verification-cache/clear`, or
`RemoteDomainPolicies.ClearVerificationCache()` over COM.


What is not here
----------------

* **Per-domain message format** (Exchange's "use rich text" and character-set
  choices). This server sends a message as it was written.
* **Recipient verification for a domain whose primary is this server's own
  database** - that is not a callout, and `RecipientParser` already answers it.
* **A per-policy SMTP port or credential** - that is a route's job.
