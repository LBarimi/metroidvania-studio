@echo off
call "%~dp0platform\win\build.bat" %*
exit /b %errorlevel%
