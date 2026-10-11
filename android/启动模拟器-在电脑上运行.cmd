@echo off
chcp 936 >nul
setlocal EnableExtensions
cd /d "%~dp0"

rem ===========================================================================
rem  TimeGuard 安卓版 -- 在电脑上跑起来（安卓模拟器）
rem
rem  双击本文件即可：查加速驱动 -> 编译 APK -> 启动模拟器 -> 等开机 -> 覆盖安装
rem  -> 代授悬浮窗权限 -> 打开 App。
rem  改完代码再双击一次就自动重编重装，不用再往手机里导 APK。
rem
rem  带参数 nopause 调用则不等待按键（给自动化用）。
rem
rem  工具链装在 E:\android-dev 下（SDK / JDK17 / Gradle 8.9），这套路径是唯一的
rem  写死项；工程自身用 %~dp0，搬到别的盘也能跑。
rem
rem  两个路径必须写全：从 Git Bash 之类 PATH 里有 GNU 同名工具的环境里双击启动时，
rem  裸写 find / tasklist 会静默跑到错的程序上，判断就永远不会触发。
rem ===========================================================================

set "SDK=C:\Program Files\Android\Sdk"
set "JAVA_HOME=C:\Program Files\Microsoft\OpenJDK\jdk-21.0.12.1+1"
set "ANDROID_HOME=%SDK%"
set "ANDROID_SDK_ROOT=%SDK%"
set "ANDROID_AVD_HOME=%USERPROFILE%\.android\avd"

set "GRADLE=C:\Program Files\Gradle\gradle-8.9\bin\gradle.bat"
set "ADB=%SDK%\platform-tools\adb.exe"
set "EMU=%SDK%\emulator\emulator.exe"
set "DRV=%SDK%\extras\google\Android_Emulator_Hypervisor_Driver\silent_install.bat"
set "FIND=%SystemRoot%\System32\find.exe"
set "TASKLIST=%SystemRoot%\System32\tasklist.exe"

set "AVD=TimeGuard"
set "PKG=com.hzy.timeguard"

echo ============================================================
echo   TimeGuard 安卓版 - 在电脑上运行
echo ============================================================
echo.

rem ---------------------------------------------------------------- 1 加速驱动
echo [1/5] 检查模拟器加速驱动 ...
"%EMU%" -accel-check >"%TEMP%\tg-accel.txt" 2>&1
if not errorlevel 1 goto accel_ok

echo.
echo   ^>^> 加速驱动还没装，模拟器起不来（x86_64 镜像强制要求硬件加速）。
echo      装一次就永久有效，以后不用再管。
echo.
echo      请右键点下面这个文件，选「以管理员身份运行」：
echo      %DRV%
echo.
echo      装完再双击一次本脚本。（若它提示 Hyper-V 已开启，需先关掉 Hyper-V。）
echo.
echo      驱动自检原始输出：
type "%TEMP%\tg-accel.txt"
goto end_fail

:accel_ok
echo       就绪 --
type "%TEMP%\tg-accel.txt"
echo.

rem ---------------------------------------------------------------- 2 编译
echo [2/5] 编译 APK（首次较慢，之后走缓存）...
call "%GRADLE%" assembleRelease --console=plain -q
if errorlevel 1 goto build_fail

set "APK=app\build\outputs\apk\release\app-release.apk"
if not exist "%APK%" set "APK=app\build\outputs\apk\release\app-release-unsigned.apk"
if not exist "%APK%" set "APK=app\build\outputs\apk\debug\app-debug.apk"
if not exist "%APK%" goto no_apk
echo       产物：%APK%
echo.

rem ---------------------------------------------------------------- 3 启动模拟器
echo [3/5] 启动模拟器 ...
"%TASKLIST%" /FI "IMAGENAME eq qemu-system-x86_64.exe" /NH 2>nul | "%FIND%" /I "qemu" >nul
if not errorlevel 1 (
  echo       模拟器已经在跑，跳过启动。
) else (
  rem 交给 scripts\start-emulator.cmd 起：模拟器在**它自己的控制台窗口**里跑，
  rem 这样本脚本这个窗口关掉时，模拟器不会被一起带走
  rem （emulator.exe 是 GUI 程序，用 start 起会继承调用者的控制台，
  rem   控制台一关它就收到 CTRL_CLOSE 跟着退）。
  rem gpu / AVD 参数都在那边，本脚本只管「是不是已经在跑」。
  start "TimeGuard 模拟器" /min "%~dp0scripts\start-emulator.cmd" %AVD%
)
echo.

rem ---------------------------------------------------------------- 4 等开机
echo [4/5] 等模拟器开机（第一次冷启动约 1-3 分钟，之后十几秒）...
"%ADB%" wait-for-device
set /a N=0
:waitboot
set "BOOT="
for /f "delims=" %%i in ('"%ADB%" shell getprop sys.boot_completed 2^>nul') do set "BOOT=%%i"
if "%BOOT%"=="1" goto booted
set /a N+=1
if %N% GEQ 120 goto boot_timeout
rem 用 ping 等 2 秒：timeout 在 stdin 被重定向时会直接报错退出
ping -n 3 127.0.0.1 >nul
goto waitboot

:booted
echo       开机完成。
echo.

rem ---------------------------------------------------------------- 5 装 + 开
echo [5/5] 安装并打开 ...
"%ADB%" install -r "%APK%"
rem 悬浮窗权限正常要手点好几层设置，这里直接代授，省得每装一次翻一遍
"%ADB%" shell appops set %PKG% SYSTEM_ALERT_WINDOW allow
"%ADB%" shell pm grant %PKG% android.permission.POST_NOTIFICATIONS 2>nul
"%ADB%" shell am start -n %PKG%/.MainActivity
echo.
echo 完成。以后改完代码，只要模拟器还开着，再双击一次本脚本就会
echo   自动编译 → 覆盖安装 → 重启 App。
goto end_ok

:build_fail
echo.
echo   编译失败，上面有报错。这次什么都没装。
goto end_fail

:no_apk
echo.
echo   编译报告成功，但没找到 APK 文件，去 app\build\outputs\apk\ 下面看看。
goto end_fail

:boot_timeout
echo.
echo   等了 4 分钟还没开机完成。先单独启动模拟器看一眼窗口卡在哪：
echo     "%EMU%" -avd %AVD%
goto end_fail

:end_fail
set "RC=1"
goto bye

:end_ok
set "RC=0"

:bye
echo.
if /i not "%~1"=="nopause" pause
endlocal & exit /b %RC%
