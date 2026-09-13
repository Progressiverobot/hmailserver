#!/usr/bin/env python3
# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""The self-service portal's script, executed rather than grepped.

The portal is served from four files in hmailserver/source/Server/Common/Util/:
Portal.html (the page, its stylesheet inline), Portal.js (the script), the
manifest and the service worker - carried into the binary by
build/generate-portal-page.py - and the headers both are served with, a C++
string literal in RestApiPortal.cpp. Until this existed, the only thing
standing behind the page was a regression test that asserted that certain
SUBSTRINGS were present in what the server served - which proves that a name
is spelled somewhere in a file, and nothing whatever about what the page does.
A script can carry a syntax error, a handler wired to an element that does not
exist, or a sign-in that keeps the password, and pass every one of those
assertions.

So: this checks the cheap truths first - the page loads only its own script,
carries no inline script or handler and names no remote resource; the headers
say script-src 'self' and frame-ancestors 'none' and allow no remote source -
and then hands the page and the script to build/portal-script-test.js, which
builds a small DOM from the markup, stubs fetch with recorded API answers, runs
the script in it and asserts behaviour - a sign-in that stores nothing secret,
a listing that renders rows, a keyboard cursor that moves, a cid: image that
resolves to the attachment download route, the change probe causing a refresh,
the address bar naming what is shown.

The headers literal keeps a shape this can parse:
    const char *PortalHeaders =
       "line\\r\\n"
       "line\\r\\n";
- a name, '=', then string literals one per line until the semicolon.

Run from the repository root:  python3 build/check-portal-script.py
Needs node on PATH (every GitHub-hosted runner has it). --no-node runs the
static checks only.
"""

import argparse
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UTIL = os.path.join(ROOT, "hmailserver", "source", "Server", "Common", "Util")
PAGE = os.path.join(UTIL, "Portal.html")
SCRIPT = os.path.join(UTIL, "Portal.js")
HEADERS_SOURCE = os.path.join(UTIL, "RestApiPortal.cpp")
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


def read(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read().replace("\r\n", "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-node", action="store_true", help="the static checks only; do not run node")
    args = parser.parse_args()

    for path in (PAGE, SCRIPT, HEADERS_SOURCE):
        if not os.path.exists(path):
            raise SystemExit("not found: %s (run this from the repository root)" % path)

    html = read(PAGE)
    found = extract(HEADERS_SOURCE)
    if "PortalHeaders" not in found:
        raise SystemExit("%s defines no PortalHeaders" % os.path.basename(HEADERS_SOURCE))
    headers = found["PortalHeaders"]

    # Cheap truths about the page and the headers, before anything is executed.
    # These are the only substring assertions here, and each one is a property
    # of the page rather than a name in it.
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
            sys.stderr.write("portal: %s\n" % problem)
        return 1

    if args.no_node:
        print("portal: the page and the headers pass the static checks")
        return 0

    node = shutil.which("node") or shutil.which("node.exe")
    if not node:
        sys.stderr.write("node is not on PATH; it runs the portal's script. Install node, or pass --no-node.\n")
        return 1

    return subprocess.run([node, RUNNER, PAGE, SCRIPT], cwd=ROOT).returncode


if __name__ == "__main__":
    sys.exit(main())
