# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""A regression test changes a [Settings] value through the settings store, never by
editing hMailServer.ini.

From schema 6042 the database is the settings store and the [Settings] section of
hMailServer.ini is its copy. A value edited into the file is put back at the next start
and reported as HM5804; a line removed from the file is written back from the stored
row. So a test that edits the file sets nothing, leaves an error that fails the next
test's SetUp, and - when it "removes" a bad value in its teardown - leaves that value
stored for every test after it. On 15 September 2026 one such teardown failed 320 of
the first 404 tests of a gate, and 107 test files still edited the file.

The suite's door is Shared/IniFileSetting.cs (Write, Delete) and Shared/ServerIniFile.cs
(SetSetting), both of which go through Settings.SetIniSetting and DeleteIniSetting. The
file is still the right place for [Directories], [Database], [Security], [GUILanguages],
[LDAP] and [SendingLimits*], which are read before or outside the store.

This check refuses, in any test source:
  - WritePrivateProfileString with the literal section "Settings";
  - WritePrivateProfileString with a section that is not a string literal, outside the
    helpers themselves, because the check cannot tell which section it is;
  - File.WriteAllLines, File.WriteAllText or File.AppendAllText in a file that names
    hMailServer.ini or ServerIniFile.Path().
A call that edits the file ON PURPOSE - a test about an edit to the file losing - is
allowed when one of the three lines above it carries "settings-store-exempt:" followed
by the reason.

Usage: python build/check-test-settings-writes.py
"""
import io
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
TESTS = os.path.join(ROOT, 'hmailserver', 'test', 'RegressionTests')

HELPERS = {
    os.path.normcase(os.path.join('Shared', 'IniFileSetting.cs')),
    os.path.normcase(os.path.join('Shared', 'IniFile.cs')),
}

EXEMPT = 'settings-store-exempt:'

PROFILE_CALL = re.compile(r'WritePrivateProfileString\s*\(\s*(?P<section>"[^"]*"|[^,]+),')
FILE_WRITE = re.compile(r'\bFile\.(WriteAllLines|WriteAllText|AppendAllText)\s*\(')
NAMES_THE_INI = re.compile(r'hMailServer\.ini|ServerIniFile\.Path\(\)', re.I)
CONSTANT = re.compile(r'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')


def exempt(lines, index):
    for back in range(0, 4):
        if index - back >= 0 and EXEMPT in lines[index - back]:
            reason = lines[index - back].split(EXEMPT, 1)[1].strip()
            return len(reason) > 0
    return False


def main():
    problems = []
    scanned = 0

    for directory, _, names in os.walk(TESTS):
        if os.sep + 'bin' in directory or os.sep + 'obj' in directory:
            continue
        for name in names:
            if not name.endswith('.cs'):
                continue
            path = os.path.join(directory, name)
            relative = os.path.relpath(path, TESTS)
            scanned += 1
            text = io.open(path, encoding='utf-8-sig', errors='replace').read()
            lines = text.splitlines()
            code_lines = [l for l in lines if not l.strip().startswith(('//', '*'))]
            # A comment that says where a setting lives does not make a file write
            # an ini write, so only code counts.
            names_ini = any(NAMES_THE_INI.search(l) for l in code_lines)
            is_helper = os.path.normcase(relative) in HELPERS
            constants = dict(CONSTANT.findall(text))

            for index, line in enumerate(lines):
                stripped = line.strip()
                if stripped.startswith('//') or stripped.startswith('///') or stripped.startswith('*'):
                    continue

                for match in PROFILE_CALL.finditer(line):
                    section = match.group('section').strip()
                    if 'DllImport' in line or 'static extern' in line:
                        continue
                    # WritePrivateProfileString(null, null, null, path) writes nothing:
                    # it is the documented way to flush the profile cache to disk.
                    if section == 'null':
                        continue
                    # A section named by a constant of the same file is read through it.
                    if section in constants:
                        section = '"%s"' % constants[section]
                    if section.startswith('"'):
                        if section.strip('"').lower() != 'settings':
                            continue
                        if is_helper and exempt(lines, index):
                            continue
                        if exempt(lines, index):
                            continue
                        problems.append((relative, index + 1, 'writes [Settings] into hMailServer.ini, which the settings store undoes at the next start: use IniFileSetting.Write or IniFileSetting.Delete'))
                    elif not is_helper and not exempt(lines, index):
                        problems.append((relative, index + 1, 'WritePrivateProfileString with a section this check cannot read (%s): write [Settings] through IniFileSetting, or name the section as a literal' % section))

                if names_ini and FILE_WRITE.search(line) and not is_helper and not exempt(lines, index):
                    problems.append((relative, index + 1, 'writes a file in a test that names hMailServer.ini: a [Settings] value goes through IniFileSetting; if this writes something else, or edits the file on purpose, say so with "%s <reason>" on the line above' % EXEMPT))

    if problems:
        for relative, line, message in problems:
            print('%s:%d: %s' % (os.path.join('hmailserver', 'test', 'RegressionTests', relative), line, message))
        print('FAIL: %d place(s) in %d test source(s) edit hMailServer.ini [Settings] instead of the settings store' % (len(problems), scanned))
        return 1

    print('OK    %d test source(s): no test edits [Settings] in hMailServer.ini outside the settings store' % scanned)
    return 0


if __name__ == '__main__':
    sys.exit(main())
