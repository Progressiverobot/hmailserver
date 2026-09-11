#!/usr/bin/env python3
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Orphan rows for the schema-upgrade gate.

The 6029 -> 6030 upgrade step adds seventeen foreign keys, and first deletes the
rows whose parent is gone. On a clean database it cannot fail, which is why no
gate ever caught the ordering defect in it: it swept children before parents,
so pruning an orphaned account re-orphaned that account's fetch accounts, app
passwords and password history, and the constraint that followed was refused.

This script writes the rows that expose that. It reads the CreateTables script
of a dialect so the INSERTs carry every NOT NULL column the tables really have -
a seed kept by hand drifts from the schema the moment a column is added - and
emits, for every child table the step constrains, a row whose parent does not
exist, plus the chains that only fail when the sweep runs in the wrong order:

   domain 999999 (absent)
     account 4242            <- fetch account 4343 <- uid row
                             <- app password
                             <- password history row
     distribution list 4444  <- recipient

Usage:  seed-orphans.py <CreateTables<dialect>.sql> pgsql|mysql   > seed.sql
"""
import re
import sys

ABSENT = 999999          # a parent id nothing will ever have
ACCOUNT = 4242           # an account under the absent domain
FETCH = 4343             # a fetch account of that account
LIST = 4444              # a distribution list under the absent domain

# child table -> (foreign-key column, id column or None, value of the FK column)
ROWS = [
    ("hm_accounts",                   "accountdomainid",                ("accountid", ACCOUNT),          ABSENT),
    ("hm_aliases",                    "aliasdomainid",                  None,                            ABSENT),
    ("hm_domain_aliases",             "dadomainid",                     None,                            ABSENT),
    ("hm_distributionlists",          "distributionlistdomainid",       ("distributionlistid", LIST),    ABSENT),
    ("hm_distributionlistsrecipients", "distributionlistrecipientlistid", None,                          LIST),
    ("hm_routeaddresses",             "routeaddressrouteid",            None,                            ABSENT),
    ("hm_fetchaccounts",              "faaccountid",                    ("faid", FETCH),                 ACCOUNT),
    ("hm_fetchaccounts_uids",         "uidfaid",                        None,                            FETCH),
    ("hm_apppasswords",               "apaccountid",                    None,                            ACCOUNT),
    ("hm_rule_criterias",             "criteriaruleid",                 None,                            ABSENT),
    ("hm_rule_actions",               "actionruleid",                   None,                            ABSENT),
    ("hm_group_members",              "membergroupid",                  None,                            ABSENT),
    ("hm_passwordhistory",            "phaccountid",                    None,                            ACCOUNT),
    ("hm_messagerecipients",          "recipientmessageid",             None,                            ABSENT),
    ("hm_message_metadata",           "metadata_messageid",             None,                            ABSENT),
    ("hm_imapexpunged",               "expungedfolderid",               None,                            ABSENT),
    ("hm_messageindexterms",          "mitmessageid",                   None,                            ABSENT),
]

# Values a UNIQUE column must not collide on.
UNIQUE_VALUES = {
    "accountaddress": "orphan@absent.example",
    "aliasname": "orphan-alias@absent.example",
    "distributionlistaddress": "orphan-list@absent.example",
}


def parse_tables(sql):
    """table name -> list of (column, type, nullable, has_default, is_identity)."""
    tables = {}
    for m in re.finditer(r"create\s+table\s+`?(\w+)`?\s*\((.*?)\)\s*(?:DEFAULT\s+CHARSET\s*=\s*\w+\s*)?;", sql, re.S | re.I):
        name = m.group(1).lower()
        cols = []
        for raw in re.split(r",\s*\n", m.group(2)):
            line = raw.strip()
            if not line:
                continue
            first = line.split(",")[0].strip()          # "uidid int auto_increment not null" of a combined line
            low = first.lower()
            if low.startswith(("primary key", "constraint", "unique", "key ", "index ", "foreign key")):
                continue
            parts = first.replace("`", "").split()
            if len(parts) < 2:
                continue
            col, typ = parts[0].lower(), parts[1].lower()
            nullable = " not null" not in low
            has_default = " default " in low
            identity = "auto_increment" in low or "serial" in typ or "identity" in low
            cols.append((col, typ, nullable, has_default, identity))
        tables[name] = cols
    return tables


def literal(col, typ, dialect):
    if col in UNIQUE_VALUES:
        return "'" + UNIQUE_VALUES[col] + "'"
    t = typ.lower()
    if "char" in t or "text" in t:
        return "'x'"
    if "timestamp" in t or "datetime" in t or t == "date":
        return "'2026-01-01 00:00:00'"
    if t.startswith(("bit", "bool")):
        return "0"
    return "0"


def main():
    if len(sys.argv) != 3 or sys.argv[2] not in ("pgsql", "mysql"):
        sys.stderr.write(__doc__)
        return 64
    dialect = sys.argv[2]
    with open(sys.argv[1], encoding="utf-8", errors="replace") as f:
        tables = parse_tables(f.read())

    out = ["-- Orphan rows for the schema-upgrade gate; generated by build/seed-orphans.py from " + sys.argv[1].replace("\\", "/").split("/")[-1]]
    missing = [t for t, _, _, _ in ROWS if t not in tables]
    if missing:
        sys.stderr.write("tables not found in the create script: %s\n" % ", ".join(missing))
        return 1

    for table, fk_col, id_col, fk_value in ROWS:
        names, values = [], []
        for col, typ, nullable, has_default, identity in tables[table]:
            if id_col and col == id_col[0]:
                names.append(col)
                values.append(str(id_col[1]))
                continue
            if col == fk_col:
                names.append(col)
                values.append(str(fk_value))
                continue
            if identity or nullable or has_default:
                continue
            names.append(col)
            values.append(literal(col, typ, dialect))
        out.append("insert into %s (%s) values (%s);" % (table, ", ".join(names), ", ".join(values)))

    sys.stdout.write("\n".join(out) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
