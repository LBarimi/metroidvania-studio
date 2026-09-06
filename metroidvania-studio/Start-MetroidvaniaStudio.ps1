param(
    [ValidateRange(1024, 65535)][int]$Port = 18765,
    [Alias('ProjectPath')][string]$Project = '',
    [switch]$Foreground,
    [switch]$Restart,
    [switch]$CheckOnly,
    [switch]$OpenBrowser,
    [string]$BuildDirectory = ''
)
$ErrorActionPreference = 'Stop'
$studioRoot = Split-Path $PSScriptRoot -Parent
if (!$CheckOnly -and !$BuildDirectory -and !(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'server/MetroidvaniaStudio.Server.dll'))) {
    & (Join-Path $PSScriptRoot 'Build-MetroidvaniaStudio.ps1')
}
& (Join-Path $PSScriptRoot 'Run-MetroidvaniaStudio.ps1') -Port $Port -Project $Project -NoBrowser:(!$OpenBrowser) -Restart:$Restart -Foreground:$Foreground -CheckOnly:$CheckOnly -BuildDirectory $BuildDirectory
