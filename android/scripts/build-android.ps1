param([switch]$SkipTests, [switch]$Debug)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$gradle = Join-Path $projectRoot 'gradlew.bat'
if (-not $env:GRADLE_USER_HOME) { $env:GRADLE_USER_HOME = Join-Path $projectRoot '.gradle-user-home' }
if (-not $env:ANDROID_USER_HOME) { $env:ANDROID_USER_HOME = Join-Path $projectRoot '.toolchain/android-user' }
# Respect developer JAVA_HOME / PATH and ANDROID_HOME / local.properties.
if (-not (Test-Path -LiteralPath $gradle)) { throw 'Gradle Wrapper is missing.' }
Push-Location $projectRoot
try {
    $variant = if ($Debug) { 'debug' } else { 'release' }
    $tasks = @(if ($Debug) { ':app:assembleDebug' } else { ':app:assembleRelease' })
    if (-not $SkipTests) { $tasks += ':app:testDebugUnitTest' }
    & $gradle --no-daemon --no-parallel --console=plain @tasks
    if ($LASTEXITCODE -ne 0) { throw "Android build failed: $LASTEXITCODE" }
    # Use AGP's current output metadata so a stale signed APK cannot hide an unsigned build.
    $outputDir = Join-Path $projectRoot "app/build/outputs/apk/$variant"
    $metadata = Get-Content -LiteralPath (Join-Path $outputDir 'output-metadata.json') -Raw | ConvertFrom-Json
    if (@($metadata.elements).Count -ne 1) { throw 'Expected one universal APK.' }
    $apk = Join-Path $outputDir $metadata.elements[0].outputFile
    $targetName = if ($Debug) { 'TimeGuard-debug.apk' } else { '息刻-安卓版.apk' }
    if (-not (Test-Path -LiteralPath $apk)) { throw 'Build completed without an APK.' }
    if ([IO.Path]::GetFileName($apk) -like '*-unsigned.apk') {
        $targetName = 'TimeGuard-release-unsigned.apk'
        Write-Warning 'Release is unsigned. Use -Debug for an installable development APK.'
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'dist') | Out-Null
    Copy-Item -LiteralPath $apk -Destination (Join-Path $projectRoot "dist/$targetName") -Force
    Write-Output 'APK ready in dist.'
} finally {
    Pop-Location
}
