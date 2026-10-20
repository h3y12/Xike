@echo off
rem Host the TimeGuard AVD in THIS console window ("TimeGuard emulator").
rem
rem Why foreground, no `start`: emulator.exe is a GUI-subsystem exe, so `start`
rem makes it inherit the caller's console. When that console window closes,
rem Windows sends CTRL_CLOSE to every process attached to it -> the emulator dies
rem together with the launcher script's window. Running it in the foreground here
rem gives it a console of its own, so closing the launcher script no longer kills
rem the emulator. Close THIS (minimized) window to stop the emulator.
rem
rem Keep this file ASCII-only: with no non-ASCII byte there is no GBK/chcp worry.
title TimeGuard emulator
set "ANDROID_HOME=C:\Program Files\Android\Sdk"
set "ANDROID_SDK_ROOT=C:\Program Files\Android\Sdk"
set "ANDROID_AVD_HOME=%USERPROFILE%\.android\avd"
set "AVD=TimeGuard"
if not "%~1"=="" set "AVD=%~1"

if not exist "%ANDROID_AVD_HOME%\%AVD%.avd" (
  echo AVD "%AVD%" not found under %ANDROID_AVD_HOME%
  echo Create it first, see TimeGuardAndroid README section "debug on PC".
  pause
  exit /b 1
)

echo Starting AVD %AVD% ... keep this window open while using the emulator.
rem -timezone: under RDP the emulator's auto timezone detection lands on GMT,
rem which makes the app's clock look 8 hours off from the host. Pin it.
"C:\Program Files\Android\Sdk\emulator\emulator.exe" -avd %AVD% -no-boot-anim -gpu swiftshader_indirect -timezone Asia/Shanghai
echo.
echo Emulator exited.
pause
