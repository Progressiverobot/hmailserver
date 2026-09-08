#!/bin/bash
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# https://www.progressiverobot.com
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# One core source, compiled the way the Linux build compiles it, so a change can
# be checked in seconds rather than by waiting for the whole census.
#
# It copies the current Windows working tree into a workspace of its own first,
# so several people can use it at once without treading on each other, and so a
# half-finished edit on Windows is what gets compiled - which is the point.
#
# Usage:  build/linux-compile-one.sh <name> <source path under Server/> [more...]
#   e.g.  build/linux-compile-one.sh mine Common/BO/Account.cpp
#
# Run it through WSL from the repository:
#   wsl.exe -d Ubuntu -- bash build/linux-compile-one.sh mine Common/BO/Account.cpp

set -u

NAME="${1:?first argument is a workspace name, e.g. your task label}"
shift
if [ $# -eq 0 ]; then
   echo "give at least one source path under Server/" >&2
   exit 2
fi

WIN_SERVER="${WIN_SERVER:-/mnt/c/Users/chris/Documents/projects/hmailserver/hmailserver/source/Server}"
WORK="$HOME/hm-$NAME"
SERVER="$WORK/Server"

mkdir -p "$WORK"
rsync -a --delete --exclude '.git' --exclude 'x64' "$WIN_SERVER/" "$SERVER/"
mkdir -p "$WORK/pch-lower"
cp "$SERVER/platform/pch/StdAfx.h" "$WORK/pch-lower/stdafx.h"

GCC_DIR=""
for candidate in $(ls -d /usr/lib/gcc/*/[0-9]* 2>/dev/null | sort -V -r); do
   if [ -d "/usr/include/c++/$(basename "$candidate")" ]; then
      GCC_DIR="$candidate"
      break
   fi
done

# libpq's headers are not on the default include path on every distribution -
# Debian and Ubuntu put them under /usr/include/postgresql - while PGConnection.h
# includes <libpq-fe.h> by that bare name, exactly as the Windows project does
# with its own include directory. Without this, six sources in Common/SQL report
# "'libpq-fe.h' file not found" and look broken when nothing is wrong with them.
# pg_config is the packaged answer to where the headers are; the fallback is the
# Debian location.
PG_INCLUDE="$(pg_config --includedir 2>/dev/null)"
if [ -z "$PG_INCLUDE" ] && [ -d /usr/include/postgresql ]; then
   PG_INCLUDE="/usr/include/postgresql"
fi

status=0
for relative in "$@"; do
   source="$SERVER/$relative"
   if [ ! -f "$source" ]; then
      echo "== $relative: no such file under Server/"
      status=1
      continue
    fi
   echo "== $relative"
   clang++ -std=c++20 -fsyntax-only -w -fdelayed-template-parsing \
      -DHM_PLATFORM_POSIX=1 -DUNICODE -D_UNICODE -DBOOST_BIND_GLOBAL_PLACEHOLDERS \
      ${GCC_DIR:+--gcc-install-dir=$GCC_DIR} \
      -I"$SERVER/platform/pch" -I"$WORK/pch-lower" \
      -I"$SERVER" -I"$SERVER/Common" -I"$SERVER/Common/Util" \
      ${PG_INCLUDE:+-I"$PG_INCLUDE"} \
      -include "$SERVER/platform/portable_stdafx.h" \
      "$source" 2>&1 | head -40
   if [ "${PIPESTATUS[0]}" -eq 0 ]; then
      echo "   COMPILES"
   else
      status=1
   fi
done

exit $status
