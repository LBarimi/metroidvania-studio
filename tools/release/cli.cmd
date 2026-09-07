@echo off
setlocal DisableDelayedExpansion
set "studioDotnet=%METROIDVANIA_STUDIO_DOTNET%"
if not defined studioDotnet if exist "%~dp0app\runtime\win-x64\dotnet.exe" set "studioDotnet=%~dp0app\runtime\win-x64\dotnet.exe"
if not defined studioDotnet if exist "%ProgramFiles%\dotnet\dotnet.exe" set "studioDotnet=%ProgramFiles%\dotnet\dotnet.exe"
if not defined studioDotnet set "studioDotnet=dotnet.exe"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
"%studioDotnet%" "%~dp0app\metroidvania-studio\cli\MetroidvaniaStudio.Cli.dll" %*
exit /b %errorlevel%
