@echo off
call "%~dp0platform\win\run.bat" %*
exit /b %errorlevel%
