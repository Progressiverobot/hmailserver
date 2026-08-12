# Verifies this machine is in the known-good state for running the hMailServer
# regression suite, and names the exact fault when it is not. Every check here
# corresponds to a failure mode that has actually burned a run:
#   - the service pointing at a stray installer's copy instead of the repo build
#   - a stale InstallLocation registry value redirecting the server's config
#   - a deliberate error left in the ERROR log by an aborted run, which fails
#     every fixture's setup in the next run
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
        Report ($app.Settings.TCPIPPorts.Count -eq 4) 'Standard port set (TCPIPPorts.Count = 4)' ("Count is {0}." -f $app.Settings.TCPIPPorts.Count)
    }
} catch {
    Report $false 'COM object creation' $_.Exception.Message
}

# 4. No leftover ERROR log. TestFixtureBase.SetUp fails every test if the file
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

# 5. clamd must be listening. It runs as a bare process from C:\clamav, not a
#    Windows service, so Get-Service finds nothing.
$clamd = [bool](Get-NetTCPConnection -LocalPort 3310 -State Listen -ErrorAction SilentlyContinue)
Report $clamd 'clamd listening on 3310' 'Start C:\clamav\clamd.exe - the live-scanner tests need it.'

# 6. SpamAssassin service must exist (TestSANotRunning stops/starts it, which
#    needs the sc sdset AU grant that a service re-registration silently drops).
$sa = Get-Service -Name SpamAssassinJAM -ErrorAction SilentlyContinue
Report ($null -ne $sa) 'SpamAssassinJAM service exists' 'The suite stops and starts it; see IMPLEMENTATION-NOTES.md for the sdset grant.'

# 7. Protocol listeners.
foreach ($port in 25, 110, 143) {
    Report ([bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)) "Listening on $port" `
        'Service is running but not listening - usually the stray-registry fault above.'
}

# 8. Interference sources that have broken runs before (warn only).
$vpn = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -match 'Proton|WireGuard' -and $_.Status -eq 'Up' }
if ($vpn) { Write-Host ("  WARN  VPN adapter up: {0} - has broken address selection in tests before." -f ($vpn.Name -join ', ')) -ForegroundColor Yellow }

# 9. Test assets.
$nunit = Join-Path $repoRoot 'hmailserver\test\packages\NUnit.ConsoleRunner.3.22.0\tools\nunit3-console.exe'
$dll = Join-Path $repoRoot 'hmailserver\test\RegressionTests\bin\x64\Debug\RegressionTests.dll'
Report (Test-Path $nunit) 'NUnit console runner present' 'Restore packages via build\build-tests.ps1.'
Report (Test-Path $dll) 'RegressionTests.dll built' 'Build via build\build-tests.ps1.'

Write-Host ''
if ($failures -eq 0) {
    Write-Host 'Pre-flight passed - safe to run the suite.' -ForegroundColor Green
    exit 0
} else {
    Write-Host ("Pre-flight FAILED ({0} problem(s)) - fix before running the suite, or results will mislead." -f $failures) -ForegroundColor Red
    exit 1
}
