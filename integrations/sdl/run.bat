@echo off
setlocal
cd /d "%~dp0"
set "preview=%~dp0builds\Release\metroidvania-studio-preview.exe"
if not exist "%preview%" set "preview=%~dp0builds\metroidvania-studio-preview.exe"
if not exist "%preview%" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Sdl.ps1"
  if errorlevel 1 (
    pause
    exit /b 1
  )
  set "preview=%~dp0builds\Release\metroidvania-studio-preview.exe"
)
if "%~1"=="" (
  "%preview%" "%~dp0samples\maps\Sample.map.json" "%~dp0samples\catalog.json" "%~dp0samples"
) else (
  "%preview%" %*
)
if errorlevel 1 pause
