# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

# Adds the native dependencies to a Syft-produced SBOM, in place.
#
# Syft scans the repository and reports what it finds there. Boost, OpenSSL and libpq are
# built outside the tree, so it finds nothing of them, and the shipped SBOM described a
# mail server with no TLS library. This adds them, in whichever of the two formats the
# file is - the workflow produces SPDX 2.3 and CycloneDX, and both go out with the release.
#
# Written to be safe to run twice: a package already present by PURL is left alone rather
# than duplicated, so a re-run of the workflow cannot produce an SBOM claiming OpenSSL
# twice. Written to fail loudly on an unrecognised file, because an SBOM this step silently
# declined to touch is exactly the failure it exists to fix.

[CmdletBinding()]
param(
   [Parameter(Mandatory = $true)] [string] $SbomFile,
   [string] $DependencyFile
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SbomFile)) { throw "SBOM not found: $SbomFile" }

$dependencies =
   if ($DependencyFile) { Get-Content $DependencyFile -Raw | ConvertFrom-Json }
   else { & (Join-Path $PSScriptRoot 'native-dependencies.ps1') | ConvertFrom-Json }

if (-not $dependencies -or $dependencies.Count -eq 0) {
   throw "No native dependencies were produced; refusing to leave the SBOM as it is."
}

$sbom = Get-Content $SbomFile -Raw | ConvertFrom-Json

$isSpdx      = $null -ne $sbom.spdxVersion
$isCycloneDx = $null -ne $sbom.bomFormat -and $sbom.bomFormat -eq 'CycloneDX'

if (-not $isSpdx -and -not $isCycloneDx) {
   throw "$SbomFile is neither SPDX (no spdxVersion) nor CycloneDX (no bomFormat). Refusing to guess."
}

$added = 0

if ($isSpdx) {
   $packages = [System.Collections.ArrayList]::new()
   if ($sbom.packages) { foreach ($package in $sbom.packages) { [void]$packages.Add($package) } }

   $relationships = [System.Collections.ArrayList]::new()
   if ($sbom.relationships) { foreach ($relationship in $sbom.relationships) { [void]$relationships.Add($relationship) } }

   # The document's own root, so the added packages hang off the same node Syft's do
   # rather than floating unattached - an SPDX package with no relationship is valid and
   # invisible to plenty of consumers.
   $root = $sbom.documentDescribes | Select-Object -First 1
   if (-not $root) { $root = 'SPDXRef-DOCUMENT' }

   foreach ($dependency in $dependencies) {
      $existing = $packages | Where-Object {
         $_.externalRefs | Where-Object { $_.referenceLocator -eq $dependency.purl }
      }
      if ($existing) { continue }

      $id = 'SPDXRef-Package-native-' + $dependency.name

      [void]$packages.Add([pscustomobject]@{
         SPDXID           = $id
         name             = $dependency.name
         versionInfo      = $dependency.version
         downloadLocation = $dependency.website
         homepage         = $dependency.website
         licenseConcluded = $dependency.license
         licenseDeclared  = $dependency.license
         copyrightText    = 'NOASSERTION'
         filesAnalyzed    = $false
         supplier         = 'NOASSERTION'
         comment          = ('Linked native dependency, not present in the repository: {0}. Version taken from {1}.' -f $dependency.why, $dependency.source)
         externalRefs     = @(
            [pscustomobject]@{ referenceCategory = 'PACKAGE-MANAGER'; referenceType = 'purl'; referenceLocator = $dependency.purl },
            [pscustomobject]@{ referenceCategory = 'SECURITY';        referenceType = 'cpe23Type'; referenceLocator = $dependency.cpe }
         )
      })

      [void]$relationships.Add([pscustomobject]@{
         spdxElementId      = $root
         relationshipType   = 'DEPENDS_ON'
         relatedSpdxElement = $id
      })

      $added++
   }

   $sbom.packages = $packages.ToArray()
   if ($sbom.PSObject.Properties.Name -contains 'relationships') { $sbom.relationships = $relationships.ToArray() }
   else { $sbom | Add-Member -NotePropertyName relationships -NotePropertyValue $relationships.ToArray() }
}
else {
   $components = [System.Collections.ArrayList]::new()
   if ($sbom.components) { foreach ($component in $sbom.components) { [void]$components.Add($component) } }

   foreach ($dependency in $dependencies) {
      if ($components | Where-Object { $_.purl -eq $dependency.purl }) { continue }

      [void]$components.Add([pscustomobject]@{
         type        = 'library'
         name        = $dependency.name
         version     = $dependency.version
         purl        = $dependency.purl
         cpe         = $dependency.cpe
         scope       = 'required'
         description = $dependency.why
         licenses    = @( [pscustomobject]@{ license = [pscustomobject]@{ id = $dependency.license } } )
         externalReferences = @( [pscustomobject]@{ type = 'website'; url = $dependency.website } )
      })

      $added++
   }

   $sbom.components = $components.ToArray()
}

# Depth matters: the default of 2 flattens externalRefs into type names and produces a
# corrupt SBOM that still looks plausible.
$sbom | ConvertTo-Json -Depth 32 | Set-Content -Path $SbomFile -Encoding UTF8

$format = if ($isSpdx) { 'SPDX' } else { 'CycloneDX' }
Write-Host ("{0}: added {1} native dependency package(s) to {2}" -f $format, $added, (Split-Path -Leaf $SbomFile))

foreach ($dependency in $dependencies) {
   Write-Host ("  {0} {1} ({2})" -f $dependency.name, $dependency.version, $dependency.license)
}
