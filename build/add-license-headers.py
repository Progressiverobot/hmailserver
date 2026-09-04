#!/usr/bin/env python3
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Puts a copyright line and an SPDX license identifier at the top of every
source file that lacks one, and reports what it did - or, with --check, what it
WOULD do and exits non-zero if that is anything.

Why a script and not an afternoon with an editor: 1,800 files, three comment
syntaxes, and four traps that each silently corrupt a file if handled by hand -

  * 85 files start with a UTF-8 byte-order mark. The header goes AFTER it, never
    before; a BOM in the middle of a file is a compile error in C++ and an
    invisible garbage character in C#.
  * Nearly every file is CRLF, a few are LF. Each keeps what it has.
  * Python scripts may start with a shebang, which must stay on line 1.
  * PowerShell comment-based help (<# ... #>) is only recognised as the
    script's help when nothing but comments and blank lines precede it, which
    plain # comment lines satisfy - so the header is plain # lines.

Files that ALREADY carry a copyright notice keep it exactly as it is - the
original author's notice is a licence condition, not decoration - and gain only
the SPDX line, appended to the end of that leading comment block. Files with no
notice get both lines.

Generated files are skipped on purpose: MIDL rewrites dlldata.c and
hMailServer_i.c on every build, so a header there lasts until the next compile.
Vendored code under libraries/ is not ours to stamp and is inventoried in
hmailserver/docs/third-party-binaries.json instead.

Usage:
  python build/add-license-headers.py            # apply
  python build/add-license-headers.py --check    # CI mode: report, change nothing
"""

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

COPYRIGHT = 'Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors'
SPDX = 'SPDX-License-Identifier: AGPL-3.0-or-later'

# Comment syntax per extension. XAML is XML, so its header is one comment
# element placed before the root element, which XML permits.
LINE_COMMENT = {
    '.cpp': '//', '.h': '//', '.hpp': '//', '.idl': '//', '.cs': '//',
    '.ps1': '#', '.psm1': '#', '.py': '#',
}
BLOCK_COMMENT = {'.xaml': ('<!--', '-->')}

SKIP_DIRS = {'.git', 'libraries', 'obj', 'bin', 'publish', 'packages', 'Output',
             'x64', 'Debug', 'Release', 'node_modules', 'DotNet', 'coverage'}

# MIDL output, regenerated on every build.
SKIP_FILES = {'dlldata.c', 'hMailServer_i.c', 'hMailServer_p.c'}

BOM = b'\xef\xbb\xbf'
HEAD_BYTES = 1200   # how far down a copyright notice is looked for


def candidates():
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        for name in sorted(filenames):
            ext = os.path.splitext(name)[1].lower()
            if ext in LINE_COMMENT or ext in BLOCK_COMMENT:
                if name in SKIP_FILES:
                    continue
                yield os.path.join(dirpath, name), ext


def leading_comment_block(lines, marker):
    """Number of consecutive lines from the top that are comments of `marker`."""
    n = 0
    for line in lines:
        if line.lstrip().startswith(marker):
            n += 1
        else:
            break
    return n


def process(path, ext, apply):
    with open(path, 'rb') as f:
        raw = f.read()
    bom = raw.startswith(BOM)
    body = raw[len(BOM):] if bom else raw
    # Lines are split on LF with any CR kept attached to its line, so a file
    # with MIXED endings (one test file had a single CRLF among 268 LF lines)
    # is still seen line by line. The first version split on whichever
    # terminator it found first, which turned that file into one 268-line
    # chunk and put the header after it. New lines take the ending the file's
    # own first line uses.
    nl = b'\r\n' if body.split(b'\n', 1)[0].endswith(b'\r') else b'\n'

    if b'SPDX-License-Identifier' in body[:HEAD_BYTES]:
        return None

    # A notice, not the word: "Copyright (c) 2010 ...", "Copyright 2026 ...",
    # "(c) 2026 ...". Prose that merely mentions copyright does not count.
    NOTICE = re.compile(rb'[Cc]opyright\s*(\(c\)|\xc2\xa9|\d{4})|\(c\)\s*\d{4}')
    has_copyright = NOTICE.search(body[:HEAD_BYTES]) is not None
    lines = body.split(nl)

    # An SPDX line that exists but sits beyond the window is one the first
    # version of this script put at the END of the leading comment block - and
    # twenty-five files open with a block of prose long enough to push it past
    # the window the check reads. Pull it out; it is re-placed below, directly
    # after the copyright line, where REUSE expects it and the check can see it.
    lines = [l for l in lines if b'SPDX-License-Identifier' not in l]

    if ext in BLOCK_COMMENT:
        open_, close = BLOCK_COMMENT[ext]
        header = [
            (open_ + ' ' + COPYRIGHT).encode() if not has_copyright else None,
            ((open_ + ' ' + SPDX + ' ' + close).encode() if has_copyright
             else ('     ' + SPDX + ' ' + close).encode()),
        ]
        header = [h for h in header if h is not None]
        new_lines = header + lines
        action = 'header' if not has_copyright else 'spdx'
    else:
        marker = LINE_COMMENT[ext]
        insert_at = 0

        # A shebang stays on line 1.
        if lines and lines[0].startswith(b'#!'):
            insert_at = 1

        if has_copyright:
            # Place the SPDX line directly after the LAST copyright line of the
            # leading comment block - not after the whole block, which in this
            # tree can be thirty lines of design notes - leaving the existing
            # notice byte-for-byte intact.
            block = leading_comment_block(lines[insert_at:], marker.encode())
            if block == 0:
                # A notice that is not a leading line comment (a block comment,
                # or a notice further down). Put a single SPDX line on top.
                at = insert_at
            else:
                at = insert_at + block
                for k in range(insert_at, insert_at + block):
                    if NOTICE.search(lines[k]):
                        at = k + 1
            new_lines = lines[:at] + [(marker + ' ' + SPDX).encode()] + lines[at:]
            action = 'spdx'
        else:
            header = [
                (marker + ' ' + COPYRIGHT).encode(),
                (marker + ' ' + SPDX).encode(),
            ]
            # Blank line between the header and whatever came first, unless
            # the file already opened with one.
            rest = lines[insert_at:]
            if rest and rest[0].strip() != b'':
                header.append(b'')
            new_lines = lines[:insert_at] + header + rest
            action = 'header'

    out = nl.join(new_lines)
    if bom:
        out = BOM + out
    if apply:
        with open(path, 'wb') as f:
            f.write(out)
    return action


def main():
    check = '--check' in sys.argv
    counts = {'header': 0, 'spdx': 0}
    touched = []
    for path, ext in candidates():
        action = process(path, ext, apply=not check)
        if action:
            counts[action] += 1
            touched.append((action, os.path.relpath(path, ROOT)))

    total = counts['header'] + counts['spdx']
    if check:
        if total:
            print('%d source file(s) lack an SPDX license identifier:' % total)
            for action, rel in touched[:40]:
                print('  %-7s %s' % (action, rel))
            if total > 40:
                print('  ... and %d more' % (total - 40))
            print()
            print('Run  python build/add-license-headers.py  to add them.')
            return 1
        print('Every source file carries an SPDX license identifier.')
        return 0

    print('Added a copyright line and SPDX identifier to %d file(s), and an SPDX '
          'identifier to %d file(s) that already had a notice.' % (counts['header'], counts['spdx']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
