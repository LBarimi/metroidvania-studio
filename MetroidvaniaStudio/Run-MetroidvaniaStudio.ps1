param(
    [ValidateRange(1024, 65535)][int]$Port = 18765,
    [Alias('ProjectPath')][string]$Project = '',
    [switch]$NoBrowser
)
$ErrorActionPreference = 'Stop'
$studioRoot = Split-Path $PSScriptRoot -Parent
$publishedDll = Join-Path $PSScriptRoot 'Server/MetroidvaniaStudio.Server.dll'
if (Test-Path -LiteralPath $publishedDll -PathType Leaf) {
    $buildDirectory = $studioRoot
} else {
    $buildsRoot = Join-Path $studioRoot 'Builds'
    $latest = Join-Path $buildsRoot 'latest.json'
    if (!(Test-Path -LiteralPath $latest -PathType Leaf)) {
        Write-Output 'No local build yet. Building once before the first launch...'
        & (Join-Path $PSScriptRoot 'Build-MetroidvaniaStudio.ps1')
    }
    $build = Get-Content -LiteralPath $latest -Raw | ConvertFrom-Json
    if ($build.formatVersion -ne 1 -or $build.folder -notmatch '^\d+\.\d+\.\d+-\d{8}-\d{6}-[a-f0-9]{8}$') {
        throw 'Invalid local build index. Run Build-MetroidvaniaStudio.bat again.'
    }
    $buildDirectory = Join-Path $buildsRoot $build.folder
}
& (Join-Path $PSScriptRoot 'Start-MetroidvaniaStudio.ps1') -BuildDirectory $buildDirectory -Port $Port -Project $Project -OpenBrowser:(!$NoBrowser)
