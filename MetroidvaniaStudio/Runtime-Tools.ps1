function Find-StudioRuntime {
    param([string]$Name, [string[]]$Locations, [string]$VersionArgument, [string]$VersionPattern, [string]$Requirement)
    $candidates = @((Get-Command $Name -CommandType Application -All -ErrorAction SilentlyContinue).Source) + $Locations
    # Desktop launches may not inherit the development shell's PATH.
    $toolchainPath = Join-Path (Split-Path $PSScriptRoot -Parent) '.local/toolchain.json'
    if (Test-Path -LiteralPath $toolchainPath -PathType Leaf) {
        try {
            $toolchain = Get-Content -LiteralPath $toolchainPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $cached = $toolchain.PSObject.Properties[$Name]
            if ($cached -and $cached.Value -is [string]) { $candidates += $cached.Value }
        } catch { Write-Verbose 'Ignoring an unreadable local toolchain cache.' }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or !(Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        try {
            $versionOutput = (& $candidate $VersionArgument 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0 -and $versionOutput -match $VersionPattern) { return $candidate }
        } catch { continue }
    }
    throw "$Requirement Install it in a standard location or add it to PATH."
}
