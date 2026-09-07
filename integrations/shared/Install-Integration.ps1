param([string]$ProjectFile, [string]$EngineRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = New-Object System.Text.UTF8Encoding($false)
$isGodot = Test-Path -LiteralPath (Join-Path $PSScriptRoot 'addons/metroidvania-studio/plugin.cfg')
$source = if ($isGodot) { Join-Path $PSScriptRoot 'addons/metroidvania-studio' } else { Join-Path $PSScriptRoot 'plugin/MetroidvaniaStudio' }
if (!(Test-Path -LiteralPath $source -PathType Container)) { throw 'Extract the complete package before running install.bat.' }
if (!$ProjectFile) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = 'Select the project to install Metroidvania Studio into'
    $dialog.Filter = if ($isGodot) { 'Godot project|project.godot' } else { 'Unreal project|*.uproject' }
    try {
        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }
        $ProjectFile = $dialog.FileName
    } finally { $dialog.Dispose() }
}
$project = (Resolve-Path -LiteralPath $ProjectFile).ProviderPath
if ($isGodot -and [IO.Path]::GetFileName($project) -ne 'project.godot') { throw 'Select project.godot.' }
if (!$isGodot -and [IO.Path]::GetExtension($project) -ne '.uproject') { throw 'Select a .uproject file.' }
$projectRoot = Split-Path -Parent $project
$relative = if ($isGodot) { 'addons/metroidvania-studio' } else { 'Plugins/MetroidvaniaStudio' }
$destination = [IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
if (!$destination.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid destination.' }
# Reject directory links before writing into an existing project.
$probe = $destination
while ($probe -and $probe.Length -ge $projectRoot.Length) {
    if (Test-Path -LiteralPath $probe) {
        if ((Get-Item -LiteralPath $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation through a directory link is not supported.' }
    }
    $probe = Split-Path -Parent $probe
}
$original = [IO.File]::ReadAllText($project)
if ($isGodot) {
    $plugin = 'res://addons/metroidvania-studio/plugin.cfg'
    $sectionPattern = '(?ms)^\[editor_plugins\][ \t]*\r?\n(?<body>.*?)(?=^\[|\z)'
    $section = [regex]::Match($original, $sectionPattern)
    if ($section.Success) {
        $body = $section.Groups['body'].Value
        $enabled = [regex]::Match($body, '(?m)^enabled\s*=\s*PackedStringArray\((?<items>[^\r\n]*)\)[ \t]*\r?$')
        if ($enabled.Success) {
            if (!$enabled.Groups['items'].Value.Contains('"' + $plugin + '"')) {
                $items = $enabled.Groups['items'].Value.Trim()
                $items = if ($items) { $items + ', "' + $plugin + '"' } else { '"' + $plugin + '"' }
                $body = $body.Substring(0,$enabled.Index) + 'enabled=PackedStringArray(' + $items + ')' + $body.Substring($enabled.Index+$enabled.Length)
            }
        } elseif ($body -match '(?m)^enabled\s*=') { throw 'Unrecognized plugin list. Install the addon manually and enable it in Project Settings.' }
        else { $body = 'enabled=PackedStringArray("' + $plugin + '")' + "`n" + $body }
        $updated = $original.Substring(0,$section.Index) + "[editor_plugins]`n" + $body + $original.Substring($section.Index+$section.Length)
    } else { $updated = $original.TrimEnd() + "`n`n[editor_plugins]`n" + 'enabled=PackedStringArray("' + $plugin + '")' + "`n" }
} else {
    $data = $original | ConvertFrom-Json
    $manifestPath = Join-Path $PSScriptRoot 'package-manifest.json'
    if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Extract the complete package before installing.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    . (Join-Path $PSScriptRoot 'Resolve-UnrealEngine.ps1')
    $association = if ($data.PSObject.Properties.Name -contains 'EngineAssociation') { [string]$data.EngineAssociation } else { '' }
    $engine = Resolve-StudioUnrealEngine -ExplicitRoot $EngineRoot -PackageEngine $manifest.engine -Association $association
    $EngineRoot = $engine.Root
    $runner = Join-Path $EngineRoot 'Engine/Build/BatchFiles/RunUAT.bat'
    if (!(Test-Path -LiteralPath $runner)) { throw 'Unreal build tools are missing.' }
    $buildRoot = Join-Path ([IO.Path]::GetTempPath()) ('ms-ue-' + [Guid]::NewGuid().ToString('N').Substring(0,12))
    Write-Host 'Installing MetroidvaniaStudio...'
    $buildArgs = @('BuildPlugin', ('-Plugin=' + (Join-Path $source 'MetroidvaniaStudio.uplugin')), ('-Package=' + $buildRoot), '-TargetPlatforms=Win64', '-UTF8Output')
    if ($engine.Major -eq 4) { $buildArgs += '-VS2019' }
    & $runner @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed. The target project has not been changed.' }
    $editorBinary = if ($engine.Major -eq 4) { 'UE4Editor' } else { 'UnrealEditor' }
    if (!(Test-Path -LiteralPath (Join-Path $buildRoot ('Binaries/Win64/' + $editorBinary + '-MetroidvaniaStudio.dll')))) { throw 'The compiled plugin is incomplete.' }
    $source = $buildRoot
    $plugins = @(if ($data.PSObject.Properties.Name -contains 'Plugins') { $data.Plugins | Where-Object { $null -ne $_ } })
    $found = $false
    foreach ($entry in $plugins) { if ($entry.Name -eq 'MetroidvaniaStudio') { $entry | Add-Member -NotePropertyName Enabled -NotePropertyValue $true -Force; $found = $true } }
    if (!$found) { $plugins += [pscustomobject]@{Name='MetroidvaniaStudio';Enabled=$true} }
    $data | Add-Member -NotePropertyName Plugins -NotePropertyValue @($plugins) -Force
    $updated = $data | ConvertTo-Json -Depth 100
}
foreach ($entry in Get-ChildItem -LiteralPath $source -Recurse -Force) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package links are not supported.' }
}
$parent = Split-Path -Parent $destination
[IO.Directory]::CreateDirectory($parent) | Out-Null
$staging = Join-Path $parent ('.studio-install-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $projectRoot ('.metroidvania-studio-backups/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($backup) | Out-Null
Copy-Item -LiteralPath $project -Destination (Join-Path $backup ([IO.Path]::GetFileName($project)))
Copy-Item -LiteralPath $source -Destination $staging -Recurse
$hadPrevious = Test-Path -LiteralPath $destination
$installed = $false
try {
    if ($hadPrevious) { Move-Item -LiteralPath $destination -Destination (Join-Path $backup 'previous-plugin') }
    Move-Item -LiteralPath $staging -Destination $destination
    $installed = $true
    [IO.File]::WriteAllText($project + '.studio-tmp', $updated, $utf8)
    Move-Item -LiteralPath ($project + '.studio-tmp') -Destination $project -Force
} catch {
    if ($installed -and (Test-Path -LiteralPath $destination)) { Move-Item -LiteralPath $destination -Destination (Join-Path $backup 'failed-plugin') }
    if (Test-Path -LiteralPath (Join-Path $backup 'previous-plugin')) { Move-Item -LiteralPath (Join-Path $backup 'previous-plugin') -Destination $destination }
    [IO.File]::WriteAllText($project,$original,$utf8)
    throw
}
Write-Host ('Installed: ' + $destination)
Write-Host ('Backup: ' + $backup)
Write-Host 'Open the project when ready. No editor was launched by this installer.'
