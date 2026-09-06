@echo off
call "%~dp0..\..\platform\win\web\build.bat" %*
exit /b %errorlevel%
