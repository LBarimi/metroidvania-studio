@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Sdl.ps1" %*
if errorlevel 1 (
  echo Build failed. See the output above.
  pause
  exit /b 1
)
pause
