<#
.SYNOPSIS
    Removes the development install made by install-dev.ps1 and restores the previous OpenRGB autostart.

.DESCRIPTION
    Run from an elevated PowerShell. Stops the Agent and removes its logon autostart (HKCU Run value),
    stops the service (lights go black), deletes the service registration,
    removes C:\Program Files\RGBcontroller, re-enables the OpenRGB autostart task if it was enabled before
    and starts it again so the old setup is back immediately. Config and logs in %ProgramData%\RGBcontroller
    are kept unless -RemoveData is given.

.EXAMPLE
    .\uninstall-dev.ps1
    .\uninstall-dev.ps1 -RemoveData
#>
#Requires -RunAsAdministrator
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'RGBcontroller'),
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$serviceName = 'RGBcontroller'
$dataDir = Join-Path $env:ProgramData 'RGBcontroller'
$stateFile = Join-Path $dataDir 'dev-install\state.json'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

foreach ($p in Get-Process RGBcontroller.UI -ErrorAction SilentlyContinue) {
    Step "Closing the UI pid $($p.Id)"
    Stop-Process -Id $p.Id -Force
}
foreach ($p in Get-Process RGBcontroller.Agent -ErrorAction SilentlyContinue) {
    Step "Stopping the Agent pid $($p.Id)"
    Stop-Process -Id $p.Id -Force
}
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'RGBcontroller.Agent' -ErrorAction SilentlyContinue) {
    Step 'Removing the Agent autostart (HKCU Run)'
    Remove-ItemProperty -Path $runKey -Name 'RGBcontroller.Agent'
}

$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Step 'Stopping RGBcontroller (lights go black)'
        Stop-Service $serviceName
        (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Step 'Deleting the service registration'
    sc.exe delete $serviceName | Out-Null
}

if (Test-Path $InstallDir) {
    Step "Removing $InstallDir"
    Remove-Item $InstallDir -Recurse -Force
}

if (Test-Path $stateFile) {
    $state = Get-Content $stateFile -Raw | ConvertFrom-Json
    $task = Get-ScheduledTask -TaskName $state.openRgbTask -ErrorAction SilentlyContinue
    if ($task -and $state.openRgbTaskWas -ne 'Disabled' -and $state.openRgbTaskWas -ne 'Missing') {
        Step "Re-enabling and starting scheduled task '$($state.openRgbTask)'"
        Enable-ScheduledTask -TaskName $state.openRgbTask | Out-Null
        Start-ScheduledTask -TaskName $state.openRgbTask
    }
    Remove-Item (Split-Path $stateFile) -Recurse -Force
}
else {
    Write-Warning 'No dev-install state found; the OpenRGB autostart task was not changed.'
}

if ($RemoveData -and (Test-Path $dataDir)) {
    Step "Removing $dataDir (config, logs, private OpenRGB settings)"
    Remove-Item $dataDir -Recurse -Force
}

Write-Host 'Uninstalled.' -ForegroundColor Green
