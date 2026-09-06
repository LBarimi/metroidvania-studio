param([switch]$NoDownload)
$ErrorActionPreference = 'Stop'
$cmake = Get-Command cmake -ErrorAction SilentlyContinue
$cmakePath = if ($cmake) { $cmake.Source } else { $null }
if (!$cmakePath) {
    $finder = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $finder) {
        $installation = & $finder -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installation) {
            $candidate = Join-Path $installation 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
            if (Test-Path -LiteralPath $candidate) { $cmakePath = $candidate }
        }
    }
}
if (!$cmakePath) { throw 'Install CMake 3.24+ and a C++17 compiler, or Visual Studio with Desktop development with C++ and CMake tools.' }
$arguments = @('-S',$PSScriptRoot,'-B',(Join-Path $PSScriptRoot 'builds'))
if ($NoDownload) { $arguments += '-DSTUDIO_FETCH_SDL=OFF' }
& $cmakePath @arguments
if ($LASTEXITCODE -ne 0) { throw 'SDL configuration failed.' }
& $cmakePath --build (Join-Path $PSScriptRoot 'builds') --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw 'SDL build failed.' }
Write-Host 'Build complete. Run run.bat to preview a room.'
