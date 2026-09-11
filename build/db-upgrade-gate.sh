#!/usr/bin/env bash
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# The schema-upgrade gate: prove that a database at schema 6029 holding orphaned
# rows upgrades to this build's schema through the real upgrade scripts, on a real
# backend. Nothing in CI executed an upgrade before 11 September 2026, which is how
# the 6029 -> 6030 step shipped sweeping children before parents (issue #93's
# report led to it). This runs on the Ubuntu VM and in the Linux workflow alike.
#
#   db-upgrade-gate.sh pgsql|mysql
#
# Environment (all have defaults for the CI services):
#   HM_BIN          the server binary (default: hmailserver on PATH)
#   HM_SCRIPTS      the DBScripts directory of the tree under test
#   DB_HOST DB_PORT DB_USER DB_PASSWORD DB_NAME
#
# Steps: create the database at this build's schema with --create-database, wind
# it back to 6029 (drop the seventeen constraints the 6030 step adds and the column
# the 6031 step adds, set the version row), seed the orphans, run
# --upgrade-database, and verify: exit 0, version = REQUIRED, seventeen foreign
# keys present. The rewind exists because the repository's history was rewritten
# and no create script older than the current one survives in it.
set -euo pipefail

backend="${1:-}"
case "$backend" in pgsql|mysql) ;; *) echo "usage: $0 pgsql|mysql" >&2; exit 64 ;; esac

HM_BIN="${HM_BIN:-hmailserver}"
HM_SCRIPTS="${HM_SCRIPTS:-$(cd "$(dirname "$0")/.." && pwd)/hmailserver/source/DBScripts}"
DB_HOST="${DB_HOST:-127.0.0.1}"
DB_USER="${DB_USER:-hmailserver}"
DB_PASSWORD="${DB_PASSWORD:-hmtest}"
DB_NAME="${DB_NAME:-hmupgrade}"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ "$backend" = pgsql ]; then
   DB_PORT="${DB_PORT:-5432}"
   ini_type=PostgreSQL
   create_script="$HM_SCRIPTS/CreateTablesPGSQL.sql"
   sql() { PGPASSWORD="$DB_PASSWORD" psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -q -t -A "$@"; }
   admin_sql() { PGPASSWORD="$DB_PASSWORD" psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d postgres -q -t -A "$@"; }
   query() { sql -c "$1" </dev/null; }
   run_file() { sql -f "$1" </dev/null >/dev/null; }
   drop_fk() { echo "alter table $1 drop constraint if exists $2;"; }
   count_fks="select count(*) from information_schema.table_constraints where constraint_type = 'FOREIGN KEY' and table_schema = 'public';"
else
   DB_PORT="${DB_PORT:-3306}"
   ini_type=MySQL
   create_script="$HM_SCRIPTS/CreateTablesMySQL.sql"
   sql() { mariadb --host="$DB_HOST" --port="$DB_PORT" --user="$DB_USER" --password="$DB_PASSWORD" --database="$DB_NAME" --batch --skip-column-names "$@"; }
   admin_sql() { mariadb --host="$DB_HOST" --port="$DB_PORT" --user="$DB_USER" --password="$DB_PASSWORD" --batch --skip-column-names "$@"; }
   query() { sql -e "$1" </dev/null; }
   run_file() { sql < "$1"; }
   drop_fk() { echo "alter table $1 drop foreign key if exists $2;"; }
   count_fks="select count(*) from information_schema.referential_constraints where constraint_schema = database();"
fi

# The schema this build needs: from Constants.h when the scripts are the tree's,
# else from the database the binary creates, which is that schema by definition.
constants="$HM_SCRIPTS/../Server/Common/Application/Constants.h"
if [ -f "$constants" ]; then
   required=$(grep -oE 'REQUIRED_DB_VERSION\s+[0-9]+' "$constants" | grep -oE '[0-9]+$')
else
   required=""
fi
echo "== gate: $backend, binary $("$HM_BIN" --version </dev/null 2>/dev/null | head -1), scripts in $HM_SCRIPTS"

# 1. A fresh database at this build's schema, made the way the installer makes it.
echo "-- dropping and creating $DB_NAME"
if [ "$backend" = pgsql ]; then
   admin_sql -c "drop database if exists $DB_NAME;" >/dev/null
else
   admin_sql -e "drop database if exists $DB_NAME;"
fi
cat > "$work/hMailServer.ini" <<INI
[Directories]
ProgramFolder=$work/program
DatabaseFolder=$work/db
DataFolder=$work/data
LogFolder=$work/logs
TempFolder=$work/temp
EventFolder=$work/events

[Database]
Type=$ini_type
Server=$DB_HOST
Port=$DB_PORT
Username=$DB_USER
Password=$DB_PASSWORD
Database=$DB_NAME
Passwordencryption=0

[Settings]
INI
mkdir -p "$work/program" "$work/db" "$work/data" "$work/logs" "$work/temp" "$work/events"
cp -r "$HM_SCRIPTS" "$work/program/DBScripts"
cp "$work/hMailServer.ini" "$work/program/hMailServer.ini"
"$HM_BIN" --config "$work/program/hMailServer.ini" --create-database </dev/null
version=$(query "select value from hm_dbversion;")
[ -n "$required" ] || required="$version"
[ "$version" = "$required" ] || { echo "created database is at $version, expected $required" >&2; exit 1; }
echo "-- created at schema $required"

# 2. Wind it back to 6029: what 6030 and 6031 added, removed.
echo "-- winding back to 6029"
{
   grep -ioE 'alter table \w+ add constraint \w+ foreign key' "$HM_SCRIPTS/Upgrade6029to6030$( [ "$backend" = pgsql ] && echo PGSQL || echo MySQL ).sql" \
      | awk '{print tolower($3), tolower($6)}' | while read -r table name; do drop_fk "$table" "$name"; done
   echo "alter table hm_fetchaccounts drop column famirrorfolders;"
   echo "update hm_dbversion set value = 6029;"
} > "$work/rewind.sql"
run_file "$work/rewind.sql"
fks=$(query "$count_fks")
[ "$fks" = 0 ] || { echo "rewind left $fks foreign key(s)" >&2; exit 1; }

# 3. The orphans.
echo "-- seeding orphaned rows"
python3 "$here/seed-orphans.py" "$create_script" "$backend" > "$work/seed.sql"
run_file "$work/seed.sql"
echo "   $(grep -c '^insert' "$work/seed.sql") rows in $(grep -c '^insert' "$work/seed.sql") tables, every one with a parent that does not exist"

# 4. The upgrade, exactly as the installer's post-install step runs it.
echo "-- upgrading"
set +e
"$HM_BIN" --config "$work/program/hMailServer.ini" --upgrade-database </dev/null
rc=$?
set -e
version=$(query "select value from hm_dbversion;")
fks=$(query "$count_fks")
echo "-- upgrade exit $rc; schema now $version; foreign keys $fks of 17"
if [ "$rc" -ne 0 ] || [ "$version" != "$required" ] || [ "$fks" != 17 ]; then
   echo "GATE FAILED: a 6029 database holding orphaned rows did not upgrade cleanly on $backend" >&2
   exit 1
fi
echo "GATE PASSED on $backend"
