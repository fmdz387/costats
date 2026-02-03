@echo off
REM Quick publish script for costats
REM Usage: publish.cmd [version] [platform]
REM Example: publish.cmd 1.0.0 x64

set VERSION=%1
set PLATFORM=%2

if "%VERSION%"=="" set VERSION=1.0.0
if "%PLATFORM%"=="" set PLATFORM=all

powershell -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Version %VERSION% -Platform %PLATFORM%
