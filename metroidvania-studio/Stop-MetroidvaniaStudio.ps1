[CmdletBinding(PositionalBinding=$false)]
param(
    [ValidateRange(1024, 65535)][int]$Port = 18765,
    [Alias('ProjectPath')][string]$Project = '',
    [switch]$NoBrowser,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$RemainingArguments
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime-Tools.ps1')
$options = Merge-StudioOptions -Options @{ Port=$Port; Project=$Project; NoBrowser=[bool]$NoBrowser } -Arguments $RemainingArguments -Allowed @('Port','Project','NoBrowser')
$arguments = @('--port', [string]$options.Port, '--no-browser')
if ($options.Project) { $arguments += @('--project', [IO.Path]::GetFullPath($options.Project)) }
Invoke-StudioLauncher -Action stop -Arguments $arguments
