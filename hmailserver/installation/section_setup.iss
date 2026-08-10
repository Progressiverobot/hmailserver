[Setup]
AppName=hMailServer
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
; Windows 7 SP1. Inno Setup 6 refuses anything below 6.1, and the old Vista SP1
; floor was fiction anyway: this is an x64-only build whose Control Panel and
; setup tools need the .NET 8 Desktop Runtime (bundled, installed when
; missing) and whose binaries link the VC++ v145 runtime.
MinVersion=6.1sp1
