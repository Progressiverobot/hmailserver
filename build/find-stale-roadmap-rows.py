"""
Finds roadmap rows that describe an absence while naming something present.

WHY THIS EXISTS. On 21 August 2026 the .mobileconfig row still read "No profile
generator anywhere in the tree" nine days after the generator shipped. Nobody
caught it by reading the roadmap, because a stale row reads exactly like a true
one - the prose is fluent and internally consistent, and only the tree disagrees.

So this asks a question a reader cannot easily ask: for every not-started row,
take the symbols it names in backticks and check whether they exist in the source
tree. A row claiming something is missing, while naming a symbol that is present,
is worth opening.

THIS IS NOT A GATE, AND SHOULD NOT BECOME ONE. When it was written it flagged
fifteen rows and thirteen were honest - a row can name `MessageIndexer` or
`ArchiveRetentionDays` while describing something around them that is genuinely
still missing, and that is normal rather than a defect. The output is a reading
list, not a failure list. Wire it into a gate and the thirteen false positives
will train everyone to ignore the two real ones.

Run it occasionally - after a wave, or before picking the next item to build.

    python build/find-stale-roadmap-rows.py
"""
import io, os, re, subprocess, sys

if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
    sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NOT_STARTED = "\u2b1c"

rows = []
for i, line in enumerate(io.open(os.path.join(ROOT, "Roadmap.md"), encoding="utf-8").read().split("\n")):
    if line.startswith("| " + NOT_STARTED):
        cells = line.split(" | ")
        if len(cells) >= 3:
            rows.append((i + 1, cells[1].strip(), " | ".join(cells[2:])))

print("%d not-started row(s) to check." % len(rows))
print()

flagged = 0

for line_no, title, body in rows:
    # Only identifiers long enough to be distinctive. Short ones ("Connect",
    # "Execute") match hundreds of unrelated files and say nothing.
    tokens = set(re.findall(r"`([A-Za-z_][A-Za-z0-9_:.]{7,40})`", body))
    hits = []

    for token in sorted(tokens)[:8]:
        probe = token.split("::")[-1].rstrip(".")
        if len(probe) < 8 or probe.startswith("hm_"):
            continue
        found = subprocess.run(
            ["grep", "-rl", "--include=*.cpp", "--include=*.h", "--include=*.cs", "--include=*.idl",
             probe, "hmailserver/source", "hmailserver/test"],
            capture_output=True, text=True, cwd=ROOT)
        if found.stdout.strip():
            hits.append((probe, len(found.stdout.strip().split("\n"))))

    if hits:
        flagged += 1
        print("Roadmap.md:%d  %s" % (line_no, title[:72]))
        for probe, count in hits:
            print("      present: %-34s in %d file(s)" % (probe, count))
        print()

print("%d row(s) flagged. Most will be honest - open each and read it against the code." % flagged)
