@echo off
setlocal
cd /d "%~dp0"
if /i "%~1"=="nopause" goto quiet
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-on-emulator.ps1" %*
set "RC=%ERRORLEVEL%"
pause
exit /b %RC%
:quiet
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-on-emulator.ps1"
exit /b %ERRORLEVEL%
