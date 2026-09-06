@echo off
call "%~dp0..\..\platform\win\build.bat" %*
exit /b %errorlevel%
