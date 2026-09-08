#!/usr/bin/env python3
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Every setting the server reads from hMailServer.INI can be set from the Control Panel.

An INI setting with no editor is not a small omission. It is invisible to a
Control Panel connected to another machine, it does not appear in the settings
search, and the only way to change it is to open a text file on the server -
which is the thing the Control Panel exists to avoid. The gap is also silent:
nothing fails, the setting simply cannot be found, and the person looking for it
concludes the feature does not exist.

Two checks, both driven off the server's own source rather than a list kept by
hand:

  coverage  Every key `IniFileSettings` reads from [Settings] is named somewhere
            in the Control Panel's source. EXEMPT below is the list of keys that
            deliberately have no editor, each with the reason; anything else
            missing fails.

  mirrored  Every INI key the server reads is read THROUGH IniFileSettings, so
            that the hm_inisettings mirror sees it. A key read with its own
            profile call is absent from the database overlay, from backup and
            restore, and from the COM settings API - so it cannot be
            administered remotely at all. OUTSIDE below records the sections
            that are known to do this, so that a NEW one fails here.

Exits 1 listing what is missing.
"""
import os
import re
import sys

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


def main():
   if not os.path.exists(INI_FILE):
      print("IniFileSettings.cpp not found at %s" % INI_FILE)
      return 1

   source = read(INI_FILE)
   by_section = {}
   for section, key in READ.findall(source):
      by_section.setdefault(section, set()).add(key)

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
         if not name.endswith((".cpp", ".h")) or name.startswith("IniFileSettings"):
            continue
         path = os.path.join(base, name)
         for section in PROFILE.findall(read(path)):
            if section not in OUTSIDE:
               direct.setdefault(section, set()).add(os.path.relpath(path, ROOT))

   for key in missing:
      print("  no Control Panel editor: [Settings] %s" % key)
   for section, files in sorted(direct.items()):
      print("  [%s] is read outside IniFileSettings, so the database mirror never "
            "sees it: %s" % (section, ", ".join(sorted(files))))

   problems = len(missing) + len(direct)
   if problems:
      print("FAIL: %d setting(s) an administrator cannot reach from the Control Panel" % problems)
      return 1

   print("OK    all %d [Settings] keys have a Control Panel editor (%d exempt), and "
         "every section is read through IniFileSettings" % (len(settings), len(EXEMPT)))
   return 0


if __name__ == "__main__":
   sys.exit(main())
