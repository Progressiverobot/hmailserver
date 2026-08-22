# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
   Checks the database schema upgrade chain against itself.

   A schema upgrade that half-applies and reports success is the worst defect
   this area has, and it does not need a bug in DBUpdater to happen - it only
   needs the five hand-maintained lists that describe the chain to disagree by
   one:

     * REQUIRED_DB_VERSION in Server\Common\Application\Constants.h
     * the upgrade step table in Tools\DBUpdater\formMain.cs (LoadScripts)
     * the version names in the same file (GetDatabaseVersionName)
     * the Upgrade<from>to<to><dialect>.sql files in DBScripts, four per step
     * the schema probes in Tools\DBUpdater\SchemaVerification.cs

   Every one of those is edited by hand, by a different person, on the day a
   column is added. Nothing was checking them. This checks them.

   The check that matters most is the [IGNORE-ERRORS] one. Three of the four
   dialects mark their ALTER TABLE statements with that marker so that re-running
   a step is harmless, but the DAL matches the marker anywhere in the statement
   and then discards *every* error - while the "update hm_dbversion" statement in
   the same script carries no marker and always runs. A marked ALTER that fails
   for a real reason therefore stamps the new version onto a database that does
   not have the column. DBUpdater now proves each such step with a probe before
   it commits; this script is what makes sure a probe exists to run.

   Run it after adding an upgrade step, and before tagging a release.
   -SelfTest checks the checks: it feeds each rule a model containing exactly the
   fault the rule exists to find, and fails if the rule does not find it.
#>

[CmdletBinding()]
param(
   [string] $RepoRoot = (Get-Item $PSScriptRoot).Parent.FullName,
   [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

$dialects = @('MSSQL', 'MySQL', 'PGSQL', 'MSSQLCE')

# Steps before this one predate PostgreSQL and SQL CE support and ship MSSQL and
# MySQL only. The number is the "From" of the first step that ships all four.
$allDialectsFrom = 5001

function Get-StepKey
{
   param([int] $From, [int] $To)

   return ('{0}->{1}' -f $From, $To)
}

# ---------------------------------------------------------------------------
# Reading the tree into a model. Everything below this point works on the model
# alone, which is what lets -SelfTest exercise the rules without a tree.
# ---------------------------------------------------------------------------
function Get-SchemaModel
{
   param([string] $Root)

   $constants = Join-Path $Root 'hmailserver\source\Server\Common\Application\Constants.h'
   $formMain  = Join-Path $Root 'hmailserver\source\Tools\DBUpdater\formMain.cs'
   $probesCs  = Join-Path $Root 'hmailserver\source\Tools\DBUpdater\SchemaVerification.cs'
   $scriptDir = Join-Path $Root 'hmailserver\source\DBScripts'

   foreach ($required in $constants, $formMain, $probesCs)
   {
      if (-not (Test-Path -LiteralPath $required))
      {
         throw ('Cannot check the schema chain: {0} not found.' -f $required)
      }
   }

   $constantsText = Get-Content -Raw -LiteralPath $constants
   $formMainText  = Get-Content -Raw -LiteralPath $formMain
   $probesText    = Get-Content -Raw -LiteralPath $probesCs

   $requiredVersion = 0
   if ($constantsText -match '(?m)^\s*#define\s+REQUIRED_DB_VERSION\s+(\d+)')
   {
      $requiredVersion = [int] $Matches[1]
   }

   $steps = @()
   foreach ($match in [regex]::Matches($formMainText, 'new\s+UpgradeScript\(\s*(\d+)\s*,\s*(\d+)\s*\)'))
   {
      $steps += @{ From = [int] $match.Groups[1].Value; To = [int] $match.Groups[2].Value }
   }

   # "case 6006:" in GetDatabaseVersionName - the versions the UI can name.
   $namedVersions = @()
   $nameBody = ''
   if ($formMainText -match '(?s)GetDatabaseVersionName\s*\(\s*int\s+version\s*\)(.+?)\r?\n      \}')
   {
      $nameBody = $Matches[1]
   }
   foreach ($match in [regex]::Matches($nameBody, '(?m)^\s*case\s+(\d+)\s*:'))
   {
      $namedVersions += [int] $match.Groups[1].Value
   }

   $probes = @()
   foreach ($match in [regex]::Matches($probesText, 'new\s+SchemaProbe\(\s*(\d+)\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\)'))
   {
      $describes = $match.Groups[2].Value
      $parts = $describes -split '\.', 2

      $probes += @{
         Version   = [int] $match.Groups[1].Value
         Describes = $describes
         Table     = $parts[0]
         Column    = if ($parts.Count -gt 1) { $parts[1] } else { '' }
         Statement = $match.Groups[3].Value
      }
   }

   $scripts = @{}
   foreach ($step in $steps)
   {
      $perDialect = @{}

      foreach ($dialect in $dialects)
      {
         $path = Join-Path $scriptDir ('Upgrade{0}to{1}{2}.sql' -f $step.From, $step.To, $dialect)

         if (-not (Test-Path -LiteralPath $path))
         {
            $perDialect[$dialect] = @{ Exists = $false }
            continue
         }

         $text = Get-Content -Raw -LiteralPath $path

         # Both shapes count as stamping the version: the very first step creates
         # hm_dbversion and inserts into it, every later step updates it.
         $stamped = @()
         foreach ($m in [regex]::Matches($text, '(?is)update\s+hm_dbversion\s+set\s+value\s*=\s*(\d+)'))
         {
            $stamped += [int] $m.Groups[1].Value
         }
         foreach ($m in [regex]::Matches($text, '(?is)insert\s+into\s+hm_dbversion\s*\([^)]*\)\s*values\s*\(\s*(\d+)\s*\)'))
         {
            $stamped += [int] $m.Groups[1].Value
         }

         $perDialect[$dialect] = @{
            Exists       = $true
            Stamped      = $stamped
            IgnoreErrors = ($text -match '\[IGNORE-ERRORS\]')
         }
      }

      $scripts[(Get-StepKey $step.From $step.To)] = $perDialect
   }

   $createText = @{}
   foreach ($pair in @{ MSSQL = 'CreateTablesMSSQL.sql'; MySQL = 'CreateTablesMYSQL.sql'; PGSQL = 'CreateTablesPGSQL.sql' }.GetEnumerator())
   {
      $path = Join-Path $scriptDir $pair.Value
      $createText[$pair.Key] = if (Test-Path -LiteralPath $path) { Get-Content -Raw -LiteralPath $path } else { '' }
   }

   return @{
      RequiredVersion = $requiredVersion
      Steps           = $steps
      NamedVersions   = $namedVersions
      Probes          = $probes
      Scripts         = $scripts
      CreateText      = $createText
   }
}

# ---------------------------------------------------------------------------
# The rules.
# ---------------------------------------------------------------------------
function Test-SchemaModel
{
   param([hashtable] $Model)

   $problems = @()
   $steps = @($Model.Steps)

   if ($steps.Count -eq 0)
   {
      return @('No upgrade steps found in DBUpdater''s LoadScripts - the version table could not be read.')
   }

   # 1. Nothing may move sideways or backwards, and no version may be the start
   #    of two different steps: GetScriptUpgradingFrom takes the first match, so
   #    a duplicate silently wins and the other step is never applied.
   $byFrom = @{}
   foreach ($step in $steps)
   {
      if ($step.To -le $step.From)
      {
         $problems += ('Upgrade step {0} does not move forward; the upgrade path walk would not terminate.' -f (Get-StepKey $step.From $step.To))
      }

      $key = [string] $step.From
      if ($byFrom.ContainsKey($key))
      {
         $problems += ('Two upgrade steps start at version {0} ({1} and {2}); only the first is ever used.' -f $step.From, $byFrom[$key], (Get-StepKey $step.From $step.To))
      }
      else
      {
         $byFrom[$key] = (Get-StepKey $step.From $step.To)
      }
   }

   # 2. The chain has to be walkable from 0 and has to consume every registered
   #    step - an unreachable step is a script that will never run.
   $reachable = @{ '0' = $true }
   $current = 0
   $walked = 0
   while ($byFrom.ContainsKey([string] $current) -and $walked -le $steps.Count)
   {
      $next = ($steps | Where-Object { $_.From -eq $current } | Select-Object -First 1).To
      $current = $next
      $reachable[[string] $current] = $true
      $walked++
   }

   $terminus = $current

   foreach ($step in $steps)
   {
      if (-not $reachable.ContainsKey([string] $step.From))
      {
         $problems += ('Upgrade step {0} is not reachable by walking the chain from 0; its script would never run.' -f (Get-StepKey $step.From $step.To))
      }
   }

   # 3. The chain has to end exactly where the server insists on being.
   if ($Model.RequiredVersion -eq 0)
   {
      $problems += 'REQUIRED_DB_VERSION could not be read from Constants.h.'
   }
   elseif ($terminus -ne $Model.RequiredVersion)
   {
      $problems += ('REQUIRED_DB_VERSION is {0} but DBUpdater''s chain ends at {1}. The server would refuse to start against a database DBUpdater considers fully upgraded.' -f $Model.RequiredVersion, $terminus)
   }

   # 4. Every step needs its scripts, and every version in the chain needs a name
   #    or the tool shows the operator a bare number.
   foreach ($step in $steps)
   {
      $key = Get-StepKey $step.From $step.To
      $perDialect = $Model.Scripts[$key]

      if ($null -eq $perDialect)
      {
         $problems += ('No script information for upgrade step {0}.' -f $key)
         continue
      }

      foreach ($dialect in $dialects)
      {
         $expected = ($dialect -in @('MSSQL', 'MySQL')) -or ($step.From -ge $allDialectsFrom)
         $info = $perDialect[$dialect]
         $exists = ($null -ne $info) -and $info.Exists

         if ($expected -and -not $exists)
         {
            $problems += ('Upgrade{0}to{1}{2}.sql is missing; DBUpdater refuses the whole upgrade path when a file it needs is absent.' -f $step.From, $step.To, $dialect)
            continue
         }

         if (-not $exists)
         {
            continue
         }

         # 5. The script has to stamp its own target version, exactly once. A
         #    script that stamps the wrong number sends the next run down the
         #    wrong branch of the chain; one that stamps nothing makes the step
         #    repeat forever.
         $stamped = @($info.Stamped)
         if ($stamped.Count -ne 1)
         {
            $problems += ('Upgrade{0}to{1}{2}.sql sets hm_dbversion {3} times; it must do so exactly once.' -f $step.From, $step.To, $dialect, $stamped.Count)
         }
         elseif ($stamped[0] -ne $step.To)
         {
            $problems += ('Upgrade{0}to{1}{2}.sql stamps hm_dbversion with {3}, not {1}.' -f $step.From, $step.To, $dialect, $stamped[0])
         }

         # 6. The one that matters. A marked script cannot report its own
         #    failure, so something else has to prove the step happened.
         if ($info.IgnoreErrors)
         {
            $hasProbe = @($Model.Probes | Where-Object { $_.Version -eq $step.To }).Count -gt 0

            if (-not $hasProbe)
            {
               $problems += ('Upgrade{0}to{1}{2}.sql carries [IGNORE-ERRORS], which discards every error, but no probe in SchemaVerification.cs proves version {1} was reached. A failed ALTER there would stamp {1} onto a database that does not have the change, and DBUpdater would report success.' -f $step.From, $step.To, $dialect)
            }
         }
      }

      foreach ($version in $step.From, $step.To)
      {
         if ($Model.NamedVersions -notcontains $version)
         {
            $problems += ('GetDatabaseVersionName has no case for version {0}, so the upgrade window shows a bare number for it.' -f $version)
         }
      }
   }

   # 7. A column a probe asserts after an upgrade has to be in the create-tables
   #    script too, or a fresh install and an upgraded install have different
   #    schemas - and the probe would fail on the fresh one.
   foreach ($probe in $Model.Probes)
   {
      if (-not $probe.Column)
      {
         continue
      }

      foreach ($dialect in @('MSSQL', 'MySQL', 'PGSQL'))
      {
         $text = $Model.CreateText[$dialect]

         if ([string]::IsNullOrEmpty($text))
         {
            $problems += ('CreateTables for {0} could not be read, so probe {1} cannot be checked against a fresh install.' -f $dialect, $probe.Describes)
            continue
         }

         if ($text -notmatch ('\b' + [regex]::Escape($probe.Column) + '\b'))
         {
            $problems += ('Probe asserts {0} after upgrade, but the {1} create-tables script does not create it - a fresh install would fail the same probe.' -f $probe.Describes, $dialect)
         }
      }
   }

   return $problems
}

# ---------------------------------------------------------------------------
# -SelfTest: every rule above gets a model built to break it. A rule that stops
# working is worth nothing, and this is cheap.
# ---------------------------------------------------------------------------
function New-ModelForSelfTest
{
   # A minimal healthy model: two steps, all four scripts present for the second,
   # one probe, and create-tables text containing the probed column.
   $model = @{
      RequiredVersion = 6006
      Steps           = @(@{ From = 0; To = 5001 }, @{ From = 5001; To = 6006 })
      NamedVersions   = @(0, 5001, 6006)
      Probes          = @(@{ Version = 6006; Describes = 'hm_t.hm_c'; Table = 'hm_t'; Column = 'hm_c'; Statement = 'select hm_c from hm_t where 1 = 0' })
      Scripts         = @{
         '0->5001'    = @{ MSSQL = @{ Exists = $true; Stamped = @(5001); IgnoreErrors = $false }
                           MySQL = @{ Exists = $true; Stamped = @(5001); IgnoreErrors = $false }
                           PGSQL = @{ Exists = $false }
                           MSSQLCE = @{ Exists = $false } }
         '5001->6006' = @{ MSSQL = @{ Exists = $true; Stamped = @(6006); IgnoreErrors = $true }
                           MySQL = @{ Exists = $true; Stamped = @(6006); IgnoreErrors = $false }
                           PGSQL = @{ Exists = $true; Stamped = @(6006); IgnoreErrors = $false }
                           MSSQLCE = @{ Exists = $true; Stamped = @(6006); IgnoreErrors = $false } }
      }
      CreateText      = @{ MSSQL = 'create table hm_t ( hm_c int )'; MySQL = 'create table hm_t ( hm_c int )'; PGSQL = 'create table hm_t ( hm_c int )' }
   }

   return $model
}

function Invoke-SelfTest
{
   $cases = @(
      @{ Name = 'healthy model reports nothing'; Expect = ''; Break = { param($m) } }

      @{ Name = 'version table ends short of REQUIRED_DB_VERSION'
         Expect = 'chain ends at'
         Break = { param($m) $m.RequiredVersion = 6007 } }

      @{ Name = 'two steps starting at the same version'
         Expect = 'Two upgrade steps start at version'
         Break = { param($m)
            $m.Steps = @($m.Steps) + @{ From = 5001; To = 6006 }
            $m.Scripts['5001->6006_dup'] = $m.Scripts['5001->6006'] } }

      @{ Name = 'a step that does not move forward'
         Expect = 'does not move forward'
         Break = { param($m)
            $m.Steps = @(@{ From = 0; To = 5001 }, @{ From = 5001; To = 5001 })
            $m.RequiredVersion = 5001
            $m.Scripts['5001->5001'] = $m.Scripts['5001->6006'] } }

      @{ Name = 'a step nothing leads to'
         Expect = 'not reachable by walking the chain'
         Break = { param($m)
            $m.Steps = @($m.Steps) + @{ From = 9000; To = 9001 }
            $m.NamedVersions = @($m.NamedVersions) + 9000 + 9001
            $m.Scripts['9000->9001'] = $m.Scripts['5001->6006'] } }

      @{ Name = 'a required dialect script missing'
         Expect = 'is missing'
         Break = { param($m) $m.Scripts['5001->6006'].PGSQL = @{ Exists = $false } } }

      @{ Name = 'a script that stamps the wrong version'
         Expect = 'stamps hm_dbversion with'
         Break = { param($m) $m.Scripts['5001->6006'].MySQL.Stamped = @(6005) } }

      @{ Name = 'a script that stamps no version'
         Expect = 'sets hm_dbversion 0 times'
         Break = { param($m) $m.Scripts['5001->6006'].MySQL.Stamped = @() } }

      @{ Name = 'an [IGNORE-ERRORS] script with no probe'
         Expect = 'carries [IGNORE-ERRORS]'
         Break = { param($m) $m.Probes = @() } }

      @{ Name = 'a version with no name in the UI'
         Expect = 'has no case for version'
         Break = { param($m) $m.NamedVersions = @(0, 5001) } }

      @{ Name = 'a probed column absent from create-tables'
         Expect = 'a fresh install would fail the same probe'
         Break = { param($m) $m.CreateText.PGSQL = 'create table hm_t ( something_else int )' } }
   )

   $failures = 0

   foreach ($case in $cases)
   {
      $model = New-ModelForSelfTest
      & $case.Break $model

      $found = @(Test-SchemaModel $model)

      # Contains(), not -like: the most important expectation here is the literal
      # text "[IGNORE-ERRORS]", and -like reads square brackets as a character
      # class, so the rule that matters most is the one whose match would silently
      # never fire.
      $matched = if ($case.Expect -eq '') { $found.Count -eq 0 }
                 else { @($found | Where-Object { $_.Contains($case.Expect) }).Count -gt 0 }

      if ($matched)
      {
         Write-Host ('  OK    {0}' -f $case.Name) -ForegroundColor Green
      }
      else
      {
         Write-Host ('  FAIL  {0}' -f $case.Name) -ForegroundColor Red
         Write-Host ('        expected a problem containing "{0}"; got: {1}' -f $case.Expect, (($found -join ' | ') -replace '^$', '(nothing)')) -ForegroundColor Yellow
         $failures++
      }
   }

   return $failures
}

# ---------------------------------------------------------------------------
# Run.
# ---------------------------------------------------------------------------
if ($SelfTest)
{
   Write-Host 'check-schema-versions self test'
   $failures = Invoke-SelfTest

   Write-Host ''
   if ($failures -eq 0)
   {
      Write-Host 'Self test passed - every rule detects the fault it exists for.' -ForegroundColor Green
      exit 0
   }

   Write-Host ('Self test FAILED ({0} rule(s) no longer detect their fault).' -f $failures) -ForegroundColor Red
   exit 1
}

Write-Host 'Database schema chain consistency'

$model = Get-SchemaModel -Root $RepoRoot
$problems = @(Test-SchemaModel $model)

Write-Host ('  {0} upgrade steps, chain 0 -> {1}, REQUIRED_DB_VERSION {2}, {3} schema probe(s)' -f
   @($model.Steps).Count,
   (@($model.Steps) | Measure-Object -Property To -Maximum).Maximum,
   $model.RequiredVersion,
   @($model.Probes).Count)

if ($problems.Count -eq 0)
{
   Write-Host '  OK    the version table, the scripts, the version names and the probes all agree' -ForegroundColor Green
   exit 0
}

foreach ($problem in $problems)
{
   Write-Host ('  FAIL  ' + $problem) -ForegroundColor Red
}

Write-Host ''
Write-Host ("{0} problem(s). A disagreement here is how a schema upgrade half-applies and reports success." -f $problems.Count) -ForegroundColor Red

exit 1
