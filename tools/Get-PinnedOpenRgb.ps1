<#
.SYNOPSIS
    Copies the pinned OpenRGB 1.0rc2 build into third_party/openrgb (without plugins) and verifies it.

.DESCRIPTION
    RGBcontroller ships its own copy of OpenRGB so the Service never depends on (or loads the plugins of)
    another installation. The copy deliberately has no plugins folder, so the Effects plugin can never load.
    The pinned build is identified by the SHA-256 of OpenRGB.exe in third_party/openrgb-pin.json.
    third_party/openrgb/ is git-ignored; run this script once per checkout.

.EXAMPLE
    .\tools\Get-PinnedOpenRgb.ps1
    .\tools\Get-PinnedOpenRgb.ps1 -Source 'D:\Downloads\OpenRGB_1.0rc2_Windows_64'
#>
param(
    [string]$Source = 'C:\Program Files\OpenRGB'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$pin = Get-Content (Join-Path $root 'third_party\openrgb-pin.json') -Raw | ConvertFrom-Json
$target = Join-Path $root 'third_party\openrgb'

$exe = Join-Path $Source 'OpenRGB.exe'
if (-not (Test-Path $exe)) { throw "OpenRGB.exe not found in $Source." }
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
if ($hash -ne $pin.sha256) {
    throw "OpenRGB.exe in $Source is not the pinned build $($pin.version) (SHA-256 $hash, expected $($pin.sha256))."
}

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target | Out-Null
Get-ChildItem $Source | Where-Object { $_.Name -ne 'plugins' } | Copy-Item -Destination $target -Recurse

Write-Host "Copied OpenRGB $($pin.version) to $target (plugins excluded)."
