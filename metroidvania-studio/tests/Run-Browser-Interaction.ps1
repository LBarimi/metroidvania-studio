param(
    [int]$Port = 18766,
    [string]$PlaywrightModule = '',
    [switch]$CameraOnly,
    [switch]$ObjectsOnly,
    [switch]$PerformanceOnly,
    [switch]$RoomOnly,
    [switch]$SyncOnly
)

$ErrorActionPreference = 'Stop'
$editorRoot = Split-Path $PSScriptRoot -Parent
$projectRoot = Split-Path $editorRoot -Parent
$localRoot = [IO.Path]::GetFullPath((Join-Path $editorRoot '.local'))
$scratchRoot = [IO.Path]::GetFullPath((Join-Path $localRoot ('browser-interaction-' + [Guid]::NewGuid().ToString('N'))))
$allowedPrefix = $localRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $scratchRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe browser-test workspace path.' }
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Choose a test port between 1024 and 65535.' }

$server = $null
$oldBase = $env:METROIDVANIA_STUDIO_BASE_URL
$oldProject = $env:METROIDVANIA_STUDIO_TEST_PROJECT_ROOT
$oldPlaywright = $env:PLAYWRIGHT_MODULE
$oldIsolated = $env:METROIDVANIA_STUDIO_TEST_ISOLATED
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
try {
    $selectedPlaywright = if (-not [string]::IsNullOrWhiteSpace($PlaywrightModule)) { $PlaywrightModule } else { $env:PLAYWRIGHT_MODULE }
    if ([string]::IsNullOrWhiteSpace($selectedPlaywright)) { $selectedPlaywright = 'playwright' }
    $env:PLAYWRIGHT_MODULE = $selectedPlaywright
    & node -e 'require(process.env.PLAYWRIGHT_MODULE)'
    if ($LASTEXITCODE -ne 0) { throw "Playwright could not be loaded from '$selectedPlaywright'. Pass -PlaywrightModule <module-path> or set PLAYWRIGHT_MODULE." }

    try { Invoke-WebRequest -UseBasicParsing ("http://127.0.0.1:$Port/api/state") -TimeoutSec 1 | Out-Null; throw "Port $Port is already in use." }
    catch { if ($_.Exception.Message -eq "Port $Port is already in use.") { throw } }

    foreach ($relative in @('Maps', '.studio', 'metroidvania-studio')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $scratchRoot $relative) | Out-Null
    }
    $webOutput = Join-Path $scratchRoot 'metroidvania-studio/dist'
    & node (Join-Path $editorRoot 'build-web.mjs') $webOutput
    if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' }
    $artifacts = Join-Path $scratchRoot 'artifacts'
    & dotnet build (Join-Path $editorRoot 'server/MetroidvaniaStudio.Server.csproj') --configuration Release --nologo --artifacts-path $artifacts
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
    # Use a disposable workspace containing only project-owned public samples.
    Copy-Item -LiteralPath (Join-Path $projectRoot 'samples/maps/Sample.map.json') -Destination (Join-Path $scratchRoot 'Maps/Baseline.map.json')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'samples/catalog.json') -Destination (Join-Path $scratchRoot '.studio/catalog.json')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'samples/textures') -Destination (Join-Path $scratchRoot 'Textures') -Recurse
    $dll = Get-ChildItem (Join-Path $artifacts 'bin/MetroidvaniaStudio.Server') -Recurse -File -Filter 'MetroidvaniaStudio.Server.dll' | Select-Object -First 1 -ExpandProperty FullName
    if (-not $dll) { throw 'The isolated MetroidvaniaStudio.Server build output was not found.' }
    $stdout = Join-Path $scratchRoot 'server.log'; $stderr = Join-Path $scratchRoot 'server-error.log'
    $arguments = @(('"' + $dll + '"'), '--studio-root', ('"' + $projectRoot + '"'), '--project', ('"' + $scratchRoot + '"'), '--web-root', ('"' + $webOutput + '"'), '--port', $Port)
    $server = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $arguments -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ($server.HasExited) { throw ('Isolated server exited early: ' + (Get-Content -Raw $stderr -ErrorAction SilentlyContinue)) }
        try { $ready = (Invoke-WebRequest -UseBasicParsing ("http://127.0.0.1:$Port/api/state") -TimeoutSec 1).StatusCode -eq 200 } catch { $ready = $false }
        if (-not $ready) { Start-Sleep -Milliseconds 100 }
    } until ($ready -or [DateTime]::UtcNow -ge $deadline)
    if (-not $ready) { throw 'Timed out waiting for the isolated browser-test server.' }

    $env:METROIDVANIA_STUDIO_BASE_URL = "http://127.0.0.1:$Port"
    $env:METROIDVANIA_STUDIO_TEST_PROJECT_ROOT = $scratchRoot
    $env:METROIDVANIA_STUDIO_TEST_ISOLATED = '1'
    if ($ObjectsOnly) {
        & node (Join-Path $PSScriptRoot 'browser-object-triggers.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Object and trigger validation failed.' }
        return
    }
    if (-not $RoomOnly -and -not $SyncOnly -and -not $PerformanceOnly) {
        & node (Join-Path $PSScriptRoot 'browser-layer-activation.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Layer activation validation failed.' }
        & node (Join-Path $PSScriptRoot 'browser-camera-settings.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Camera settings validation failed.' }
        if ($CameraOnly) { return }
        & node (Join-Path $PSScriptRoot 'browser-standalone.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Standalone workflow validation failed.' }
        & node (Join-Path $PSScriptRoot 'browser-interaction.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Browser interaction validation failed.' }
    }
    if (-not $SyncOnly -and -not $PerformanceOnly) {
        & node (Join-Path $PSScriptRoot 'browser-room-context-menu.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Room context menu validation failed.' }
        & node (Join-Path $PSScriptRoot 'browser-room-workflow.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Room workflow validation failed.' }
        & node (Join-Path $PSScriptRoot 'browser-object-triggers.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Object and trigger validation failed.' }
        & node (Join-Path $PSScriptRoot 'browser-multi-room.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Multi-room selection validation failed.' }
        & node (Join-Path $PSScriptRoot 'browser-studio-workflow.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Studio workflow checks failed.' }
    }
    if (-not $RoomOnly -and -not $PerformanceOnly) {
        & node (Join-Path $PSScriptRoot 'browser-sync-status.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Export status validation failed.' }
    }
    if (-not $RoomOnly -and -not $SyncOnly) {
        & node (Join-Path $PSScriptRoot 'browser-performance.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Browser performance validation failed.' }
        & node (Join-Path $PSScriptRoot 'browser-brush-latency.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Brush input latency validation failed.' }
        & node (Join-Path $PSScriptRoot 'browser-tile-scaling.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Tile-count scaling validation failed.' }
    }
}
finally {
    $env:METROIDVANIA_STUDIO_BASE_URL = $oldBase
    $env:METROIDVANIA_STUDIO_TEST_PROJECT_ROOT = $oldProject
    $env:PLAYWRIGHT_MODULE = $oldPlaywright
    $env:METROIDVANIA_STUDIO_TEST_ISOLATED = $oldIsolated
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force; $server.WaitForExit() }
    $resolved = [IO.Path]::GetFullPath($scratchRoot)
    if ($resolved.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
