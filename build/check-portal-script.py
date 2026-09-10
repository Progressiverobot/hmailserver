#!/usr/bin/env python3
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later
"""The self-service portal's script, executed rather than grepped.

The portal is three C++ string literals in
hmailserver/source/Server/Common/Util/RestApiPortal.cpp: PortalHtml, PortalScript
and PortalHeaders. Until this existed, the only thing standing behind them was a
regression test that asserted that certain SUBSTRINGS were present in what the
server served - which proves that a name is spelled somewhere in a file, and
nothing whatever about what the page does. A script can carry a syntax error, a
handler wired to an element that does not exist, or a sign-in that keeps the
password, and pass every one of those assertions.

So: this recovers the two literals, unescapes them back into an .html and a .js,
and hands them to build/portal-script-test.js, which builds a small DOM from the
markup, stubs fetch with recorded API answers, runs the script in it and asserts
behaviour - a sign-in that stores nothing secret, a listing that renders rows, a
keyboard cursor that moves, a cid: image that resolves to the attachment download
route, the change probe causing a refresh, the address bar naming what is shown.

The literals therefore keep a shape this can parse, which is the shape they have:

    const char *Name =
       "line\n"
       "line\n";

- a name, '=', then string literals one per line until the semicolon. Nothing
else in the file may look like that, and a raw string literal (R"(...)") is not
understood on purpose: the concatenated form is what keeps the page readable in
a diff.

Run from the repository root:  python3 build/check-portal-script.py
Needs node on PATH (every GitHub-hosted runner has it). --keep names a directory
to leave the extracted files in, for looking at by hand.
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "hmailserver", "source", "Server", "Common", "Util", "RestApiPortal.cpp")
RUNNER = os.path.join(ROOT, "build", "portal-script-test.js")

# "const char *Name =", then only string-literal lines until the one ending in ';'
DEFINITION = re.compile(r'^\s*const\s+char\s*\*\s*(\w+)\s*=\s*$')
LITERAL = re.compile(r'^\s*"((?:[^"\\]|\\.)*)"\s*(;?)\s*$')

ESCAPES = {"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\", "'": "'", "0": "\0"}


def unescape(text, where):
    """Turn one C++ string literal's body back into the bytes it stands for."""
    out = []
    i = 0
    while i < len(text):
        c = text[i]
        if c != "\\":
            out.append(c)
            i += 1
            continue
        if i + 1 >= len(text):
            raise SystemExit("%s: a literal ends in a backslash" % where)
        nxt = text[i + 1]
        if nxt not in ESCAPES:
            raise SystemExit("%s: \\%s is an escape this parser does not know" % (where, nxt))
        out.append(ESCAPES[nxt])
        i += 2
    return "".join(out)


def extract(path):
    """{name: text} for every 'const char *Name =' in the file."""
    with open(path, encoding="utf-8") as handle:
        lines = handle.read().split("\n")
    found = {}
    i = 0
    while i < len(lines):
        head = DEFINITION.match(lines[i])
        if not head:
            i += 1
            continue
        name = head.group(1)
        i += 1
        parts = []
        closed = False
        while i < len(lines):
            piece = LITERAL.match(lines[i])
            if not piece:
                raise SystemExit(
                    "%s:%d: %s is not the shape this parser needs - every line of it must be "
                    "one string literal, and the last must end with ';'. See the comment at the "
                    "top of the file." % (os.path.basename(path), i + 1, name))
            parts.append(unescape(piece.group(1), "%s line %d" % (name, i + 1)))
            i += 1
            if piece.group(2) == ";":
                closed = True
                break
        if not closed:
            raise SystemExit("%s: %s is never closed with a ';'" % (os.path.basename(path), name))
        found[name] = "".join(parts)
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--keep", metavar="DIR", help="write the extracted page and script here and keep them")
    parser.add_argument("--extract-only", action="store_true", help="extract, check the shape, and do not run node")
    args = parser.parse_args()

    if not os.path.exists(SOURCE):
        raise SystemExit("not found: %s (run this from the repository root)" % SOURCE)

    found = extract(SOURCE)
    for wanted in ("PortalHtml", "PortalScript", "PortalHeaders"):
        if wanted not in found:
            raise SystemExit("%s defines no %s" % (os.path.basename(SOURCE), wanted))

    html, script, headers = found["PortalHtml"], found["PortalScript"], found["PortalHeaders"]

    # Cheap truths about the three, before anything is executed. These are the
    # only substring assertions here, and each one is a property of the page
    # rather than a name in it.
    problems = []
    if "<script" in html and 'src="/portal.js"' not in html:
        problems.append("the page loads a script that is not /portal.js")
    if re.search(r"<script[^>]*>\s*[^<\s]", html):
        problems.append("the page carries inline script, which its Content-Security-Policy forbids")
    if re.search(r"\son\w+\s*=", html):
        problems.append("the page carries an inline event handler, which its Content-Security-Policy forbids")
    for remote in re.findall(r'(?:src|href)\s*=\s*"(https?:)?//[^"]*"', html):
        problems.append("the page names a remote resource (%s), which its policy forbids" % remote)
    if "script-src 'self'" not in headers:
        problems.append("the headers do not name script-src 'self'")
    if "frame-ancestors 'none'" not in headers:
        problems.append("the headers do not name frame-ancestors 'none'")
    for directive in re.findall(r"(?:default|img|connect|frame|style|script)-src ([^;\r\n]*)", headers):
        for source in directive.split():
            if source.startswith("http") or source.startswith("//") or source == "*":
                problems.append("the headers allow a remote source (%s)" % source)
    if problems:
        for problem in problems:
            sys.stderr.write("RestApiPortal.cpp: %s\n" % problem)
        return 1

    where = args.keep or tempfile.mkdtemp(prefix="portal-script-")
    os.makedirs(where, exist_ok=True)
    page = os.path.join(where, "portal.html")
    code = os.path.join(where, "portal.js")
    with open(page, "w", encoding="utf-8", newline="") as handle:
        handle.write(html)
    with open(code, "w", encoding="utf-8", newline="") as handle:
        handle.write(script)
    with open(os.path.join(where, "portal.headers"), "w", encoding="utf-8", newline="") as handle:
        handle.write(headers)

    if args.extract_only:
        print("extracted %d bytes of page and %d bytes of script into %s" % (len(html), len(script), where))
        return 0

    node = shutil.which("node") or shutil.which("node.exe")
    if not node:
        sys.stderr.write("node is not on PATH; it runs the portal's script. Install node, or pass --extract-only.\n")
        return 1

    try:
        run = subprocess.run([node, RUNNER, page, code], cwd=ROOT)
    finally:
        if not args.keep:
            shutil.rmtree(where, ignore_errors=True)
    return run.returncode


if __name__ == "__main__":
    sys.exit(main())
