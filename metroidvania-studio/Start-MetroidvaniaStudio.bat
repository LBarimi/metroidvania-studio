@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 goto :directory_error

echo Starting MetroidvaniaStudio...
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-MetroidvaniaStudio.ps1" -OpenBrowser %*
if errorlevel 1 goto :failed

popd
exit /b 0
:failed
set "launcherExitCode=%errorlevel%"
echo.
echo MetroidvaniaStudio could not start. See the error above.
popd
pause
exit /b %launcherExitCode%
:directory_error
echo Could not open the studio folder.
pause
exit /b 1
