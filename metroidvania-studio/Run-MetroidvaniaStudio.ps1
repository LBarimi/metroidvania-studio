[CmdletBinding(PositionalBinding=$false)]
param(
    [ValidateRange(1024, 65535)][int]$Port = 18765,
    [Alias('ProjectPath')][string]$Project = '',
    [switch]$NoBrowser,
    [switch]$Restart,
    [switch]$Foreground,
    [string]$BuildDirectory = '',
    [switch]$CheckOnly,
    [Parameter(ValueFromRemainingArguments=$true)][string[]]$RemainingArguments
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime-Tools.ps1')
$options = Merge-StudioOptions -Options @{ Port=$Port; Project=$Project; NoBrowser=[bool]$NoBrowser; Restart=[bool]$Restart; Foreground=[bool]$Foreground; BuildDirectory=$BuildDirectory; CheckOnly=[bool]$CheckOnly } -Arguments $RemainingArguments -Allowed @('Port','Project','NoBrowser','Restart','Foreground','BuildDirectory','CheckOnly')
$arguments = @('--port', [string]$options.Port)
if ($options.Project) { $arguments += @('--project', [IO.Path]::GetFullPath($options.Project)) }
if ($options.NoBrowser) { $arguments += '--no-browser' }
if ($options.Restart) { $arguments += '--restart' }
if ($options.Foreground) { $arguments += '--foreground' }
if ($options.BuildDirectory) { $arguments += @('--build-directory', [IO.Path]::GetFullPath($options.BuildDirectory)) }
Invoke-StudioLauncher -Action $(if ($options.CheckOnly) { 'check' } else { 'run' }) -Arguments $arguments
