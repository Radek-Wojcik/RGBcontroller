<#
.SYNOPSIS
    Builds RGBcontroller and optionally runs the unit tests.

.DESCRIPTION
    Uses a .NET 10 SDK from PATH, or the user-local SDK in %LOCALAPPDATA%\Microsoft\dotnet
    (installed with the official dotnet-install.ps1, no admin rights needed).

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Test
    .\build.ps1 -Configuration Release -Test
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Find-Dotnet {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($dotnet in $candidates) {
        if (& $dotnet --list-sdks | Where-Object { $_ -match '^10\.' }) { return $dotnet }
    }
    throw '.NET 10 SDK not found. Install it (user-local, no admin): https://dot.net/v1/dotnet-install.ps1 -Channel 10.0'
}

$dotnet = Find-Dotnet
$solution = Join-Path $PSScriptRoot 'RGBcontroller.sln'
Write-Host "Using $dotnet"

& $dotnet build $solution -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }

if ($Test) {
    & $dotnet test $solution -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
}
