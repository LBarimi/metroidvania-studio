param([string]$UnityPath = '', [switch]$Connection, [int]$Port = 18767)
$ErrorActionPreference = 'Stop'
$adapter = Split-Path $PSScriptRoot -Parent
$studio = [IO.Path]::GetFullPath((Join-Path $adapter '../..'))
if (!$UnityPath) { $UnityPath = Join-Path $env:ProgramFiles 'Unity/Hub/Editor/6000.3.9f1/Editor/Unity.exe' }
if (!(Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw 'Unity 6000.3.9f1 is required for adapter validation. Pass -UnityPath.' }
. (Join-Path $studio 'MetroidvaniaStudio/Runtime-Tools.ps1')
$node = Find-StudioRuntime -Name node.exe -VersionArgument --version -VersionPattern '^v(?:2[4-9]|[3-9][0-9]|[1-9][0-9]{2,})\.' -Requirement 'Node.js 24 or later is required.' -Locations @($env:METROIDVANIA_STUDIO_NODE, "$env:ProgramFiles/nodejs/node.exe")
& $node (Join-Path $adapter 'build-package.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Package build failed.' }
$version = (Get-Content -LiteralPath (Join-Path $studio 'version.json') -Raw | ConvertFrom-Json).version
$package = Join-Path $adapter "Builds/MetroidvaniaStudio-Unity-$version.unitypackage"
$project = Join-Path $adapter ('Builds/Validation/' + [Guid]::NewGuid().ToString('N'))
foreach ($relative in @('Assets/Editor', 'Assets/ValidationFixtures', 'Packages', 'ProjectSettings')) { New-Item -ItemType Directory -Path (Join-Path $project $relative) -Force | Out-Null }
Copy-Item -LiteralPath (Join-Path $studio 'Samples/catalog.json') -Destination (Join-Path $project 'Assets/ValidationFixtures/catalog.json')
Copy-Item -LiteralPath (Join-Path $studio 'Samples/Textures') -Destination (Join-Path $project 'Assets/ValidationFixtures/Textures') -Recurse
$manifest = @{ dependencies = @{'com.unity.modules.imageconversion'='1.0.0'; 'com.unity.modules.physics2d'='1.0.0'; 'com.unity.modules.tilemap'='1.0.0'; 'com.unity.modules.imgui'='1.0.0'; 'com.unity.modules.jsonserialize'='1.0.0'} } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $project 'Packages/manifest.json'), $manifest)
[IO.File]::WriteAllText((Join-Path $project 'ProjectSettings/ProjectVersion.txt'), "m_EditorVersion: 6000.3.9f1`n")
function Invoke-ValidationEditor([string[]]$Extra, [string]$LogName) {
    $log = Join-Path $project $LogName
    $arguments = @('-batchmode', '-nographics', '-projectPath', ('"' + $project + '"'), '-logFile', ('"' + $log + '"')) + $Extra
    $process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(180000)) { Stop-Process -Id $process.Id; throw "Validation timed out. See $log" }
    if ($process.ExitCode -ne 0) { throw "Validation failed. See $log" }
}
# Install before compiling test code that references the package.
Invoke-ValidationEditor -Extra @('-quit', '-importPackage', ('"' + $package + '"')) -LogName 'import.log'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Editor/StudioIntegrationValidation.cs') -Destination (Join-Path $project 'Assets/Editor/StudioIntegrationValidation.cs')
Invoke-ValidationEditor -Extra @('-executeMethod', 'StudioIntegrationValidation.Run') -LogName 'tests.log'
$result = Get-Content -LiteralPath (Join-Path $project 'validation-result.json') -Raw | ConvertFrom-Json
if ($result.failed -ne 0) { throw 'Integration checks failed.' }
Write-Output "Unity integration checks passed: $($result.passed). Results: $project"

if ($Connection) {
    $workspace = Join-Path $project 'StudioWorkspace'
    $previousUrl = $env:METROIDVANIA_STUDIO_TEST_URL
    $started = $false
    try {
        $env:METROIDVANIA_STUDIO_TEST_URL = "http://127.0.0.1:$Port/"
        & (Join-Path $studio 'MetroidvaniaStudio/Run-MetroidvaniaStudio.ps1') -NoBrowser -Project $workspace -Port $Port
        $started = $true
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Editor/StudioConnectionValidation.cs') -Destination (Join-Path $project 'Assets/Editor/StudioConnectionValidation.cs')
        Invoke-ValidationEditor -Extra @('-executeMethod', 'StudioConnectionValidation.Run') -LogName 'connection.log'
        Write-Output 'Local studio connection checks passed: 4.'
    } finally {
        if ($started) { & (Join-Path $studio 'MetroidvaniaStudio/Stop-MetroidvaniaStudio.ps1') -Project $workspace -Port $Port }
        $env:METROIDVANIA_STUDIO_TEST_URL = $previousUrl
    }
}
