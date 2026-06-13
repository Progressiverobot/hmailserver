# Downloads the .NET 8 Desktop Runtime installer that the hMailServer
# installer bundles for the Control Panel component. The file is not kept
# in the repository (55+ MB); run this once before building the installer.
$dest = Join-Path $PSScriptRoot '..\hmailserver\installation\DotNet'
New-Item -ItemType Directory -Force $dest | Out-Null
$file = Join-Path $dest 'windowsdesktop-runtime-8.0-win-x64.exe'

if (Test-Path $file) {
    Write-Host "already present: $file"
    return
}

Write-Host 'downloading .NET 8 Desktop Runtime (x64)...'
Invoke-WebRequest -Uri 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe' -OutFile $file
Write-Host ("downloaded {0} ({1:N1} MB)" -f $file, ((Get-Item $file).Length / 1MB))
