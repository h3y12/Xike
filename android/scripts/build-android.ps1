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
    $versionName = [string]$metadata.elements[0].versionName
    if ($versionName -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected release version.' }
    $targetName = if ($Debug) { 'TimeGuard-debug.apk' } else { "息刻-安卓版-$versionName.apk" }
    if (-not (Test-Path -LiteralPath $apk)) { throw 'Build completed without an APK.' }
    if ([IO.Path]::GetFileName($apk) -like '*-unsigned.apk') {
        $targetName = 'TimeGuard-release-unsigned.apk'
        Write-Warning 'Release is unsigned. Use -Debug for an installable development APK.'
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'dist') | Out-Null
    Copy-Item -LiteralPath $apk -Destination (Join-Path $projectRoot "dist/$targetName") -Force
    if (-not $Debug -and $targetName -ne 'TimeGuard-release-unsigned.apk') {
        $dist = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
        $expected = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $dist.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Dist is outside the project.' }
        if ((Get-Item -LiteralPath $dist).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to prune a linked dist folder.' }
        $packages = @(Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Name -match '^息刻-安卓版-\d+\.\d+\.\d+\.apk$' } |
            Sort-Object { [version]($_.BaseName -replace '^息刻-安卓版-', '') } -Descending)
        $packages | Select-Object -Skip 2 | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        $lines = @($packages | Select-Object -First 2 | ForEach-Object {
            ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + $_.Name
        })
        [IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))
    }
    Write-Output 'APK ready in dist.'
} finally {
    Pop-Location
}
