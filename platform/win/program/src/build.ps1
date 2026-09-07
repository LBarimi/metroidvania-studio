[CmdletBinding()]
param([switch]$IncludeRuntime)
$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$selfContained = if ($IncludeRuntime) { 'true' } else { 'false' }
$studioRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '../../../..'))
$localRoot = Join-Path $studioRoot '.local'
[IO.Directory]::CreateDirectory($localRoot) | Out-Null
. (Join-Path $studioRoot 'metroidvania-studio/Runtime-Tools.ps1')
$node = Find-StudioRuntime -Name node.exe -VersionArgument --version -VersionPattern '^v(?:2[4-9]|[3-9][0-9]|[1-9][0-9]{2,})\.' -Requirement 'Node.js 24 or later is required for building.' -Locations @("$env:ProgramFiles/nodejs/node.exe")
$dotnet = Find-StudioRuntime -Name dotnet.exe -Locations (Get-StudioDotnetLocations) -VersionArgument --list-sdks -VersionPattern '(?m)^10\.' -Requirement '.NET SDK 10 is required for building.'
$buildLock = [IO.File]::Open((Join-Path $localRoot 'desktop-build.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$previousPackages = $env:NUGET_PACKAGES
$previousDotnet = $env:METROIDVANIA_STUDIO_DOTNET
$env:NUGET_PACKAGES = Join-Path $localRoot 'desktop-nuget'
$env:METROIDVANIA_STUDIO_DOTNET = $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
try {
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $stage = Join-Path $localRoot ("desktop-builds/" + $stamp)
    $payload = Join-Path $stage 'app'
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    & $node (Join-Path $studioRoot 'metroidvania-studio/build-web.mjs') (Join-Path $payload 'metroidvania-studio/dist')
    if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' }
    $styleFile = Join-Path $payload 'metroidvania-studio/dist/styles.css'
    [IO.File]::AppendAllText($styleFile, [IO.File]::ReadAllText((Join-Path $sourceRoot 'desktop.css')), [Text.UTF8Encoding]::new($false))
    $appFile = Join-Path $payload 'metroidvania-studio/dist/app.js'
    $desktopLabels = @{}
    foreach ($row in (Import-Csv -LiteralPath (Join-Path $sourceRoot 'desktop-locale.csv') -Encoding UTF8)) {
        $desktopLabels[$row.Key] = @{ KR=$row.KR; EN=$row.EN }
    }
    $desktopScript = 'const desktopLabels = ' + ($desktopLabels | ConvertTo-Json -Depth 4 -Compress) + ";`n" + [IO.File]::ReadAllText((Join-Path $sourceRoot 'desktop-bridge.js'))
    [IO.File]::AppendAllText($appFile, $desktopScript, [Text.UTF8Encoding]::new($false))
    foreach ($relative in @('samples', 'docs', 'metroidvania-studio/localization', 'metroidvania-studio/contracts/FORMAT.md', 'metroidvania-studio/contracts/map-format-v2.schema.json')) {
        $destination = Join-Path $payload $relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $studioRoot $relative) -Destination $destination -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $studioRoot 'tools/release/cli.cmd') -Destination (Join-Path $stage 'metroidvania-studio-cli.cmd')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'desktop-self-test.js') -Destination (Join-Path $payload 'desktop-self-test.js')
    & (Join-Path $sourceRoot 'build-icon.ps1')
    $projectFile = Join-Path $sourceRoot 'MetroidvaniaStudio.Desktop.csproj'
    $version = (Get-Content -LiteralPath (Join-Path $studioRoot 'version.json') -Raw | ConvertFrom-Json).version
    & $node (Join-Path $studioRoot 'tools/scripting/build-runtime.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Lua runtime build failed.' }
    $cliProject = Join-Path $studioRoot 'metroidvania-studio/cli/MetroidvaniaStudio.Cli.csproj'
    & $dotnet build $cliProject --no-incremental -c Release --self-contained false '-p:UseAppHost=false' '-p:UseSharedCompilation=false' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$studioRoot=/_/src" "-p:Version=$version" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Automation worker build failed.' }
    & $dotnet publish $cliProject --no-build --no-restore -c Release --self-contained false '-p:UseAppHost=false' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$studioRoot=/_/src" "-p:Version=$version" --output (Join-Path $payload 'metroidvania-studio/cli') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Automation worker publish failed.' }
    & $dotnet restore $projectFile --runtime win-x64 --configfile (Join-Path $sourceRoot 'NuGet.Config') "-p:SelfContained=$selfContained" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop dependencies could not be restored.' }
    & $dotnet build $projectFile --no-incremental --configuration Release --runtime win-x64 --self-contained $selfContained --no-restore "-p:EnableCompressionInSingleFile=$selfContained" '-p:UseSharedCompilation=false' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$studioRoot=/_/src" "-p:Version=$version" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop rebuild failed.' }
    & $dotnet publish $projectFile --configuration Release --runtime win-x64 --self-contained $selfContained --no-restore --output $stage "-p:EnableCompressionInSingleFile=$selfContained" '-p:UseSharedCompilation=false' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$studioRoot=/_/src" "-p:Version=$version" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    foreach ($notice in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'NOTICE')) {
        $path = Join-Path $studioRoot $notice
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $stage $notice) }
    }
    $noticeRoot = Join-Path $payload 'notices'
    [IO.Directory]::CreateDirectory($noticeRoot) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $env:NUGET_PACKAGES 'microsoft.web.webview2/1.0.4191.47') -File |
        Where-Object { $_.Name -match 'license|notice' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeRoot ('webview2-' + $_.Name)) }
    Get-ChildItem -LiteralPath $env:NUGET_PACKAGES -Directory | Where-Object Name -Match 'runtime.win-x64' | ForEach-Object {
        $packageName = $_.Name
        Get-ChildItem -LiteralPath $_.FullName -Directory | ForEach-Object {
            $packageVersion = $_.Name
            Get-ChildItem -LiteralPath $_.FullName -File | Where-Object Name -Match 'license|notice' | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeRoot ($packageName + '-' + $packageVersion + '-' + $_.Name.ToLowerInvariant()))
            }
        }
    }
    [IO.File]::WriteAllText((Join-Path $stage 'desktop-settings.json'), "{`n  `"workspaceRelativePath`": `"../../../.local/workspace`"`n}`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $stage 'build-info.json'), (@{ version=$version; builtAt=[DateTime]::UtcNow.ToString('O'); target='win-x64'; mode='local-program'; runtimeIncluded=[bool]$IncludeRuntime } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $runtimeText = if ($IncludeRuntime) {
        'This build includes .NET 10. Microsoft WebView2 Runtime must be installed.'
    } else {
        '.NET 10 Desktop Runtime (x64), ASP.NET Core Runtime 10 (x64), and Microsoft WebView2 Runtime must be installed. This build shares the installed runtimes to reduce application size.'
    }
    [IO.File]::WriteAllText((Join-Path $stage 'runtime-requirements.txt'), $runtimeText + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $output = [IO.Path]::GetFullPath((Join-Path $studioRoot 'builds/win/program'))
    $backup = [IO.Path]::GetFullPath((Join-Path $localRoot ("desktop-backups/" + $stamp)))
    foreach ($target in @($stage, $output, $backup)) {
        if (!$target.StartsWith($studioRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build paths must stay inside the studio checkout.' }
    }
    if (!(Test-Path -LiteralPath (Join-Path $stage 'metroidvania-studio.exe'))) { throw 'The executable was not created.' }
    [IO.Directory]::CreateDirectory((Split-Path $output -Parent)) | Out-Null
    if (Test-Path -LiteralPath $output) {
        [IO.Directory]::CreateDirectory((Split-Path $backup -Parent)) | Out-Null
        Move-Item -LiteralPath $output -Destination $backup
    }
    try { Move-Item -LiteralPath $stage -Destination $output }
    catch {
        if ((Test-Path -LiteralPath $backup) -and !(Test-Path -LiteralPath $output)) { Move-Item -LiteralPath $backup -Destination $output }
        throw
    }
    Write-Output ("Program ready: " + (Join-Path $output 'metroidvania-studio.exe'))
} finally {
    $buildLock.Dispose()
    $env:NUGET_PACKAGES = $previousPackages
    $env:METROIDVANIA_STUDIO_DOTNET = $previousDotnet
}
