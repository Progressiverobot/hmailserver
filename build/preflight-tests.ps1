# Verifies this machine is in the known-good state for running the hMailServer
# regression suite, and names the exact fault when it is not. Every check here
# corresponds to a failure mode that has actually burned a run:
#   - the service pointing at a stray installer's copy instead of the repo build
#   - a stale InstallLocation registry value redirecting the server's config
#   - a deliberate error left in the ERROR log by an aborted run, which fails
#     every fixture's setup in the next run
#   - a bench database left behind by a release that moved REQUIRED_DB_VERSION,
#     which stops the service for a reason none of the other checks name
#   - clamd not listening, which fails the live-scanner tests
# Run with -Clean to remove a stale ERROR log instead of just reporting it.
Param(
    [switch]$Clean
)

$repoRoot = (Get-Item $PSScriptRoot).Parent.FullName
$expectedExe = Join-Path $repoRoot 'hmailserver\source\Server\hMailServer\x64\Release\hMailServer.exe'
$failures = 0

function Report([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) {
        Write-Host ("  OK    {0}" -f $name) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL  {0}" -f $name) -ForegroundColor Red
        if ($detail) { Write-Host ("        {0}" -f $detail) -ForegroundColor Yellow }
        $script:failures++
    }
}

Write-Host "hMailServer regression pre-flight" -ForegroundColor Cyan

# 1. Service must exist and run from the repo's Release output. A stray
#    installer re-points PathName; recovery is post-build.ps1 + the sc sdset
#    Authenticated Users grant + deleting HKLM\SOFTWARE\hMailServer (both views).
$svc = Get-CimInstance Win32_Service -Filter "Name='hMailServer'" -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    Report $false 'hMailServer service exists' 'Service not found. Run build\post-build.ps1 -Configuration Release.'
} else {
    $pathOk = $svc.PathName -like "*$expectedExe*"
    Report $pathOk 'Service runs the repo Release build' `
        ("PathName is {0} - expected {1}. Recover: post-build.ps1, re-apply the sc sdset AU grant, then delete HKLM\SOFTWARE\hMailServer in BOTH /reg:32 and /reg:64." -f $svc.PathName, $expectedExe)

    if ($svc.State -ne 'Running') {
        try { Start-Service hMailServer -ErrorAction Stop; Start-Sleep -Seconds 3; Report $true 'Service running (started now)' '' }
        catch { Report $false 'Service running' "Could not start: $($_.Exception.Message). If access denied, the sc sdset AU grant was lost." }
    } else {
        Report $true 'Service running' ''
    }
}

# 2. No stray InstallLocation registry value. The x64 server reads the 32-bit
#    view; a leftover value points it at a non-existent INI and it comes up with
#    empty configuration while still reporting "Running".
foreach ($view in '/reg:32', '/reg:64') {
    reg query "HKLM\SOFTWARE\hMailServer" /v InstallLocation $view 2>$null | Out-Null
    Report ($LASTEXITCODE -ne 0) "No stray InstallLocation ($view)" `
        "Delete it (elevated): reg delete `"HKLM\SOFTWARE\hMailServer`" $view /f - a stray installer left it and the server will read the wrong INI."
}

# 3. COM must authenticate with the suite's standard password and see the
#    expected test fixtures. Zeroes here mean the wrong instance is running.
$app = $null
try {
    $app = New-Object -ComObject hMailServer.Application
    $auth = $app.Authenticate('Administrator', 'testar')
    Report ($null -ne $auth) 'COM authentication (Administrator/testar)' `
        'Authentication failed - if a stray install is running, its password is not testar.'
    if ($null -ne $auth) {
        Report ($app.Domains.Count -eq 1) 'Test domain present (Domains.Count = 1)' ("Count is {0}." -f $app.Domains.Count)
        # This is also the check RELEASE.md step 4 relies on for the TLS fixtures'
        # twelve extra ports: an aborted run leaves them registered, so the count
        # comes back 16. Worth naming in the message, because "Count is 16" on its
        # own does not tell anyone what to delete.
        Report ($app.Settings.TCPIPPorts.Count -eq 4) 'Standard port set (TCPIPPorts.Count = 4)' `
            ("Count is {0}. More than 4 usually means an aborted run left the TLS fixtures' extra ports registered - remove the non-standard ports in the Control Panel, or re-add the four standard ones if there are fewer." -f $app.Settings.TCPIPPorts.Count)
    }
} catch {
    Report $false 'COM object creation' $_.Exception.Message
}

# 4. The bench database has to be at the schema version this build compiles in.
#    REQUIRED_DB_VERSION moves whenever a release adds a column - it went 6005 ->
#    6006 for hm_imapfolders.folderspecialuse - and a bench nobody upgraded fails
#    in a way that points somewhere else entirely: Application::OnDatabaseConnected
#    refuses the connection, so check 1 says "could not start" and check 5 says
#    there is a stale ERROR log, and neither of them says the word "database".
#    This check is here to say it, and it goes before the ERROR log check because
#    the log entry it is about to find is this one.
if ($null -ne $app) {
    try {
        $currentDbVersion  = $app.Database.CurrentVersion
        $requiredDbVersion = $app.Database.RequiredVersion
        Report ($currentDbVersion -eq $requiredDbVersion) 'Database schema at the required version' `
            ("Schema is {0}, this build requires {1}. Run the published DBUpdater.exe (build\build-tools.ps1 publishes it to Tools\DBUpdater\publish) - the service will not start until they match." -f $currentDbVersion, $requiredDbVersion)
    } catch {
        Report $false 'Database schema version check' `
            ("Could not read the schema version: {0}" -f $_.Exception.Message)
    }
}

# 5. No leftover ERROR log. TestFixtureBase.SetUp fails every test if the file
#    exists at all - an aborted run's deliberate scanner error poisons the next
#    run completely (observed as 1038/1038 failed).
if ($null -ne $app) {
    try {
        $errorLog = $app.Settings.Logging.CurrentErrorLog
        if (Test-Path $errorLog) {
            if ($Clean) {
                Remove-Item $errorLog -Force
                Report $true 'ERROR log (removed by -Clean)' ''
            } else {
                Report $false 'No stale ERROR log' `
                    ("{0} exists (probably from an aborted run). Inspect it, then re-run with -Clean." -f $errorLog)
            }
        } else {
            Report $true 'No stale ERROR log' ''
        }
    } catch {
        Report $false 'ERROR log check' $_.Exception.Message
    }
}

# 6. clamd must be listening. It runs as a bare process from C:\clamav, not a
#    Windows service, so Get-Service finds nothing.
$clamd = [bool](Get-NetTCPConnection -LocalPort 3310 -State Listen -ErrorAction SilentlyContinue)
Report $clamd 'clamd listening on 3310' 'Start C:\clamav\clamd.exe - the live-scanner tests need it.'

# 7. SpamAssassin service must exist (TestSANotRunning stops/starts it, which
#    needs the sc sdset AU grant that a service re-registration silently drops).
$sa = Get-Service -Name SpamAssassinJAM -ErrorAction SilentlyContinue
Report ($null -ne $sa) 'SpamAssassinJAM service exists' 'The suite stops and starts it; see IMPLEMENTATION-NOTES.md for the sdset grant.'

# 8. Protocol listeners.
foreach ($port in 25, 110, 143) {
    Report ([bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)) "Listening on $port" `
        'Service is running but not listening - usually the stray-registry fault above.'
}

# 9. Interference sources that have broken runs before (warn only).
$vpn = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -match 'Proton|WireGuard' -and $_.Status -eq 'Up' }
if ($vpn) { Write-Host ("  WARN  VPN adapter up: {0} - has broken address selection in tests before." -f ($vpn.Name -join ', ')) -ForegroundColor Yellow }

# 10. Test assets.
$nunit = Join-Path $repoRoot 'hmailserver\test\packages\NUnit.ConsoleRunner.3.22.0\tools\nunit3-console.exe'
$dll = Join-Path $repoRoot 'hmailserver\test\RegressionTests\bin\x64\Debug\RegressionTests.dll'
Report (Test-Path $nunit) 'NUnit console runner present' 'Restore packages via build\build-tests.ps1.'
Report (Test-Path $dll) 'RegressionTests.dll built' 'Build via build\build-tests.ps1.'

# 11. Orphan test files.
#
# RegressionTests.csproj is a legacy non-SDK project: it lists every source file
# explicitly, with no glob. So a new test file that nobody adds to the csproj is
# not merely unrun - it is invisible, and a green suite says nothing about it.
# This is not hypothetical: SSL\TlsOptionsTests.cs sat committed and uncompiled
# for months, six tests that had never executed once, and it was only found by
# reading the csproj against the directory. Cheap check, so it runs every time.
$testRoot = Join-Path $repoRoot 'hmailserver\test\RegressionTests'
$csprojPath = Join-Path $testRoot 'RegressionTests.csproj'
if (Test-Path $csprojPath) {
    $csprojText = Get-Content -Raw -LiteralPath $csprojPath
    $included = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($m in [regex]::Matches($csprojText, '<Compile\s+Include="([^"]+)"')) {
        [void]$included.Add($m.Groups[1].Value.Replace('/', '\'))
    }

    $skip = @('\bin\', '\obj\', '\Properties\', '\packages\')
    $orphans = @()
    foreach ($file in Get-ChildItem -LiteralPath $testRoot -Recurse -Filter *.cs -File) {
        $rel = $file.FullName.Substring($testRoot.Length).TrimStart('\')
        if ($skip | Where-Object { ('\' + $rel) -like ("*" + $_ + "*") }) { continue }
        if (-not $included.Contains($rel)) { $orphans += $rel }
    }

    Report ($orphans.Count -eq 0) 'No orphan test files' `
        ("Not in RegressionTests.csproj, so never compiled or run: {0}" -f ($orphans -join ', '))
}

# 12. No leftover test-only settings in the server's ini.
#
#     Two fixtures point the server at a fake DNS server on 127.0.0.1 and put the
#     setting back in their teardown - AntiSpam\DKIM\Verification and
#     AntiSpam\DmarcRptReporting. Kill a run while either is in flight and the
#     teardown never happens, so the setting survives into every later run with
#     nothing listening on 127.0.0.1:53. Every lookup then waits for the query
#     timeout and fails: SURBL, DNSBL, SPF, DKIM, DMARC and MX alike, in tests
#     that have nothing to do with DNS and never mention it.
#
#     IniFileSettings caches the file at process start, so removing the key is
#     only half the fix - the service has to be restarted afterwards.
$serverIni = Join-Path $repoRoot 'hmailserver\source\Server\hMailServer\x64\Release\hMailServer.ini'
if (Test-Path $serverIni) {
    # Each of these is written by a fixture and removed by its teardown, so any of
    # them surviving into the next run means a run was killed while it was in flight.
    # They are listed together because the failure they cause is the same shape every
    # time: tests that never mention the setting fail for reasons that never mention
    # it either.
    #   DNSServer              - points the resolver at a fake nothing is serving
    #   Pop3LoginDelaySeconds  - refuses the second POP3 logon of every test
    #   PasswordPolicy*        - refuses the password every fixture creates its accounts with,
    #                            so the whole suite fails in setup rather than anywhere useful
    #   QuarantineEnabled      - turns spam REFUSALS into acceptances, so every
    #                            anti-spam test that expects a 550 sees a 250
    $leftoverKeys = 'DNSServer', 'Pop3LoginDelaySeconds', 'PasswordPolicyMinimumLength',
                    'PasswordPolicyRequireMixedCase', 'PasswordPolicyRequireDigit',
                    'PasswordPolicyRequireNonAlphanumeric', 'PasswordPolicyRejectCommon',
                    'QuarantineEnabled', 'PasswordPolicyHistoryCount',
                    'PasswordPolicyMaximumAgeDays', 'DmarcTreeWalkEnabled',
                    'AuthenticationResultsEnabled', 'DNSQueryTimeout', 'SpfVoidLookupLimit'

    $iniLines = @(Get-Content -LiteralPath $serverIni)
    $pattern = '^\s*(' + ($leftoverKeys -join '|') + ')\s*='
    $leftovers = $iniLines | Where-Object { $_ -match $pattern }

    if ($leftovers -and $Clean) {
        Set-Content -LiteralPath $serverIni -Value ($iniLines | Where-Object { $_ -notmatch $pattern })
        Write-Host ('  CLEAN Removed leftover test settings from hMailServer.ini - restart the service: {0}' -f ($leftovers -join '; ')) -ForegroundColor Yellow
        $leftovers = $null
    }

    Report (-not $leftovers) 'No leftover test-only ini settings' `
        ("hMailServer.ini still has '{0}', left by an aborted fixture. Re-run with -Clean, then restart the service." -f ($leftovers -join '; '))
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host 'Pre-flight passed - safe to run the suite.' -ForegroundColor Green
    exit 0
} else {
    Write-Host ("Pre-flight FAILED ({0} problem(s)) - fix before running the suite, or results will mislead." -f $failures) -ForegroundColor Red
    exit 1
}
