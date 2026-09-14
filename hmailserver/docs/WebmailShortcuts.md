Webmail: keyboard shortcuts and search operators
================================================

The webmail at `/portal` has a keyboard map (press `?` in it) and a search
syntax (the search box lists it in its tooltip). This page is the same two
lists, for anyone who reads before they click. The page itself is the source:
the keyboard map is the `keys-overlay` in
`hmailserver/source/Server/Common/Util/Portal.html`, the search syntax
is the `x-operators` line of `/api/v1/me/search` in `RestApiServer.cpp` and
the OpenAPI document at `/api/v1/openapi.json`.

Keyboard shortcuts
------------------

The keys act on the message list and on an open message; none is captured
while the cursor is in a text field, except the ones the fields themselves
use.

| Key | What it does |
| --- | --- |
| `j` or `↓` | Next message |
| `k` or `↑` | Previous message |
| `Enter` or `o` | Open the message under the cursor - beside the list when the reading pane is on, in its place when it is off |
| `u` or `Esc` | Back to the list |
| `x` | Tick or untick the message under the cursor |
| `Shift` + click, `Ctrl` + click | On a row or its box: Shift ticks every row from the last one clicked (or ticked with `x`) to this one; Ctrl (Cmd on a Mac) ticks or unticks the row without opening it |
| `s` | Star or unstar the message |
| `e` | Archive the message |
| `!` | File the message as junk, or as not junk from the Junk folder |
| `#` | Delete the message (to Trash) |
| `l` | Label the message (the Label menu of the open message) |
| `m` | Mute or unmute the conversation: it leaves the inbox, and a rule the page writes files its replies into the archive folder |
| `c` | Write a new message |
| `r` `a` `f` | Reply, reply to all, forward the open message |
| `/` | Search |
| `Ctrl` + `K` | The palette: an action, a page, a folder or a contact |
| `?` | The list of shortcuts |
| `Esc` | Close the list or the palette |
| `↓` `↑` `Enter` `Tab` | In To, Cc and Bcc: choose a completion |
| `↓` `↑` `Esc` | In the empty search box: the last ten searches are offered under it (kept in this browser, forgotten at sign-out); the arrows move through them, Enter runs one, Esc closes the list |

Search operators
----------------

The search box searches the folder shown; the options panel beside it (the
sliders icon) builds the same syntax from fields - from, to, subject, words,
label, dates, attachments, unread, starred - and can search every folder the
account may read. Words must all be found; the order does not matter.

| Operator | What it matches |
| --- | --- |
| `word` | The word in the subject or the sender; failing those, anywhere in the message, which is then read whole (for a message small enough). Every word given must be found. |
| `"quoted phrase"` | The words together, in that order. |
| `from:` | The sender's address or name. |
| `to:` | A recipient's address or name, in To or Cc. |
| `subject:` | The subject. |
| `has:attachment` | Messages carrying an attachment. |
| `before:YYYY-MM-DD` | Messages dated before that day. |
| `after:YYYY-MM-DD` | Messages dated after that day. |
| `in:folder` | Messages in that folder (a whole path may be given). |
| `is:unread` / `is:read` | By the seen flag. |
| `is:flagged` / `is:unflagged` | By the flag. |
| `is:answered` | Messages that have been replied to. |
| `label:name` | Messages carrying that label (an IMAP keyword); several `label:` terms must all be present. |

Examples: `from:alice is:unread`, `subject:"quarterly report" has:attachment`,
`in:Archive after:2026-01-01 label:travel`.
