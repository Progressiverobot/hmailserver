# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

$RunCount = 0;

do
{
    echo "Ran tests $RunCount times...";
    Get-Date

    ..\..\..\libraries\nunit-2.6.3\nunit-console-x86.exe .\Bin\Debug\RegressionTests.dll /config=Release /labels /stoponerror /out=TestResult.log
    $RunCount++;
    
}
while ($LASTEXITCODE -eq 0)