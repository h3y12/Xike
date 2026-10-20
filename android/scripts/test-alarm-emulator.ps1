param([string]$Serial = 'emulator-5556', [string]$Apk = "$PSScriptRoot\..\dist\息刻-安卓版.apk")
$ErrorActionPreference = 'Stop'
$adb = 'C:\Program Files\Android\Sdk\platform-tools\adb.exe'
function Adb([string[]]$Arguments) {
    $result = & $adb -s $Serial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "adb failed: $Arguments $result" }
    return ($result -join "`n")
}
if ($Serial -notlike 'emulator-*' -or (Adb @('shell','getprop','ro.kernel.qemu')).Trim() -ne '1') { throw 'This destructive fixture only supports an Android emulator.' }
function TapId([string]$Id) {
    $null = Adb @('shell','uiautomator','dump','/sdcard/xike-test.xml')
    [xml]$ui = Adb @('shell','cat','/sdcard/xike-test.xml')
    $node = $ui.SelectSingleNode("//node[@resource-id='$Id']")
    if (!$node) { throw "UI control absent: $Id" }
    if ($node.bounds -notmatch '\[(\d+),(\d+)\]\[(\d+),(\d+)\]') { throw 'Invalid bounds' }
    $x = [int](([int]$Matches[1]+[int]$Matches[3])/2)
    $y = [int](([int]$Matches[2]+[int]$Matches[4])/2)
    $null = Adb @('shell','input','tap',"$x","$y")
}
$null = Adb @('root')
$null = Adb @('install','-r',(Resolve-Path -LiteralPath $Apk).Path)
# Clear only this test installation, never a user's physical phone.
$null = Adb @('shell','pm','clear','com.hzy.timeguard')
$null = Adb @('shell','pm','grant','com.hzy.timeguard','android.permission.POST_NOTIFICATIONS')
$null = Adb @('shell','appops','set','com.hzy.timeguard','SCHEDULE_EXACT_ALARM','allow')
$null = Adb @('shell','appops','set','com.hzy.timeguard','USE_FULL_SCREEN_INTENT','allow')
$null = Adb @('shell','settings','put','global','auto_time','0')
# Emulator clock only; 07:00 UTC is the editor's default time.
$null = Adb @('shell','cmd','alarm','set-timezone','GMT')
$day = [DateTimeOffset]::new(2026,10,9,6,50,0,[TimeSpan]::Zero).ToUnixTimeMilliseconds()
$null = Adb @('shell','cmd','alarm','set-time',"$day")
$null = Adb @('shell','input','keyevent','KEYCODE_WAKEUP')
$null = Adb @('shell','wm','dismiss-keyguard')
$null = Adb @('shell','am','start','-n','com.hzy.timeguard/.MainActivity')
Start-Sleep -Seconds 2
$null = Adb @('shell','input','swipe','360','1100','360','450','300')
TapId 'com.hzy.timeguard:id/btnAlarmAdd'
TapId 'com.hzy.timeguard:id/rbAlarmOnce'
TapId 'android:id/button1'
$registration = Adb @('shell','dumpsys','alarm')
if ($registration -notmatch 'com.hzy.timeguard.alarm.FIRE') { throw 'Saved alarm was not registered' }
$null = Adb @('shell','input','keyevent','KEYCODE_HOME')
$due = $day + 600000
$null = Adb @('shell','cmd','alarm','set-time',"$($due - 20000)")
Start-Sleep -Seconds 1
$testPid = (Adb @('shell','pidof','com.hzy.timeguard')).Trim()
$null = Adb @('shell','kill','-9',$testPid)
$null = Adb @('shell','input','keyevent','KEYCODE_SLEEP')
Start-Sleep -Seconds 21
$services = Adb @('shell','dumpsys','activity','services','com.hzy.timeguard')
if ($services -notmatch 'isForeground=true foregroundId=1002') { throw 'System failed to cold-start the ringing service at scheduled time' }
if ($services -match 'OverlayService') { throw 'Unexpected overlay dependency' }
$activities = Adb @('shell','dumpsys','activity','activities')

$notifications = Adb @('shell','dumpsys','notification','--noredact')
$audio = Adb @('shell','dumpsys','audio')
$pref = Adb @('shell','cat','/data/user/0/com.hzy.timeguard/shared_prefs/timeguard.xml')
if ($pref -notmatch '<string name="alarms"\s*/>|<string name="alarms"></string>') { throw 'One-shot remained after firing' }
$folder = Join-Path $PSScriptRoot '..\_verify'
$services | Set-Content -LiteralPath "$folder\emulator-ring-services.txt"
$activities | Set-Content -LiteralPath "$folder\emulator-ring-activity.txt"
$notifications | Set-Content -LiteralPath "$folder\emulator-ring-notification.txt"
$audio | Set-Content -LiteralPath "$folder\emulator-ring-audio.txt"
if ($activities -notmatch '(?:mResumedActivity|ResumedActivity):.*AlarmRingActivity') { throw 'No ringing controls on lock screen' }
TapId 'com.hzy.timeguard:id/btnAlarmDismiss'
Start-Sleep -Seconds 1
$services = Adb @('shell','dumpsys','activity','services','com.hzy.timeguard')
if ($services -match 'ServiceRecord.*AlarmRingService') { throw 'Lock-screen stop did not stop ringing' }
'PASS: real UI saves one-shot; no overlay; process killed; locked; system cold-starts alarm; lock-screen controls shown; one-shot deleted; stop dismisses service.'
# Restore network time for this emulator.
$null = Adb @('shell','settings','put','global','auto_time','1')
