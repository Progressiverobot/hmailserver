function Find-MsBuild {
    param(
        # Empty = latest installed Visual Studio / Build Tools.
        [string]$VsWhereMinVersion = ''
    )

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

    $msbuild = $null
    if (Test-Path $vswhere) {
        try {
            $vswhereArgs = @('-latest', '-products', '*', '-requires', 'Microsoft.Component.MSBuild', '-find', 'MSBuild\**\Bin\MSBuild.exe')
            if ($VsWhereMinVersion) { $vswhereArgs = @('-version', $VsWhereMinVersion) + $vswhereArgs }

            $msbuild = & $vswhere @vswhereArgs | Select-Object -First 1
        } catch {
            $msbuild = $null
        }
    } else {
        Write-Verbose "vswhere not found at $vswhere"
    }

    if (-not $msbuild) {
        $msbuildCmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
        if ($msbuildCmd) { $msbuild = $msbuildCmd.Source }
    }

    return $msbuild
}
