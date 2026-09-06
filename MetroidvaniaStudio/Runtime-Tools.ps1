$script:StudioToolsRoot = Split-Path $PSScriptRoot -Parent
function Find-StudioRuntime {
    param([string]$Name, [string[]]$Locations, [string]$VersionArgument, [string]$VersionPattern, [string]$Requirement)
    $override = if ($Name -eq 'node.exe') { $env:METROIDVANIA_STUDIO_NODE } else { $env:METROIDVANIA_STUDIO_DOTNET }
    $candidates = @($override) + @((Get-Command $Name -CommandType Application -All -ErrorAction SilentlyContinue).Source) + $Locations
    $toolchainPath = Join-Path $script:StudioToolsRoot '.local/toolchain.json'
    if (Test-Path -LiteralPath $toolchainPath -PathType Leaf) {
        try {
            $toolchain = Get-Content -LiteralPath $toolchainPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $cached = $toolchain.PSObject.Properties[$Name]
            if ($cached -and $cached.Value -is [string]) { $candidates += $cached.Value }
        } catch { Write-Verbose 'Ignoring an unreadable local toolchain cache.' }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $resolved = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue
            if (!$resolved) { continue }
            $candidate = $resolved.Source
        }
        try {
            $versionOutput = (& $candidate $VersionArgument 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0 -and $versionOutput -match $VersionPattern) { return [IO.Path]::GetFullPath($candidate) }
        } catch { continue }
    }
    throw "$Requirement Install it in a standard location, add it to PATH, or set the tool environment override."
}
function Get-StudioDotnetLocations {
    $locations = @("$env:ProgramFiles/dotnet/dotnet.exe", "$env:LOCALAPPDATA/Microsoft/dotnet/dotnet.exe", "$env:USERPROFILE/.dotnet/dotnet.exe")
    if ($env:DOTNET_ROOT) { $locations += Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
    return $locations
}
function Invoke-StudioLauncher {
    param([string]$Action, [string[]]$Arguments)
    $launcher = Join-Path $script:StudioToolsRoot 'MetroidvaniaStudio/Launcher/MetroidvaniaStudio.Launcher.dll'
    $published = Test-Path -LiteralPath $launcher -PathType Leaf
    if (!$published) { $launcher = Join-Path $script:StudioToolsRoot 'MetroidvaniaStudio/Launcher/bin/Release/net10.0/MetroidvaniaStudio.Launcher.dll' }
    $launcherExists = Test-Path -LiteralPath $launcher -PathType Leaf
    $latestExists = Test-Path -LiteralPath (Join-Path $script:StudioToolsRoot 'Builds/latest.json') -PathType Leaf
    $hasExplicitBuild = $Arguments -contains '--build-directory'
    if (!$launcherExists -and $Action -eq 'check') { & (Join-Path $script:StudioToolsRoot 'MetroidvaniaStudio/Build-MetroidvaniaStudio.ps1') -CheckOnly; return }
    if (!$published -and $Action -eq 'run' -and (!$launcherExists -or (!$latestExists -and !$hasExplicitBuild))) {
        Write-Output 'No complete local build yet. Building once before the first launch...'
        & (Join-Path $script:StudioToolsRoot 'MetroidvaniaStudio/Build-MetroidvaniaStudio.ps1')
    }
    if (!(Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'The launcher build was not produced.' }
    $dotnet = Find-StudioRuntime -Name dotnet.exe -Locations (Get-StudioDotnetLocations) -VersionArgument --list-runtimes -VersionPattern '(?m)^Microsoft\.AspNetCore\.App 10\.' -Requirement 'ASP.NET Core Runtime 10 is required.'
    & $dotnet $launcher $Action --studio-root $script:StudioToolsRoot @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Launcher $Action failed (exit $LASTEXITCODE)." }
}

function Merge-StudioOptions {
    param([hashtable]$Options, [string[]]$Arguments, [string[]]$Allowed)
    $names = @{ '--project'='Project'; '--port'='Port'; '--no-browser'='NoBrowser'; '--restart'='Restart'; '--foreground'='Foreground'; '--build-directory'='BuildDirectory'; '--check'='CheckOnly' }
    $switches = @('NoBrowser','Restart','Foreground','CheckOnly')
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        $name = $names[$Arguments[$index]]
        if (!$name -or $Allowed -notcontains $name) { throw "Unknown argument: $($Arguments[$index])" }
        if ($switches -contains $name) { $Options[$name] = $true; continue }
        $index++
        if ($index -ge $Arguments.Count) { throw "Missing value for $name." }
        $Options[$name] = $Arguments[$index]
    }
    if ($Options.ContainsKey('Port')) {
        $portValue = 0
        if (![int]::TryParse([string]$Options.Port, [ref]$portValue) -or $portValue -lt 1024 -or $portValue -gt 65535) { throw 'Port must be between 1024 and 65535.' }
        $Options.Port = $portValue
    }
    return $Options
}
