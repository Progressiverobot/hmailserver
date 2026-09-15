What a domain does to a message
================================

Three things a domain can be told to do to the mail passing through it: mark
what comes from outside the organisation, say when a sender has never written
before, and put a legal footer on what leaves. All three are per domain, all
three are off on every existing and every new domain, and while they are off
this costs nothing — no extra read of a message, no extra query, no extra write.

They are configured in three places, which are the same three settings:

* the Control Panel's domain dialog, **Tagging and disclaimer**;
* the Control Deck's domain editor, **What this domain does to a message**;
* `PUT /api/v1/domains/{name}` — `external_tag_subject`, `external_tag_header`,
  `external_tag_text`, `first_contact_tip`, `disclaimer_enabled`,
  `disclaimer_plain_text`, `disclaimer_html`.

Over COM they are `Domain.ExternalTagSubject`, `.ExternalTagHeader`,
`.ExternalTagText`, `.FirstContactTip`, `.DisclaimerEnabled`,
`.DisclaimerPlainText` and `.DisclaimerHTML`. Schema 6044 added the seven
columns and the table behind the first-contact note.

Who counts as outside
---------------------

One rule, in one function — `MessageOrigin::IsExternalSender` — because the
answer decides what a reader is told about a stranger, and two copies of it
would eventually disagree. A sender is **outside** when none of these is true:

1. **The session authenticated.** Read from the topmost `Received:` header,
   which is the one this server wrote before the message file existed, and only
   when its `by` clause names this server. `X-AuthUser` is deliberately not
   consulted: the header is added only when the field is absent, so a sender who
   supplies their own keeps it — it is a claim, not a fact.
2. **A trusted incoming relay handed it over.** The address the message was
   received from is in the incoming-relay list. See the sharp edge below.
3. **Every address the message names is at a domain this server hosts** — the
   envelope sender and the `From:` address, each checked against the accounts,
   the active aliases, the domains and the domain aliases. *Either* of them
   being outside is enough: a message whose envelope claims a hosted domain
   while its visible `From` does not is outside, because the disagreement is
   resolved in the direction that warns.

A message with no `From` and a null return path, from an unauthenticated session
and no trusted relay, is outside: nothing about it belongs to this installation.
A bounce **this** server generated is inside, because its `From` is a hosted
postmaster address.

### The sharp edge: an inbound gateway

Condition 2 means that if you run a filtering gateway in front of this server
**and list it as an incoming relay**, nothing arriving through it is ever
tagged. That is the correct reading of the relay list — it is a statement that
the machine is part of your own edge, and it is what lets this server look past
it for the real originating address — but it does disable the tag for every
message a gateway user receives. If you want the tag and you have a gateway,
the choice is between the tag and the relay declaration; there is no third
answer today, and pretending otherwise would be worse than saying so.

### What this is not

It is **not** anti-spoofing. A message that forges a hosted address in both its
envelope and its `From` is judged inside and is not tagged. The control for that
is DMARC with a policy on your own domain, which this server enforces on the way
in. External-sender tagging labels *origin*; it does not authenticate identity,
and no mail system's tag does.

The external-sender tag
-----------------------

Two switches, because they cost different things.

**`external_tag_subject`** puts the tag text at the front of the Subject.
Effective in every client, including ones that show nothing else — and it
**breaks any DKIM signature the sender made over their Subject**, exactly as the
anti-spam subject prefix (`PrependSubject`) has always done, and for exactly the
same reason: the bytes the signature covers change. This is a deliberate repeat
of an existing trade rather than a new one. The inbound DKIM verdict is
unaffected, because the spam tests run during the SMTP conversation and this
runs at delivery; what breaks is any *downstream* re-verification, and the
forwarding case is what ARC exists for.

**`external_tag_header`** stamps `X-hMailServer-External: YES` instead.
Prepending a field is signature-safe — DKIM selects duplicate fields from the
bottom up — so the sender's signature is untouched. The webmail turns it into a
line above the message: *This message came from outside your organisation.*
Prefer this one wherever the reader can show it.

**`external_tag_text`** is the subject tag, at most 100 characters; empty means
the shipped `[EXTERNAL]`. It is per domain so that a domain whose readers do not
read English can say it in the language they do. A subject that already begins
with the tag — a reply coming back from outside — does not collect a second one;
the comparison ignores case.

The tag is applied to **the recipient account's own copy**, at local delivery,
for two reasons. The switch belongs to the *recipient's* domain, which is not
known until the message has been split between its recipients; and a message
addressed both here and to the outside world must not carry the tag out with it.
It runs above the account's rules and its Sieve script, so both can match on the
header, and below the per-account spam settings, so the subject it reads is the
one the reader will see.

Because it rewrites that account's file, a domain with tagging on does not share
one delivered file between several recipients (`DeliveryHardLinks`). That is the
same cost the per-account spam settings already carry.

Nothing stops a sender writing `X-hMailServer-External` themselves. A forged one
can only make their own message look *more* suspicious, never less, so it is not
a hole — but where a domain has turned the feature on, the field is made to mean
what this server says: a forged copy on a message judged inside is removed.

The first-contact note
----------------------

`first_contact_tip` stamps `X-hMailServer-First-Contact: YES` when an **outside**
sender writes to an account that has had mail from other people but never from
them. The webmail shows it as *You do not usually get mail from this sender.*

### Where the memory lives, and what it costs

A table, `hm_knownsenders`: one row per account and sender address, with a count
and two timestamps. Not the message index — `hm_messages` knows what arrived but
not who from, because the sender is in the file rather than in a column, so
answering the question from it would mean opening every message in the mailbox
on every delivery.

Per delivered copy, with the switch on: one indexed `SELECT`, then one `UPDATE`
when the sender is known, or one further `SELECT` and one `INSERT` the first time
a given address writes to a given account. Two round trips in the steady state.
With the switch off: nothing at all — the row is never looked for.

The memory has a second half. When an account that **signed in** sends a message,
each of its recipients is recorded as somebody that account knows, so their reply
is not announced as a first contact. Only for an authenticated session: on an
unauthenticated one the envelope sender is a claim, and believing it would let
anybody seed an account's memory with the addresses they meant to write from and
so silence the note in advance.

### Two honest limits

**A cold start.** A brand-new installation, or the day the switch is turned on,
has no memory, so for a while every sender looks new. The one degenerate case is
suppressed — the very first message an account ever receives carries no note,
because there is nothing for it to be unusual against — but the days after that
are noisy by construction. Microsoft 365's first-contact tip behaves the same way
in a new tenant. Expect a week.

**A restore from backup.** The table restores with the rest of the database, so
the memory is as of the backup: senders who first wrote after it are forgotten
and their next message is announced as a first contact again. One redundant note
each; nothing is lost. A restore of the mail store *without* the database has no
memory at all and behaves like a fresh installation.

The domain disclaimer
---------------------

`disclaimer_enabled` appends the domain's legal footer to mail leaving the
organisation. `disclaimer_plain_text` and `disclaimer_html` are the two forms;
filling in only the plain one uses it for both, with its line breaks turned into
line breaks in HTML, which is what the domain signature already does.

It runs where the signature runs — `SMTPConnection::DoPreAcceptMessageModifications_`,
against the same `MessageData` and committed by the same single write — because
that is the one place on this server where an outgoing message's body is
modified, and a second one would be a second thing to keep correct. That is also
**before** `DKIMSigner::Sign`, which runs at delivery, so this server's own
signature covers the footer rather than being broken by it.

The footer is appended to the message's `text/plain` part and to its `text/html`
part, in place, wherever those sit in a multipart tree — so a
`multipart/alternative` gets one copy in each and a `multipart/mixed` carrying an
attachment gets it in the text, not after the attachment. A message that has
neither a text nor an HTML part (one attachment and nothing else) is left alone
rather than given a text part it never had.

### What it is never added to

* **A signed or encrypted message** — `multipart/signed`, `multipart/encrypted`,
  `application/pkcs7-mime`, `application/x-pkcs7-mime`, `application/pgp-encrypted`
  or `application/pgp-signature`. **What happens instead: the message is sent
  exactly as the sender wrote it, with no footer**, and a line naming the message
  and its content type is written to the application log so that an administrator
  whose footer is missing from precisely the mail their finance team signs can
  find out why. There is no option to do otherwise, and there should not be:
  appending to a `multipart/signed` breaks verification in every client that
  checks, and an encrypted part cannot be appended to at all because the server
  cannot read it. A wrapper around the signed part would be a different message
  with a different shape, which is not what "append a footer" means.
* **An automatic reply, a vacation message or a delivery report** — anything
  RFC 3834 marks as auto-submitted.
* **A bounce** — a null envelope sender.
* **A list posting** — `List-Id`, `List-Unsubscribe`, or a `Precedence:` of
  bulk, list or junk.
* **A message with no recipient outside this server.** A footer is a statement to
  the outside world; on a note between two colleagues it is noise. A message
  addressed to *both* an outside partner and a colleague does get one, and the
  colleague sees it too — there is one message and one body.
* **A message this server already footed**, which carries
  `X-hMailServer-Disclaimer` naming the domain that added it.
* **A message relayed in from outside that merely claims a hosted sender
  domain.** The footer goes on mail this installation actually sent:
  authenticated, or from an address that is an account here. Attaching a
  company's legal text to a forgery would be worse than attaching it to nothing.

### Once, not once per hop

A reply that quotes a message which already carried the footer does not collect
a second copy. The body is compared with the footer after both have had their
quote markers, their tags and their white space removed, so the `>` prefixes a
plain-text reply adds and the tags an HTML client wraps around it do not hide the
match. A reply that trims the quotation away entirely gets a fresh footer, which
is correct: there is no longer a copy in it.

A footer shorter than eight characters is never treated as already present —
a needle that short would match almost any body, and the administrator would get
no footer at all.

What the webmail shows
----------------------

`GET /api/v1/me/messages/{id}` carries three fields the reader uses:
`external_stamped` (this server's own verdict, from the stamped header),
`first_contact`, and `external` — which stays what it has always been, the
sender's domain against the account's, now also true whenever the server
stamped. The badge beside the sender comes from `external`; the two lines above
the message come from `external_stamped` and `first_contact`, so they appear only
when the recipient's domain asked for them.

Where the code is
-----------------

| | |
|---|---|
| `Common/Util/MessageOrigin.*` | The rule, and its unit tests (`MessageOriginTester`, run from `ClassTester::DoTests`). |
| `Common/Util/ExternalSenderTagger.*` | The tag and the note, applied at local delivery. |
| `Common/Util/DisclaimerAdder.*` | The footer, applied at submission beside the signature. |
| `Common/Persistence/PersistentKnownSender.*` | `hm_knownsenders`. |
| `SMTP/LocalDelivery.cpp` | Where the tagger is called. |
| `SMTP/SMTPConnection.cpp` | Where the disclaimer and the outgoing half of the memory are called. |
| `test/RegressionTests/SMTP/DomainTransforms.cs` | The delivery tests. |
| `test/RegressionTests/API/RestApiDomains.cs` | The REST round trip. |
