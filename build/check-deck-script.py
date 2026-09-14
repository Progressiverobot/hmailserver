#!/usr/bin/env python3
# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""The Control Deck's script, executed rather than grepped.

The Control Deck is one file, hmailserver/installation/WebAdmin/index.html: the
markup, one style block and one inline script, served by the server at / and
installed by the packages. It is the only graphical administration tool a Linux
server has, and until this existed nothing ran its script: a view could draw
nothing from a perfectly good answer, a form could send a key the route refuses,
a refusal could be swallowed, and every check would stay green.

So: this reads the page, lifts the script out, checks the cheap truths about the
markup against the Content-Security-Policy the server sends for the page (read
from RestApiServer.cpp, not copied here), and hands the page and the script to
build/deck-script-test.js, which builds a small DOM from the markup, answers
fetch out of a recorded server whose state the writes change, runs the script in
it and asserts behaviour: a sign-in that keeps no password, each view drawn from
its answers, each form sending the JSON its route takes, each refusal shown in
the server's own words, each change read back after the round trip.

Run from the repository root:  python3 build/check-deck-script.py
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
PAGE = os.path.join(ROOT, "hmailserver", "installation", "WebAdmin", "index.html")
SERVER = os.path.join(ROOT, "hmailserver", "source", "Server", "Common", "Util", "RestApiServer.cpp")
RUNNER = os.path.join(ROOT, "build", "deck-script-test.js")

SCRIPT = re.compile(r"<script(?P<attrs>[^>]*)>(?P<body>.*?)</script>", re.S | re.I)


def read(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read()


def policy_for_page(source):
    """The Content-Security-Policy HandleWebAdminPage_ sends, as {directive: [sources]}."""
    at = source.find("RestApiServer::HandleWebAdminPage_()")
    if at < 0:
        raise SystemExit("RestApiServer.cpp no longer defines HandleWebAdminPage_; this check reads the page's policy from it")
    match = re.search(r'"Content-Security-Policy: ([^"]*?)\\r\\n"', source[at:])
    if not match:
        raise SystemExit("HandleWebAdminPage_ sends no Content-Security-Policy header this check can read")
    policy = {}
    for directive in match.group(1).split(";"):
        words = directive.split()
        if words:
            policy[words[0]] = words[1:]
    return policy


def check_markup(page, policy):
    problems = []
    scripts = list(SCRIPT.finditer(page))
    if len(scripts) != 1:
        problems.append("the page has %d script blocks; the Deck is one inline script" % len(scripts))
    for block in scripts:
        if re.search(r"\bsrc\s*=", block.group("attrs")):
            problems.append("the page loads a script from a file, which the policy's script-src (%s) does not allow"
                            % " ".join(policy.get("script-src", [])))
    if "'unsafe-inline'" not in policy.get("script-src", []):
        problems.append("the policy no longer allows the inline script the page is made of (script-src %s)"
                        % " ".join(policy.get("script-src", [])))
    if "'unsafe-inline'" not in policy.get("style-src", []):
        problems.append("the policy no longer allows the inline style the page is made of")

    markup = SCRIPT.sub("", page)
    if re.search(r"<(link|iframe|frame|object|embed|base)\b", markup, re.I):
        problems.append("the page carries a link, frame, object, embed or base element; the policy allows none of them")
    for remote in re.findall(r"""(?:src|href|action)\s*=\s*["'](?:https?:)?//[^"']*""", markup, re.I):
        problems.append("the page names a remote resource (%s)" % remote)
    for remote in re.findall(r"url\(\s*['\"]?(?:https?:)?//[^)]*\)", markup, re.I):
        problems.append("the style names a remote resource (%s)" % remote)
    if re.search(r"@import\b", markup, re.I):
        problems.append("the style imports another sheet, which style-src forbids")
    for handler in re.findall(r"<[^>]*\s(on\w+)\s*=", markup, re.I):
        problems.append("the markup carries an inline event handler (%s); listeners are attached in the script" % handler)
    if re.search(r"""<form\b[^>]*\baction\s*=""", markup, re.I):
        problems.append("a form names an action; the page posts through fetch to this origin only")

    allowed = set(policy.keys())
    for directive, sources in policy.items():
        for source in sources:
            if source.startswith("http") or source.startswith("//") or source == "*":
                problems.append("the policy allows a remote source (%s %s)" % (directive, source))
    for needed in ("connect-src", "frame-ancestors", "base-uri", "default-src"):
        if needed not in allowed:
            problems.append("the policy names no %s" % needed)
    if policy.get("connect-src") != ["'self'"]:
        problems.append("connect-src is not 'self' alone (%s); the page's API calls are same-origin" % " ".join(policy.get("connect-src", [])))
    if policy.get("frame-ancestors") != ["'none'"]:
        problems.append("frame-ancestors is not 'none'; the page must not be framed")
    if "'none'" not in policy.get("default-src", []):
        problems.append("default-src is not 'none'")
    return problems


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--keep", metavar="DIR", help="write the extracted page and script here and keep them")
    parser.add_argument("--extract-only", action="store_true", help="extract, check the markup, and do not run node")
    args = parser.parse_args()

    for path in (PAGE, SERVER, RUNNER):
        if not os.path.exists(path):
            raise SystemExit("not found: %s (run this from the repository root)" % path)

    page = read(PAGE)
    policy = policy_for_page(read(SERVER))
    problems = check_markup(page, policy)
    if problems:
        for problem in problems:
            sys.stderr.write("index.html: %s\n" % problem)
        return 1

    script = SCRIPT.search(page).group("body")

    where = args.keep or tempfile.mkdtemp(prefix="deck-script-")
    os.makedirs(where, exist_ok=True)
    page_copy = os.path.join(where, "index.html")
    code = os.path.join(where, "deck.js")
    with open(page_copy, "w", encoding="utf-8", newline="") as handle:
        handle.write(page)
    with open(code, "w", encoding="utf-8", newline="") as handle:
        handle.write(script)

    if args.extract_only:
        print("extracted %d bytes of page and %d bytes of script into %s" % (len(page), len(script), where))
        return 0

    node = shutil.which("node") or shutil.which("node.exe")
    if not node:
        sys.stderr.write("node is not on PATH; it runs the Deck's script. Install node, or pass --extract-only.\n")
        return 1

    try:
        run = subprocess.run([node, RUNNER, page_copy, code], cwd=ROOT)
    finally:
        if not args.keep:
            shutil.rmtree(where, ignore_errors=True)
    return run.returncode


if __name__ == "__main__":
    sys.exit(main())
