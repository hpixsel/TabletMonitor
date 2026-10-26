<#
.SYNOPSIS
    Publishes a new TabletMonitor version as a GitHub release – the source of in-app updates.
.DESCRIPTION
    1. Sets the version in windows/TabletMonitor.csproj and android/app/build.gradle.kts (versionCode + 1)
    2. Builds both apps (build.ps1)
    3. Commits "Release vX.Y.Z", pushes and creates the release with:
         TabletMonitor-X.Y.Z-win-x64.exe, TabletMonitor-X.Y.Z.apk, SHA256SUMS.txt
       The apps look for exactly these names and verify downloads against SHA256SUMS.txt.
    Requires the GitHub CLI (gh) logged in and the APK signing key in android/signing/ – updates of the
    Android app only install over a build signed with the same key.
.EXAMPLE
    ./release.ps1 -Version 0.2.0 -NotesFile notes.md
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$Version,
    [string]$NotesFile
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Test-Path (Join-Path $root 'android\signing\signing.properties'))) {
    throw 'android/signing/signing.properties is missing – a release signed with another key could not update installed apps.'
}
if (git -C $root status --porcelain) {
    throw 'The working tree has uncommitted changes.'
}

# 1. Version numbers
$csproj = Join-Path $root 'windows\TabletMonitor.csproj'
(Get-Content $csproj -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" | Set-Content $csproj -NoNewline
$gradle = Join-Path $root 'android\app\build.gradle.kts'
$g = Get-Content $gradle -Raw
$code = [int]([regex]::Match($g, 'versionCode = (\d+)').Groups[1].Value) + 1
$g = $g -replace 'versionCode = \d+', "versionCode = $code" -replace 'versionName = "[^"]+"', "versionName = ""$Version"""
Set-Content $gradle $g -NoNewline

# 2. Build
& (Join-Path $root 'build.ps1')

# 3. Assets
$assets = Join-Path $root 'build\release'
if (Test-Path $assets) { Remove-Item -Recurse -Force $assets }
New-Item -ItemType Directory $assets | Out-Null
Copy-Item (Join-Path $root 'build\TabletMonitor.exe') (Join-Path $assets "TabletMonitor-$Version-win-x64.exe")
Copy-Item (Join-Path $root 'build\TabletMonitor.apk') (Join-Path $assets "TabletMonitor-$Version.apk")
Get-ChildItem $assets -File | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content (Join-Path $assets 'SHA256SUMS.txt') -Encoding ascii

# 4. Commit, push, release
git -C $root commit -q -am "Release v$Version"
git -C $root push -q
$notes = if ($NotesFile) { @('--notes-file', $NotesFile) } else { @('--generate-notes') }
Push-Location $root
try {
    gh release create "v$Version" --target main --title "TabletMonitor $Version" --latest @notes (Get-ChildItem $assets -File).FullName
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
}
finally {
    Pop-Location
}
Write-Host "==> Released v$Version – installed apps will offer the update automatically" -ForegroundColor Green
