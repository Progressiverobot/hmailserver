# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
   Renders the winget manifests and the Chocolatey package for a release.

   Two package managers ask the same four questions - what is the package called,
   where is the installer, what does it hash to, and how is it driven without a
   wizard - and both want the answers in a file of their own. The answers all come
   from one release, so they are written once here and rendered from the templates
   in hmailserver/installation/winget and hmailserver/installation/chocolatey.

   Nothing is invented. The identity fields in those templates are the installer's
   own (section_setup.iss and section_setup_64.iss: AppId, AppVersion, AppVerName,
   MinVersion, PrivilegesRequired), the silent switches are the ones the installer
   smoke test uses, and the SHA-256 is computed here from the actual file - either
   a local one or the release asset, downloaded and hashed. A manifest whose hash
   is copied from a release note is a manifest that can be wrong; this one cannot.

   What it writes, under -OutputDirectory:

     winget\manifests\p\ProgressiveRobot\hMailServer\<version>\
         ProgressiveRobot.hMailServer.yaml               (version)
         ProgressiveRobot.hMailServer.installer.yaml     (installer)
         ProgressiveRobot.hMailServer.locale.en-US.yaml  (default locale)
     chocolatey\
         hmailserver.nuspec
         tools\chocolateyinstall.ps1
         tools\chocolateyuninstall.ps1

   The winget path is the one microsoft/winget-pkgs requires, so the directory can
   be handed to `wingetcreate submit` or copied into a fork of that repository as
   it stands. The Chocolatey directory is what `choco pack` takes.

   Usage:

     # From a local installer (the release build's own output):
     build\make-package-manifests.ps1 -Version 6.3.3 `
         -InstallerPath C:\...\hMailServer-6.3.3-x64.exe

     # From the published release (downloads it once and hashes it):
     build\make-package-manifests.ps1 -Version 6.3.3

     # Render-only shape check, no network, no release needed (CI, on a pull
     # request that touches the templates):
     build\make-package-manifests.ps1 -Check

   Exits non-zero if anything is unrendered, malformed or missing.
#>

[CmdletBinding()]
param(
    # The release version, as the installer carries it: 6.3.3, not v6.3.3.
    [string] $Version,

    # The release tag. Defaults to v<Version>.
    [string] $Tag,

    # A local copy of the installer to hash (and, with -RequireSignedInstaller,
    # to check the Authenticode signature of).
    [string] $InstallerPath,

    # Where the installer will be downloaded from by winget and Chocolatey.
    # Defaults to the release asset URL for the tag.
    [string] $InstallerUrl,

    # The SHA-256, when it is already known and neither the file nor the network
    # is available. Computed from the installer when this is not given.
    [string] $Sha256,

    # The release date, YYYY-MM-DD, for the installer manifest's ReleaseDate.
    # Defaults to today.
    [string] $ReleaseDate,

    # Where to write. Defaults to logs\package-manifests (ignored by git).
    [string] $OutputDirectory,

    # Shape check: render with a placeholder version, URL and hash so that the
    # templates can be proven to render without a release in existence. Writes to
    # a temporary directory and deletes it again.
    [switch] $Check,

    # Fail unless the local installer carries a valid Authenticode signature.
    [switch] $RequireSignedInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$wingetTemplates = Join-Path $repoRoot 'hmailserver\installation\winget'
$chocoTemplates = Join-Path $repoRoot 'hmailserver\installation\chocolatey'

# ---------------------------------------------------------------------------
# What is being described
# ---------------------------------------------------------------------------

if ($Check) {
    if (-not $Version) { $Version = '0.0.0' }
    if (-not $Sha256) { $Sha256 = '0' * 64 }
    if (-not $ReleaseDate) { $ReleaseDate = '2026-01-01' }
}

if (-not $Version) {
    throw '-Version is required (for example -Version 6.3.3). Use -Check for a shape check without a release.'
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "-Version must be three numbers, as the installer carries it: '$Version' is not (v6.3.3 and 6.3.3.0 are both wrong here)."
}

if (-not $Tag) { $Tag = "v$Version" }
if (-not $ReleaseDate) { $ReleaseDate = (Get-Date -Format 'yyyy-MM-dd') }

if ($ReleaseDate -notmatch '^\d{4}-\d{2}-\d{2}$') {
    throw "-ReleaseDate must be YYYY-MM-DD: '$ReleaseDate' is not."
}

$installerName = "hMailServer-$Version-x64.exe"
if (-not $InstallerUrl) {
    $InstallerUrl = "https://github.com/Progressiverobot/hmailserver/releases/download/$Tag/$installerName"
}

if ($InstallerUrl -notmatch '^https://') {
    throw "-InstallerUrl must be https: '$InstallerUrl' is not. winget and Chocolatey both refuse anything else, and so does this."
}

# ---------------------------------------------------------------------------
# The hash: from the file if there is one, from the release if there is not
# ---------------------------------------------------------------------------

if ($InstallerPath) {
    if (-not (Test-Path -LiteralPath $InstallerPath)) {
        throw "No installer at $InstallerPath."
    }

    $item = Get-Item -LiteralPath $InstallerPath
    if ($item.Name -ne $installerName) {
        Write-Warning "The installer is named $($item.Name) but version $Version expects $installerName - check that -Version matches the file."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $item.FullName
    Write-Host "Authenticode: $($signature.Status)$(if ($signature.SignerCertificate) { " - $($signature.SignerCertificate.Subject)" })"
    if ($RequireSignedInstaller -and $signature.Status -ne 'Valid') {
        throw "The installer's Authenticode signature is $($signature.Status), not Valid. A package manager would hand that to every user of it."
    }

    $computed = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($Sha256 -and $Sha256.ToUpperInvariant() -ne $computed) {
        throw "The -Sha256 given ($Sha256) is not the file's ($computed). One of the two is wrong, and the file is not."
    }
    $Sha256 = $computed
    Write-Host "Installer:    $($item.FullName) ($([math]::Round($item.Length / 1MB, 1)) MB)"
}
elseif (-not $Sha256) {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) "hmailserver-$Version-$installerName"
    Write-Host "Downloading   $InstallerUrl"
    Invoke-WebRequest -Uri $InstallerUrl -OutFile $temp -UseBasicParsing
    $Sha256 = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToUpperInvariant()
    Write-Host "Downloaded    $([math]::Round((Get-Item -LiteralPath $temp).Length / 1MB, 1)) MB"
    Remove-Item -LiteralPath $temp -Force
}

$Sha256 = $Sha256.ToUpperInvariant()
if ($Sha256 -notmatch '^[0-9A-F]{64}$') {
    throw "The SHA-256 must be 64 hexadecimal characters: '$Sha256' is not."
}

# ---------------------------------------------------------------------------
# Render
# ---------------------------------------------------------------------------

$tokens = @{
    '@VERSION@'          = $Version
    '@TAG@'              = $Tag
    '@INSTALLER_URL@'    = $InstallerUrl
    '@INSTALLER_SHA256@' = $Sha256
    '@RELEASE_DATE@'     = $ReleaseDate
}

function Convert-Template {
    param(
        [Parameter(Mandatory = $true)][string] $From,
        [Parameter(Mandatory = $true)][string] $To
    )

    if (-not (Test-Path -LiteralPath $From)) { throw "No template at $From." }

    $text = Get-Content -LiteralPath $From -Raw

    # The template's own commentary, which has no business in a file submitted to
    # microsoft/winget-pkgs or shipped in a package: whole lines opening with #~,
    # and XML comments opening with <!--#~.
    $text = [regex]::Replace($text, '(?s)<!--#~.*?-->\r?\n', '')
    $text = [regex]::Replace($text, '(?m)^#~.*\r?\n', '')

    foreach ($token in $tokens.Keys) {
        $text = $text.Replace($token, $tokens[$token])
    }

    $leftover = [regex]::Matches($text, '@[A-Z0-9_]+@') | ForEach-Object { $_.Value } | Sort-Object -Unique
    if ($leftover) {
        throw "$From left $($leftover -join ', ') unrendered - the template has a token this script does not know."
    }
    if ($text -match '(?m)^#~') {
        throw "$From left a #~ line in the rendered output."
    }
    if ($text.Trim().Length -eq 0) {
        throw "$From rendered to nothing."
    }

    $directory = Split-Path -Parent $To
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    # UTF-8 without a byte-order mark: winget-pkgs takes either, Chocolatey's
    # nuspec reader takes either, and no BOM is the one both read without a
    # question.
    [System.IO.File]::WriteAllText($To, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  wrote $To"
    return $To
}

if ($Check) {
    $OutputDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "hmailserver-package-check-$([System.Guid]::NewGuid().ToString('N'))"
}
elseif (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'logs\package-manifests'
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$wingetOut = Join-Path $OutputDirectory "winget\manifests\p\ProgressiveRobot\hMailServer\$Version"
$chocoOut = Join-Path $OutputDirectory 'chocolatey'

Write-Host ''
Write-Host "hMailServer $Version ($Tag), released $ReleaseDate"
Write-Host "  $InstallerUrl"
Write-Host "  SHA-256 $Sha256"
Write-Host ''

$written = @()
$written += Convert-Template -From (Join-Path $wingetTemplates 'ProgressiveRobot.hMailServer.yaml.in') `
    -To (Join-Path $wingetOut 'ProgressiveRobot.hMailServer.yaml')
$written += Convert-Template -From (Join-Path $wingetTemplates 'ProgressiveRobot.hMailServer.installer.yaml.in') `
    -To (Join-Path $wingetOut 'ProgressiveRobot.hMailServer.installer.yaml')
$written += Convert-Template -From (Join-Path $wingetTemplates 'ProgressiveRobot.hMailServer.locale.en-US.yaml.in') `
    -To (Join-Path $wingetOut 'ProgressiveRobot.hMailServer.locale.en-US.yaml')

$written += Convert-Template -From (Join-Path $chocoTemplates 'hmailserver.nuspec.in') `
    -To (Join-Path $chocoOut 'hmailserver.nuspec')
$written += Convert-Template -From (Join-Path $chocoTemplates 'tools\chocolateyinstall.ps1.in') `
    -To (Join-Path $chocoOut 'tools\chocolateyinstall.ps1')

$uninstall = Join-Path $chocoOut 'tools\chocolateyuninstall.ps1'
Copy-Item -LiteralPath (Join-Path $chocoTemplates 'tools\chocolateyuninstall.ps1') -Destination $uninstall -Force
Write-Host "  wrote $uninstall"
$written += $uninstall

# ---------------------------------------------------------------------------
# Check what was written, rather than trusting that it was
# ---------------------------------------------------------------------------

$problems = @()

foreach ($file in $written) {
    if (-not (Test-Path -LiteralPath $file)) { $problems += "missing: $file"; continue }
    if ((Get-Item -LiteralPath $file).Length -eq 0) { $problems += "empty: $file" }
}

# The three winget manifests have to agree with each other about the package and
# the version, because winget-pkgs rejects the submission if they do not - and it
# is a great deal cheaper to learn that here.
foreach ($manifest in (Get-ChildItem -LiteralPath $wingetOut -Filter *.yaml)) {
    $text = Get-Content -LiteralPath $manifest.FullName -Raw
    if ($text -notmatch '(?m)^PackageIdentifier:\s*ProgressiveRobot\.hMailServer\s*$') {
        $problems += "$($manifest.Name): PackageIdentifier is not ProgressiveRobot.hMailServer"
    }
    if ($text -notmatch "(?m)^PackageVersion:\s*""$([regex]::Escape($Version))""\s*$") {
        $problems += "$($manifest.Name): PackageVersion is not ""$Version"""
    }
    if ($text -notmatch '(?m)^ManifestVersion:\s*\d+\.\d+\.\d+\s*$') {
        $problems += "$($manifest.Name): no ManifestVersion"
    }
    if ($text -match "`t") {
        $problems += "$($manifest.Name): contains a tab, which YAML forbids for indentation"
    }
}

$installerManifest = Get-Content -LiteralPath (Join-Path $wingetOut 'ProgressiveRobot.hMailServer.installer.yaml') -Raw
if ($installerManifest -notmatch [regex]::Escape($Sha256)) { $problems += 'the installer manifest does not carry the hash' }
if ($installerManifest -notmatch [regex]::Escape($InstallerUrl)) { $problems += 'the installer manifest does not carry the URL' }
if ($installerManifest -notmatch '(?m)^ProductCode:\s*hMailServer_is1\s*$') {
    $problems += 'the installer manifest has lost ProductCode: hMailServer_is1, which is how winget recognises an installation it did not make and how `winget upgrade` finds this one'
}

$nuspec = Join-Path $chocoOut 'hmailserver.nuspec'
try {
    [xml] $parsed = Get-Content -LiteralPath $nuspec -Raw
    if ($parsed.package.metadata.version -ne $Version) {
        $problems += "the nuspec version is $($parsed.package.metadata.version), not $Version"
    }
    if ($parsed.package.metadata.id -ne 'hmailserver') {
        $problems += "the nuspec id is $($parsed.package.metadata.id), not hmailserver"
    }
}
catch {
    $problems += "the nuspec is not well-formed XML: $($_.Exception.Message)"
}

$chocoInstall = Get-Content -LiteralPath (Join-Path $chocoOut 'tools\chocolateyinstall.ps1') -Raw
if ($chocoInstall -notmatch [regex]::Escape($Sha256)) { $problems += 'the Chocolatey install script does not carry the hash' }
if ($chocoInstall -notmatch [regex]::Escape($InstallerUrl)) { $problems += 'the Chocolatey install script does not carry the URL' }
if ($chocoInstall -notmatch '/SUPPRESSMSGBOXES') {
    $problems += 'the Chocolatey install script has lost /SUPPRESSMSGBOXES, without which a failed database setup reports success'
}

if ($problems.Count -gt 0) {
    Write-Host ''
    foreach ($problem in $problems) { Write-Host "  PROBLEM $problem" }
    if ($Check -and (Test-Path -LiteralPath $OutputDirectory)) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }
    throw "$($problems.Count) problem(s) with the rendered package files."
}

Write-Host ''
Write-Host "$($written.Count) files rendered and checked."

if ($Check) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    Write-Host 'Shape check only: the templates render and agree with each other. Nothing was kept.'
}
else {
    Write-Host ''
    Write-Host "winget:     $wingetOut"
    Write-Host "            wingetcreate submit --token <token> `"$wingetOut`""
    Write-Host "Chocolatey: $chocoOut"
    Write-Host "            choco pack `"$nuspec`" --outputdirectory `"$chocoOut`""
}
