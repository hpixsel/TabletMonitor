<#
.SYNOPSIS
    Builds the whole TabletMonitor package: the Android APK and the single Windows exe that embeds it.
.DESCRIPTION
    Output: build\TabletMonitor.exe (everything in one file) and build\TabletMonitor.apk.
    Requirements: .NET 10 SDK, JDK 17+, Android SDK (ANDROID_HOME or android\local.properties).
    adb is downloaded from Google on the first build.
#>
param(
    [switch]$SkipAndroid
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null

# adb (USB mode) comes from Google's Android SDK Platform-Tools and is not stored in the repository.
$adbDir = Join-Path $root 'third_party\adb'
if (-not (Test-Path (Join-Path $adbDir 'adb.exe'))) {
    Write-Host '==> Downloading Android SDK Platform-Tools (adb)' -ForegroundColor Cyan
    $zip = Join-Path ([IO.Path]::GetTempPath()) 'tabletmonitor-platform-tools.zip'
    $extract = Join-Path ([IO.Path]::GetTempPath()) 'tabletmonitor-platform-tools'
    Invoke-WebRequest 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $zip
    if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
    Expand-Archive $zip $extract
    New-Item -ItemType Directory -Force $adbDir | Out-Null
    foreach ($file in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt') {
        Copy-Item (Join-Path $extract "platform-tools\$file") $adbDir -Force
    }
    Remove-Item -Recurse -Force $extract, $zip
}

if (-not $SkipAndroid) {
    Write-Host '==> Android app' -ForegroundColor Cyan
    Push-Location (Join-Path $root 'android')
    try {
        & .\gradlew.bat assembleRelease --console=plain
        if ($LASTEXITCODE -ne 0) { throw 'Gradle build failed' }
    }
    finally {
        Pop-Location
    }
    Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') (Join-Path $out 'TabletMonitor.apk') -Force
}

Write-Host '==> Windows app' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'windows\TabletMonitor.csproj') -c Release -o (Join-Path $out 'publish')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Copy-Item (Join-Path $out 'publish\TabletMonitor.exe') (Join-Path $out 'TabletMonitor.exe') -Force

$size = [math]::Round((Get-Item (Join-Path $out 'TabletMonitor.exe')).Length / 1MB, 1)
Write-Host "==> Done: build\TabletMonitor.exe ($size MB)" -ForegroundColor Green
