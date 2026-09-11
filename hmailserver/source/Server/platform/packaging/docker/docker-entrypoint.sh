#!/usr/bin/env bash
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# https://www.progressiverobot.com
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# The container's entrypoint. The environment becomes the configuration, each
# part only when it is set, each idempotent, and then the server runs:
#
#   HM_DB_TYPE (PostgreSQL|MySQL), HM_DB_HOST, HM_DB_PORT, HM_DB_NAME,
#   HM_DB_USER, HM_DB_PASSWORD   -> the [Database] section of hMailServer.ini,
#                                   rewritten from these on every start so the
#                                   environment is the truth, not a stale file
#   HM_ADMIN_PASSWORD            -> [Security] AdministratorPassword, through the
#                                   server's own --set-admin-password, so the file
#                                   holds a PBKDF2 hash and never the password.
#                                   Without it the REST API refuses to start, and
#                                   on Linux the REST API is the administration.
#   HM_REST_PORT, HM_REST_BIND_ADDRESS, HM_REST_CERTIFICATE_FILE,
#   HM_REST_PRIVATE_KEY_FILE     -> [Settings] RestApiPort, RestApiBindAddress,
#                                   RestApiCertificateFile, RestApiPrivateKeyFile.
#                                   The packaged file has the port at 0 (off) and
#                                   the bind at 127.0.0.1, which inside a container
#                                   is the container's own loopback; to reach the
#                                   API from outside, bind 0.0.0.0 with a
#                                   certificate and key (the server requires TLS
#                                   off loopback), or run with host networking.
#   HM_DB_PASSWORD_FILE, HM_ADMIN_PASSWORD_FILE
#                                -> the same two secrets read from a file (a Docker
#                                   or Compose secret) instead of the environment
#   HM_CREATE_DATABASE (default 1) -> hmailserver --create-database, which is a
#                                   no-op when the database already exists
#   HM_UPGRADE_DATABASE (default 1) -> hmailserver --upgrade-database, which is a
#                                   no-op when the schema is current
#
# then exec the command - hmailserver --foreground unless told otherwise - so
# signals reach the server and it is PID 1's child, not a grandchild.
set -eu

INI="${HM_INI:-/etc/hmailserver/hMailServer.ini}"

# VAR_FILE, when set, is where VAR's value is read from; setting both is a
# mistake, not a precedence question.
file_env() {
   local var="$1" file_var="${1}_FILE"
   if [ -n "${!file_var:-}" ]; then
      if [ -n "${!var:-}" ]; then
         echo "$var and $file_var are both set; set one of them" >&2
         exit 64
      fi
      if [ ! -r "${!file_var}" ]; then
         echo "$file_var names ${!file_var}, which cannot be read" >&2
         exit 66
      fi
      export "$var"="$(cat "${!file_var}")"
   fi
}

needs_writable_ini() {
   if [ ! -w "$INI" ]; then
      echo "$INI is not writable by $(id -un); $1 cannot be written from the environment" >&2
      exit 1
   fi
}

# set_key SECTION KEY VALUE: the key's line in that section is replaced (a
# commented-out ";KEY=" counts, so the packaged file's ;RestApiCertificateFile=
# becomes a live setting); a missing key is added at the end of the section; a
# missing section is added at the end of the file. Written back through the
# existing file, so its owner and mode stay.
set_key() {
   local tmp
   tmp="$(mktemp)"
   awk -v s="[$1]" -v k="$2" -v v="$3" '
      BEGIN { insec = 0; done = 0 }
      /^\[/ {
         if (insec && !done) { print k "=" v; done = 1 }
         insec = ($0 == s)
      }
      insec && !done && $0 ~ ("^;?" k "=") { print k "=" v; done = 1; next }
      { print }
      END {
         if (!done) {
            if (!insec) print s
            print k "=" v
         }
      }' "$INI" > "$tmp"
   cat "$tmp" > "$INI"
   rm -f "$tmp"
}

write_database_section() {
   # Everything except the [Database] section is kept as it is; that section is
   # replaced. awk drops the old section (from its header to the next header)
   # and the new one is appended.
   local tmp
   tmp="$(mktemp)"
   awk 'BEGIN{skip=0} /^\[/{skip=($0=="[Database]")} !skip{print}' "$INI" > "$tmp"
   {
      cat "$tmp"
      printf '\n[Database]\nType=%s\nServer=%s\nPort=%s\nDatabase=%s\nUsername=%s\nPassword=%s\nPasswordencryption=0\n' \
         "$HM_DB_TYPE" "${HM_DB_HOST:-db}" "${HM_DB_PORT:-$default_port}" "${HM_DB_NAME:-hmailserver}" \
         "${HM_DB_USER:-hmailserver}" "${HM_DB_PASSWORD:-}"
   } > "$INI"
   rm -f "$tmp"
}

file_env HM_DB_PASSWORD
file_env HM_ADMIN_PASSWORD

if [ -n "${HM_DB_TYPE:-}" ]; then
   case "$HM_DB_TYPE" in
      PostgreSQL|postgresql|postgres|pgsql) HM_DB_TYPE=PostgreSQL; default_port=5432 ;;
      MySQL|mysql|MariaDB|mariadb)          HM_DB_TYPE=MySQL;      default_port=3306 ;;
      MSSQL|mssql)                          HM_DB_TYPE=MSSQL;      default_port=1433 ;;
      *) echo "HM_DB_TYPE must be PostgreSQL, MySQL or MSSQL, not '$HM_DB_TYPE'" >&2; exit 64 ;;
   esac
   needs_writable_ini "the [Database] section"
   write_database_section
   echo "[Database] written from the environment: $HM_DB_TYPE on ${HM_DB_HOST:-db}:${HM_DB_PORT:-$default_port}, database ${HM_DB_NAME:-hmailserver}"
fi

rest_written=""
if [ -n "${HM_REST_PORT:-}" ]; then
   needs_writable_ini "RestApiPort"
   set_key Settings RestApiPort "$HM_REST_PORT"
   rest_written="${rest_written} port ${HM_REST_PORT}"
fi
if [ -n "${HM_REST_BIND_ADDRESS:-}" ]; then
   needs_writable_ini "RestApiBindAddress"
   set_key Settings RestApiBindAddress "$HM_REST_BIND_ADDRESS"
   rest_written="${rest_written} bind ${HM_REST_BIND_ADDRESS}"
fi
if [ -n "${HM_REST_CERTIFICATE_FILE:-}" ]; then
   needs_writable_ini "RestApiCertificateFile"
   set_key Settings RestApiCertificateFile "$HM_REST_CERTIFICATE_FILE"
   rest_written="${rest_written} certificate ${HM_REST_CERTIFICATE_FILE}"
fi
if [ -n "${HM_REST_PRIVATE_KEY_FILE:-}" ]; then
   needs_writable_ini "RestApiPrivateKeyFile"
   set_key Settings RestApiPrivateKeyFile "$HM_REST_PRIVATE_KEY_FILE"
   rest_written="${rest_written} key ${HM_REST_PRIVATE_KEY_FILE}"
fi
[ -z "$rest_written" ] || echo "[Settings] REST API written from the environment:${rest_written}"

if [ -n "${HM_ADMIN_PASSWORD:-}" ]; then
   needs_writable_ini "AdministratorPassword"
   # The server's own verb: one line on standard input, a PBKDF2 hash in the
   # file, the file's owner and mode kept. The password itself is in no file.
   printf '%s\n' "$HM_ADMIN_PASSWORD" | hmailserver --config "$INI" --set-admin-password
   echo "[Security] AdministratorPassword written as a hash from HM_ADMIN_PASSWORD"
fi

if [ "${HM_CREATE_DATABASE:-1}" != 0 ]; then
   # Exit status 0 also when the database already exists; anything else is a
   # reason not to start.
   hmailserver --config "$INI" --create-database
fi

if [ "${HM_UPGRADE_DATABASE:-1}" != 0 ]; then
   hmailserver --config "$INI" --upgrade-database
fi

if [ "${1:-}" = "hmailserver" ] && ! printf '%s\n' "$@" | grep -q -- '--config'; then
   shift
   set -- hmailserver --config "$INI" "$@"
fi
exec "$@"
