# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

# Downloads the .NET Desktop Runtime installer that the hMailServer installer
# bundles for the Control Panel component. The file is not kept in the repository
# (55+ MB, and .gitignore excludes the whole DotNet folder); run this once before
# building the installer.
#
# Keep the version here in step with three other places, or the installer will
# either ship the wrong runtime or decide it does not need to install one:
#   * <TargetFramework> in the nine .csproj files under source/Tools
#   * global.json (the SDK pin)
#   * hMailServerInnoExtension.iss -> DotNetDesktopMissing(), which globs
#     Microsoft.WindowsDesktop.App\<major>.* to decide whether to run this
#   * section_files_64.iss and section_run.iss, which reference the file by name
param(
    [string] $Channel = '10.0'
)

$dest = Join-Path $PSScriptRoot '..\hmailserver\installation\DotNet'
New-Item -ItemType Directory -Force $dest | Out-Null
$file = Join-Path $dest ("windowsdesktop-runtime-{0}-win-x64.exe" -f $Channel)

if (Test-Path $file) {
    Write-Host "already present: $file"
    return
}

Write-Host ("downloading .NET {0} Desktop Runtime (x64)..." -f $Channel)
Invoke-WebRequest -Uri ("https://aka.ms/dotnet/{0}/windowsdesktop-runtime-win-x64.exe" -f $Channel) -OutFile $file

$size = (Get-Item $file).Length
if ($size -lt 40MB) {
    # aka.ms redirects; a short file means we saved an error page rather than the
    # installer, which would otherwise fail silently at install time.
    Remove-Item $file -Force
    throw ("downloaded file was only {0:N1} MB - expected 50+ MB. The aka.ms link for channel {1} may have moved." -f ($size / 1MB), $Channel)
}

Write-Host ("downloaded {0} ({1:N1} MB)" -f $file, ($size / 1MB))
