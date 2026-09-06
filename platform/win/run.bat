@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\..\metroidvania-studio\Run-MetroidvaniaStudio.ps1" %*
set "studioExit=%errorlevel%"
if not "%studioExit%"=="0" if not defined CI if not defined METROIDVANIA_STUDIO_NO_PAUSE pause
exit /b %studioExit%
