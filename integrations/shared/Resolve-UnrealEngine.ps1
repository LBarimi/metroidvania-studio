function Resolve-StudioUnrealEngine {
    param([string]$ExplicitRoot, [string]$PackageEngine, [string]$Association)
    $major = switch ($PackageEngine) { 'ue4' { 4 } 'ue5' { 5 } default { throw 'Invalid Unreal package manifest.' } }
    $projectMinor = $null
    if ($Association -match '^(\d+)\.(\d+)(?:\.|$)') {
        if ([int]$Matches[1] -ne $major) { throw 'Choose the package for the project engine version.' }
        $projectMinor = [int]$Matches[2]
    }
    function Read-StudioEngineVersion([string]$Candidate) {
        if (!$Candidate) { return $null }
        $versionFile = Join-Path $Candidate 'Engine/Build/Build.version'
        if (!(Test-Path -LiteralPath $versionFile -PathType Leaf)) { return $null }
        try {
            $version = Get-Content -LiteralPath $versionFile -Raw | ConvertFrom-Json
            if ($version.MajorVersion -ne $major) { return $null }
            if ($major -eq 4 -and $version.MinorVersion -ne 27) { return $null }
            if ($null -ne $projectMinor -and $version.MinorVersion -ne $projectMinor) { return $null }
            return [pscustomobject]@{Root=[IO.Path]::GetFullPath($Candidate); Major=$major; Minor=[int]$version.MinorVersion}
        } catch { return $null }
    }
    if ($ExplicitRoot) {
        $engine = Read-StudioEngineVersion $ExplicitRoot
        if (!$engine) { throw 'The selected engine does not match this package and project.' }
        return $engine
    }
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    foreach ($name in @("UNREAL_ENGINE${major}_PATH", 'UNREAL_ENGINE_PATH')) {
        foreach ($scope in @('Process','User','Machine')) {
            $value = [Environment]::GetEnvironmentVariable($name,$scope)
            if ($value) { $candidates.Add($value) }
        }
    }
    if ($Association) {
        foreach ($key in @(('HKLM:\SOFTWARE\EpicGames\Unreal Engine\' + $Association),
                          ('HKLM:\SOFTWARE\WOW6432Node\EpicGames\Unreal Engine\' + $Association))) {
            $entry = Get-ItemProperty -LiteralPath $key -Name InstalledDirectory -ErrorAction SilentlyContinue
            if ($entry) { $candidates.Add($entry.InstalledDirectory) }
        }
        $entry = Get-ItemProperty -LiteralPath 'HKCU:\SOFTWARE\Epic Games\Unreal Engine\Builds' -Name $Association -ErrorAction SilentlyContinue
        if ($entry) { $candidates.Add($entry.$Association) }
    }
    $programData = [Environment]::GetFolderPath('CommonApplicationData')
    if ($programData) {
        $installed = Join-Path $programData 'Epic/UnrealEngineLauncher/LauncherInstalled.dat'
        if (Test-Path -LiteralPath $installed) {
            try {
                $entries = Get-Content -LiteralPath $installed -Raw | ConvertFrom-Json
                foreach ($entry in $entries.InstallationList) {
                    if ($entry.AppName -match '^UE_') { $candidates.Add($entry.InstallLocation) }
                }
            } catch { }
        }
    }
    # Engine installations can share a parent folder even when only one is on PATH.
    $parents = @($candidates | ForEach-Object { Split-Path -Parent $_ } | Select-Object -Unique)
    $programFiles = [Environment]::GetFolderPath('ProgramFiles')
    if ($programFiles) { $parents += Join-Path $programFiles 'Epic Games' }
    foreach ($parent in $parents) {
        if ($parent -and (Test-Path -LiteralPath $parent -PathType Container)) {
            foreach ($folder in Get-ChildItem -LiteralPath $parent -Directory -Filter 'UE_*' -ErrorAction SilentlyContinue) {
                $candidates.Add($folder.FullName)
            }
        }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        $engine = Read-StudioEngineVersion $candidate
        if ($engine) { return $engine }
    }
    throw "Engine not found for this package. Set UNREAL_ENGINE${major}_PATH or pass -EngineRoot."
}
