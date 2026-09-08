[CmdletBinding(PositionalBinding=$false)]
param([switch]$CheckOnly, [switch]$Rebuild, [Parameter(ValueFromRemainingArguments=$true)][string[]]$RemainingArguments)
$ErrorActionPreference = 'Stop'
$studioRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Runtime-Tools.ps1')
$options = Merge-StudioOptions -Options @{ CheckOnly=[bool]$CheckOnly; Rebuild=[bool]$Rebuild } -Arguments $RemainingArguments -Allowed @('CheckOnly','Rebuild')
$node = Find-StudioRuntime -Name node.exe -VersionArgument --version -VersionPattern '^v(?:2[4-9]|[3-9][0-9]|[1-9][0-9]{2,})\.' -Requirement 'Node.js 24 or later is required for building.' -Locations @("$env:ProgramFiles/nodejs/node.exe", "$env:LOCALAPPDATA/Programs/nodejs/node.exe")
$dotnet = Find-StudioRuntime -Name dotnet.exe -Locations (Get-StudioDotnetLocations) -VersionArgument --list-sdks -VersionPattern '(?m)^10\.' -Requirement '.NET SDK 10 is required for building.'
$arguments = @('--studio-root', $studioRoot, '--dotnet', $dotnet)
if ($options.CheckOnly) { $arguments += '--check' }
if ($options.Rebuild) { $arguments += '--rebuild' }
$previous = $env:METROIDVANIA_STUDIO_DOTNET
try {
    $env:METROIDVANIA_STUDIO_DOTNET = $dotnet
    & $node (Join-Path $studioRoot 'platform/shared/build.mjs') @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. The previous successful build is still available.' }
} finally { $env:METROIDVANIA_STUDIO_DOTNET = $previous }
