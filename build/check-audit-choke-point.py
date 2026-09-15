#!/usr/bin/env python3
# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""Every administrative change reaches the audit trail, and no write route skips it.

An audit trail with a hole in it is worse than none: it reads as a complete
record of who changed what, and the one change somebody went out of their way to
make is the one it does not have. The hole is never deliberate - it is a new COM
setter, or a new REST route, written by somebody who had no reason to know this
class existed. So it fails here, with the file and the line, rather than being
discovered by a regulator.

Five rules, in the shape of build/check-authz-choke-point.py:

  recorded    AuditTrail::RecordStatement and ::RecordSettingChange are called
              from ONE file each - the statement chokepoint in the DAL and the
              settings chokepoint in PropertySet. A third caller means one
              change recorded twice, or recorded in a place that does not know
              what the change was.

  installed   The actor - who is making the change - is installed at one place
              per interface: RestApiServer::ProcessRequest_ for REST and the
              Deck, COMAuthentication's two authorisation questions for COM and
              so for the Control Panel. AuditTrail::SetActor is named nowhere
              else, because an actor set anywhere else is a change attributed to
              whoever happened to be there before.

  com         Every COM method that writes either asks one of those
              authorisation questions - which installs the actor on the thread
              that is about to do the writing - or carries an AuditScope of its
              own. EXEMPT below is the list of write-shaped methods that change
              nothing persistent, each with the reason.

  rest        Every route kind that mutates is answered through the dispatch in
              ProcessRequest_, which is where the scope is installed. A handler
              reached any other way is a change made with no actor on the
              thread, and so a change nobody made.

  table       hm_audit is written by AuditTrail and by nothing else. The row a
              caller writes for itself is the row that says whatever it likes.

Run from the repository root: python3 build/check-audit-choke-point.py
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
SERVER = os.path.join(ROOT, "hmailserver", "source", "Server")
COM = os.path.join(SERVER, "COM")

# symbol -> the files (relative to hmailserver/source/Server, forward slashes)
# that may name it.
ALLOWED = {
   "RecordStatement(": {
      "Common/Util/AuditTrail.cpp",
      "Common/Util/AuditTrail.h",
      "Common/SQL/DatabaseConnectionManager.cpp",   # the one statement chokepoint
   },
   "RecordSettingChange(": {
      "Common/Util/AuditTrail.cpp",
      "Common/Util/AuditTrail.h",
      "Common/Application/PropertySet.cpp",         # the one settings chokepoint
   },
   "AuditTrail::SetActor(": {
      "Common/Util/AuditTrail.cpp",                 # AuditScope itself
      "COM/COMAuthentication.cpp",                  # the COM identity chokepoint
   },
   "AuditTrail::ClearActor(": {
      "Common/Util/AuditTrail.cpp",
   },
}

# The call sites the rules above protect must exist, or they guard nothing.
REQUIRED = [
   ("Common/SQL/DatabaseConnectionManager.cpp",
    "AuditTrail::Instance()->RecordStatement(command, bResult);",
    "the statement chokepoint no longer records anything"),
   ("Common/Application/PropertySet.cpp",
    "AuditTrail::Instance()->RecordSettingChange(sPropertyName,",
    "the settings chokepoint no longer records anything"),
   ("Common/Util/RestApiServer.cpp",
    "AuditScope auditScope(AuditActorFor_(caller));",
    "REST no longer says who is making a change"),
   ("COM/COMAuthentication.cpp",
    "AuditTrail::SetActor(GetAuditActor());",
    "COM no longer says who is making a change"),
   ("Common/Util/AuditTrail.h",
    "bool Verify(__int64 &firstBroken, __int64 &rowsChecked, String &reason);",
    "the chain can no longer be walked, which is the whole point of chaining it"),
]

# A COM method, by its signature.
COM_METHOD = re.compile(r"^STDMETHODIMP\s+(\w+)::(\w+)\s*\(", re.M)

# What a method body looks like when it PERSISTS something - a row through the
# object or collection layer, or a value through Configuration and so through
# hm_settings. A property setter on a business object is not here on purpose: it
# sets a field in memory and changes nothing until Save, which is.
HARD_MARKERS = ("SaveObject(", "DeleteObject(", "DeleteItem", "DeleteAll", "DeleteByDBID",
                "ini_file_settings_->Set", "antiVirusConfiguration_.", "AntiSpamConfiguration().Set")

# What installs the actor on the calling thread, one way or the other.
INSTALLS = ("GetIsServerAdmin(", "GetIsDomainAdmin(", "AuditScope ")


# The settings interfaces reach the configuration through a cached pointer, and
# a getter names that pointer too - so what says "this writes" is a Set on it,
# directly or through one of the sub-configurations. Matched on the receiver so
# that GetBackupOption(BOSettings), which merely has the letters, is not a write.
CONFIG_SET = re.compile(r"(?:config_|cache_config_|ini_file_settings_)->(?:\w+\(\)->)?Set[A-Z]")


def persists(body):
   if any(marker in body for marker in HARD_MARKERS):
      return True

   return CONFIG_SET.search(body) is not None


# A method that reaches one of those markers and still changes nothing in the
# configuration, with the reason. A method belongs here only when the thing it
# writes is not configuration; "nobody will notice" is not a reason.
EXEMPT = {
   "InterfaceAttachments::Add": "attaches a file to a message being composed; a message is traffic, not configuration.",
   "InterfaceAttachments::Clear": "as Add.",
   "InterfaceAttachment::Delete": "removes an attachment from a message being composed.",
   "InterfaceAttachment::SaveAs": "writes an attachment to a file the caller names; nothing in the server changes.",
   "InterfaceMessage::Save": "a message, which is traffic and not configuration.",
   "InterfaceMessages::Clear": "the messages of a folder.",
   "InterfaceMessages::DeleteByDBID": "one message of a folder.",
   "InterfaceMessages::Delete": "one message of a folder.",
   "InterfaceAccount::DeleteMessages": "empties a mailbox; the account itself is unchanged.",
   "InterfaceDatabase::SetPassword": "the setup wizard's connection test, before there is a database to record into.",
   "InterfaceDatabase::SetUsername": "as SetPassword.",
   "InterfaceDatabase::SetServer": "as SetPassword.",
   "InterfaceDatabase::SetPort": "as SetPassword.",
   "InterfaceDatabase::SetDatabaseName": "as SetPassword.",
   "InterfaceDatabase::SetDatabaseType": "as SetPassword.",
   "InterfaceDatabase::SetDatabaseServerFailureType": "as SetPassword.",
}


def read(path):
   with open(path, "r", encoding="utf-8", errors="replace") as handle:
      return handle.read()


def relative(path):
   return os.path.relpath(path, SERVER).replace(os.sep, "/")


def sources():
   for base, _, names in os.walk(SERVER):
      for name in names:
         if name.endswith((".cpp", ".h")):
            yield os.path.join(base, name)


def check_allowed(problems):
   for path in sources():
      rel = relative(path)
      lines = read(path).split("\n")

      for number, line in enumerate(lines, 1):
         for symbol, allowed in ALLOWED.items():
            if symbol in line and rel not in allowed:
               problems.append("%s:%d: %s outside the audit chokepoints - see AuditTrail.h"
                               % (rel, number, symbol[:-1]))


def check_required(problems):
   for rel, needle, why in REQUIRED:
      path = os.path.join(SERVER, rel.replace("/", os.sep))
      text = read(path) if os.path.exists(path) else ""

      if needle not in text:
         problems.append("%s: %s is gone - %s" % (rel, needle.split("(")[0].strip(), why))


def method_body(text, start):
   """The text of the method whose signature starts at start, by brace depth."""
   open_brace = text.find("{", start)
   if open_brace < 0:
      return ""

   depth = 0
   index = open_brace
   while index < len(text):
      if text[index] == "{":
         depth += 1
      elif text[index] == "}":
         depth -= 1
         if depth == 0:
            return text[open_brace:index + 1]
      index += 1

   return text[open_brace:]


def check_com(problems):
   for name in sorted(os.listdir(COM)):
      if not name.endswith(".cpp"):
         continue

      path = os.path.join(COM, name)
      text = read(path)

      for match in COM_METHOD.finditer(text):
         method = "%s::%s" % (match.group(1), match.group(2))
         if method in EXEMPT:
            continue

         body = method_body(text, match.end())
         if not persists(body):
            continue

         if any(marker in body for marker in INSTALLS):
            continue

         line = text.count(os.linesep[-1], 0, match.start()) + 1
         problems.append("COM/%s:%d: %s persists something without installing the audit actor - ask "
                         "GetIsServerAdmin/GetIsDomainAdmin, or open an AuditScope(GetAuditActor()), "
                         "or name it in EXEMPT with the reason" % (name, line, method))


def check_rest(problems):
   text = read(os.path.join(SERVER, "Common", "Util", "RestApiServer.cpp"))

   # The scope has to be installed BEFORE the dispatch, or the handlers run
   # without it.
   install = text.find("AuditScope auditScope(AuditActorFor_(caller));")
   dispatch = text.find("switch (route.kind)")

   if install < 0 or dispatch < 0 or install > dispatch:
      problems.append("Common/Util/RestApiServer.cpp: the audit scope is not installed before the route dispatch")

   # And exactly once: a second one would mean a second dispatch somewhere that
   # the first does not cover.
   if text.count("AuditScope auditScope(") != 1:
      problems.append("Common/Util/RestApiServer.cpp: the audit scope is installed %d times; there is one dispatch, so there is one install"
                      % text.count("AuditScope auditScope("))


def check_table(problems):
   for path in sources():
      rel = relative(path)
      if rel == "Common/Util/AuditTrail.cpp":
         continue

      lower = read(path).lower()
      for marker in ("insert into hm_audit", "update hm_audit", "delete from hm_audit", '"hm_audit"'):
         if marker in lower:
            problems.append("%s: writes hm_audit directly; the chain is AuditTrail's to keep" % rel)
            break


def main():
   problems = []

   check_allowed(problems)
   check_required(problems)
   check_com(problems)
   check_rest(problems)
   check_table(problems)

   if problems:
      print("Audit chokepoint: %d problem(s)" % len(problems))
      for problem in problems:
         print("  " + problem)
      return 1

   print("Audit chokepoint: every administrative change is recorded once, by whoever made it")
   return 0


if __name__ == "__main__":
   sys.exit(main())
