@echo off
setlocal
pushd "%~dp0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-DeskSideDisplay.ps1"
if errorlevel 1 pause
popd
exit /b %errorlevel%
