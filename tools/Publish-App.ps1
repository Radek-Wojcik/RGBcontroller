<#
.SYNOPSIS
    Publishes the Service, the Agent and the UI (Release, win-x64, self-contained, ReadyToRun) together with the pinned
    OpenRGB copy into one folder. Used by install-dev.ps1 and by the installer build.

.EXAMPLE
    .\tools\Publish-App.ps1                                   # artifacts\publish\app
    .\tools\Publish-App.ps1 -OutputDir D:\drop\app
#>
param(
    [string]$OutputDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\publish\app')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $root 'third_party\openrgb\OpenRGB.exe'))) {
    Step 'Copying the pinned OpenRGB build'
    & (Join-Path $root 'tools\Get-PinnedOpenRgb.ps1')
}

& (Join-Path $root 'tools\Build-Frontend.ps1')
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
# All executables share one self-contained folder; their common libraries have the same (central) versions.
foreach ($project in 'RGBcontroller.Service', 'RGBcontroller.Agent', 'RGBcontroller.UI') {
    Step "Publishing $project (Release, win-x64, self-contained, ReadyToRun)"
    & $dotnet publish (Join-Path $root "src\$project\$project.csproj") `
        -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o $OutputDir --nologo --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $project failed ($LASTEXITCODE)." }
}

if (-not (Test-Path (Join-Path $OutputDir 'openrgb\OpenRGB.exe'))) { throw 'Published output is missing openrgb\OpenRGB.exe.' }
Write-Host "Published to $OutputDir" -ForegroundColor Green
