#define HMAILSERVERLIBS = GetEnv("hMailServerLibs")
#define OPENSSL_LIBS_PATH HMAILSERVERLIBS + "\openssl-4.0.1\out64\bin"
#define POSTGRESQL_LIBPQ_PATH HMAILSERVERLIBS + "\postgresql-18.3\builddir\src\interfaces\libpq"

; The bundled .NET Desktop Runtime, defined in one place because it used to be
; spelled out in four and they drifted: build\get-dotnet-runtime.ps1 names the
; channel, section_files_64.iss and section_run.iss name the file, and
; DotNetDesktopMissing() in hMailServerInnoExtension.iss globs the installed
; shared framework to decide whether to run it. When those disagree the
; installer either ships the wrong runtime or decides it does not need one - and
; the second failure is the dangerous one, because DBSetup/DBSetupQuick/DBUpdater
; are .NET apps that run during installation. If they cannot start, the new
; server binary ends up against the old database schema.
; DOTNET_MAJOR is what the probe matches: a framework-dependent .NET app rolls
; forward across minor and patch versions but never across a major version.
#define DOTNET_MAJOR "10"
#define DOTNET_CHANNEL "10.0"
#define DOTNET_RUNTIME_FILE "windowsdesktop-runtime-" + DOTNET_CHANNEL + "-win-x64.exe"

#include "section_setup.iss"
#include "section_setup_64.iss"
#include "section_custom_messages.iss"
#include "section_languages.iss"
#include "section_istool.iss"
#include "section_types.iss"
#include "section_components.iss"

#include "section_files_common.iss"

#include "section_files_64.iss"

#include "section_messages.iss"
#include "section_ini.iss"
#include "section_registry.iss"
#include "section_dirs.iss"
#include "section_run.iss"
#include "section_uninstallrun.iss"

#include "section_icons.iss"

#include "hMailServerInnoExtension.iss"

