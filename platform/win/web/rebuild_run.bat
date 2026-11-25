@echo off
setlocal DisableDelayedExpansion

echo [1/3] Stop Metroidvania Studio
set "studioPath=%~dp0..\..\..\metroidvania-studio"
if exist "%studioPath%\launcher\MetroidvaniaStudio.Launcher.dll" goto stop
if exist "%studioPath%\launcher\bin\Release\net10.0\MetroidvaniaStudio.Launcher.dll" goto stop
if exist "%studioPath%\.local\server-*.json" goto stop
echo No matching background session is running.
goto build

:stop
call "%~dp0stop.bat" %*
if errorlevel 1 exit /b %errorlevel%

:build
echo [2/3] Rebuild Metroidvania Studio
call "%~dp0build.bat" --rebuild
if errorlevel 1 exit /b %errorlevel%

echo [3/3] Run Metroidvania Studio
call "%~dp0run.bat" %*
exit /b %errorlevel%
