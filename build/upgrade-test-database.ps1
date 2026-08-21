<#
   Applies the pending schema upgrade scripts to the regression bench's
   database, through the running hMailServer service's COM API.

   WHY THIS EXISTS. Every coordinated schema bump needs the bench database moved
   forward too, or the version pin in Application::OnDatabaseConnected refuses
   the connection and the whole suite fails with an error about DBUpdater rather
   than about whatever was being tested. That had been done by hand four times
   before this script existed.

   WHY THROUGH COM, which is the part worth knowing. The database file lives
   under Program Files and is owned by the service account; an ordinary shell
   cannot open it for writing, and SQL Server Compact reports that as
   "Access to the database file is not allowed ... SeCreateFile" - or, through
   the OLE DB provider, as a bare E_FAIL that names nothing at all. The service
   runs as LocalSystem and already has the handle, so asking IT to run the
   statements needs no elevation and no UAC prompt. This is the same route
   DBUpdater.exe takes.

   The service must be RUNNING and still able to connect to the database, which
   means: run this BEFORE building a server whose REQUIRED_DB_VERSION has moved
   past what the bench holds. Once the new binary is in place the service will
   refuse the old database, and this script has nothing to talk to.

   WHAT IT DOES NOT DO. It does not create a database, it does not run the
   CREATE scripts, and it will not move a database backwards.
#>

[CmdletBinding()]
param(
   [string] $AdministratorPassword = 'testar',
   [string] $ScriptDirectory,
   [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not bound when a param default is evaluated under -File, so
# the script-relative paths are resolved here instead.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ScriptDirectory) { $ScriptDirectory = Join-Path $root '..\hmailserver\source\DBScripts' }

$requiredHeader = Join-Path $root '..\hmailserver\source\Server\Common\Application\Constants.h'
$requiredMatch = Select-String -LiteralPath $requiredHeader -Pattern 'define\s+REQUIRED_DB_VERSION\s+(\d+)'
if (-not $requiredMatch) { throw 'Could not read REQUIRED_DB_VERSION from Constants.h.' }
$required = [int] $requiredMatch.Matches[0].Groups[1].Value

# The server splits a script into commands on a BLANK LINE, not on a semicolon -
# see SQLScriptParser. Splitting any other way is what produced an installer that
# could not create its own database, so this mirrors it exactly.
function Split-ScriptStatements([string] $text)
{
   $normalised = $text -replace "`r`n", "`n"
   return $normalised -split "`n`n" |
      ForEach-Object { $_.Trim() } |
      Where-Object { $_.Length -gt 0 -and -not $_.StartsWith('--') }
}

$service = Get-Service hMailServer -ErrorAction SilentlyContinue
if (-not $service -or $service.Status -ne 'Running')
{
   throw 'The hMailServer service is not running. This script upgrades the database through it, so start it first - and note that a server built with a NEWER REQUIRED_DB_VERSION will refuse the old database, in which case restore the previous binary, run this, then rebuild.'
}

$application = New-Object -ComObject hMailServer.Application
if (-not $application.Authenticate('Administrator', $AdministratorPassword))
{
   throw 'COM authentication failed. Check the administrator password (the bench uses "testar").'
}

$current = [int] $application.Database.CurrentVersion

Write-Host "Bench database is at $current; this source tree requires $required."

if ($current -eq $required)
{
   Write-Host '  OK    already current - nothing to do.'
   return
}

if ($current -gt $required)
{
   throw "The database ($current) is NEWER than this source tree requires ($required). This script does not move a database backwards."
}

$applied = 0

while ($current -lt $required)
{
   $script = Get-ChildItem -LiteralPath $ScriptDirectory -Filter "Upgrade${current}to*MSSQLCE.sql" |
             Select-Object -First 1

   if (-not $script)
   {
      throw "No Compact Edition upgrade script from $current. The chain is broken - check-schema-versions.ps1 will say where."
   }

   if ($script.Name -notmatch "^Upgrade${current}to(\d+)MSSQLCE\.sql$")
   {
      throw "Could not read the target version out of $($script.Name)."
   }

   $target = [int] $matches[1]

   Write-Host "  -> $($script.Name)"

   if ($WhatIf)
   {
      $current = $target
      continue
   }

   foreach ($statement in Split-ScriptStatements (Get-Content -LiteralPath $script.FullName -Raw))
   {
      # [IGNORE-ERRORS] marks a statement whose failure is expected - Compact
      # Edition has no IF NOT EXISTS, so the scripts rely on it for anything that
      # may already be present.
      $ignoreErrors = $statement -match '\[IGNORE-ERRORS\]'
      $sql = ($statement -replace '---?\s*\[IGNORE-ERRORS\]', '').Trim()

      if (-not $sql) { continue }

      try
      {
         $application.Database.ExecuteSQL($sql)
      }
      catch
      {
         if (-not $ignoreErrors)
         {
            throw "Statement failed in $($script.Name):`n$sql`n`n$($_.Exception.Message)"
         }
      }
   }

   $current = $target
   $applied++
}

Write-Host ''

if ($WhatIf)
{
   Write-Host "Would have applied $applied script(s) to reach $required."
   return
}

$verified = [int] $application.Database.CurrentVersion

if ($verified -ne $required)
{
   throw "Applied $applied script(s) but the database reports $verified, not $required."
}

Write-Host "Applied $applied script(s). The bench database is now at $verified."
