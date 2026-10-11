@echo off
rem ---------------------------------------------------------------------------
rem  Republish TimeGuard to dist\ -- double-click this file after every change.
rem
rem  Why this exists: the desktop shortcut points at dist\TimeGuard.exe, which
rem  is a *single-file publish* output. bin\Release is NOT what the shortcut
rem  runs. If you forget to publish, clicking the icon still starts the old
rem  build even though the code and all self-tests are up to date.
rem
rem  ASCII only on purpose: no non-ASCII text in .cmd files.
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0"

echo [1/3] Checking whether dist\TimeGuard.exe is running ...
rem  Full path to find.exe on purpose: if this script is ever started from a
rem  shell whose PATH has GNU find first (Git Bash, MSYS), a bare "find /I"
rem  silently runs the wrong tool and the check below never fires.
set FIND=%SystemRoot%\System32\find.exe
tasklist /FI "IMAGENAME eq TimeGuard.exe" /NH 2>nul | "%FIND%" /I "TimeGuard.exe" >nul
if not errorlevel 1 (
  echo.
  echo    TimeGuard is still running. Nothing was changed.
  echo    Close it first: right-click the widget or the tray icon, then Exit.
  echo    Do NOT just leave it running -- the second copy would quit at once
  echo    because of the single-instance lock, and you would keep looking at
  echo    the OLD window.
  exit /b 1
)

set DOTNET=dotnet
if exist "%ProgramFiles%\dotnet\dotnet.exe" set DOTNET="%ProgramFiles%\dotnet\dotnet.exe"

echo [2/3] Publishing to dist\ ...
%DOTNET% publish -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:DebugType=none -o dist -v minimal
if errorlevel 1 (
  echo.
  echo    publish FAILED. Nothing usable was produced.
  exit /b 1
)

echo [3/3] Result:
dir dist\TimeGuard.exe | "%FIND%" /I "TimeGuard.exe"
echo.
echo    Note: MSBuild skips a file whose content is unchanged, so an unchanged
echo    timestamp right after publishing is normal -- what matters is that the
echo    publish step above reported success.
echo.
echo    The desktop shortcut points at this file, so clicking the icon now
echo    starts the build you just made.
endlocal
