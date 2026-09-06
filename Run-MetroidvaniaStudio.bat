@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0MetroidvaniaStudio\Run-MetroidvaniaStudio.ps1" %*
set "studioExitCode=%errorlevel%"
if "%studioExitCode%"=="0" exit /b 0
echo.
echo MetroidvaniaStudio could not run. See the error above.
if not defined METROIDVANIA_STUDIO_NO_PAUSE pause
exit /b %studioExitCode%
