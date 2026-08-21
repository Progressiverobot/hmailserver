# escape=\
# Pinned by digest, not by tag. A tag is a moving target: "ltsc2022" is
# whatever Microsoft last pushed, so an image that built and passed yesterday
# can be a different base today - which is both a reproducibility problem and
# the supply-chain one Scorecard's pinned-dependencies check is about.
#
# The cost is the honest other half: a pinned digest does NOT pick up base-image
# security updates on its own. Refresh it deliberately with
#   docker pull mcr.microsoft.com/windows/servercore:ltsc2022
#   docker inspect --format='{{index .RepoDigests 0}}' mcr.microsoft.com/windows/servercore:ltsc2022
# and update the digest below, rather than dropping back to the bare tag.
FROM mcr.microsoft.com/windows/servercore:ltsc2022@sha256:0df5b1c3394198525f426e379b61d8f09ce60cb449b1cb4cab163e965672cd67

SHELL ["powershell", "-Command", "$ErrorActionPreference = 'Stop';"]

# Enable .NET Framework 3.5
RUN dism /online /enable-feature /featurename:NetFx3 /all

RUN echo "Hello"

# Copy hMailServer installer (expected at repository root)
COPY Docker/hMailServer.exe C:\\hMailServer.exe

# # Install hMailServer silently
RUN Start-Process -FilePath C:\\hMailServer.exe -ArgumentList '/SILENT' -NoNewWindow; \
    Start-Sleep -Seconds 3; \
    $tmpProc = Get-Process | Where-Object { $_.MainModule.FileName -like '*.tmp' } | Select-Object -First 1; \
    if ($tmpProc) { \
        Write-Host \"Waiting for child process: $($tmpProc.Name) (PID $($tmpProc.Id))\"; \
        $tmpProc.WaitForExit(); \
    } else { \
        Write-Host \"No .tmp process found, assuming install completed immediately.\"; \
    }

# Copy regression test binaries (both Debug and Release; the test runner will pick one)
COPY RegressionTests/bin/x64/Debug/ C:\\tests\\Debug\\

# Copy NUnit.ConsoleRunner package tools (3.11.1)
COPY RegressionTests/packages/NUnit.ConsoleRunner.3.11.1/tools/ C:\\nunit\\

# Copy test runner script (moved to hmailserver/test/Docker)
COPY Docker/run-tests.ps1 C:\\run-tests.ps1

ENTRYPOINT ["powershell","-NoProfile","-ExecutionPolicy","Bypass","-File","C:\\\\run-tests.ps1"]
