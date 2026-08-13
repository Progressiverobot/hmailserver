[Setup]
AppName=hMailServer
; Pinned rather than left to default to AppName. The uninstall registry key is
; "<AppId>_is1", and hMailServerInnoExtension.iss reads
; ...\Uninstall\hMailServer_is1\InstallLocation by name in two places (GetInifile
; and OverrideInstallationFolder, the latter being how an x86-to-x64 upgrade finds
; the existing installation directory). This value produces exactly the key those
; already use; changing AppName without it would silently break both.
AppId=hMailServer
AppCopyright=Copyright (C) 2026
DefaultDirName={pf}\hMailServer
DefaultGroupName=hMailServer
PrivilegesRequired=admin
SolidCompression=yes
WizardImageFile=setup.bmp
WizardSmallImageFile=setup-small.bmp
LicenseFile=license.rtf
AllowNoIcons=yes
Uninstallable=true
DirExistsWarning=no
CreateAppDir=true
; Windows 10 1607 (build 14393): the floor of the .NET 10 Desktop Runtime,
; which the Control Panel and the setup tools require (bundled, installed
; when missing - its installer refuses anything older, which would leave the
; server without a database). The binaries link the VC++ v145 runtime.
MinVersion=10.0.14393
