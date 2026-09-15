#!/usr/bin/env python3
# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""The webmail's catalogues against the page they translate.

English is the key. The page (hmailserver/source/Server/Common/Util/
Portal.html, and Portal.js) holds the keys in two forms: the texts of its markup - text
nodes, placeholders, aria-labels, titles, alts - which the script walks at
run time, and the literals the script hands to t(), tf() and K() - the last being how a
table of texts the script draws later, such as a tour's steps, marks them.
Each language
lives in hmailserver/source/Server/Common/Util/PortalLanguages/<code>.json as
one object, English to translation, and build/generate-portal-languages.py
embeds every catalogue in PortalLanguagesData.cpp.

Checked, for every catalogue:
  keys       every key the page has is in the catalogue, and nothing else is;
  empty      no translation is empty;
  tokens     a {0}-style placeholder, a {first_name}-style token, a keyboard
             key (Ctrl+K, Enter) and the search syntax a reader types
             verbatim (from:, is:unread, label:name ...) survive translation;
  generated  PortalLanguagesData.cpp is what the generator would write now.

Usage: check-portal-languages.py [--keys]   (--keys prints the key set)
"""
import io
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PAGE_HTML = os.path.join(ROOT, "hmailserver", "source", "Server", "Common", "Util", "Portal.html")
PAGE_JS = os.path.join(ROOT, "hmailserver", "source", "Server", "Common", "Util", "Portal.js")
CATALOGUES = os.path.join(ROOT, "hmailserver", "source", "Server", "Common", "Util", "PortalLanguages")
GENERATED = os.path.join(ROOT, "hmailserver", "source", "Server", "Common", "Util", "PortalLanguagesData.cpp")
GENERATOR = os.path.join(ROOT, "build", "generate-portal-languages.py")

# What must survive translation, when the English has it.
SURVIVORS = ["from:", "to:", "subject:", "has:attachment", "has:link", "before:", "after:", "in:folder", "is:unread", "is:read",
             "is:flagged", "is:unflagged", "is:answered", "label:name", "cc:", "bcc:", "filename:", "larger:", "smaller:",
             "older_than:", "newer_than:", "is:muted", "is:pinned", "category:", "in_reply_to:", "INBOX", "Ctrl+K", ".eml", "{first_name}",
             "{subject}", "{name}", "Sieve", "IMAP", "SMTP", "hMailServer"]


def page_keys():
    html = io.open(PAGE_HTML, "r", encoding="utf-8-sig").read()
    script = io.open(PAGE_JS, "r", encoding="utf-8-sig").read()
    # Style and script blocks are not text a reader sees.
    html = re.sub(r"<style[^>]*>.*?</style>", "", html, flags=re.S)
    html = re.sub(r"<script[^>]*>.*?</script>", "", html, flags=re.S)
    keys = set()
    for m in re.finditer(r">([^<>]+)<", html):
        s = m.group(1).strip()
        if re.search(r"[A-Za-z]{2,}", s) and not s.startswith("<!--"):
            keys.add(s)
    for attr in ("placeholder", "aria-label", "title", "alt"):
        for m in re.finditer(attr + r'="([^"]+)"', html):
            s = m.group(1).strip()
            if re.search(r"[A-Za-z]{2,}", s):
                keys.add(s)
    for m in re.finditer(r"\b(?:t|tf|K)\('((?:[^'\\]|\\.)*)'", script):
        keys.add(m.group(1).replace("\\'", "'").replace("\\\\", "\\"))
    return keys


def tokens_of(s):
    found = set(re.findall(r"\{[A-Za-z0-9_]+\}", s))
    for survivor in SURVIVORS:
        # An operator, not the tail of a word: "cc:" is not in "Bcc:".
        if re.search(r"(?<![A-Za-z])" + re.escape(survivor), s):
            found.add(survivor)
    return found


def main():
    keys = page_keys()
    if "--keys" in sys.argv:
        for k in sorted(keys):
            print(k)
        return 0
    problems = []
    files = sorted(f for f in os.listdir(CATALOGUES) if f.endswith(".json")) if os.path.isdir(CATALOGUES) else []
    if not files:
        problems.append("no catalogues under " + CATALOGUES)
    for f in files:
        code = f[:-5]
        try:
            data = json.load(io.open(os.path.join(CATALOGUES, f), "r", encoding="utf-8"))
        except ValueError as why:
            problems.append("%s: not JSON (%s)" % (f, why))
            continue
        if not isinstance(data, dict):
            problems.append("%s: not an object" % f)
            continue
        missing = keys - set(data)
        extra = set(data) - keys
        for k in sorted(missing):
            problems.append("%s: missing: %s" % (code, k))
        for k in sorted(extra):
            problems.append("%s: not on the page: %s" % (code, k))
        for k, v in data.items():
            if k not in keys:
                continue
            if not isinstance(v, str) or not v.strip():
                problems.append("%s: empty: %s" % (code, k))
                continue
            for token in tokens_of(k):
                if token not in v:
                    problems.append("%s: '%s' lost from: %s" % (code, token, k))
    if os.path.exists(GENERATOR):
        expected = subprocess.run([sys.executable, GENERATOR, "--print"], capture_output=True, text=True, encoding="utf-8")
        if expected.returncode != 0:
            problems.append("the generator failed: " + expected.stderr.strip())
        else:
            current = io.open(GENERATED, "r", encoding="utf-8-sig", newline="").read().replace("\r\n", "\n") if os.path.exists(GENERATED) else ""
            if current != expected.stdout.replace("\r\n", "\n"):
                problems.append("PortalLanguagesData.cpp is not what generate-portal-languages.py writes now; run it")
    print("Portal languages: %d keys on the page, %d catalogue(s)" % (len(keys), len(files)))
    if problems:
        for p in problems[:200]:
            print("  FAIL  " + p)
        if len(problems) > 200:
            print("  ... and %d more" % (len(problems) - 200))
        return 1
    print("  OK    every catalogue has every key and nothing else, no empty translation, every token kept, the generated unit is current")
    return 0


if __name__ == "__main__":
    sys.exit(main())
