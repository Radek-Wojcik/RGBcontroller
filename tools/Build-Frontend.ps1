<#
.SYNOPSIS
    Builds the web front end (ui/, React + Vite) into ui/dist, which RGBcontroller.UI ships as wwwroot.

.DESCRIPTION
    Uses Node.js from PATH or the portable copy in %USERPROFILE%\.tools\nodejs (no admin rights). Without Node the
    build is skipped with a warning and the UI shows its bridge test page instead. Installs the locked packages
    (npm ci) when node_modules is missing or older than package-lock.json.

.EXAMPLE
    .\tools\Build-Frontend.ps1
    .\tools\Build-Frontend.ps1 -Test
#>
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$ui = Join-Path (Split-Path $PSScriptRoot -Parent) 'ui'

$portable = Join-Path $env:USERPROFILE '.tools\nodejs'
if (-not (Get-Command node -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $portable 'node.exe'))) {
    $env:Path = "$portable;$env:Path"
}

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Warning 'Node.js not found: the front end is not built (the UI shows its bridge test page).'
    return
}

Push-Location $ui
try {
    $modules = Join-Path $ui 'node_modules'
    $lock = Join-Path $ui 'package-lock.json'
    if (-not (Test-Path $modules) -or (Get-Item $lock).LastWriteTime -gt (Get-Item $modules).LastWriteTime) {
        & npm ci --no-fund --no-audit
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)." }
    }

    & npm run build
    if ($LASTEXITCODE -ne 0) { throw "Front end build failed ($LASTEXITCODE)." }

    if ($Test) {
        & npm test
        if ($LASTEXITCODE -ne 0) { throw "Front end tests failed ($LASTEXITCODE)." }
    }
}
finally {
    Pop-Location
}
