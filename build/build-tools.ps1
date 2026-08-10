# Builds and publishes the .NET tools the installer ships: the database setup
# tools into their per-project publish folders (merged into {app}\Bin by the
# installer) plus DataDirectorySynchronizer and ImportTool. Run before
# compiling the installer. ControlPanel has its own publish step (see
# AGENTS.md); this script covers "hMailServer Tools.sln" only.
Param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$toolsDir = Resolve-Path (Join-Path $scriptRoot '..\hmailserver\source\Tools')

$publishProjects = 'DBSetup', 'DBSetupQuick', 'DBUpdater', 'DataDirectorySynchronizer', 'ImportTool'

foreach ($project in $publishProjects) {
    $csproj = Join-Path $toolsDir "$project\$project.csproj"
    $output = Join-Path $toolsDir "$project\publish"

    Write-Host "Publishing $project -> $output"
    dotnet publish $csproj -c $Configuration -o $output
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "All tools published."
