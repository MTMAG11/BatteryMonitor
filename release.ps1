<#
.SYNOPSIS
  Builds every release variant into .\release with the file names the README and the GitHub release use.

.DESCRIPTION
  Reads the version from BatteryMonitor\BatteryMonitor.csproj and produces:
    BatteryMonitor-win-x64-net8.exe          needs the .NET 8 Desktop Runtime installed, small
    BatteryMonitor-win-x64-standalone.exe    runs anywhere, large (the runtime is bundled inside)
    BatteryMonitor-win-arm64-net8.exe
    BatteryMonitor-win-arm64-standalone.exe
    BatteryMonitor-win-x64-setup.exe         installer (needs Inno Setup, see below)
    BatteryMonitor-win-arm64-setup.exe
    SHA256SUMS.txt                           checksums so people can verify their download

  The installers need Inno Setup (free, https://jrsoftware.org/isinfo.php). If it isn't installed they are skipped.

  Usage:   .\release.ps1            all four
           .\release.ps1 -X64Only   only the two x64 files (faster for a quick test)
#>
param([switch]$X64Only)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'BatteryMonitor\BatteryMonitor.csproj'
$out  = Join-Path $root 'release'
$work = Join-Path $out '_build'

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $proj" }
Write-Host "Building Battery Monitor v$version" -ForegroundColor Cyan

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out, $work | Out-Null

$runtimes = if ($X64Only) { @('win-x64') } else { @('win-x64', 'win-arm64') }

foreach ($rid in $runtimes) {
    foreach ($mode in @('net8', 'standalone')) {
        $self = if ($mode -eq 'standalone') { 'true' } else { 'false' }
        $dir  = Join-Path $work "$rid-$mode"
        Write-Host "  -> $rid / $mode" -ForegroundColor Yellow

        $args = @('publish', $proj, '-c', 'Release', '-r', $rid, '--self-contained', $self,
                  '-p:PublishSingleFile=true', '-o', $dir, '--nologo', '-v', 'quiet')
        if ($mode -eq 'standalone') { $args += '-p:IncludeNativeLibrariesForSelfExtract=true' }

        & dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid / $mode" }

        Copy-Item (Join-Path $dir 'BatteryMonitor.exe') (Join-Path $out "BatteryMonitor-$rid-$mode.exe")
    }
}
Remove-Item $work -Recurse -Force

# Installers (Inno Setup). Skipped with a warning if Inno Setup isn't installed.
$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($iscc) {
    foreach ($rid in $runtimes) {
        $arch = $rid -replace '^win-', ''
        Write-Host "  -> installer $arch" -ForegroundColor Yellow
        & $iscc "/DMyAppVersion=$version" "/DArch=$arch" (Join-Path $root 'installer\BatteryMonitor.iss') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $arch" }
    }
} else {
    Write-Warning "Inno Setup not found - installers skipped. Install it from https://jrsoftware.org/isdl.php and run again."
}
# Checksums, so a download can be verified with:  Get-FileHash <file> -Algorithm SHA256
Get-ChildItem $out -Filter *.exe | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content (Join-Path $out 'SHA256SUMS.txt')

Write-Host "`nDone. Files in $out :" -ForegroundColor Green
Get-ChildItem $out | ForEach-Object { "{0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Write-Host "`nNext: add a CHANGELOG entry for $version, commit, then:  git tag -a v$version -m `"v$version`" ; git push origin v$version"

