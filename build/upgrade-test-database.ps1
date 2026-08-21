<#
   Applies the pending schema upgrade scripts to the regression bench's SQL
   Server Compact database.

   WHY THIS EXISTS. Every coordinated schema bump needs the bench database moved
   forward too, or the version pin in Application::OnDatabaseConnected refuses
   the connection and the whole suite fails with an error about DBUpdater rather
   than about whatever was being tested. That had been done by hand four times
   before this script existed, and by hand it is slow and easy to get subtly
   wrong - the password is DPAPI-protected, the provider name is not the obvious
   one, and SQL Server Compact rejects a multi-statement command, so the script
   has to be split exactly the way the server splits it.

   DBUpdater.exe cannot be used here: it drives the upgrade through the COM API
   against a running service, and the service will not start against a database
   whose version it has just been taught to reject. So this talks to the file
   directly, the same way DBUpdater's own SQL runner does.

   WHAT IT DOES NOT DO. It does not create a database, it does not run the
   CREATE scripts, and it will not move a database backwards. It applies
   UpgradeNNNNtoMMMMMSSQLCE.sql in order, from whatever version the file is at
   to REQUIRED_DB_VERSION, and stops at the first statement that fails.

   Run it from Windows PowerShell, not pwsh 7: the SQL Server Compact OLE DB
   provider is not loadable from the newer host.
#>

[CmdletBinding()]
param(
   [string] $IniPath = 'C:\Program Files\hMailServer\Bin\hMailServer.ini',
   [string] $DatabasePath = 'C:\Program Files\hMailServer\Database\hMailServer.sdf',
   [string] $ScriptDirectory,
   [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not bound when a param default is evaluated under -File,
# so the two script-relative paths are resolved here instead.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ScriptDirectory) { $ScriptDirectory = Join-Path $root '..\hmailserver\source\DBScripts' }

function Get-IniValue([string] $path, [string] $section, [string] $key)
{
   $inSection = $false
   foreach ($line in Get-Content -LiteralPath $path)
   {
      $trimmed = $line.Trim()
      if ($trimmed -match '^\[(.+)\]$') { $inSection = ($matches[1] -eq $section); continue }
      if (-not $inSection) { continue }
      if ($trimmed -match "^$([regex]::Escape($key))=(.*)$") { return $matches[1] }
   }
   return $null
}

# The stored password is a DPAPI blob, machine-scoped and with no entropy - the
# same shape Crypt::ProtectSecret writes. Unprotecting it needs no secret of our
# own, only the right scope; LocalMachine, because the service runs as
# LocalSystem and CurrentUser would fail with a misleading "key not valid".
function Unprotect-DatabasePassword([string] $base64)
{
   if ([string]::IsNullOrWhiteSpace($base64)) { return '' }

   Add-Type -AssemblyName System.Security
   $bytes = [Convert]::FromBase64String($base64)
   $clear = [System.Security.Cryptography.ProtectedData]::Unprotect(
      $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)

   return [System.Text.Encoding]::Unicode.GetString($clear)
}

# The server splits a script into commands on a BLANK LINE, not on a semicolon -
# see SQLScriptParser. Splitting any other way is what produced an installer
# that could not create its own database, so this mirrors it exactly.
function Split-ScriptStatements([string] $text)
{
   $normalised = $text -replace "`r`n", "`n"
   return $normalised -split "`n`n" |
      ForEach-Object { $_.Trim() } |
      Where-Object { $_.Length -gt 0 -and -not $_.StartsWith('--') }
}

if (-not (Test-Path -LiteralPath $DatabasePath))
{
   throw "No database at $DatabasePath."
}

$requiredHeader = Join-Path $root '..\hmailserver\source\Server\Common\Application\Constants.h'
$requiredMatch = Select-String -LiteralPath $requiredHeader -Pattern 'define\s+REQUIRED_DB_VERSION\s+(\d+)'
if (-not $requiredMatch) { throw 'Could not read REQUIRED_DB_VERSION from Constants.h.' }
$required = [int] $requiredMatch.Matches[0].Groups[1].Value

$password = Unprotect-DatabasePassword (Get-IniValue $IniPath 'Database' 'Password')

# ADODB rather than System.Data.SqlServerCe: the managed provider needs its
# native components registered for the exact ADO.NET version, which this machine
# does not have, while the OLE DB provider is what the server itself uses.
$connectionString = "Provider=Microsoft.SQLSERVER.CE.OLEDB.4.0;Data Source=$DatabasePath;"
if ($password) { $connectionString += "SSCE:Database Password=$password;" }

$connection = New-Object -ComObject ADODB.Connection
$connection.Open($connectionString)

try
{
   $recordset = $connection.Execute('select value from hm_dbversion')
   $current = [int] $recordset.Fields.Item(0).Value
   $recordset.Close()

   Write-Host "Bench database is at $current; REQUIRED_DB_VERSION is $required."

   if ($current -eq $required)
   {
      Write-Host '  OK    already current - nothing to do.'
      return
   }

   if ($current -gt $required)
   {
      throw "The database ($current) is NEWER than this build requires ($required). This script does not move a database backwards."
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

      $statements = Split-ScriptStatements (Get-Content -LiteralPath $script.FullName -Raw)

      foreach ($statement in $statements)
      {
         # [IGNORE-ERRORS] marks a statement whose failure is expected - Compact
         # Edition has no IF NOT EXISTS, so the scripts rely on it for anything
         # that may already be present.
         $ignoreErrors = $statement -match '\[IGNORE-ERRORS\]'
         $sql = ($statement -replace '---?\s*\[IGNORE-ERRORS\]', '').Trim()

         if (-not $sql) { continue }

         try
         {
            $connection.Execute($sql) | Out-Null
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
   }
   else
   {
      $recordset = $connection.Execute('select value from hm_dbversion')
      $verified = [int] $recordset.Fields.Item(0).Value
      $recordset.Close()

      if ($verified -ne $required)
      {
         throw "Applied $applied script(s) but the database reports $verified, not $required."
      }

      Write-Host "Applied $applied script(s). The bench database is now at $verified."
   }
}
finally
{
   $connection.Close()
}
