@echo off
setlocal
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Install-Integration.ps1" %*
if errorlevel 1 (
  echo Installation failed. See the error above.
  pause
  exit /b 1
)
pause
