# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# https://www.progressiverobot.com
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# The portable build of the hMailServer core.
#
# WHAT THIS IS AND IS NOT
#
# It is not a second build of the shipping server. hMailServer.vcxproj remains
# the only thing that produces hMailServer.exe, and nothing here is compiled on
# Windows. This builds the core - Common, IMAP, POP3, SMTP, ExternalFetcher -
# on POSIX, so that the Linux and AArch64 section of the roadmap has something
# to measure and something to grow.
#
# It deliberately does NOT build:
#   Server/COM                 the ATL administration API. Windows by definition;
#                              it is what the "Administration without COM" row is
#                              about, and the core reaches it through one file.
#   Server/zlib                vendored upstream C with its own build; the system
#                              zlib is used here instead.
#   Server/hMailServer         the ATL executable and its service registration.
#                              The "service host" row replaces it with a main().
#   Server/hMailServer.Updater the live-update apply helper: it runs a Windows
#                              installer, which has no meaning here yet.
#   Server/hMailServer.Minidump  Windows Error Reporting.
#
# WINDOWS_ONLY_SOURCES below is the list of core files that wrap a Win32
# subsystem with no POSIX equivalent yet. Each one is a roadmap row, not an
# oversight, and each is named with the row it belongs to. Removing a name from
# that list is how a row gets finished.
#
# HOW TO BUILD
#   cmake -S hmailserver/source/Server -B build/linux -G Ninja \
#         -DCMAKE_BUILD_TYPE=Release
#   cmake --build build/linux
#
# HOW TO MEASURE INSTEAD OF BUILD
#   build/linux-tu-census.sh compiles every core source on its own and prints how
#   many a POSIX compiler accepts. That number, not this target, is what the
#   roadmap's first Linux row asks for.

cmake_minimum_required(VERSION 3.22)

# ---------------------------------------------------------------- the version
#
# NOT written here. Common/Application/Version.h is the one place the number
# lives - the Windows resources, the installer and the update checker all stamp
# from it - and this file's PROJECT_VERSION becomes CPACK_PACKAGE_VERSION, which
# decides the four package FILE NAMES that .github/workflows/sign-release.yml
# matches by exact equality and that the Linux update checker builds from a tag.
# A hand-written number here that lagged Version.h by one release produced
# packages nothing would ever find, and nothing checked it.
#
# So it is read out of the header at configure time. The other Linux stamp -
# pkgver in platform/packaging/PKGBUILD - cannot be derived this way, because
# makepkg parses that file as a shell script and never runs CMake; it is verified
# instead, by build/check-linux-version-stamp.sh, which is a required step of
# .github/workflows/linux-build.yml. CMAKE_CURRENT_LIST_DIR rather than
# CMAKE_CURRENT_SOURCE_DIR only because this runs before project().
file(STRINGS ${CMAKE_CURRENT_LIST_DIR}/Common/Application/Version.h HMAILSERVER_VERSION_DEFINE
   REGEX "^#define[ \t]+HMAILSERVER_VERSION[ \t]+\"")

string(REGEX REPLACE "^.*\"([0-9]+\\.[0-9]+\\.[0-9]+)\".*$" "\\1"
   HMAILSERVER_VERSION "${HMAILSERVER_VERSION_DEFINE}")

if(NOT HMAILSERVER_VERSION MATCHES "^[0-9]+\\.[0-9]+\\.[0-9]+$")
   message(FATAL_ERROR
      "Could not read HMAILSERVER_VERSION out of Common/Application/Version.h. "
      "That header is where the version of everything this repository builds comes "
      "from; the line read was: '${HMAILSERVER_VERSION_DEFINE}'.")
endif()

project(hmailserver_core
   VERSION ${HMAILSERVER_VERSION}
   DESCRIPTION "hMailServer core, portable build"
   LANGUAGES CXX)

message(STATUS "hMailServer version ${PROJECT_VERSION}, read from Common/Application/Version.h")

if(WIN32)
   message(FATAL_ERROR
      "This CMake build is the POSIX one. On Windows the server is built from "
      "hmailserver/source/Server/hMailServer/hMailServer.sln with MSVC.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Reproducibility: the same promise the Windows build makes with /Brepro and
# /pdbaltpath, by the mechanisms this toolchain has. SOURCE_DATE_EPOCH is honoured
# by the archiver and by __DATE__/__TIME__ when the environment sets it.
add_compile_options(
   -ffile-prefix-map=${CMAKE_CURRENT_SOURCE_DIR}=.
   -fno-common)

# ---------------------------------------------------------------- the sources

set(SERVER_DIR ${CMAKE_CURRENT_SOURCE_DIR})

file(GLOB_RECURSE CORE_SOURCES CONFIGURE_DEPENDS
   ${SERVER_DIR}/Common/*.cpp
   ${SERVER_DIR}/IMAP/*.cpp
   ${SERVER_DIR}/POP3/*.cpp
   ${SERVER_DIR}/SMTP/*.cpp
   ${SERVER_DIR}/ExternalFetcher/*.cpp)

# Each of these wraps a Windows subsystem the POSIX build does not have. The
# roadmap row that would replace it is named beside it; when one is written,
# delete the line.
#
# A name may only be here if NOTHING THIS BUILD COMPILES REFERENCES IT. Removing
# a translation unit does not remove the calls to it, and a file left out because
# it "looks like Windows" takes its symbols with it - which is why the four that
# used to be on this list are not any more: DNSResolverWinApi, DataProtector and
# SSPIValidation are each called by name from code that does build (DNSResolver,
# Crypt, PasswordValidator), and ScriptServer is called from twenty places. They
# have POSIX arms of their own instead. Every name below was checked the same way
# before it was left here.
#
# The seven SQL files are a milder case: each is wholly inside
# "#ifndef HM_PLATFORM_POSIX" and so compiles here to an empty translation unit.
# Leaving them out costs nothing and says plainly that the backends are deferred;
# DALConnectionFactory refuses those two backends by name on this platform, so
# nothing reaches them.
set(WINDOWS_ONLY_SOURCES
   Common/SQL/ADOConnection.cpp                 # "Database backends that survive"
   Common/SQL/ADORecordset.cpp                  # "Database backends that survive"
   Common/SQL/ADOInt64Helper.cpp                # "Database backends that survive"
   Common/SQL/SQLCEConnection.cpp               # "Database backends that survive"
   Common/SQL/SQLCERecordset.cpp                # "Database backends that survive"
   Common/SQL/Macros/MSSQLMacroExpander.cpp     # "Database backends that survive"
   Common/SQL/Macros/SQLCEMacroExpander.cpp     # "Database backends that survive"
   Common/Util/ServiceManager.cpp               # "The service host": systemd owns this
   Common/Util/Registry.cpp                     # the registry; the INI is the POSIX answer
)

# Files that are in the tree but in NO build - not this one and not
# hMailServer.vcxproj, which is the only thing that produces the shipping server.
# They are leftovers: LoggerFile.h still derives from HIS_Logger and names
# HIS_String, from before the program was renamed; ServerThreads.h includes a
# ThreadIdentifier.h that no longer exists; OutboundPortConnection.h includes a
# ProtocolParser.h that no longer exists; SMTPCommandHelp.cpp calls
# SMTPConnection::SendData, which was removed. Nothing includes any of their
# headers.
#
# The rule this list follows is simply that the POSIX build compiles the set the
# Windows project compiles, less the Windows-only rows above. Six of these do
# still compile here, and are excluded anyway: a file the shipping server has not
# built for years is not something this build should be the first to link.
#
# Each was checked against hMailServer.vcxproj by path, case-insensitively - the
# project spells some directories differently from the filesystem ("Common\Bo"),
# so a case-sensitive comparison reports nearly the whole tree as missing.
set(UNREFERENCED_SOURCES
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

foreach(relative IN LISTS WINDOWS_ONLY_SOURCES UNREFERENCED_SOURCES)
   list(REMOVE_ITEM CORE_SOURCES ${SERVER_DIR}/${relative})
endforeach()

list(LENGTH CORE_SOURCES CORE_SOURCE_COUNT)
message(STATUS "hMailServer core: ${CORE_SOURCE_COUNT} translation units offered to this build")

# ---------------------------------------------------------------- dependencies

find_package(Threads REQUIRED)
find_package(OpenSSL 3.0 REQUIRED)
find_package(ZLIB REQUIRED)
# Boost.System is not asked for as a component, and Boost::headers is linked
# below in place of Boost::system. It has been header-only since 1.69 and 1.90
# removed the compiled library altogether, so asking for the component makes a
# build against a current Boost fail at CONFIGURE time - "Could not find a
# package configuration file provided by boost_system" - before a single source
# is compiled, and naming the target makes it fail at generate time.
# Boost.Chrono is a library, not headers, in 1.83 - the Boost of Ubuntu 24.04 and
# Debian 12 - and Boost.Thread reaches it for every sleep_for and steady_clock
# the server calls. Against 1.90 it links without being named, which is how the
# first CI build came to fail at the link step on a runner and not on the
# machine the port was written on.
# Boost is linked STATICALLY. A Boost library's soname carries its version
# (libboost_thread.so.1.83.0), so a package built against the Boost of one
# distribution release installs on that release and no other: the 6.3.1 .deb,
# built on Ubuntu 24.04, was refused by Ubuntu 26.04 for five Boost sonames and
# nothing else (11 September 2026). With the archives linked in, dpkg-shlibdeps
# and rpmbuild's find-requires see no Boost at all and the package depends on
# what every release ships under a stable soname: libssl3, libpq5, libldap2,
# libstdc++6. The -dev packages carry the archives on Debian, Ubuntu and Fedora;
# the AppImage is unaffected (it bundled the shared libraries and now has none
# to bundle). Boost.Regex's static archive pulls ICU in only where ICU is
# called, which this code does not, so no libicu soname appears either.
set(Boost_USE_STATIC_LIBS ON)
find_package(Boost 1.83 REQUIRED COMPONENTS thread chrono filesystem regex)

# libpq. PGConnection.h and PGRecordset.h include <libpq-fe.h> by that bare name,
# the way the Windows project reaches it through an include directory, and Debian
# and Ubuntu put the header under /usr/include/postgresql where no compiler looks
# by default. It is REQUIRED because six sources in Common/SQL name it and none of
# them is on the Windows-only list: PostgreSQL is a backend this build has, not
# one it defers. MySQL needs no such package - that client is loaded at run time
# with dlopen and the tree declares its own entry points.
find_package(PostgreSQL REQUIRED)

# iconv. Unicode::FromCodePage and Unicode::ToCodePage are what
# MultiByteToWideChar and WideCharToMultiByte are on this platform, and iconv is
# how they convert - which every MIME header in a character set other than the
# message's own goes through. On glibc it is part of the C library and this finds
# nothing to link; elsewhere (musl with libiconv, the BSDs, macOS) it is a
# separate library, and the imported target below is what makes that difference
# invisible here.
find_package(Iconv REQUIRED)

# OpenLDAP. LdapClient.cpp is written against wldap32 on Windows and against
# libldap here - the same function names over a different library, which is why
# that file has two arms - and directory authentication on this platform is that
# client or nothing, so it is REQUIRED. Found with find_path and find_library
# rather than a package module because CMake ships none for it, and Debian's
# libldap-dev, Fedora's openldap-devel and Arch's libldap all put ldap.h and the
# libraries where the default search looks. liblber is named as well as libldap:
# the BER functions the client calls by name (ber_memfree) are exported from it,
# and a link line that relies on libldap pulling it in is one that breaks under
# --as-needed.
find_path(LDAP_INCLUDE_DIR NAMES ldap.h)
find_library(LDAP_LIBRARY NAMES ldap)
find_library(LBER_LIBRARY NAMES lber)

if(NOT LDAP_INCLUDE_DIR OR NOT LDAP_LIBRARY OR NOT LBER_LIBRARY)
   message(FATAL_ERROR
      "OpenLDAP was not found (ldap.h, libldap and liblber are all needed). "
      "Directory authentication on this platform is built on it. Install "
      "libldap-dev (Debian, Ubuntu), openldap-devel (Fedora, RHEL) or libldap "
      "(Arch) and configure again.")
endif()

# ---------------------------------------------------------------- the target

add_library(hmailserver_core STATIC ${CORE_SOURCES})

# Both spellings of the precompiled header have to resolve on a case-sensitive
# filesystem. Only StdAfx.h is in the repository - a second file differing by
# case cannot be checked out on Windows - so the lowercase one is generated here.
set(PCH_LOWER ${CMAKE_CURRENT_BINARY_DIR}/pch-lower)
file(MAKE_DIRECTORY ${PCH_LOWER})
configure_file(${SERVER_DIR}/platform/pch/StdAfx.h ${PCH_LOWER}/stdafx.h COPYONLY)

target_include_directories(hmailserver_core PUBLIC
   ${SERVER_DIR}/platform/pch
   ${PCH_LOWER}
   ${SERVER_DIR}
   ${SERVER_DIR}/Common
   ${SERVER_DIR}/Common/Util
   ${LDAP_INCLUDE_DIR})

target_compile_definitions(hmailserver_core PUBLIC
   HM_PLATFORM_POSIX=1
   UNICODE
   _UNICODE
   BOOST_BIND_GLOBAL_PLACEHOLDERS)

# The precompiled-header substitute, forced into every translation unit exactly
# as the MSVC build forces its own.
target_compile_options(hmailserver_core PUBLIC
   -include ${SERVER_DIR}/platform/portable_stdafx.h)

# clang and GCC are both first-class here. An earlier version of this file passed
# clang -fdelayed-template-parsing and warned that GCC would fail in
# Common/Util/StdString.h; measured on 8 September 2026, every core file compiles
# under clang without the flag and under GCC 13 and 15, and the CI matrix builds
# with both.

target_link_libraries(hmailserver_core PUBLIC
   Threads::Threads
   OpenSSL::SSL
   OpenSSL::Crypto
   ZLIB::ZLIB
   PostgreSQL::PostgreSQL
   Iconv::Iconv
   ${LDAP_LIBRARY}
   ${LBER_LIBRARY}
   Boost::headers
   Boost::thread
   Boost::chrono
   Boost::filesystem
   Boost::regex
   ${CMAKE_DL_LIBS})

# ---------------------------------------------------------------- the service

# The executable a package installs and systemd starts. platform/main.cpp is the
# whole of it: the Windows service host is 500 lines of COM registration and SCM
# callbacks, none of which has a meaning here.
add_executable(hmailserver ${SERVER_DIR}/platform/main.cpp)
target_link_libraries(hmailserver PRIVATE hmailserver_core)
set_target_properties(hmailserver PROPERTIES OUTPUT_NAME hmailserver)

include(GNUInstallDirs)
install(TARGETS hmailserver RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR})
install(DIRECTORY ${SERVER_DIR}/../DBScripts/
   DESTINATION ${CMAKE_INSTALL_DATADIR}/hmailserver/DBScripts
   FILES_MATCHING PATTERN "*.sql")

# The Control Deck - the administration page the REST listener serves at GET /.
#
# RestApiServer::HandleWebAdminPage_ builds the path as
# GetProgramDirectory() + "WebAdmin" + "index.html", and IniFileSettings gives
# the program directory a trailing separator when it reads it, so with the
# ProgramFolder=/usr/share/hmailserver that platform/packaging/hMailServer.ini
# sets, the file the server opens is exactly
# /usr/share/hmailserver/WebAdmin/index.html - which is what the DESTINATION
# below resolves to under the /usr prefix these packages are built with, and it
# is the same derivation the DBScripts rule above relies on.
#
# Until this rule existed, every packaged Linux server answered GET / with the
# built-in "Web administration page not installed" stub: the page was in the
# repository, the code looked for it, and nothing put it there. The install
# check in .github/workflows/linux-build.yml now asks the installed server for
# the page and refuses the stub.
install(FILES ${SERVER_DIR}/../../installation/WebAdmin/index.html
   DESTINATION ${CMAKE_INSTALL_DATADIR}/hmailserver/WebAdmin)
# Absolute for the same reason, and /usr/lib rather than /lib because that is
# where a distribution's own units live and where systemd looks first.
install(FILES ${SERVER_DIR}/platform/packaging/hmailserver.service
   DESTINATION /usr/lib/systemd/system)
# /etc, written out rather than taken from CMAKE_INSTALL_SYSCONFDIR. CPack
# prepends the packaging prefix to every RELATIVE destination, and GNUInstallDirs
# gives a relative "etc", so the indirection puts the configuration at
# /usr/etc/hmailserver - which the package then installs, the server never reads,
# and rpm rejects when its %config list names the real path.
install(FILES ${SERVER_DIR}/platform/packaging/hMailServer.ini
   DESTINATION /etc/hmailserver)
install(FILES ${SERVER_DIR}/platform/packaging/hmailserver.logrotate
   RENAME hmailserver
   DESTINATION /etc/logrotate.d)

# Two data files the server reads from its own directory and cannot work without.
# tlds.txt is the public-suffix list every DMARC organisational-domain decision
# needs (TLD::Initialize reports 4335 without it); dh2048.pem is the Diffie-Hellman
# group, and SslContextInitializer reports a critical 5603 for EVERY TLS context
# when it is missing - which is to say the server would install and then refuse
# every encrypted connection. They go beside the binary, which is where the code
# looks: Utilities::GetBinDirectory is the running executable's own directory.
install(FILES
   ${SERVER_DIR}/../../installation/Extras/tlds.txt
   ${SERVER_DIR}/../../installation/Extras/dh2048.pem
   DESTINATION ${CMAKE_INSTALL_BINDIR})

# ---------------------------------------------------------------- packages
#
# One version, one set of packages, built from this one description. CPack makes
# the .deb and the .rpm; the AppImage and the Arch PKGBUILD are scripts beside
# this file, because neither is something CPack does well.
# What auto-ban does below the server on Linux: the hook the server runs when
# AutoBanFirewall=1 is set, and a fail2ban filter and jail for the other half,
# which needs no capability at all because fail2ban runs as root and reads the
# application log. The jail is installed disabled, as Debian asks of a shipped
# jail; the filter and the jail are configuration, kept on upgrade.
install(PROGRAMS ${SERVER_DIR}/platform/packaging/autoban-hook
   DESTINATION lib/hmailserver)
install(FILES ${SERVER_DIR}/platform/packaging/fail2ban/filter.d/hmailserver.conf
   DESTINATION /etc/fail2ban/filter.d)
install(FILES ${SERVER_DIR}/platform/packaging/fail2ban/jail.d/hmailserver.conf
   DESTINATION /etc/fail2ban/jail.d)

set(CPACK_PACKAGE_NAME hmailserver)
set(CPACK_PACKAGE_VENDOR "Progressive Robot Ltd")
set(CPACK_PACKAGE_CONTACT "christopher.j.holloway@outlook.com")
set(CPACK_PACKAGE_DESCRIPTION_SUMMARY "hMailServer, a mail server for SMTP, IMAP and POP3")
set(CPACK_PACKAGE_HOMEPAGE_URL "https://github.com/Progressiverobot/hmailserver")
set(CPACK_PACKAGE_VERSION ${PROJECT_VERSION})
set(CPACK_PACKAGING_INSTALL_PREFIX /usr)
# DESTDIR mode, stated rather than left to CPack's version to decide. The unit,
# the configuration and the logrotate rule are installed to absolute paths (see
# the install rules above and why), and how CPack treats an absolute DESTINATION
# without this flag has varied between CMake releases: the 4.x on the machine
# the port was written on packaged them where they were written, and the 3.28
# on ubuntu-24.04 - the first CI packaging run, on the ARM runner - did not,
# which the install check caught as a missing /etc/hmailserver/hMailServer.ini.
# With DESTDIR set, every install rule runs exactly as `make install` would
# into the staging tree, and the layout is the one the rules say.
set(CPACK_SET_DESTDIR ON)
set(CPACK_STRIP_FILES ON)

# The architecture each packager names differently for the same machine.
if(CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
   set(CPACK_DEBIAN_PACKAGE_ARCHITECTURE arm64)
   set(CPACK_RPM_PACKAGE_ARCHITECTURE aarch64)
else()
   set(CPACK_DEBIAN_PACKAGE_ARCHITECTURE amd64)
   set(CPACK_RPM_PACKAGE_ARCHITECTURE x86_64)
endif()

set(CPACK_DEBIAN_FILE_NAME DEB-DEFAULT)
set(CPACK_DEBIAN_PACKAGE_SECTION mail)
set(CPACK_DEBIAN_PACKAGE_SHLIBDEPS ON)
# An upgrade must not overwrite the configuration. CPack's DEB generator writes no
# conffiles list of its own, so one is generated here and handed to dpkg with the
# maintainer scripts; the RPM equivalent is the %config(noreplace) list below.
# Without these two, upgrading replaces /etc/hmailserver/hMailServer.ini and takes
# the database credentials with it.
file(WRITE ${CMAKE_CURRENT_BINARY_DIR}/packaging/conffiles
   "/etc/hmailserver/hMailServer.ini
/etc/logrotate.d/hmailserver
/etc/fail2ban/filter.d/hmailserver.conf
/etc/fail2ban/jail.d/hmailserver.conf
")

set(CPACK_RPM_USER_FILELIST
   "%config(noreplace) /etc/hmailserver/hMailServer.ini"
   "%config(noreplace) /etc/logrotate.d/hmailserver"
   "%config(noreplace) /etc/fail2ban/filter.d/hmailserver.conf"
   "%config(noreplace) /etc/fail2ban/jail.d/hmailserver.conf")

set(CPACK_DEBIAN_PACKAGE_CONTROL_EXTRA
   "${SERVER_DIR}/platform/packaging/debian/postinst;${SERVER_DIR}/platform/packaging/debian/prerm;${SERVER_DIR}/platform/packaging/debian/postrm;${CMAKE_CURRENT_BINARY_DIR}/packaging/conffiles")

set(CPACK_RPM_FILE_NAME RPM-DEFAULT)
set(CPACK_RPM_PACKAGE_GROUP "Applications/Internet")
set(CPACK_RPM_PACKAGE_LICENSE "AGPL-3.0-or-later")
set(CPACK_RPM_POST_INSTALL_SCRIPT_FILE ${SERVER_DIR}/platform/packaging/rpm/post)
set(CPACK_RPM_PRE_UNINSTALL_SCRIPT_FILE ${SERVER_DIR}/platform/packaging/rpm/preun)
# Without a %postun, rpm -e leaves systemd holding a unit file that is gone.
set(CPACK_RPM_POST_UNINSTALL_SCRIPT_FILE ${SERVER_DIR}/platform/packaging/rpm/postun)
# /usr/lib/systemd/system and /etc/logrotate.d belong to other packages.
set(CPACK_RPM_EXCLUDE_FROM_AUTO_FILELIST_ADDITION
   /usr/lib/systemd /usr/lib/systemd/system /etc/logrotate.d)

set(CPACK_GENERATOR "DEB;RPM")
include(CPack)

# ---------------------------------------------------------------- the census

add_custom_target(tu-census
   COMMAND ${CMAKE_COMMAND} -E env SERVER=${SERVER_DIR}
           bash ${SERVER_DIR}/../../../build/linux-tu-census.sh
   COMMENT "Counting the core translation units a POSIX compiler accepts"
   USES_TERMINAL)
