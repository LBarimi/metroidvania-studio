$ErrorActionPreference = 'Stop'
$studioRoot = Split-Path $PSScriptRoot -Parent
$buildsRoot = Join-Path $studioRoot 'Builds'
$localRoot = Join-Path $studioRoot '.local'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
. (Join-Path $PSScriptRoot 'Runtime-Tools.ps1')

New-Item -ItemType Directory -Path $buildsRoot, $localRoot -Force | Out-Null
$buildLock = $null
$previousDotnet = $env:METROIDVANIA_STUDIO_DOTNET
try {
    try { $buildLock = [IO.File]::Open((Join-Path $localRoot 'build.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] { throw 'Another build is running. Wait for it to finish.' }
    $nodePath = Find-StudioRuntime -Name node.exe -VersionArgument --version -VersionPattern '^v(?:2[4-9]|[3-9][0-9]|[1-9][0-9]{2,})\.' -Requirement 'Node.js 24 or later is required for building.' -Locations @(
        $env:METROIDVANIA_STUDIO_NODE, "$env:ProgramFiles/nodejs/node.exe", "$env:LOCALAPPDATA/Programs/nodejs/node.exe")
    $dotnetLocations = @($env:METROIDVANIA_STUDIO_DOTNET, "$env:ProgramFiles/dotnet/dotnet.exe", "$env:LOCALAPPDATA/Microsoft/dotnet/dotnet.exe")
    if ($env:DOTNET_ROOT) { $dotnetLocations += Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
    $dotnetPath = Find-StudioRuntime -Name dotnet.exe -Locations $dotnetLocations -VersionArgument --list-sdks -VersionPattern '(?m)^10\.' -Requirement '.NET SDK 10 is required for building.'
    $env:METROIDVANIA_STUDIO_DOTNET = $dotnetPath
    $version = (Get-Content -LiteralPath (Join-Path $studioRoot 'version.json') -Raw | ConvertFrom-Json).version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid build version.' }
    $name = $version + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $output = Join-Path $buildsRoot $name
    $webOutput = Join-Path $output 'MetroidvaniaStudio/dist'
    Write-Output 'Building web editor...'
    & $nodePath (Join-Path $PSScriptRoot 'build-web.mjs') $webOutput
    if ($LASTEXITCODE -ne 0) { throw 'Web build failed. The previous build is still available.' }
    Write-Output 'Building local server...'
    & $dotnetPath publish (Join-Path $PSScriptRoot 'Server/MetroidvaniaStudio.Server.csproj') --configuration Release --self-contained false -p:UseAppHost=false -p:UseSharedCompilation=false -p:DebugType=None --output (Join-Path $output 'MetroidvaniaStudio/Server') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed. The previous build is still available.' }
    $inputs = Get-Content -LiteralPath (Join-Path $studioRoot 'Tools/Build/package-inputs.json') -Raw | ConvertFrom-Json
    foreach ($relative in $inputs) {
        if ($relative -eq 'MetroidvaniaStudio/dist') { continue }
        $destination = Join-Path $output $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $studioRoot $relative) -Destination $destination -Recurse -Force
    }
    foreach ($notice in @('LICENSE', 'NOTICE')) {
        $source = Join-Path $studioRoot $notice
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $output $notice) }
    }
    # Machine-specific paths stay in ignored local state and are revalidated on use.
    $toolchainPath = Join-Path $localRoot 'toolchain.json'
    $toolchainTemp = Join-Path $localRoot ('toolchain-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $toolchainJson = @{ 'node.exe' = $nodePath; 'dotnet.exe' = $dotnetPath } | ConvertTo-Json
    [IO.File]::WriteAllText($toolchainTemp, $toolchainJson, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $toolchainPath) { [IO.File]::Replace($toolchainTemp, $toolchainPath, [NullString]::Value) }
    else { [IO.File]::Move($toolchainTemp, $toolchainPath) }
    # Publish the pointer last. Builds never overwrite the files of a running server.
    $latest = Join-Path $buildsRoot 'latest.json'
    $temporary = Join-Path $buildsRoot ('latest-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $json = @{ formatVersion = 1; folder = $name; version = $version } | ConvertTo-Json
    [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $latest) { [IO.File]::Replace($temporary, $latest, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $latest) }
    Write-Output "Build ready: $output"
    Write-Output 'Double-click Run-MetroidvaniaStudio.bat to open the editor.'
} finally {
    $env:METROIDVANIA_STUDIO_DOTNET = $previousDotnet
    if ($buildLock) { $buildLock.Dispose() }
}
