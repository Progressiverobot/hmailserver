# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
   Re-registers hMailServer's COM type library and interfaces. MUST BE RUN
   ELEVATED.

   WHEN YOU NEED THIS. Any change to hMailServer.idl that adds a NEW interface -
   a new business object exposed over COM - leaves the regression suite unable to
   talk to it until this has run. Adding a property or a method to an interface
   that already exists does not, because the IID has not changed and the type
   library is read from the binary's own path.

   WHY IT NEEDS ELEVATION, which is the part that surprises people. hMailServer
   is an OUT-OF-PROCESS COM server: it runs as a Windows service under
   LocalSystem, and the test process runs as you. Every interface pointer
   crossing between them is marshalled, and the marshaller finds its marshaller
   by looking the IID up under HKEY_CLASSES_ROOT\Interface. That lookup happens
   on BOTH sides, so a per-user registration under HKCU is no use at all - the
   service would never see it. HKLM is the only place both parties read, and
   writing there needs administrative rights.

   The symptom when it has not been run is unmistakable once you have seen it:

       SetUp : System.Runtime.InteropServices.COMException :
               Interface not registered (Exception from HRESULT: 0x80040155)

   0x80040155 is REGDB_E_IIDNOTREG. It fires in the fixture's SETUP, so every
   test in the fixture errors rather than fails, and the message names no
   interface - which sends people looking at their own code first.

   AND BUILD RELEASE FIRST. The registered type library is a PATH, and it points
   at the Release binary. Registering after a Debug-only build registers whatever
   Release last contained - which is how an IDL change appears to have been
   registered and still is not there. Worse, MSBuild has been seen to skip MIDL
   for one configuration while running it for the other, so:

       build\build.ps1 -Configuration Release      # MIDL must actually re-run
       build\register-com.ps1                      # elevated
       build\build-tests.ps1                       # tlbimp reads the new typelib

   If the type library still looks stale, delete
   source\Server\hMailServer\hMailServer\x64\Release\hMailServer.tlb and build
   again - and delete the test project's Interop.hMailServer.dll and
   obj\...\RegressionTests.csproj.ResolveComReference.cache, because MSBuild
   caches the generated interop and will happily reuse yesterday's.
#>

[CmdletBinding()]
param(
   # /RegisterTypeLib registers the type library, the coclasses and the
   # interfaces. /Register does all of that AND installs the Windows service,
   # which is not wanted on a bench where the service already exists and is
   # pointed at the right binary.
   [switch] $IncludeService
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
   throw ('This must run elevated: it writes interface registrations under HKLM, and an ' +
          'unelevated write is refused. Re-run it from an administrator PowerShell.')
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root '..\hmailserver\source\Server\hMailServer\x64\Release\hMailServer.exe'

if (-not (Test-Path $exe))
{
   throw "No Release binary at $exe. Run build\build.ps1 -Configuration Release first - the registration records a path, and it records this one."
}

$binary = Get-Item $exe
Write-Host ("Registering {0} (built {1})" -f $binary.Name, $binary.LastWriteTime)

# The service holds its own binary open, so a registration that has to rewrite
# nothing still works - but stopping first keeps this consistent with the build
# step that usually precedes it.
$service = Get-Service hMailServer -ErrorAction SilentlyContinue
$wasRunning = $service -and $service.Status -eq 'Running'

if ($wasRunning)
{
   Write-Host '  stopping the service'
   Stop-Service hMailServer -Force
   Start-Sleep -Seconds 2
}

$argument = if ($IncludeService) { '/Register' } else { '/RegisterTypeLib' }
$process = Start-Process $exe -ArgumentList $argument -PassThru -Wait -WindowStyle Hidden

if ($process.ExitCode -ne 0)
{
   throw "$($binary.Name) $argument returned $($process.ExitCode). Registration failed."
}

Write-Host "  $argument succeeded"

if ($wasRunning)
{
   Write-Host '  starting the service'
   Start-Service hMailServer
}

# Proving it, rather than trusting the exit code: a registration that reports
# success and leaves the IID absent is exactly the state this script exists to
# get out of, and it is invisible until a test fails in its setup.
$expected = @{
   'IInterfaceBlockedSenders' = '{778D2305-46DD-40B6-A34F-4C4065FD2E9E}'
   'IInterfaceBlockedSender'  = '{F192BDA6-7B52-4969-8476-DF66F9A32AC7}'
}

$missing = @()

foreach ($name in $expected.Keys)
{
   if (-not (Test-Path ("HKLM:\SOFTWARE\Classes\Interface\" + $expected[$name])))
   {
      $missing += $name
   }
}

if ($missing.Count -gt 0)
{
   throw ("Registration reported success but these interfaces are still absent from HKLM: " +
          ($missing -join ', ') + ". The type library in the Release binary is probably stale - see the notes at the top of this script.")
}

Write-Host ''
Write-Host 'Registered. Rebuild the tests so tlbimp picks up the new type library:' -ForegroundColor Green
Write-Host '  build\build-tests.ps1'
