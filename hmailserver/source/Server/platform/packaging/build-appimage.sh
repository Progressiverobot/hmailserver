#!/bin/bash
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# https://www.progressiverobot.com
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# WHAT THIS PRODUCES, AND WHAT IT IS NOT FOR
#
# An AppImage of a mail server is a try-it-out artefact and nothing more. Read
# this paragraph before you deploy one, because everything about it is different
# from the .deb and the .rpm:
#
#   * It runs as whoever double-clicked it. There is no hmailserver system user,
#     no privilege separation, and no systemd unit with the hardening this
#     repository spent a file on - the whole of ProtectSystem, the capability
#     bounding set and the syscall filter are simply absent.
#   * Its data lives under $HOME. The mail store, the logs and the configuration
#     go to ${XDG_DATA_HOME:-$HOME/.local/share}/hmailserver unless
#     HMAILSERVER_HOME says otherwise - which is exactly where a mail store
#     should not be. A backup that covers the machine's services will not cover
#     it, and a home directory on NFS will not survive a message store on it.
#   * It cannot bind port 25. An ordinary user has no CAP_NET_BIND_SERVICE, and
#     the listener ports live in the DATABASE (hm_tcpipports: 25, 587, 110 and
#     143 as created), not in the configuration file - so trying it out means
#     changing those rows to ports above 1024, or granting the capability to the
#     extracted binary by hand.
#   * It still needs a database. There is no such thing as a self-contained
#     hMailServer: PostgreSQL or MySQL/MariaDB has to be running somewhere and
#     the schema has to have been created, exactly as for a package install.
#
# A real installation uses the .deb or the .rpm, which put the store in
# /var/lib/hmailserver under a user that owns it, and run the server under a unit
# that constrains it. Use this to see the thing work on a laptop, to reproduce a
# bug against a specific build, or to hand somebody a binary without asking them
# to install anything. Not to run mail.
#
# USAGE
#   build-appimage.sh [--build-dir DIR] [--output-dir DIR]
#
#   --build-dir   a tree that has ALREADY been built, i.e. one where
#                 "cmake --build" has produced the hmailserver executable.
#                 Default: build/linux relative to the repository root.
#   --output-dir  where the .AppImage is written. Default: the build directory.
#
# It builds nothing itself. Build first:
#
#   cmake -S hmailserver/source/Server -B build/linux -G Ninja \
#         -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=/usr
#   cmake --build build/linux

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# packaging -> platform -> Server -> source -> hmailserver -> repository root
REPO_ROOT="$(cd "${HERE}/../../../../.." && pwd)"
SERVER_DIR="${REPO_ROOT}/hmailserver/source/Server"

BUILD_DIR="${REPO_ROOT}/build/linux"
OUTPUT_DIR=""

while [ "$#" -gt 0 ]; do
   case "$1" in
      --build-dir)
         [ "$#" -ge 2 ] || { echo "--build-dir needs a directory." >&2; exit 2; }
         BUILD_DIR="$2"
         shift 2
         ;;
      --output-dir)
         [ "$#" -ge 2 ] || { echo "--output-dir needs a directory." >&2; exit 2; }
         OUTPUT_DIR="$2"
         shift 2
         ;;
      -h|--help)
         sed -n '5,50p' "${BASH_SOURCE[0]}"
         exit 0
         ;;
      *)
         echo "Unknown option: $1" >&2
         exit 2
         ;;
   esac
done

BUILD_DIR="$(cd "${BUILD_DIR}" 2>/dev/null && pwd || true)"
if [ -z "${BUILD_DIR}" ]; then
   echo "The build directory does not exist. Build the tree first; see --help." >&2
   exit 1
fi

BINARY="${BUILD_DIR}/hmailserver"
if [ ! -x "${BINARY}" ]; then
   echo "No executable at ${BINARY}. This script packages an already-built tree; it does not build one." >&2
   exit 1
fi

[ -n "${OUTPUT_DIR}" ] || OUTPUT_DIR="${BUILD_DIR}"
mkdir -p "${OUTPUT_DIR}"
OUTPUT_DIR="$(cd "${OUTPUT_DIR}" && pwd)"

# ---------------------------------------------------------------- version, arch

# The binary is the authority on its own version - it prints what Version.h said
# when it was compiled, which is the only answer that cannot be stale. Version.h
# itself is the fallback for a cross-built tree whose binary will not run here;
# it used to be the CMakeLists, which stopped working the day the CMakeLists
# began reading the header rather than carrying a literal of its own.
VERSION="$("${BINARY}" --version 2>/dev/null | awk '{ print $2 }' || true)"
if [ -z "${VERSION}" ]; then
   VERSION="$(sed -n 's/^#define[[:space:]][[:space:]]*HMAILSERVER_VERSION[[:space:]][[:space:]]*"\([0-9.]*\)".*/\1/p' \
      "${SERVER_DIR}/Common/Application/Version.h" | head -n 1)"
fi
if [ -z "${VERSION}" ]; then
   echo "Could not determine the version from the binary or from Common/Application/Version.h." >&2
   exit 1
fi

ARCH="$(uname -m)"
case "${ARCH}" in
   x86_64|aarch64) ;;
   arm64) ARCH="aarch64" ;;
   *)
      echo "No linuxdeploy build is published for ${ARCH}; only x86_64 and aarch64 are packaged this way." >&2
      exit 1
      ;;
esac

echo "hMailServer ${VERSION} (${ARCH}) from ${BUILD_DIR}"

# ---------------------------------------------------------------- the tools

# Downloaded once and kept, because a build that fetches two AppImages on every
# run is a build that fails when GitHub is slow. Override TOOLS_DIR to share one
# cache between checkouts.
TOOLS_DIR="${TOOLS_DIR:-${REPO_ROOT}/build/.appimage-tools}"
mkdir -p "${TOOLS_DIR}"

LINUXDEPLOY="${TOOLS_DIR}/linuxdeploy-${ARCH}.AppImage"
LINUXDEPLOY_PLUGIN="${TOOLS_DIR}/linuxdeploy-plugin-appimage-${ARCH}.AppImage"

fetch_tool()
{
   local destination="$1"
   local url="$2"

   if [ -x "${destination}" ]; then
      return 0
   fi

   echo "Fetching $(basename "${destination}")"

   if command -v curl >/dev/null 2>&1; then
      curl --fail --location --silent --show-error --output "${destination}" "${url}"
   elif command -v wget >/dev/null 2>&1; then
      wget --quiet --output-document "${destination}" "${url}"
   else
      echo "Neither curl nor wget is installed, and ${url} has to be fetched." >&2
      exit 1
   fi

   chmod +x "${destination}"
}

fetch_tool "${LINUXDEPLOY}" \
   "https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-${ARCH}.AppImage"
fetch_tool "${LINUXDEPLOY_PLUGIN}" \
   "https://github.com/linuxdeploy/linuxdeploy-plugin-appimage/releases/download/continuous/linuxdeploy-plugin-appimage-${ARCH}.AppImage"

# An AppImage needs FUSE to mount itself, and a container or a CI runner usually
# has none. Extracting instead of mounting works everywhere and costs a little
# disk, so it is simply always on for the TOOLS - it says nothing about how the
# AppImage this script produces will be run.
export APPIMAGE_EXTRACT_AND_RUN=1

# linuxdeploy finds its plugins on PATH, by file name.
export PATH="${TOOLS_DIR}:${PATH}"

# ---------------------------------------------------------------- the AppDir

APPDIR="${BUILD_DIR}/AppDir"
rm -rf "${APPDIR}"
mkdir -p "${APPDIR}/usr/bin" "${APPDIR}/usr/share/applications" \
         "${APPDIR}/usr/share/hmailserver"

install -m 0755 "${BINARY}" "${APPDIR}/usr/bin/hmailserver"

# The SQL the administrator has to run to create the schema. It is small, it is
# what the .deb and .rpm install to the same relative path, and an AppImage
# without it cannot get as far as a database.
cp -r "${REPO_ROOT}/hmailserver/source/DBScripts" "${APPDIR}/usr/share/hmailserver/DBScripts"

# The Control Deck. AppRun points ProgramFolder at this same directory, and
# RestApiServer::HandleWebAdminPage_ opens <ProgramFolder>/WebAdmin/index.html;
# without the file the REST listener answers GET / with a stub saying the page
# is not installed, which for a try-it-out artefact is the first thing anybody
# would see.
install -Dm 0644 "${REPO_ROOT}/hmailserver/installation/WebAdmin/index.html" \
   "${APPDIR}/usr/share/hmailserver/WebAdmin/index.html"

# The configuration the AppRun below copies out on first run. It is the packaged
# default with its absolute paths still in it; AppRun rewrites them, so this file
# is a template and never read in place.
install -m 0644 "${HERE}/hMailServer.ini" "${APPDIR}/usr/share/hmailserver/hMailServer.ini.default"

# There is no Linux icon in this repository - the only icon is a Windows .ico for
# the Control Panel - so one is drawn here rather than a placeholder being
# committed. Replace it when the project has artwork; linuxdeploy takes SVG.
cat > "${BUILD_DIR}/hmailserver.svg" <<'ICON'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" width="128" height="128">
  <rect width="128" height="128" rx="16" fill="#1f4e79"/>
  <rect x="20" y="38" width="88" height="56" rx="6" fill="#ffffff"/>
  <path d="M20 44 L64 74 L108 44" fill="none" stroke="#1f4e79" stroke-width="7"
        stroke-linecap="round" stroke-linejoin="round"/>
</svg>
ICON

cat > "${BUILD_DIR}/hmailserver.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=hMailServer
Comment=A mail server for SMTP, IMAP and POP3
Exec=hmailserver
Icon=hmailserver
Terminal=true
Categories=Network;Email;
DESKTOP

# The AppRun. linuxdeploy would otherwise write one that is a bare symlink to the
# executable, which would leave the server looking for hMailServer.ini beside a
# binary inside a read-only mount and finding none.
#
# What this does instead is the whole difference between an AppImage that works
# out of the box and one that prints a configuration error: it keeps a
# per-invoking-user home, creates the directory layout there the first time, and
# hands the server that configuration explicitly with --config.
cat > "${BUILD_DIR}/AppRun" <<'APPRUN'
#!/bin/sh
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# The AppImage entry point. See build-appimage.sh for what this artefact is and
# is not; the short version is that everything below keeps state in the invoking
# user's home, which is the right answer for trying something out and the wrong
# answer for running mail.

set -e

APPDIR="$(dirname "$(readlink -f "$0")")"

HMAILSERVER_HOME="${HMAILSERVER_HOME:-${XDG_DATA_HOME:-$HOME/.local/share}/hmailserver}"
CONFIG="${HMAILSERVER_HOME}/hMailServer.ini"

mkdir -p "${HMAILSERVER_HOME}/data" \
         "${HMAILSERVER_HOME}/logs" \
         "${HMAILSERVER_HOME}/temp" \
         "${HMAILSERVER_HOME}/events" \
         "${HMAILSERVER_HOME}/database"

if [ ! -f "${CONFIG}" ]; then
   # The packaged default with its six [Directories] values repointed under the
   # per-user home. Every other line - the [Database] section that still has to
   # be filled in, and the comments explaining it - is carried across unchanged.
   sed \
      -e "s|^ProgramFolder=.*|ProgramFolder=${APPDIR}/usr/share/hmailserver|" \
      -e "s|^DataFolder=.*|DataFolder=${HMAILSERVER_HOME}/data|" \
      -e "s|^LogFolder=.*|LogFolder=${HMAILSERVER_HOME}/logs|" \
      -e "s|^TempFolder=.*|TempFolder=${HMAILSERVER_HOME}/temp|" \
      -e "s|^EventFolder=.*|EventFolder=${HMAILSERVER_HOME}/events|" \
      -e "s|^DatabaseFolder=.*|DatabaseFolder=${HMAILSERVER_HOME}/database|" \
      "${APPDIR}/usr/share/hmailserver/hMailServer.ini.default" > "${CONFIG}"

   chmod 0600 "${CONFIG}"

   echo "A configuration has been created at ${CONFIG}." >&2
   echo "It has no database configured yet; edit the [Database] section, create" >&2
   echo "the schema from ${APPDIR}/usr/share/hmailserver/DBScripts, then run this" >&2
   echo "AppImage again with --check-config." >&2
fi

# ProgramFolder is the one value in this file that CANNOT be allowed to persist,
# so it is rewritten on EVERY run and not only when the file is created. It
# points inside the AppImage's own mount, and that path is different every time:
# FUSE mounts the image at a fresh /tmp/.mount_XXXXXX. Left as the first run
# wrote it, the second run would look for DBScripts and for the Control Deck
# under a mount point that no longer exists - and the server would answer GET /
# with the "not installed" stub on every run after the first.
#
# Only this one line is touched. Everything else in the file is the
# administrator's, including the [Database] section they filled in, and stays
# exactly as they wrote it.
sed -i "s|^ProgramFolder=.*|ProgramFolder=${APPDIR}/usr/share/hmailserver|" "${CONFIG}"
# sed -i writes a new file and renames it over the old one. GNU sed carries the
# original's mode across; not every sed does, and this file holds a database
# password, so the mode is restated rather than assumed.
chmod 0600 "${CONFIG}"

# --config is given explicitly rather than relying on the search order, because
# the first place the server looks is beside its own executable - which here is
# inside a read-only mount.
exec "${APPDIR}/usr/bin/hmailserver" --config "${CONFIG}" "$@"
APPRUN
chmod +x "${BUILD_DIR}/AppRun"

# ---------------------------------------------------------------- build it

OUTPUT_NAME="hMailServer-${VERSION}-${ARCH}.AppImage"

# OUTPUT is how linuxdeploy-plugin-appimage is told what to call the file; VERSION
# stops it from inventing one out of the git description of whatever directory it
# happens to be run from.
export OUTPUT="${OUTPUT_DIR}/${OUTPUT_NAME}"
export VERSION

rm -f "${OUTPUT}"

"${LINUXDEPLOY}" \
   --appdir "${APPDIR}" \
   --executable "${APPDIR}/usr/bin/hmailserver" \
   --desktop-file "${BUILD_DIR}/hmailserver.desktop" \
   --icon-file "${BUILD_DIR}/hmailserver.svg" \
   --custom-apprun "${BUILD_DIR}/AppRun" \
   --output appimage

if [ ! -f "${OUTPUT}" ]; then
   echo "linuxdeploy finished but ${OUTPUT} is not there." >&2
   exit 1
fi

echo
echo "${OUTPUT}"
echo
echo "Remember what this is: it runs as you, keeps its mail store under"
echo "\$HOME, cannot bind port 25, and still needs a database. Install the"
echo ".deb or the .rpm to run mail."
