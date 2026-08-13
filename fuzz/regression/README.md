Fixed-finding reproducers
=========================

One directory per target (`mime_message_fuzzer`, `mime_header_fuzzer`,
`mime_decode_fuzzer`). Each file in them is a libFuzzer input that **used to
crash, hang or trip AddressSanitizer** and no longer does.

`run-fuzz.ps1` passes these directories to libFuzzer as read-only seeds, so
every run — including the seconds-long `-Replay` mode — re-executes all of them.
That is what stops a fixed parser bug from coming back unnoticed.

Rules
-----

1. **Never edit a file in here, and never re-save one.** These are exact byte
   sequences; a single changed byte usually means the input no longer reaches the
   code it was kept for, and nothing will tell you. `fuzz\.gitattributes` marks
   this whole subtree binary so git will not normalise line endings on checkout
   the way it does for everything else in this repository.
2. **Name the file after the finding**, not after the SHA1 libFuzzer gave it:
   `mimeencodedword-decode-oob-read` beats `crash-0f3a...`. A year later the name
   is the only documentation anyone reads.
3. **Add the fix's real test too.** A corpus entry proves the process no longer
   dies. It does not prove the parser now does the right thing, and it only runs
   when somebody runs the fuzzer. If the bug was reachable over SMTP, IMAP or
   POP3 — most of them are — the finding also deserves a test in
   `hmailserver\test\RegressionTests`, which runs on every release.

Adding one
----------

```powershell
# after the fix, confirm the artifact no longer reproduces
.\fuzz\run-fuzz.ps1 -Target mime_header_fuzzer -Reproduce .\fuzz\artifacts\mime_header_fuzzer\crash-<sha1>

# then keep it forever
New-Item -ItemType Directory -Force .\fuzz\regression\mime_header_fuzzer | Out-Null
Copy-Item .\fuzz\artifacts\mime_header_fuzzer\crash-<sha1> `
          .\fuzz\regression\mime_header_fuzzer\mimeencodedword-decode-oob-read
```

Use `Copy-Item`, not an editor and not `Set-Content`: both of the latter will
rewrite the bytes.
