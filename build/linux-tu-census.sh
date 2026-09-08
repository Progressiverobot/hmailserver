#!/bin/bash
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# https://www.progressiverobot.com
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# How much of the server core compiles on Linux, counted rather than estimated.
#
# The roadmap's Linux section opens with a row whose deliverable is not a running
# server but a number: how many of the core's translation units a POSIX compiler
# accepts. This script is that number. It compiles every core source on its own,
# to /dev/null, with the portable precompiled header shadowing the ATL one, and
# prints how many succeeded, how many failed, and - the useful part - what the
# failures are, grouped by the first error each one gives.
#
# It is deliberately not the CMake build. A census must not stop at the first
# failure and must not care about linking, so it runs the compiler once per file
# and tallies. The CMake build compiles the subset that is known to work.
#
# Usage:  build/linux-tu-census.sh [-j N] [--cc clang++|g++] [--filter substring]
#         --cc g++ is a first-class measurement, not a curiosity: both compilers
#         read 497/497 on 8 September 2026.
#         SERVER=<path>  when the sources are not beside this script

set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER="${SERVER:-$HERE/../hmailserver/source/Server}"
JOBS="$(nproc 2>/dev/null || echo 4)"
CC="clang++"
FILTER=""
TARGET=""
OUT="${OUT:-/tmp/hm-tu-census}"

while [ $# -gt 0 ]; do
   case "$1" in
      -j) JOBS="$2"; shift 2 ;;
      --cc) CC="$2"; shift 2 ;;
      --filter) FILTER="$2"; shift 2 ;;
      --out) OUT="$2"; shift 2 ;;
      # The architecture to compile FOR. The census is a syntax measurement, so a
      # target triple is enough to answer the question the roadmap asks about
      # AArch64 - whether the sources are architecture-clean - without a full
      # cross toolchain and a second copy of every library.
      --target) TARGET="$2"; shift 2 ;;
      *) echo "unknown argument: $1" >&2; exit 2 ;;
   esac
done

if [ ! -d "$SERVER/Common" ]; then
   echo "SERVER=$SERVER does not look like the server source tree." >&2
   exit 2
fi

# A census taken over a case-INSENSITIVE filesystem is not a census of what a
# Linux build would do, and it does not merely flatter the number - it destroys
# it. The sources are routinely edited from Windows and reached through WSL as
# /mnt/c, which is a DrvFs mount that matches paths without regard to case. On
# such a mount the <ctime> that hm_platform.h includes resolves to
# Common/Util/Time.h, because -I$SERVER/Common/Util is on the include path and
# "time.h" matches "Time.h" there; every one of the 505 sources then fails with
# the same error and the script reports 0%, which is false in both directions -
# it hides the real failures and invents 500 imaginary ones.
#
# So the tree is copied to a case-sensitive filesystem first whenever the one it
# sits on is not. The probe is the tree's own Common directory asked for in the
# wrong case: a filesystem that answers is one this census cannot be run on
# directly. Copying is what linux-compile-one.sh has always done, for the same
# reason.
if [ -d "$SERVER/COMMON" ]; then
   CASE_SENSITIVE_COPY="${CASE_SENSITIVE_COPY:-$HOME/hm-census-source}"
   echo "The sources are on a case-insensitive filesystem; copying them to $CASE_SENSITIVE_COPY first."
   mkdir -p "$CASE_SENSITIVE_COPY"
   rsync -a --delete --exclude '.git' --exclude 'x64' "$SERVER/" "$CASE_SENSITIVE_COPY/"
   SERVER="$CASE_SENSITIVE_COPY"

   if [ -d "$SERVER/COMMON" ]; then
      echo "$CASE_SENSITIVE_COPY is case-insensitive too; the census would be meaningless. Set CASE_SENSITIVE_COPY to a path on a native Linux filesystem." >&2
      exit 2
   fi
fi

rm -rf "$OUT"
mkdir -p "$OUT/log" "$OUT/pch-lower"
# The lowercase spelling of the shadowing header, which cannot be checked in
# beside the other on a case-insensitive filesystem.
cp "$SERVER/platform/pch/StdAfx.h" "$OUT/pch-lower/stdafx.h"

# What the census covers: the core. Not Server/COM (the ATL administration API,
# which is Windows by definition and has its own roadmap row), not zlib (vendored
# C with its own upstream build), not the three Windows executables, not the fuzz
# harness (which has its own shim and its own build script).
# Eleven sources under Server/ are in no build at all: hMailServer.vcxproj does
# not list them, so MSVC has never compiled them either. LoggerFile.h still
# derives from a class the program had before it was renamed; ServerThreads.h and
# OutboundPortConnection.h include headers that exist nowhere in the repository;
# SMTPCommandHelp.cpp calls a method that was removed. They are tree residue, not
# work owed, and counting them as failures would misstate the measurement. The
# list is the same one hmailserver/source/Server/CMakeLists.txt excludes.
UNREFERENCED_SOURCES=(
   Common/Application/LoggerFile.cpp
   Common/Application/ServerThreads.cpp
   Common/BO/Collection.cpp
   Common/Cache/AccountCache.cpp
   Common/Cache/Cache.cpp
   Common/Cache/CacheReaderWithDbFallback.cpp
   Common/Diagnostics/OutboundPortConnection.cpp
   Common/Diagnostics/TestConnectionResult.cpp
   Common/Util/Mutex.cpp
   SMTP/SMTPCommands/ISMTPCommand.cpp
   SMTP/SMTPCommands/SMTPCommandHelp.cpp
)

mapfile -t SOURCES < <(find "$SERVER" -name '*.cpp' \
   -not -path "$SERVER/COM/*" \
   -not -path "$SERVER/zlib/*" \
   -not -path "$SERVER/hMailServer/*" \
   -not -path "$SERVER/hMailServer.Minidump/*" \
   -not -path "$SERVER/hMailServer.Updater/*" \
   | sort)

for unreferenced in "${UNREFERENCED_SOURCES[@]}"; do
   for index in "${!SOURCES[@]}"; do
      if [ "${SOURCES[$index]}" = "$SERVER/$unreferenced" ]; then
         unset 'SOURCES[index]'
      fi
   done
done
SOURCES=("${SOURCES[@]}")

if [ -n "$FILTER" ]; then
   mapfile -t SOURCES < <(printf '%s\n' "${SOURCES[@]}" | grep -- "$FILTER")
fi

# clang picks the newest GCC installation it can see for libstdc++, which on a
# distribution carrying two of them can be one whose headers are not installed -
# every file then fails with "'cstddef' file not found", which looks like a
# missing compiler and is not. GCC_DIR pins it; the default is the newest that
# actually has a c++ header directory.
if [ -z "${GCC_DIR:-}" ] && [ "$CC" = "clang++" ]; then
   for candidate in $(ls -d /usr/lib/gcc/*/[0-9]* 2>/dev/null | sort -V -r); do
      version="$(basename "$candidate")"
      if [ -d "/usr/include/c++/$version" ]; then
         GCC_DIR="$candidate"
         break
      fi
   done
fi

FLAGS=(
   -std=c++20
   -fsyntax-only
   -w                       # a census counts what compiles, not what warns
   -DHM_PLATFORM_POSIX=1
   -DUNICODE -D_UNICODE
   -DBOOST_BIND_GLOBAL_PLACEHOLDERS
   "-I$SERVER/platform/pch"
   "-I$OUT/pch-lower"
   "-I$SERVER"
   "-I$SERVER/Common"
   "-I$SERVER/Common/Util"
   -include "$SERVER/platform/portable_stdafx.h"
)

# libpq's headers are not on the default include path on every distribution -
# Debian and Ubuntu put them under /usr/include/postgresql - while PGConnection.h
# includes <libpq-fe.h> by that bare name, exactly as the Windows project does
# with its own include directory. Without this the six PostgreSQL sources in
# Common/SQL are counted as failures for a reason that is not theirs. pg_config
# is the packaged answer to where the headers are; the fallback is the Debian
# location.
PG_INCLUDE="$(pg_config --includedir 2>/dev/null)"
if [ -z "$PG_INCLUDE" ] && [ -d /usr/include/postgresql ]; then
   PG_INCLUDE="/usr/include/postgresql"
fi

if [ -n "$PG_INCLUDE" ]; then
   FLAGS+=("-I$PG_INCLUDE")
fi

if [ -n "${GCC_DIR:-}" ]; then
   FLAGS+=("--gcc-install-dir=$GCC_DIR")
fi

# No -fdelayed-template-parsing. It was passed on the belief that
# Common/Util/StdString.h needed clang's MSVC-compatible template mode; measured
# on 8 September 2026, every one of the 497 files compiles without it under
# clang, and under GCC 13 and 15, which never had the option. A census is a
# measurement of the code as the compiler sees it, so it is not given a mode
# the build does not use either.

if [ -n "$TARGET" ]; then
   # A cross target needs the triple and the cross GCC installation; clang finds
   # that architecture's headers and libraries from the installation directory on
   # its own. Pointing a --sysroot at /usr/<triple> instead LOOKS right and is
   # not: Debian's cross packages put their headers at <triple>/include, while a
   # sysroot means <sysroot>/usr/include, and every file then fails on stdlib.h.
   FLAGS+=("--target=$TARGET")
   for cross in $(ls -d "/usr/lib/gcc-cross/$TARGET"/[0-9]* 2>/dev/null | sort -V -r); do
      FLAGS+=("--gcc-install-dir=$cross")
      break
   done
   # Boost's headers are the same source for either architecture and this census
   # does not link, so the host's are used; Boost reads the target's own macros.
   #
   # OpenSSL's configuration header is the one file Debian puts under the
   # multiarch directory - /usr/include/x86_64-linux-gnu/openssl/opensslconf.h -
   # which a cross target never searches, so every file failed on it on a
   # runner with no arm64 headers. Searched LAST, after the target's own
   # directories, so a real arm64 header wins wherever one is installed and the
   # host's configuration serves only where none is. For a syntax census that
   # is the right answer; it would not be for a link.
   host_multiarch="$(gcc -print-multiarch 2>/dev/null || echo x86_64-linux-gnu)"
   if [ -d "/usr/include/$host_multiarch" ]; then
      FLAGS+=("-idirafter" "/usr/include/$host_multiarch")
   fi
   echo "Compiling for $TARGET"
fi

compile_one() {
   local src="$1"
   local name
   name="$(echo "${src#$SERVER/}" | tr '/' '_')"
   if $CC "${FLAGS[@]}" "$src" > "$OUT/log/$name.log" 2>&1; then
      echo "OK   $src"
   else
      echo "FAIL $src"
   fi
}
export -f compile_one
export CC SERVER OUT
export FLAGS_STR="${FLAGS[*]}"

# xargs keeps the job count without a dependency on GNU parallel.
printf '%s\n' "${SOURCES[@]}" | xargs -P "$JOBS" -I{} bash -c '
   src="{}"
   name="$(echo "${src#$SERVER/}" | tr "/" "_")"
   read -r -a flags <<< "$FLAGS_STR"
   if $CC "${flags[@]}" "$src" > "$OUT/log/$name.log" 2>&1; then
      echo "OK   $src"
   else
      echo "FAIL $src"
   fi
' > "$OUT/result.txt"

TOTAL="${#SOURCES[@]}"
OKS=$(grep -c '^OK   ' "$OUT/result.txt" || true)
FAILS=$(grep -c '^FAIL ' "$OUT/result.txt" || true)

echo
echo "Translation units offered:  $TOTAL"
echo "Compiled with $CC:          $OKS"
echo "Failed:                     $FAILS"
if [ "$TOTAL" -gt 0 ]; then
   echo "Share compiling:            $(( OKS * 100 / TOTAL ))%"
fi

if [ "$FAILS" -gt 0 ]; then
   echo
   echo "The first error of each failure, most common first:"
   grep '^FAIL ' "$OUT/result.txt" | sed 's/^FAIL //' | while read -r src; do
      name="$(echo "${src#$SERVER/}" | tr '/' '_')"
      grep -m1 -E '(error|fatal error):' "$OUT/log/$name.log" 2>/dev/null \
         | sed -E 's/.*(error|fatal error): //' \
         | sed -E "s/'[^']*'/'X'/g" \
         | cut -c1-90
   done | sort | uniq -c | sort -rn | head -30
   echo
   # The grouped list above masks names so that the same shape of error counts
   # once; this one does not, because a single failure on a machine one cannot
   # log into - a CI runner - is diagnosed from this line or not at all.
   echo "Each failing file, with its first error as the compiler wrote it:"
   grep '^FAIL ' "$OUT/result.txt" | sed 's/^FAIL //' | head -40 | while read -r src; do
      name="$(echo "${src#$SERVER/}" | tr '/' '_')"
      first="$(grep -m1 -E '(error|fatal error):' "$OUT/log/$name.log" 2>/dev/null | cut -c1-240)"
      echo "  ${src#$SERVER/}"
      echo "     ${first:-(no error line; see the log)}"
   done
   echo
   echo "Full logs: $OUT/log"
fi

# The census never fails the build; its output is the measurement.
exit 0
