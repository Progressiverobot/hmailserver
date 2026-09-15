# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

# The Chocolatey uninstall script. It is NOT a template - nothing in it changes
# from version to version, so build/make-package-manifests.ps1 copies it as it is.
#
# The installer is Inno Setup, so the uninstaller is unins000.exe in the
# installation directory and its path is in the uninstall registry key that
# section_setup.iss pins by name: AppId=hMailServer gives the key
# hMailServer_is1. Get-UninstallRegistryKey finds it by display name, which
# covers an installation made by the installer rather than by this package.
#
# What is deliberately NOT done here: deleting the message store, the database or
# the configuration. The installer's uninstaller leaves them, and so does this -
# an administrator removing a package should not lose the mail.

$ErrorActionPreference = 'Stop'

$key = Get-UninstallRegistryKey -SoftwareName 'hMailServer*'

if (-not $key) {
    Write-Warning 'hMailServer is not in the uninstall registry; nothing to uninstall.'
    return
}

if (@($key).Count -gt 1) {
    Write-Warning "$(@($key).Count) matches for 'hMailServer*' in the uninstall registry:"
    @($key) | ForEach-Object { Write-Warning "  $($_.DisplayName) $($_.DisplayVersion)" }
    Write-Warning 'Uninstall the one you mean by hand; this package will not guess.'
    return
}

$file = $key.UninstallString.Trim('"')

$packageArgs = @{
    packageName    = 'hmailserver'
    fileType       = 'exe'
    file           = $file
    silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    validExitCodes = @(0)
}

Uninstall-ChocolateyPackage @packageArgs

Write-Host ''
Write-Host 'hMailServer is uninstalled. The message store, the database and hMailServer.ini'
Write-Host 'were left where they are - delete them by hand if that is what you want.'
