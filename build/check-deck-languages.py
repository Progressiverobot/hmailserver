#!/usr/bin/env python3
# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""The Control Deck's catalogues against the page they translate.

English is the key. The page (hmailserver/installation/WebAdmin/index.html)
holds the keys in two forms: the texts of its static markup - text nodes,
placeholders, aria-labels, titles - which the script walks at run time, and
the literals the script hands to t(), tf() and K(). Each language lives in
hmailserver/installation/WebAdmin/languages/<code>.json as one object,
English to translation, installed beside the page and served by the server
at /deck-lang/<code>.json; nothing is generated from them.

Checked, for every catalogue:
  keys       every key the page has is in the catalogue, and nothing else is;
  empty      no translation is empty;
  tokens     a {0}-style placeholder, a number, and the server's own words -
             a protocol, a setting name, a pattern the administrator types
             verbatim - survive translation;
  markup     no translation carries a character HTML reads (< > & "), since
             the page puts a translation where the English stood, in text and
             in an attribute alike, without escaping it;
  form       the file is what json.dumps writes with a two-space indent,
             sorted keys, the characters as they are and CRLF line endings -
             the portal catalogues' form, so a diff is a change of text;
  languages  the catalogues on disk are exactly the languages the page's
             switch offers, English apart.

Usage: check-deck-languages.py [--keys]   (--keys prints the key set)
"""
import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PAGE = os.path.join(ROOT, "hmailserver", "installation", "WebAdmin", "index.html")
CATALOGUES = os.path.join(ROOT, "hmailserver", "installation", "WebAdmin", "languages")

# The names the page shows in every language: the logo's letters, the product
# and the page. They are text nodes of the markup and not keys.
NAMES = {"hM", "hMailServer", "Control Deck"}

# What must survive translation, when the English has it: the server's own
# words and the patterns an administrator types back verbatim.
SURVIVORS = ["hMailServer.ini", "hMailServer", "OpenAPI", "ManageSieve", "Sieve", "IMAP", "SMTP", "POP3", "DKIM", "DNSSEC",
             "DANE", "TLSA", "SURBL", "DNS", "STARTTLS", "SSL/TLS", "TLS", "MX", "IPv4", "IPv6", "MB", "KB",
             "YYYY-MM-DD", "HH:MM:SS", "127.0.0.2", "192.168.*", "2001:db8:*", "*.exe", "user+anything@domain", "user@domain",
             "ScheduledBackupTime", "ScheduledBackupIntervalMinutes", "ScheduledBackupKeepCount", "ScheduledBackupMaxAgeDays",
             "Active Directory", "Windows", "domain_members", "membership", "announcement", "Date"]

SCRIPT = re.compile(r"<script(?P<attrs>[^>]*)>(?P<body>.*?)</script>", re.S | re.I)
LITERAL = re.compile(r"""\b(?:t|tf|K)\((?:"((?:[^"\\]|\\.)*)"|'((?:[^'\\]|\\.)*)')""")
LANGUAGE_TABLE = re.compile(r"const LANGUAGES=\[(.*?)\];")
LANGUAGE_ENTRY = re.compile(r'\["([A-Za-z-]+)","[^"]*"\]')


def read(path):
    return io.open(path, "r", encoding="utf-8-sig").read()


def unescape(literal):
    return re.sub(r"\\(.)", lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), literal)


def page_parts():
    page = read(PAGE)
    scripts = SCRIPT.findall(page)
    if len(scripts) != 1:
        raise SystemExit("index.html has %d script blocks; the Deck is one inline script" % len(scripts))
    script = scripts[0][1]
    body = page[page.find("<body"):] if "<body" in page else page
    markup = SCRIPT.sub("", body)
    markup = re.sub(r"<style[^>]*>.*?</style>", "", markup, flags=re.S)
    return markup, script


def page_keys():
    markup, script = page_parts()
    keys = set()
    for m in re.finditer(r">([^<>]+)<", markup):
        s = m.group(1).strip()
        if re.search(r"[A-Za-z]{2,}", s) and not s.startswith("<!--") and s not in NAMES:
            keys.add(s)
    for attr in ("placeholder", "aria-label", "title", "alt"):
        for m in re.finditer(attr + r'="([^"]+)"', markup):
            s = m.group(1).strip()
            if re.search(r"[A-Za-z]{2,}", s) and s not in NAMES:
                keys.add(s)
    for m in LITERAL.finditer(script):
        keys.add(unescape(m.group(1) if m.group(1) is not None else m.group(2)))
    return keys


def page_languages():
    _, script = page_parts()
    table = LANGUAGE_TABLE.search(script)
    if not table:
        raise SystemExit("index.html no longer declares const LANGUAGES=[...]; this check reads the switch's languages from it")
    return [code for code in LANGUAGE_ENTRY.findall(table.group(1)) if code != "en"]


def tokens_of(s):
    found = set(re.findall(r"\{[0-9]+\}", s))
    found.update(re.findall(r"(?<![\w.*:-])-?\d+(?![\w.*:-])", s))
    for survivor in SURVIVORS:
        # A word, not the tail or the head of a longer one: DNS is not in DNSSEC.
        if re.search(r"(?<![A-Za-z])" + re.escape(survivor) + r"(?![A-Za-z])", s):
            found.add(survivor)
    return found


def canonical(data):
    return (json.dumps(data, indent=2, ensure_ascii=False, sort_keys=True) + "\n").replace("\n", "\r\n").encode("utf-8")


def main():
    keys = page_keys()
    if "--keys" in sys.argv:
        # The keys carry characters outside a console's code page (an arrow,
        # a dash, the quotes); the list is for a file.
        sys.stdout.reconfigure(encoding="utf-8")
        for k in sorted(keys):
            print(k)
        return 0
    problems = []
    files = sorted(f for f in os.listdir(CATALOGUES) if f.endswith(".json")) if os.path.isdir(CATALOGUES) else []
    if not files:
        problems.append("no catalogues under " + CATALOGUES)
    offered = page_languages()
    on_disk = [f[:-5] for f in files]
    for code in sorted(set(offered) - set(on_disk)):
        problems.append("%s: the page offers it and there is no catalogue" % code)
    for code in sorted(set(on_disk) - set(offered)):
        problems.append("%s: a catalogue the page's switch does not offer" % code)
    for f in files:
        code = f[:-5]
        raw = io.open(os.path.join(CATALOGUES, f), "rb").read()
        try:
            data = json.loads(raw.decode("utf-8"))
        except ValueError as why:
            problems.append("%s: not JSON (%s)" % (f, why))
            continue
        if not isinstance(data, dict):
            problems.append("%s: not an object" % f)
            continue
        if raw != canonical(data):
            problems.append("%s: not in the catalogues' form (two-space indent, sorted keys, characters as they are, CRLF, one final newline)" % f)
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
            if re.search(r'[<>&"]', v):
                problems.append("%s: a character HTML reads in: %s" % (code, k))
    print("Deck languages: %d keys on the page, %d catalogue(s)" % (len(keys), len(files)))
    if problems:
        for p in problems[:200]:
            print("  FAIL  " + p)
        if len(problems) > 200:
            print("  ... and %d more" % (len(problems) - 200))
        return 1
    print("  OK    every catalogue has every key and nothing else, no empty translation, every token kept, nothing HTML would read, the form is the catalogues' form, and the switch offers exactly the catalogues there are")
    return 0


if __name__ == "__main__":
    sys.exit(main())
