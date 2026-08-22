# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
Grant modify permissions for the hMailServer installation folder.

This script will relaunch itself elevated if not already running as Administrator.
It grants 'Everyone' Modify permissions recursively to:
  C:\Program Files (x86)\hMailServer
  C:\Program Files\hMailServer

Use this before running the test suite if your tests need write access to the
installation directory. Run with care — granting 'Everyone' write access is
insecure on multi-user machines. Prefer running tests in a dedicated test VM.
#>

$targets = @(
    'C:\Program Files (x86)\hMailServer',
    'C:\Program Files\hMailServer'
)

function Test-IsElevated {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsElevated)) {
    Write-Host "Not running elevated - relaunching as Administrator..."
    try {
        # -Wait and the child's exit code, because the bare "exit" that used to be
        # here returned 0 whatever happened: a declined UAC prompt, or an icacls
        # failure inside the elevated run, both reported success to whatever called
        # this script.
        $child = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath `
            -Verb RunAs -WindowStyle Normal -Wait -PassThru
        exit $child.ExitCode
    } catch {
        Write-Error "Failed to elevate. Aborting."
        exit 1
    }
}

$exitCode = 0

foreach ($target in $targets) {
    if (-not (Test-Path -Path $target)) {
        Write-Host "Target path not found, skipping: $target"
        continue
    }

    Write-Host "Applying permissions to: $target"

    $cmd = "icacls `"$target`" /grant Everyone:(OI)(CI)M /T"
    Write-Host $cmd

    $proc = Start-Process -FilePath 'icacls' -ArgumentList @("`"$target`"", "/grant", "Everyone:(OI)(CI)M", "/T") -NoNewWindow -Wait -PassThru

    if ($proc.ExitCode -eq 0) {
        Write-Host "Permissions applied successfully."
    } else {
        Write-Error "icacls exited with code $($proc.ExitCode) for: $target"
        $exitCode = $proc.ExitCode
    }
}

exit $exitCode
