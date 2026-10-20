$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$downloads = Join-Path $projectRoot '.toolchain\downloads'
$logFile = Join-Path $projectRoot '.toolchain\install.log'
$statusFile = Join-Path $projectRoot '.toolchain\install-status.txt'
$sdkRoot = 'C:\Program Files\Android\Sdk'
$gradleRoot = 'C:\Program Files\Gradle'
$javaRoot = 'C:\Program Files\Microsoft\OpenJDK'
$jdkHome = Join-Path $javaRoot 'jdk-21.0.12.1+1'
Start-Transcript -Path $logFile -Force
try {
    $expectedGradle = (Get-Content (Join-Path $downloads 'gradle-8.9-bin.zip.sha256') -Raw).Trim()
    $expectedJava = ((Get-Content (Join-Path $downloads 'jdk.sha256') -Raw).Trim() -split '\s+')[0]
    foreach ($package in @(
        @('gradle-8.9-bin.zip', $expectedGradle),
        @('jdk.zip', $expectedJava),
        @('commandlinetools.zip', '90ae805d20434428bffcb699c290860f19bb5f66a67e6b330067e3de801fb04a')
    )) {
        if ((Get-FileHash (Join-Path $downloads $package[0]) -Algorithm SHA256).Hash -ne $package[1]) {
            throw "Checksum mismatch: $($package[0])"
        }
    }
    New-Item -ItemType Directory -Force -Path "$sdkRoot\cmdline-tools", $gradleRoot, $javaRoot | Out-Null
    if (-not (Test-Path "$gradleRoot\gradle-8.9\bin\gradle.bat")) {
        Expand-Archive -LiteralPath (Join-Path $downloads 'gradle-8.9-bin.zip') -DestinationPath $gradleRoot -Force
    }
    if (-not (Test-Path "$jdkHome\bin\java.exe")) {
        Expand-Archive -LiteralPath (Join-Path $downloads 'jdk.zip') -DestinationPath $javaRoot -Force
    }
    if (-not (Test-Path "$sdkRoot\cmdline-tools\latest\bin\sdkmanager.bat")) {
        $extractRoot = Join-Path $projectRoot '.toolchain\extract\android'
        Expand-Archive -LiteralPath (Join-Path $downloads 'commandlinetools.zip') -DestinationPath $extractRoot -Force
        New-Item -ItemType Directory -Force -Path "$sdkRoot\cmdline-tools\latest" | Out-Null
        Copy-Item -Path "$extractRoot\cmdline-tools\*" -Destination "$sdkRoot\cmdline-tools\latest" -Recurse -Force
    }
    $env:JAVA_HOME = $jdkHome
    $env:ANDROID_HOME = $sdkRoot
    $env:ANDROID_SDK_ROOT = $sdkRoot
    $env:Path = "$jdkHome\bin;$env:Path"
    Write-Output 'Installing Android API 35, build tools, and platform tools...'
    1..50 | ForEach-Object { 'y' } | & "$sdkRoot\cmdline-tools\latest\bin\sdkmanager.bat" "--sdk_root=$sdkRoot" --licenses
    if ($LASTEXITCODE -ne 0) { throw "SDK licenses failed: $LASTEXITCODE" }
    & "$sdkRoot\cmdline-tools\latest\bin\sdkmanager.bat" "--sdk_root=$sdkRoot" 'platform-tools' 'platforms;android-35' 'build-tools;34.0.0' 'build-tools;35.0.0'
    if ($LASTEXITCODE -ne 0) { throw "SDK install failed: $LASTEXITCODE" }
    [Environment]::SetEnvironmentVariable('JAVA_HOME', $jdkHome, 'User')
    [Environment]::SetEnvironmentVariable('ANDROID_HOME', $sdkRoot, 'User')
    [Environment]::SetEnvironmentVariable('ANDROID_SDK_ROOT', $sdkRoot, 'User')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    foreach ($entry in @("$jdkHome\bin", "$gradleRoot\gradle-8.9\bin", "$sdkRoot\platform-tools", "$sdkRoot\cmdline-tools\latest\bin")) {
        if ($entries -notcontains $entry) { $entries += $entry }
    }
    [Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'User')
    'SUCCESS' | Set-Content -LiteralPath $statusFile
    Write-Output 'Shared toolchain installation completed.'
} catch {
    ('FAILED: ' + $_.Exception.Message) | Set-Content -LiteralPath $statusFile
    Write-Output $_.Exception.Message
    exit 1
} finally {
    Stop-Transcript
}
