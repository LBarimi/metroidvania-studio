@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0src\build.ps1" %*
if errorlevel 1 pause
