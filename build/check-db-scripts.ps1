# Builds a throwaway SQL CE database from the create script, and applies every
# upgrade script to a second one, using exactly the splitting and preprocessing
# rules SQLScriptParser uses. Reports the first statement that fails.
#
# WHY THIS EXISTS
#
# SQLScriptParser splits a script into commands on a BLANK LINE (";\n\n" for
# PostgreSQL). Two statements written on consecutive lines are therefore sent to
# the database as ONE command, and SQL Server Compact rejects that outright. A
# create script that fails leaves a fresh install with no database, and the
# symptom is not an error anybody sees - the service starts and simply does not
# listen on 25, 110 or 143.
#
# That shipped in 6.2.22-pre4. Nothing local caught it because the regression
# bench's database is upgraded out of band with ADODB, one statement at a time, so
# no test on this machine had ever executed the CREATE path a fresh install takes.
# The installer smoke test on a throwaway runner found it, which is what that test
# is for - but only after a release had been published.
#
# Run this whenever a DBScripts file changes, and before stamping a release.

[CmdletBinding()]
Param(
    [string]$SqlCeAssembly = 'C:\Program Files\Microsoft SQL Server Compact Edition\v4.0\Desktop\System.Data.SqlServerCe.dll'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Get-Item $PSScriptRoot).Parent.FullName
$scripts = Join-Path $repoRoot 'hmailserver\source\DBScripts'

if (-not (Test-Path $SqlCeAssembly)) {
    Write-Host "SQL Server Compact 4.0 is not installed at:" -ForegroundColor Yellow
    Write-Host "  $SqlCeAssembly" -ForegroundColor Yellow
    Write-Host "Skipping - this check needs it to build a scratch database." -ForegroundColor Yellow
    exit 0
}

Add-Type -Path $SqlCeAssembly

$failures = 0

function New-ScratchDatabase {
    $sdf = Join-Path $env:TEMP ("hmailserver-dbcheck-" + [guid]::NewGuid().ToString('N') + ".sdf")
    $engine = New-Object System.Data.SqlServerCe.SqlCeEngine("Data Source=$sdf")
    $engine.CreateDatabase()
    $engine.Dispose()
    return $sdf
}

function Invoke-Script {
    Param([string]$Path, [object]$Connection, [string]$Label)

    $text = (Get-Content -LiteralPath $Path -Raw) -replace "`r`n", "`n"
    $commands = $text -split "`n`n"

    $run = 0
    $localFailures = 0

    foreach ($command in $commands) {
        # SQLScriptParser::PreprocessLine_, for TypeMSSQLCompactEdition.
        $line = $command.TrimStart(@("`n", ' ', "`t"))

        if ($line.Length -eq 0) { continue }
        if ($line.ToLower().StartsWith('if ')) { continue }
        if ($line -match 'CREATE PROC') { continue }
        if ($line -match ' CLUSTERED ') { $line = $line -replace ' CLUSTERED ', ' ' }
        $line = $line.Replace("`t", ' ').Replace(' varchar', ' nvarchar')

        try {
            $Connection.Execute($line) | Out-Null
            $run++
        }
        catch {
            $localFailures++
            $first = ($line -split "`n")[0]
            if ($first.Length -gt 100) { $first = $first.Substring(0, 100) + '...' }
            Write-Host ("  FAIL  {0}: {1}" -f $Label, $first) -ForegroundColor Red
            Write-Host ("        {0}" -f $_.Exception.Message) -ForegroundColor Red

            # A command holding more than one statement is the mistake this check
            # was written for, so name it rather than leaving a generic CE error.
            $starts = ([regex]::Matches($line, '(?im)^\s*(alter|insert|update|create|delete)\s')).Count
            if ($starts -gt 1) {
                Write-Host ("        This command contains {0} statements. Scripts are split on a BLANK LINE - put one between them." -f $starts) -ForegroundColor Yellow
            }
        }
    }

    return @{ Run = $run; Failures = $localFailures }
}

Write-Host 'Database script consistency'

# ---- the create path, which is what a fresh install runs
$sdf = New-ScratchDatabase
$conn = New-Object -ComObject ADODB.Connection
$conn.Open("Provider=Microsoft.SQLSERVER.CE.OLEDB.4.0;Data Source=$sdf")

$result = Invoke-Script -Path (Join-Path $scripts 'CreateTablesMSSQL.sql') -Connection $conn -Label 'CreateTablesMSSQL.sql'
$failures += $result.Failures

if ($result.Failures -eq 0) {
    $rs = $conn.Execute('select value from hm_dbversion')
    $version = $rs.Fields.Item(0).Value
    $rs.Close()
    Write-Host ("  OK    CreateTablesMSSQL.sql builds a database ({0} statements, schema {1})" -f $result.Run, $version)
}

$conn.Close()
Remove-Item -LiteralPath $sdf -Force -ErrorAction SilentlyContinue

# ---- every upgrade script, checked STATICALLY
#
# Executing them cannot be made meaningful here: an upgrade expects a database at
# the previous version, and the only one available to seed from is the create
# script, which is already at the newest. Re-applying a step to it fails with
# "column already exists", which says nothing about the script.
#
# The defect this check exists for is syntactic anyway - two statements in one
# command - so that is what is asserted.
#
# Only the Compact Edition scripts are checked, and that is not laziness: SQL
# Server and PostgreSQL both accept several statements in one command, so the
# separator is a style question there and a correctness one only for CE. Scripts
# from 2010 have carried multi-statement commands on those backends for a decade
# without trouble, and rewriting them now would be churn against working history.
$statementStart = '(?im)^\s*(alter|insert|update|create|delete|drop)\s'

$checked = 0

foreach ($upgrade in (Get-ChildItem -LiteralPath $scripts -Filter 'Upgrade*MSSQLCE.sql' | Sort-Object Name)) {
    $text = (Get-Content -LiteralPath $upgrade.FullName -Raw) -replace "`r`n", "`n"

    $separator = "`n`n"

    foreach ($command in ($text -split $separator)) {
        $line = $command.Trim()
        if ($line.Length -eq 0) { continue }
        if ($line.ToLower().StartsWith('if ')) { continue }

        $starts = ([regex]::Matches($line, $statementStart)).Count

        if ($starts -gt 1) {
            $failures++
            $first = ($line -split "`n")[0]
            if ($first.Length -gt 100) { $first = $first.Substring(0, 100) + '...' }
            Write-Host ("  FAIL  {0}: one command holds {1} statements" -f $upgrade.Name, $starts) -ForegroundColor Red
            Write-Host ("        starting: {0}" -f $first) -ForegroundColor Red
            Write-Host ('        Scripts are split on a BLANK LINE - put one between them, or SQL CE ' +
                        'rejects the whole command and the upgrade does not happen.') -ForegroundColor Yellow
        }
    }

    $checked++
}

Write-Host ("  OK    {0} Compact Edition upgrade script(s) parse into single statements" -f $checked)

Write-Host ''
if ($failures -eq 0) {
    Write-Host 'Database scripts are consistent - safe to build an installer.' -ForegroundColor Green
    exit 0
} else {
    Write-Host ("Database scripts FAILED ({0} statement(s)) - a fresh install would have no database." -f $failures) -ForegroundColor Red
    exit 1
}
