#!/usr/bin/env python3
# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""Every setting the server reads from hMailServer.INI can be set from the Control Panel.

An INI setting with no editor is not a small omission. It is invisible to a
Control Panel connected to another machine, it does not appear in the settings
search, and the only way to change it is to open a text file on the server -
which is the thing the Control Panel exists to avoid. The gap is also silent:
nothing fails, the setting simply cannot be found, and the person looking for it
concludes the feature does not exist.

Three checks, all driven off the server's own source rather than a list kept by
hand:

  coverage  Every key `IniFileSettings` reads from [Settings] is named somewhere
            in the Control Panel's source. EXEMPT below is the list of keys that
            deliberately have no editor, each with the reason; anything else
            missing fails.

  frozen    The rule since 15 September 2026, in .github/CONTRIBUTING.md: a
            SETTING BELONGS IN THE DATABASE, and no key is added to [Settings].
            build/ini-settings-baseline.txt is every key the server read from the
            file on the day the database became the settings store; a key that
            appears in [Settings] and is not in it fails here. The bootstrap
            sections - [Directories], [Database], [Security] and [GUILanguages],
            which are read before the database is open - can still gain a key, by
            adding it to the baseline in the same commit and saying why in the
            commit message. --write-baseline regenerates the file for the one case
            where a key is legitimately REMOVED.

  mirrored  Every INI key the server reads is read THROUGH IniFileSettings, so
            that the settings store sees it. A key read with its own profile call
            is absent from the stored values, from backup and restore, and from
            the COM settings API - so it cannot be administered remotely at all.
            OUTSIDE below records the sections that are known to do this, so that
            a NEW one fails here. The seam itself is two files - IniFileSettings
            and IniSettingStore, the only class that touches the file as a file -
            and neither can be outside itself, so both are skipped.

Exits 1 listing what is missing.
"""
import os
import re
import sys

BASELINE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ini-settings-baseline.txt")

# The sections that are read before the database is open, and are therefore the
# only place a genuinely pre-database key can go. [Settings] is not one of them:
# it is the section the store moved out of.
BOOTSTRAP = ("Directories", "Database", "Security", "GUILanguages")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SERVER = os.path.join(ROOT, "hmailserver", "source", "Server")
INI_FILE = os.path.join(SERVER, "Common", "Application", "IniFileSettings.cpp")
CONTROL_PANEL = os.path.join(ROOT, "hmailserver", "source", "Tools", "ControlPanel")

# A [Settings] key with no editor, and why. A key belongs here only when an
# editor would be wrong - not when one has not been written yet.
EXEMPT = {
   "SimulateDatabaseFailureFor":
      "A fault-injection switch for the regression suite. An editor would offer an "
      "administrator a way to break their own server on purpose.",
   "SimulateSpoolWriteFailure":
      "A fault-injection switch for the regression suite, as above.",
}

# Sections read outside IniFileSettings today. Each is a gap - it is invisible to
# the database mirror - and each is recorded here so that the number cannot grow
# without somebody deciding to let it.
OUTSIDE = {
   "LDAP": "LdapSettings.cpp reads the directory settings directly.",
   "SendingLimits": "RateLimiter reads the per-account limits directly.",
   "SendingLimitsOverrides": "A free-form section: one line per address, so it has no fixed key set.",
}

READ = re.compile(r'ReadIniSetting(?:String|Integer)_\(\s*"([A-Za-z]+)"\s*,\s*"([A-Za-z0-9]+)"')
# A profile call outside IniFileSettings: the Windows API, or our own wrapper.
PROFILE = re.compile(r'GetPrivateProfile(?:String|Int)W?\s*\(\s*_?T?\(?"([A-Za-z]+)"')


def read(path):
   with open(path, "r", encoding="utf-8", errors="replace") as handle:
      return handle.read()


def control_panel_text():
   parts = []
   for base, _, names in os.walk(CONTROL_PANEL):
      if os.sep + "obj" in base or os.sep + "bin" in base:
         continue
      for name in names:
         if name.endswith((".cs", ".xaml")):
            parts.append(read(os.path.join(base, name)))
   return "\n".join(parts)


def read_baseline():
   """The committed baseline, as a set of (section, key)."""
   pairs = set()
   if not os.path.exists(BASELINE):
      return None
   for line in read(BASELINE).splitlines():
      line = line.strip()
      if not line or line.startswith("#"):
         continue
      parts = line.split()
      if len(parts) != 2:
         continue
      pairs.add((parts[0], parts[1]))
   return pairs


def write_baseline(pairs):
   """Rewrites the baseline, keeping the explanation at the top of the file."""
   header = []
   if os.path.exists(BASELINE):
      for line in read(BASELINE).splitlines():
         if line.startswith("#") or not line.strip():
            header.append(line)
         else:
            break
      while header and not header[-1].strip():
         header.pop()

   lines = list(header)
   section = None
   for pair in sorted(pairs):
      if pair[0] != section:
         lines.append("")
         section = pair[0]
      lines.append("%s %s" % pair)

   with open(BASELINE, "w", encoding="utf-8", newline="\r\n") as handle:
      handle.write("\n".join(lines) + "\n")

   print("Wrote %s: %d key(s)." % (os.path.basename(BASELINE), len(pairs)))


def main():
   if not os.path.exists(INI_FILE):
      print("IniFileSettings.cpp not found at %s" % INI_FILE)
      return 1

   source = read(INI_FILE)
   by_section = {}
   for section, key in READ.findall(source):
      by_section.setdefault(section, set()).add(key)

   present = set()
   for section, keys in by_section.items():
      for key in keys:
         present.add((section, key))

   if "--write-baseline" in sys.argv:
      write_baseline(present)
      return 0

   baseline = read_baseline()
   frozen = []

   if baseline is None:
      frozen.append("  the baseline %s is missing, so nothing is holding the rule that "
                    "a setting goes in the database" % os.path.basename(BASELINE))
      baseline = present

   for section, key in sorted(present - baseline):
      if section == "Settings":
         frozen.append(
            "  [Settings] %s is new. A SETTING BELONGS IN THE DATABASE: put it in "
            "hm_settings, reached through Property and PropertySet, and give it a Control "
            "Panel editor - .github/CONTRIBUTING.md says so and this check is what holds "
            "it. The [Settings] section is a cache of hm_inisettings now, not a store, so a "
            "key added here is a setting nobody can administer remotely, that no backup "
            "carries and that two nodes cannot share." % key)
      elif section in BOOTSTRAP:
         frozen.append(
            "  [%s] %s is new. That section is read before the database is open, so a key "
            "there is allowed - but it is meant to be rare: add it to %s in this same "
            "commit and say in the commit message why it cannot wait for the database."
            % (section, key, os.path.basename(BASELINE)))
      else:
         frozen.append(
            "  [%s] %s is new, and [%s] is not one of the sections that are read before the "
            "database is open. A setting belongs in hm_settings; a bootstrap key belongs in "
            "[Directories], [Database] or [Security]." % (section, key, section))

   for section, key in sorted(baseline - present):
      frozen.append(
         "  [%s] %s is in %s but the server no longer reads it. Removing a setting from the "
         "file is the direction this is going: delete the line in the same commit, or run "
         "python3 build/check-ini-coverage.py --write-baseline."
         % (section, key, os.path.basename(BASELINE)))

   settings = sorted(by_section.get("Settings", ()))
   if not settings:
      print("No [Settings] keys found - has IniFileSettings.cpp changed shape?")
      return 1

   panel = control_panel_text()
   # A key counts as reachable when the Control Panel names it anywhere: as the
   # key of an INI-backed editor, or in the help text beside a COM-backed one.
   # Several settings are stored in the INI but edited through a COM property
   # under a different name - the recipient tarpit is SmtpTarpitCount in the file
   # and AntiSpam.TarpitCount on the object - and the card that edits those says
   # so in its own words. Requiring the quoted key would call those missing.
   missing = [key for key in settings
              if key not in EXEMPT and not re.search(r"\b%s\b" % re.escape(key), panel)]

   # Anything reading the INI directly, other than IniFileSettings itself.
   direct = {}
   for base, _, names in os.walk(SERVER):
      if os.sep + "x64" in base:
         continue
      for name in names:
         # The seam itself is two files: IniFileSettings, which every reader goes
         # through, and IniSettingStore behind it, which is the only thing that
         # reads or writes the file as a file. Neither can be "outside" itself.
         if not name.endswith((".cpp", ".h")) or name.startswith(("IniFileSettings", "IniSettingStore")):
            continue
         path = os.path.join(base, name)
         for section in PROFILE.findall(read(path)):
            if section not in OUTSIDE:
               direct.setdefault(section, set()).add(os.path.relpath(path, ROOT))

   for key in missing:
      print("  no Control Panel editor: [Settings] %s" % key)
   for line in frozen:
      print(line)
   for section, files in sorted(direct.items()):
      print("  [%s] is read outside IniFileSettings, so the settings store never "
            "sees it: %s" % (section, ", ".join(sorted(files))))

   problems = len(missing) + len(frozen) + len(direct)
   if problems:
      print("FAIL: %d problem(s) with the settings the server reads from hMailServer.INI" % problems)
      return 1

   print("OK    all %d [Settings] keys have a Control Panel editor (%d exempt), the %d key(s) "
         "in the baseline are exactly the keys the server reads, and every section is read "
         "through IniFileSettings" % (len(settings), len(EXEMPT), len(baseline)))
   return 0


if __name__ == "__main__":
   sys.exit(main())
