param(
    [ValidateRange(1024, 65535)][int]$Port = 18765,
    [Alias('ProjectPath')][string]$Project = '',
    [switch]$Foreground,
    [switch]$Restart,
    [switch]$CheckOnly,
    [switch]$OpenBrowser
)
$ErrorActionPreference = 'Stop'
$editorRoot = $PSScriptRoot
$studioRoot = Split-Path $editorRoot -Parent
$projectRoot = if ([string]::IsNullOrWhiteSpace($Project)) { Join-Path $studioRoot '.local/workspace' } else { $Project }
$projectRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$localRoot = Join-Path $editorRoot '.local'
$sessionPath = Join-Path $localRoot "server-$Port.json"
$editorUrl = "http://127.0.0.1:$Port/"
$healthUrl = $editorUrl + 'api/health'
$publishedDll = Join-Path $editorRoot 'Server/MetroidvaniaStudio.Server.dll'
$isPublished = Test-Path -LiteralPath $publishedDll -PathType Leaf

function Find-StudioRuntime {
    param([string]$Name, [string[]]$Locations, [string]$VersionArgument, [string]$VersionPattern, [string]$Requirement)
    $candidates = @((Get-Command $Name -CommandType Application -All -ErrorAction SilentlyContinue).Source) + $Locations
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or !(Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        try {
            $versionOutput = (& $candidate $VersionArgument 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0 -and $versionOutput -match $VersionPattern) { return $candidate }
        } catch { continue }
    }
    throw "$Requirement Install it in a standard location or add it to PATH."
}
function Assert-Workspace {
    param($Health)
    if (!$Health.instanceId -or !$Health.projectPath -or
        ![string]::Equals([IO.Path]::GetFullPath($Health.projectPath).TrimEnd([IO.Path]::DirectorySeparatorChar), $projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Port $Port is serving a different workspace or application. Choose another -Port."
    }
}
function Complete-StudioStartup {
    param($ServerProcess, [string]$Dll, [int]$TimeoutSeconds = 20)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($ServerProcess.HasExited) { throw ('Server failed. See ' + (Join-Path $localRoot "server-$Port-error.log")) }
        $health = $null
        try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 1 } catch { }
        if ($null -ne $health) {
            Assert-Workspace $health
            if (!$ServerProcess.HasExited) {
                $record = @{ pid = $ServerProcess.Id; processStartUtc = $ServerProcess.StartTime.ToUniversalTime().ToString('O');
                    port = $Port; instanceId = $health.instanceId; projectPath = $projectRoot; dllPath = $Dll }
                $record | ConvertTo-Json | Set-Content -LiteralPath ($sessionPath + '.tmp') -Encoding UTF8
                Move-Item -LiteralPath ($sessionPath + '.tmp') -Destination $sessionPath -Force
                if ($OpenBrowser) { Start-Process -FilePath $editorUrl }
                return
            }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "MetroidvaniaStudio did not become ready. See $localRoot/server-$Port.log and server-$Port-error.log."
}

if (!$CheckOnly) {
    $active = $null
    try { $active = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2 }
    catch { if ($_.Exception.Response) { throw "Port $Port is serving a different application." } }
    if ($null -ne $active) {
        Assert-Workspace $active
        if (!$Restart) {
            Write-Output "MetroidvaniaStudio is already running: $editorUrl"
            Write-Output 'Use -Restart to rebuild and restart after saving the current session.'
            if ($OpenBrowser) { Start-Process -FilePath $editorUrl }
            return
        }
    }
}
$dotnetLocations = @($env:METROIDVANIA_STUDIO_DOTNET, "$env:ProgramFiles/dotnet/dotnet.exe", "$env:LOCALAPPDATA/Microsoft/dotnet/dotnet.exe")
if ($env:DOTNET_ROOT) { $dotnetLocations += Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
if ($isPublished) {
    $dotnetPath = Find-StudioRuntime -Name dotnet.exe -Locations $dotnetLocations -VersionArgument --list-runtimes -VersionPattern '(?m)^Microsoft\.AspNetCore\.App 10\.' -Requirement 'ASP.NET Core Runtime 10 is required.'
    $dll = $publishedDll
} else {
    $nodePath = Find-StudioRuntime -Name node.exe -VersionArgument --version -VersionPattern '^v(?:2[4-9]|[3-9][0-9]|[1-9][0-9]{2,})\.' -Requirement 'Node.js 24 or later is required.' -Locations @(
        $env:METROIDVANIA_STUDIO_NODE, "$env:ProgramFiles/nodejs/node.exe", "$env:LOCALAPPDATA/Programs/nodejs/node.exe")
    $dotnetPath = Find-StudioRuntime -Name dotnet.exe -Locations $dotnetLocations -VersionArgument --list-sdks -VersionPattern '(?m)^10\.' -Requirement '.NET SDK 10 is required.'
    Write-Output "Node.js: $nodePath"
    $dll = Join-Path $editorRoot 'Server/bin/Release/net10.0/MetroidvaniaStudio.Server.dll'
}
Write-Output ".NET: $dotnetPath"
Write-Output "Workspace: $projectRoot"
if ($CheckOnly) { return }
New-Item -ItemType Directory -Path $localRoot -Force | Out-Null
if (!$isPublished) {
    $previousDotnet = $env:METROIDVANIA_STUDIO_DOTNET
    try {
        $env:METROIDVANIA_STUDIO_DOTNET = $dotnetPath
        & $nodePath (Join-Path $editorRoot 'build-web.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' }
    } finally { $env:METROIDVANIA_STUDIO_DOTNET = $previousDotnet }
}
if ($Restart) {
    Write-Output 'Saving and stopping the current MetroidvaniaStudio session...'
    & (Join-Path $editorRoot 'Stop-MetroidvaniaStudio.ps1') -Port $Port -Project $projectRoot
}
if (!$isPublished) {
    & $dotnetPath build (Join-Path $editorRoot 'Server/MetroidvaniaStudio.Server.csproj') --configuration Release --nologo -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
}
New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
if ($Foreground) { & $dotnetPath $dll --studio-root $studioRoot --project $projectRoot --port $Port; exit $LASTEXITCODE }
$arguments = @(('"' + $dll + '"'), '--studio-root', ('"' + $studioRoot + '"'), '--project', ('"' + $projectRoot + '"'), '--port', $Port)
$process = Start-Process -FilePath $dotnetPath -ArgumentList $arguments -WorkingDirectory $studioRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $localRoot "server-$Port.log") -RedirectStandardError (Join-Path $localRoot "server-$Port-error.log")
Complete-StudioStartup -ServerProcess $process -Dll $dll
Write-Output "MetroidvaniaStudio: $editorUrl"
Write-Output "MiniMap: ${editorUrl}?view=minimap"
Write-Output "PID: $($process.Id) / Workspace: $projectRoot"
