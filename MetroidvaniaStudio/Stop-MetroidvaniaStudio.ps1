param(
    [ValidateRange(1024, 65535)][int]$Port = 18765,
    [Alias('ProjectPath')][string]$Project = ''
)
$ErrorActionPreference = 'Stop'
$studioRoot = Split-Path $PSScriptRoot -Parent
$projectRoot = if ([string]::IsNullOrWhiteSpace($Project)) { Join-Path $studioRoot '.local/workspace' } else { $Project }
$projectRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$sessionPath = Join-Path $PSScriptRoot ".local/server-$Port.json"
$healthUrl = "http://127.0.0.1:$Port/api/health"
$health = $null
try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2 } catch { }
if (!(Test-Path -LiteralPath $sessionPath -PathType Leaf)) {
    if ($health) { throw 'A server is running without a matching launcher session record. Stop it through its original launcher.' }
    return
}
$record = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
if ($record.port -ne $Port -or ![string]::Equals($record.projectPath, $projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The recorded server belongs to another workspace; nothing was stopped.'
}
$serverProcess = Get-Process -Id ([int]$record.pid) -ErrorAction SilentlyContinue
if (!$serverProcess) {
    if ($health) { throw 'The port belongs to a different server; nothing was stopped.' }
    Remove-Item -LiteralPath $sessionPath
    return
}
# Modern PowerShell decodes ISO JSON timestamps into DateTime values already.
# Converting one back through the default string format would lose fractional ticks.
$expectedStart = if ($record.processStartUtc -is [DateTime]) {
    $record.processStartUtc.ToUniversalTime()
} else {
    [DateTime]::Parse([string]$record.processStartUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}
$command = Get-CimInstance Win32_Process -Filter "ProcessId = $($serverProcess.Id)"
if ($serverProcess.StartTime.ToUniversalTime() -ne $expectedStart -or !$command.CommandLine -or
    !$command.CommandLine.Contains($record.dllPath) -or $command.Name -ne 'dotnet.exe') {
    throw 'The recorded PID belongs to a different process; nothing was stopped.'
}
if (!$health -or $health.instanceId -ne $record.instanceId -or
    ![string]::Equals($health.projectPath, $projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The server session could not be verified. Keep it running to preserve pending edits.'
}
Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/shutdown" -Method Post -ContentType 'application/json' -Body '{}' -Headers @{ 'X-Metroidvania-Studio-Instance' = $record.instanceId } -TimeoutSec 30 | Out-Null
if (!$serverProcess.WaitForExit(15000)) { throw 'The server is still saving. It was not force-stopped; retry after it finishes.' }
Remove-Item -LiteralPath $sessionPath
Write-Output 'MetroidvaniaStudio saved its session and stopped.'
