#!/usr/bin/env python3
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Every statement in a MySQL script ends with a blank line.

SQLScriptParser splits a script into commands on a blank line ("\\n\\n"; the
PostgreSQL dialect on ";\\n\\n") and sends each command to the database as one
query. Two statements written on consecutive lines are therefore one command,
and MySQL refuses a command holding two statements: "You have an error in your
SQL syntax ... near 'drop table if exists hm_smimekeys' at line 2". SQL Server
and PostgreSQL take a batch of statements, so the same text passes there, which
is how the MySQL create script was broken from the tenth webmail wave (12
September 2026, two CREATE INDEX lines) until the Linux build's database gate
said so - a fresh MySQL installation could not create its database, and no
Windows check had run that path. build/check-db-scripts.ps1 proves the SQL
Server Compact dialect by executing it; this is the same rule for the MySQL
dialect, as text, so it runs on every push without a MySQL server.

Usage: check-sql-separators.py [--fix]   (from the repository root or anywhere)
  --fix   insert the missing blank line after every statement that lacks one
"""
import io
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS = os.path.join(ROOT, "hmailserver", "source", "DBScripts")


def statement_ends(command):
    """Line indexes (within the command) of every ';' that ends a statement,
    outside quotes and outside a -- comment."""
    ends = []
    for index, line in enumerate(command.split("\n")):
        quote = None
        i = 0
        while i < len(line):
            c = line[i]
            if quote:
                if c == quote:
                    quote = None
            elif c in ("'", '"', "`"):
                quote = c
            elif c == "-" and line[i:i + 2] == "--":
                break
            elif c == ";":
                ends.append((index, line[i + 1:].strip()))
            i += 1
    return ends


def check(path, fix):
    raw = io.open(path, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    crlf = "\r\n" in text
    text = text.replace("\r\n", "\n")
    commands = text.split("\n\n")
    problems = []
    fixed = []
    line_no = 1
    for command in commands:
        lines = command.split("\n")
        ends = statement_ends(command)
        if len(ends) > 1 or (ends and ends[0][1]):
            first = line_no + ends[0][0]
            problems.append("%s:%d: %d statements in one command (a blank line must follow every statement): %s"
                            % (os.path.relpath(path, ROOT), first, max(len(ends), 2), lines[ends[0][0]].strip()[:60]))
            if fix:
                # A blank line after every statement-ending line but the command's last.
                end_lines = {index for index, _ in ends}
                out = []
                for index, line in enumerate(lines):
                    out.append(line)
                    if index in end_lines and index != len(lines) - 1 and lines[index + 1].strip():
                        out.append("")
                lines = out
        fixed.append("\n".join(lines))
        line_no += command.count("\n") + 2
    if fix and problems:
        new = "\n\n".join(fixed)
        if crlf:
            new = new.replace("\n", "\r\n")
        io.open(path, "wb").write((b"\xef\xbb\xbf" if bom else b"") + new.encode("utf-8"))
    return problems, len([c for c in commands if c.strip()])


def main():
    fix = "--fix" in sys.argv[1:]
    files = sorted(f for f in os.listdir(SCRIPTS) if f.lower().endswith("mysql.sql"))
    problems = []
    commands = 0
    for name in files:
        found, count = check(os.path.join(SCRIPTS, name), fix)
        problems += found
        commands += count
    for p in problems:
        print(p)
    if problems:
        print("%d command(s) hold more than one statement in %d MySQL script(s)%s"
              % (len(problems), len(files), " - fixed" if fix else "; run with --fix to insert the blank lines"))
        return 0 if fix else 1
    print("%d MySQL scripts, %d commands, one statement each" % (len(files), commands))
    return 0


if __name__ == "__main__":
    sys.exit(main())
