# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""An upgrade script adds a setting row only if the row is not already there.

A plain "insert into hm_settings ... values (...)" in an Upgrade*.sql script fails the
whole step on a database that already has the row, and there are two ways a database
has it. The schema-upgrade gate in linux-build.yml creates a database at this build's
schema - every setting row included - and winds its structure back to 6029 before
upgrading it, so the step that introduced a setting meets its own row; that is how
the 6042 to 6043 step failed on MySQL and PostgreSQL on 15 September 2026. And a
database a newer build has written to, restored under an older version stamp, is in
the same position. The pattern the 6025 to 6026 step already used survives both:

    insert into hm_settings (settingname, settingstring, settinginteger)
       select 'Name', '', 0 from hm_dbversion
       where not exists (select settingname from hm_settings where settingname = 'Name')

Usage: python build/check-upgrade-inserts.py
"""
import io
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
SCRIPTS = os.path.join(ROOT, 'hmailserver', 'source', 'DBScripts')

PLAIN = re.compile(r'insert\s+into\s+hm_settings\s*\([^)]*\)\s*values', re.I)
STEP = re.compile(r'^Upgrade(\d+)to(\d+)', re.I)

# The steps the gate replays, and every step written since. The scripts before them
# shipped years ago, have run on every database that will ever meet them, and are
# left as they are.
FIRST_CHECKED_STEP = 6030


def main():
    problems = []
    scanned = 0
    for name in sorted(os.listdir(SCRIPTS)):
        if not (name.startswith('Upgrade') and name.endswith('.sql')):
            continue
        step = STEP.match(name)
        if not step or int(step.group(2)) < FIRST_CHECKED_STEP:
            continue
        scanned += 1
        text = io.open(os.path.join(SCRIPTS, name), encoding='utf-8-sig', errors='replace').read()
        for number, line in enumerate(text.splitlines(), 1):
            if PLAIN.search(line):
                problems.append('%s:%d: a setting row inserted with VALUES fails the step on a database that already has it; '
                                'use insert ... select ... from hm_dbversion where not exists (...), as Upgrade6025to6026 does' % (name, number))

    if problems:
        print('\n'.join(problems))
        print('FAIL: %d setting insert(s) in %d upgrade script(s) are not idempotent' % (len(problems), scanned))
        return 1

    print('OK    %d upgrade script(s) from 6030 on: every setting row is inserted only if it is not already there' % scanned)
    return 0


if __name__ == '__main__':
    sys.exit(main())
