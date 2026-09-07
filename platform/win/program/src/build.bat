@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "studioExit=%errorlevel%"
if not "%studioExit%"=="0" if not defined CI pause
exit /b %studioExit%
