$ErrorActionPreference = 'Stop'
$studioPayload = $PSScriptRoot
$studioDotnet = $env:METROIDVANIA_STUDIO_DOTNET
if (!$studioDotnet) {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) { $studioDotnet = $command.Source }
}
if (!$studioDotnet) {
    foreach ($candidate in @("$env:ProgramFiles/dotnet/dotnet.exe", "$env:LOCALAPPDATA/Microsoft/dotnet/dotnet.exe", "$env:USERPROFILE/.dotnet/dotnet.exe")) {
        if (Test-Path -LiteralPath $candidate) { $studioDotnet = $candidate; break }
    }
}
if (!$studioDotnet) { throw 'Install ASP.NET Core Runtime 10 before starting this web package.' }
$studioArguments = @($args)
$studioAction = 'run'
if ($studioArguments.Count -gt 0 -and $studioArguments[0] -in @('--stop', '--check')) {
    $studioAction = if ($studioArguments[0] -eq '--stop') { 'stop' } else { 'check' }
    $studioArguments = @($studioArguments | Select-Object -Skip 1)
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$studioWorkspace = Split-Path -Parent $studioPayload
& $studioDotnet (Join-Path $studioPayload 'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll') $studioAction --studio-root $studioPayload --storage-root $studioWorkspace --auto-port @studioArguments
exit $LASTEXITCODE
