param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$Avd,
    [ValidatePattern('^emulator-[0-9]+$')][string]$Serial
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }
if (-not $sdk) { throw 'Set ANDROID_HOME to your Android SDK directory first.' }
$adb = Join-Path $sdk 'platform-tools/adb.exe'
$emulator = Join-Path $sdk 'emulator/emulator.exe'
if (-not (Test-Path -LiteralPath $adb)) { throw 'Android platform-tools/adb.exe is missing.' }
function EmulatorDevices {
    $lines = & $adb devices
    if ($LASTEXITCODE -ne 0) { throw 'adb devices failed.' }
    @($lines | ForEach-Object { if ($_ -match '^(emulator-[0-9]+)\s+device$') { $Matches[1] } })
}
$devices = @(EmulatorDevices)
if (-not $devices.Count) {
    if (-not (Test-Path -LiteralPath $emulator)) { throw 'Start an emulator in Android Studio first.' }
    if (-not $Avd) {
        $avds = @(& $emulator -list-avds | Where-Object { $_.Trim() })
        if ($LASTEXITCODE -ne 0 -or $avds.Count -ne 1) { throw 'Select an AVD with -Avd, or start one in Android Studio.' }
        $Avd = $avds[0].Trim()
    }
    Start-Process -FilePath $emulator -ArgumentList @('-avd', $Avd, '-no-boot-anim') -WindowStyle Hidden | Out-Null
    for ($i = 0; $i -lt 90 -and -not $devices.Count; $i++) {
        Start-Sleep -Seconds 2
        $devices = @(EmulatorDevices)
    }
}
if (-not $Serial) {
    if ($devices.Count -ne 1) { throw 'Select exactly one emulator with -Serial emulator-PORT.' }
    $Serial = $devices[0]
}
if ($Serial -notin $devices) { throw 'The requested emulator is not online.' }
$qemu = & $adb -s $Serial shell getprop ro.kernel.qemu
if ($LASTEXITCODE -ne 0 -or ($qemu -join '').Trim() -ne '1') { throw 'Refusing to install: target is not an emulator.' }
for ($i = 0; $i -lt 120; $i++) {
    $boot = & $adb -s $Serial shell getprop sys.boot_completed
    if ($LASTEXITCODE -eq 0 -and ($boot -join '').Trim() -eq '1') { break }
    if ($i -eq 119) { throw 'Emulator boot timed out.' }
    Start-Sleep -Seconds 2
}
& (Join-Path $PSScriptRoot 'build-android.ps1') -Debug
$apk = Join-Path $projectRoot 'dist/TimeGuard-debug.apk'
& $adb -s $Serial install -r $apk
if ($LASTEXITCODE -ne 0) { throw 'APK installation failed. An existing release-signed app needs a separate emulator or manual removal.' }
& $adb -s $Serial shell am start -n com.hzy.timeguard/.MainActivity
if ($LASTEXITCODE -ne 0) { throw 'Application launch failed.' }
Write-Output 'Xike is running on the selected emulator. Grant application permissions in Settings as needed.'
