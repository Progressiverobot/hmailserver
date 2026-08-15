# The real-Postfix bench

A test rig that delivers mail into hMailServer through a **real Postfix relay**
(PIPELINING on, PMG-style configuration), because one class of defect is
invisible to every test that writes well-formed bytes through a helper: framing
bugs that depend on how a real MTA segments its output.

It exists because it caught one. On 2026-08-15 the rig reproduced discussion
#18's hang against a build whose 1493-test suite was green: Postfix pipelines
`QUIT` (or the next `MAIL FROM`) behind the terminating dot in the same TCP
segment, and the server's end-of-data detection only ever examined the tail of
the received buffer, so a mid-buffer terminator was never recognised and the
session hung. The same session also measured two facts worth keeping: stock
Postfix (3.10) **normalizes bare LFs to CRLF** before retransmission — a
bare-LF body cannot reach this server *via* Postfix — and its QUIT pipelining
is routine, not exotic.

## Setup (once)

1. `wsl --install -d Ubuntu --no-launch`, then in `%UserProfile%\.wslconfig`:

       [wsl2]
       networkingMode=mirrored

   Mirrored networking shares loopback with the Windows host, which is how
   Postfix inside WSL reaches hMailServer at `127.0.0.1:25`. Then
   `wsl --shutdown` to apply.

2. Inside WSL (`wsl -d Ubuntu -u root`):

       apt-get update && apt-get install -y postfix python3
       ip addr add 127.0.0.2/8 dev lo

   The extra loopback address matters: Postfix refuses to deliver to an address
   it considers its own ("mail for 127.0.0.1 loops back to myself"), so the
   bench binds Postfix to `127.0.0.2` and leaves `127.0.0.1` for hMailServer.
   `ip addr add` does not survive a WSL restart; re-run it (setup.sh does).

3. Run `setup.sh` (this directory) inside WSL as root. It configures Postfix as
   a PMG-shaped relay: listener on `127.0.0.2:2525`, transport map routing the
   bench domain to `[127.0.0.1]:25`, bare-LF input accepted
   (`smtpd_forbid_bare_newline = no`, as an older PMG would), short backoffs.

4. On the hMailServer side, create the bench domain and account (the regression
   suite deletes them whenever it runs, so re-create before each bench run):

       Domain  pipelining.example.test  (active)
       Account test@pipelining.example.test

## Running

- `run-stage2.sh` — injects four wire-level cases into Postfix (CRLF-proper,
  bare-LF final line, bare-LF mid-body, pipelined MAIL/RCPT/DATA with
  SIZE/8BITMIME) and requires all four to be `status=sent` into hMailServer.
  Success looks like `sent=4` within the first poll interval; before the
  2026-08-15 fix these hung for Postfix's full 600 s data-done timeout.
- `capture_server.py` + `run-matrix.sh` — stage 1: same injection, but relayed
  into a byte-recording sink instead of hMailServer, writing each transmitted
  message (`/tmp/capture-N.bin`) with its tail hex. Use it when the question is
  "what does Postfix actually put on the wire", which is the question that
  cracked #18.

## Environment trap: the WSL clock

WSL2's clock on this host oscillates by seconds-to-minutes (AMD host; observed
on both `tsc` and `hyperv_clocksource_tsc_page` clocksources). Postfix's qmgr
schedules by queue-file mtime and skips files stamped "in the future", so
deliveries can sit in the active queue indefinitely. The runner scripts work
around it by backdating queue files and flushing in a loop; if the queue still
sits, `wsl --shutdown` resyncs the clock transiently. Do not trust file-time
ordering inside WSL.
