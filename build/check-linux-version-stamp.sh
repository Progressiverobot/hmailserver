#!/bin/bash
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# https://www.progressiverobot.com
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# WHY THIS EXISTS
#
# The version of a Linux package is not decoration. CPACK_PACKAGE_VERSION decides
# the four file names the packages are written with, and
# .github/workflows/sign-release.yml matches those names by EXACT equality when it
# reports which platforms a release carries - as does the server's own Linux
# update checker, which builds the name its package manager and architecture would
# install and looks for precisely that asset. A package built at the wrong version
# is not a mislabelled package; it is a package nothing will ever find.
#
# Until this check existed, the number was hand-written in two places - the
# CMakeLists' project(VERSION) and pkgver in the PKGBUILD - and neither was named
# by RELEASE.md's stamp step. Nothing anywhere failed when one of them lagged
# behind Common/Application/Version.h, which is where the Windows resources, the
# installer and the update checker all take the version from.
#
# WHAT IT ENFORCES
#
#   1. Version.h parses, and its numeric triple agrees with its string.
#   2. The CMakeLists DERIVES its version from Version.h and carries no literal
#      of its own. This is checked rather than assumed, because the failure it
#      guards against is somebody putting the literal back.
#   3. pkgver in platform/packaging/PKGBUILD equals Version.h's version. This one
#      genuinely cannot be derived: makepkg parses the PKGBUILD as a shell script
#      and never runs CMake.
#   4. Nothing else under platform/packaging/ carries a hard-coded version at all.
#      The packaging README used to spell out "hmailserver_6.2.28_amd64.deb" in
#      five places; those now say <version>, and this check is what keeps a
#      literal from creeping back in and going stale.
#
# It reads files and prints. It changes nothing, and it needs no toolchain - which
# is what lets it run as the FIRST step of the Linux workflow, before anything is
# compiled, so a missed stamp is reported in seconds rather than after a build.
#
# USAGE
#   build/check-linux-version-stamp.sh [--repo-root DIR]
#
# Exit status: 0 when every stamp agrees, 1 when any of them does not.

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${HERE}/.." && pwd)"

while [ "$#" -gt 0 ]; do
   case "$1" in
      --repo-root)
         [ "$#" -ge 2 ] || { echo "--repo-root needs a directory." >&2; exit 2; }
         REPO_ROOT="$(cd "$2" && pwd)"
         shift 2
         ;;
      -h|--help)
         sed -n '5,45p' "${BASH_SOURCE[0]}"
         exit 0
         ;;
      *)
         echo "Unknown option: $1" >&2
         exit 2
         ;;
   esac
done

SERVER_DIR="${REPO_ROOT}/hmailserver/source/Server"
VERSION_H="${SERVER_DIR}/Common/Application/Version.h"
CMAKELISTS="${SERVER_DIR}/CMakeLists.txt"
PACKAGING="${SERVER_DIR}/platform/packaging"
PKGBUILD="${PACKAGING}/PKGBUILD"

failures=0

# GitHub renders "::error::" as an annotation on the run; anywhere else it is
# just a line that begins with the word. Both are meant to be read.
fail()
{
   echo "::error::$*"
   failures=$((failures + 1))
}

for file in "${VERSION_H}" "${CMAKELISTS}" "${PKGBUILD}"; do
   if [ ! -f "${file}" ]; then
      fail "${file#"${REPO_ROOT}/"} is not there. This check cannot say anything about the version stamps without it."
      exit 1
   fi
done

# ---------------------------------------------------------------- 1. Version.h

VERSION="$(sed -n 's/^#define[[:space:]][[:space:]]*HMAILSERVER_VERSION[[:space:]][[:space:]]*"\([0-9][0-9.]*\)".*/\1/p' "${VERSION_H}" | head -n 1)"

if [[ ! "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
   fail "Could not read a three-part HMAILSERVER_VERSION out of Common/Application/Version.h; got '${VERSION}'."
   exit 1
fi

echo "Version.h                                  ${VERSION}"

# The numeric quadruple is what the Windows version resource is built from, and
# its first three fields are the same number written another way. They have gone
# out of step before.
NUMERIC="$(sed -n 's/^#define[[:space:]][[:space:]]*HMAILSERVER_VERSION_NUMERIC[[:space:]][[:space:]]*\(.*\)$/\1/p' "${VERSION_H}" | head -n 1 | tr -d ' \t\r')"
NUMERIC_TRIPLE="$(echo "${NUMERIC}" | cut -d, -f1-3 | tr ',' '.')"

if [ "${NUMERIC_TRIPLE}" != "${VERSION}" ]; then
   fail "Version.h disagrees with itself: HMAILSERVER_VERSION is ${VERSION} but HMAILSERVER_VERSION_NUMERIC begins ${NUMERIC_TRIPLE} (the whole value is '${NUMERIC}')."
else
   echo "Version.h numeric                          ${NUMERIC}"
fi

# ------------------------------------------------------- 2. the CMakeLists derives

# The two halves of the derivation, checked separately so the message says which
# one went missing.
cmake_failures_before="${failures}"

if ! grep -Eq 'file\(STRINGS[^)]*Version\.h' "${CMAKELISTS}"; then
   fail "hmailserver/source/Server/CMakeLists.txt no longer reads Common/Application/Version.h. The package version must be derived from that header, never written here: CPACK_PACKAGE_VERSION becomes the four package file names, and sign-release.yml and the Linux update checker both match those names exactly."
fi

# POSIX character classes, and the trailing one is not tidiness: files in this
# repository reach a Linux runner with CRLF endings, a bare $ matches nothing
# after a carriage return, and "\r" inside a bracket expression is a backslash
# and an r rather than a carriage return. [[:space:]] covers it, and covered it
# only after the first version of this line failed on the very file it checks.
if ! grep -Eq '^[[:space:]]*VERSION[[:space:]]+\$\{HMAILSERVER_VERSION\}[[:space:]]*$' "${CMAKELISTS}"; then
   fail "The project() call in hmailserver/source/Server/CMakeLists.txt does not read 'VERSION \${HMAILSERVER_VERSION}'. Whatever it reads instead is a second place the version lives, and this check cannot verify it."
fi

# And no literal, which is the failure this whole file exists because of.
literal_in_cmake="$(grep -nE '^[[:space:]]*VERSION[[:space:]]+[0-9]+\.[0-9]+' "${CMAKELISTS}" || true)"
if [ -n "${literal_in_cmake}" ]; then
   fail "hmailserver/source/Server/CMakeLists.txt carries a hard-coded version: ${literal_in_cmake}. Delete it; the value comes from Version.h."
fi

if [ "${failures}" -eq "${cmake_failures_before}" ]; then
   echo "CMakeLists.txt                             derived from Version.h"
fi

# ---------------------------------------------------------------- 3. the PKGBUILD

PKGVER="$(sed -n 's/^pkgver=\(.*\)$/\1/p' "${PKGBUILD}" | head -n 1 | tr -d ' \t\r')"

if [ -z "${PKGVER}" ]; then
   fail "platform/packaging/PKGBUILD has no pkgver= line."
elif [ "${PKGVER}" != "${VERSION}" ]; then
   fail "platform/packaging/PKGBUILD says pkgver=${PKGVER} and Version.h says ${VERSION}. Stamping a release means editing both. The Arch package would build the git tag v${PKGVER}, which is not the release being made."
else
   echo "PKGBUILD pkgver                            ${PKGVER}"
fi

# ------------------------------------------- 4. no other literal under packaging/

# Deliberately narrow: "6.x.y" and nothing else, so the OpenSSL 3.0, Boost 1.83
# and CMake 3.22 that these files legitimately name are not swept up. The
# PKGBUILD's own pkgver line is the one occurrence there is meant to be.
stray="$(grep -rnE '6\.[0-9]+\.[0-9]+' "${PACKAGING}" 2>/dev/null | grep -v '^[^:]*/PKGBUILD:[0-9]*:pkgver=' || true)"

if [ -n "${stray}" ]; then
   fail "A version literal has appeared under platform/packaging/ outside the PKGBUILD's pkgver line. Nothing there should spell a version out - the file names are '<version>' in the README for exactly this reason, because a literal in prose goes stale the release after it is written:"
   echo "${stray}" | sed "s|^${REPO_ROOT}/||" | sed 's/^/    /'
fi

# ---------------------------------------------------------------- the verdict

echo

if [ "${failures}" -ne 0 ]; then
   echo "${failures} version-stamp problem(s). The Linux packages would be built, named and"
   echo "published at a version that does not match what the server reports for itself."
   echo
   echo "Stamping a release on this platform is two edits and no more:"
   echo "  hmailserver/source/Server/Common/Application/Version.h   HMAILSERVER_VERSION and HMAILSERVER_VERSION_NUMERIC"
   echo "  hmailserver/source/Server/platform/packaging/PKGBUILD    pkgver"
   exit 1
fi

echo "Every Linux version stamp agrees with Version.h: ${VERSION}."
exit 0
