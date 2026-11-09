<#
.SYNOPSIS
    Builds installer\Output\RGBcontroller-Setup-<version>.exe: publishes the app (tools\Publish-App.ps1) and compiles
    installer\RGBcontroller.iss with Inno Setup 6.

.DESCRIPTION
    Needs Inno Setup 6 (ISCC.exe): installed in Program Files, on PATH, or given with -Iscc. Nothing is installed or
    registered on this PC by building. The installer binary is git-ignored.

.EXAMPLE
    .\installer\Build-Installer.ps1
    .\installer\Build-Installer.ps1 -SkipPublish        # reuse artifacts\publish\app
#>
param(
    [string]$Iscc,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root 'artifacts\publish\app'

if (-not $Iscc) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    $Iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { $Iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
}
if (-not $Iscc -or -not (Test-Path $Iscc)) {
    throw 'Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isdl.php or pass -Iscc <path>.'
}

$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
if ($props -notmatch '<Version>([^<]+)</Version>') { throw 'No <Version> in Directory.Build.props.' }
$version = $Matches[1]

if (-not $SkipPublish) { & (Join-Path $root 'tools\Publish-App.ps1') -OutputDir $publishDir }
if (-not (Test-Path (Join-Path $publishDir 'RGBcontroller.Service.exe'))) { throw "No published app in $publishDir." }

Write-Host "==> Compiling the installer $version" -ForegroundColor Cyan
& $Iscc "/DMyAppVersion=$version" "/DPublishDir=$publishDir" (Join-Path $PSScriptRoot 'RGBcontroller.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }
Write-Host "Built $(Join-Path $PSScriptRoot "Output\RGBcontroller-Setup-$version.exe")" -ForegroundColor Green
