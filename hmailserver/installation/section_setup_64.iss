[Setup]
OutputBaseFilename=hMailServer-6.2.24-x64
AppVerName=hMailServer 6.2.24-x64
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
AppVersion=6.2.24
; VersionInfoVersion is <version>.0 on purpose. The build number (HMAILSERVER_BUILD in
; Version.h) travels in Version.h and the release tag; the installer and the seven .csproj
; files carry the version alone, so a build-only re-cut never has to touch them. RELEASE.md, step 7.
VersionInfoVersion=6.2.24.0
