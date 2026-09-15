#!/usr/bin/env python3
# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""A new per-account table cannot be added without deciding what the backup does with it.

WHY THIS EXISTS

Between 6.3.0 and 6.3.3 this server grew seven tables that hold one account's
data - its address book, the webmail's settings, its scheduled messages, its
files, its S/MIME keys, its calendar and its calendar objects - and not one of
them reached the backup. That was not a missing feature. Every one of them
carries a foreign key to hm_accounts with ON DELETE CASCADE, and a restore calls
Domains::DeleteAll() before it puts anything back, so restoring an archive
DELETED all of it and the archive had never held it. Nothing failed, because
nothing was looking: BackupExecuter writes what the business objects know how to
write, and a table with no business object is invisible to it.

So the rule is written down here instead of remembered. A table that holds one
account's data must be named in
hmailserver/source/Server/Common/Application/AccountStores.cpp - either in
stores_, which puts it in the archive, or in elsewhere_, with the reason it is
not there. A table in neither fails this check by name, with what to do.

WHAT IT CHECKS

  schema      Every table that reaches hm_accounts through ON DELETE CASCADE
              foreign keys, and every table with a column named like an account
              id, is declared in exactly one of the two lists.

  columns     Every column of a table in stores_ is declared, with the role it
              plays, and no column is declared that the schema does not have.
              This is the second half of the same defect: a column added to a
              store that is already in the backup would otherwise be dropped
              just as quietly as a whole table was.

  roles       Each store names exactly one identity column and one account
              column; a child store names exactly one parent column and a
              top-level store names none; a child store's parent is itself a
              store.

  dialects    The three CREATE TABLE scripts agree on the columns of every
              store. The archive carries column names as attribute names, so a
              store whose columns differ between backends is an archive that
              restores onto one and not another.

  tested      Every store is named in the regression fixture that backs it up,
              deletes it and restores it. A store carried by code no test
              exercises is a store nobody has seen come back.

WHAT IT DOES NOT CHECK

Whether a business object named in elsewhere_ really writes every column of its
table. That would need the persister and the XMLStore read together, and the
mapping between a column and a property is by hand. What the reason field buys
is a person having decided; this check makes sure somebody did.

SQL Server Compact, which has no CREATE TABLE script of its own - its schema is
built by walking the upgrade chain. build/check-db-scripts.ps1 builds a Compact
database from that chain and runs the schema probes against it, which is where a
CE step that forgot a column is caught.

Exits 1 listing what is wrong and what to do about it.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS = os.path.join(ROOT, "hmailserver", "source", "DBScripts")
STORES = os.path.join(ROOT, "hmailserver", "source", "Server", "Common", "Application", "AccountStores.cpp")
FIXTURE = os.path.join(ROOT, "hmailserver", "test", "RegressionTests", "Infrastructure", "BackupAccountStores.cs")

CREATE_SCRIPTS = ["CreateTablesMySQL.sql", "CreateTablesMSSQL.sql", "CreateTablesPGSQL.sql"]

ACCOUNTS = "hm_accounts"

# A column that names an account. The cascade graph is the strong signal; this is
# the weak one, and it is here because a table added without its foreign key
# declared is still a per-account table and is still deleted with the account.
ACCOUNT_COLUMN = re.compile(r"^[a-z_]*accountid$")

# Words that begin a table constraint rather than a column, in any of the three
# dialects.
NOT_A_COLUMN = ("primary", "unique", "key", "index", "constraint", "foreign", "check", "fulltext")


def read(path):
   with open(path, "r", encoding="utf-8", errors="replace") as handle:
      return handle.read()


def split_top_level(body):
   """The comma-separated parts of a CREATE TABLE body, ignoring commas in ()."""
   parts = []
   depth = 0
   current = []

   for character in body:
      if character == "(":
         depth += 1
      elif character == ")":
         depth -= 1

      if character == "," and depth == 0:
         parts.append("".join(current))
         current = []
      else:
         current.append(character)

   parts.append("".join(current))
   return parts


def parse_tables(text):
   """{table: [column, ...]} for every CREATE TABLE in one script."""
   tables = {}

   for match in re.finditer(r"create\s+table\s+`?(\w+)`?", text, re.IGNORECASE):
      name = match.group(1).lower()

      opening = text.find("(", match.end())
      if opening < 0:
         continue

      depth = 0
      closing = -1
      for index in range(opening, len(text)):
         if text[index] == "(":
            depth += 1
         elif text[index] == ")":
            depth -= 1
            if depth == 0:
               closing = index
               break

      if closing < 0:
         continue

      columns = []
      for part in split_top_level(text[opening + 1:closing]):
         part = part.strip().strip("`")
         if not part:
            continue

         # The leading identifier, however the dialect spells it: MySQL writes
         # `unique(`contactid`)` with no space, so splitting on whitespace alone
         # takes a whole constraint for a column name.
         leading = re.match(r"[`\"\[]?([A-Za-z_]\w*)", part)
         if not leading:
            continue

         first = leading.group(1).lower()
         if first in NOT_A_COLUMN:
            continue

         columns.append(first)

      tables[name] = columns

   return tables


def parse_cascades(text):
   """[(child, parent), ...] for every ON DELETE CASCADE foreign key."""
   pattern = re.compile(
      r"alter\s+table\s+`?(\w+)`?\s+add\s+constraint\s+\w+\s+foreign\s+key\s*\([^)]*\)\s*"
      r"references\s+`?(\w+)`?\s*\([^)]*\)\s*on\s+delete\s+cascade",
      re.IGNORECASE)

   return [(match.group(1).lower(), match.group(2).lower()) for match in pattern.finditer(text)]


def array_block(text, declaration):
   start = text.find(declaration)
   if start < 0:
      return None

   opening = text.find("{", start)
   depth = 0
   for index in range(opening, len(text)):
      if text[index] == "{":
         depth += 1
      elif text[index] == "}":
         depth -= 1
         if depth == 0:
            return text[opening + 1:index]

   return None


def main():
   missing = [path for path in [STORES, FIXTURE] if not os.path.exists(path)]
   if missing:
      for path in missing:
         print("  cannot read %s" % os.path.relpath(path, ROOT))
      print("FAIL: the per-account store declarations or their fixture are not where this check expects them")
      return 1

   # ---- the schema ------------------------------------------------------
   per_script = {}
   cascades = set()

   for name in CREATE_SCRIPTS:
      path = os.path.join(SCRIPTS, name)
      if not os.path.exists(path):
         print("  cannot read %s" % os.path.relpath(path, ROOT))
         print("FAIL: a CREATE TABLE script is missing")
         return 1

      text = read(path)
      per_script[name] = parse_tables(text)
      cascades.update(parse_cascades(text))

   schema = {}
   for name in CREATE_SCRIPTS:
      for table, columns in per_script[name].items():
         schema.setdefault(table, set()).update(columns)

   # Everything that goes when an account goes: the cascade graph, walked.
   owned = set()
   changed = True
   while changed:
      changed = False
      for child, parent in cascades:
         if (parent == ACCOUNTS or parent in owned) and child not in owned:
            owned.add(child)
            changed = True

   named_like = set()
   for table, columns in schema.items():
      if table == ACCOUNTS:
         continue
      if any(ACCOUNT_COLUMN.match(column) for column in columns):
         named_like.add(table)

   per_account = owned | named_like

   # ---- the declarations ------------------------------------------------
   text = read(STORES)

   stores_block = array_block(text, "AccountStores::stores_[]")
   columns_block = array_block(text, "AccountStores::columns_[]")
   elsewhere_block = array_block(text, "AccountStores::elsewhere_[]")

   if stores_block is None or columns_block is None or elsewhere_block is None:
      print("  AccountStores.cpp no longer declares stores_, columns_ and elsewhere_ in the form this check reads")
      print("FAIL: the declarations could not be read")
      return 1

   stores = []
   for match in re.finditer(r"\{\s*_T\(\"(\w+)\"\)\s*,\s*(?:_T\(\"(\w+)\"\)|0)\s*\}", stores_block):
      stores.append((match.group(1), match.group(2)))

   declared_columns = {}
   roles = {}
   for match in re.finditer(r"\{\s*_T\(\"(\w+)\"\)\s*,\s*_T\(\"(\w+)\"\)\s*,\s*AccountStores::Role(\w+)\s*\}", columns_block):
      table, column, role = match.group(1), match.group(2), match.group(3)
      declared_columns.setdefault(table, []).append(column)
      roles.setdefault(table, {}).setdefault(role, []).append(column)

   elsewhere = {}
   for match in re.finditer(r"\{\s*_T\(\"(\w+)\"\)\s*,\s*((?:\s*_T\(\"[^\"]*\"\))+)\s*\}", elsewhere_block):
      elsewhere[match.group(1)] = "".join(re.findall(r"_T\(\"([^\"]*)\"\)", match.group(2)))

   store_names = [table for table, _ in stores]

   problems = []
   checks = 0

   # ---- schema: nothing unaccounted for ---------------------------------
   for table in sorted(per_account):
      checks += 1
      if table in store_names or table in elsewhere:
         continue

      how = "cascades from %s" % ACCOUNTS if table in owned else "has an account-id column"
      problems.append(
         "%s %s and is in neither list. Decide what the backup does with it: put it in "
         "stores_ in Server/Common/Application/AccountStores.cpp with every one of its "
         "columns in columns_ and a test in RegressionTests/Infrastructure/"
         "BackupAccountStores.cs, or put it in elsewhere_ with the reason it is not "
         "carried. Restoring a backup deletes every account, so a per-account table "
         "that is in neither is data a restore destroys." % (table, how))

   for table in sorted(set(store_names) & set(elsewhere)):
      checks += 1
      problems.append("%s is in both stores_ and elsewhere_; it can only be in one" % table)

   # ---- the stores themselves -------------------------------------------
   for table, parent in stores:
      checks += 1
      if table not in schema:
         problems.append("stores_ names %s, which no CREATE TABLE script has" % table)
         continue

      if parent and parent not in store_names:
         problems.append("%s names %s as its parent store, which is not itself in stores_" % (table, parent))

      declared = declared_columns.get(table, [])

      checks += 1
      for column in sorted(schema[table] - set(declared)):
         problems.append(
            "%s.%s is in the schema and not in columns_, so the backup would not carry it. "
            "Add it with its role (RoleText, RoleNumber, RoleTimestamp, RoleMessage or "
            "RoleFolder), and assert it in BackupAccountStores.cs." % (table, column))

      for column in sorted(set(declared) - schema[table]):
         problems.append("columns_ declares %s.%s, which the schema does not have" % (table, column))

      if len(declared) != len(set(declared)):
         problems.append("columns_ declares a column of %s twice" % table)

      table_roles = roles.get(table, {})

      checks += 1
      for role, expected in (("Identity", 1), ("Account", 1), ("Parent", 1 if parent else 0)):
         found = len(table_roles.get(role, []))
         if found != expected:
            problems.append("%s declares %d Role%s column(s); it must declare %d" % (table, found, role, expected))

   # ---- the dialects agree ----------------------------------------------
   for table, _ in stores:
      checks += 1
      shapes = {}
      for name in CREATE_SCRIPTS:
         if table in per_script[name]:
            shapes[name] = tuple(sorted(per_script[name][table]))

      if len(shapes) != len(CREATE_SCRIPTS):
         absent = [name for name in CREATE_SCRIPTS if name not in shapes]
         problems.append("%s is not created by %s, so a restore onto that backend would fail"
                         % (table, ", ".join(absent)))
         continue

      if len(set(shapes.values())) != 1:
         problems.append("%s has different columns in different CREATE TABLE scripts; the archive "
                         "carries column names, so it would restore onto one backend and not another" % table)

   # ---- every store is exercised ----------------------------------------
   fixture = read(FIXTURE)
   for table, _ in stores:
      checks += 1
      if table not in fixture:
         problems.append(
            "%s is carried by the backup and is named nowhere in "
            "RegressionTests/Infrastructure/BackupAccountStores.cs. Add a test that makes a row, "
            "takes a backup, deletes the domain, restores and finds the row again." % table)

   # ---- elsewhere_ is a decision, not a hole ----------------------------
   for table, reason in sorted(elsewhere.items()):
      checks += 1
      if table not in schema:
         problems.append("elsewhere_ names %s, which no CREATE TABLE script has" % table)
      if len(reason.strip()) < 20:
         problems.append("elsewhere_ gives no real reason for %s" % table)

   for problem in problems:
      print("  " + problem)

   if problems:
      print("FAIL: %d problem(s) in what the backup does with the per-account tables" % len(problems))
      return 1

   print("OK    %d checks: %d table(s) in stores_ (every column declared, the three CREATE TABLE scripts "
         "agreeing, each with a test) and %d in elsewhere_ account for all %d per-account table(s) the "
         "schema has" % (checks, len(store_names), len(elsewhere), len(per_account)))
   return 0


if __name__ == "__main__":
   sys.exit(main())
