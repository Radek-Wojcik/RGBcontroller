<#
.SYNOPSIS
    Development install of the RGBcontroller Service on this PC (reversible with uninstall-dev.ps1).

.DESCRIPTION
    Run from an elevated PowerShell. Steps, all logged to the console:
      1. publishes the Service, the Agent and the UI (self-contained, ReadyToRun) together with the pinned OpenRGB copy,
      2. creates %ProgramData%\RGBcontroller (write: SYSTEM and Administrators, read: Users), records and then
         DISABLES (never deletes) the existing OpenRGB autostart task and stops that OpenRGB,
      3. copies the build to C:\Program Files\RGBcontroller,
      4. registers the "RGBcontroller" service (LocalSystem, automatic start, restart on failure 1 s / 5 s / 30 s),
      5. starts it, registers the Agent (tray, hotkeys, screen capture) to start at logon for the current user
         (HKCU Run value "RGBcontroller.Agent") and starts the Agent unelevated.
    The previous autostart state is saved in %ProgramData%\RGBcontroller\dev-install\state.json so
    uninstall-dev.ps1 can restore it. Running this script again updates the installed build.

.EXAMPLE
    .\install-dev.ps1
#>
#Requires -RunAsAdministrator
param(
    [string]$OpenRgbTaskName = 'OpenRGB_Admin',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'RGBcontroller')
)

$ErrorActionPreference = 'Stop'
$serviceName = 'RGBcontroller'
$root = $PSScriptRoot
$stateDir = Join-Path $env:ProgramData 'RGBcontroller\dev-install'
$stateFile = Join-Path $stateDir 'state.json'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$agentRunValue = 'RGBcontroller.Agent'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# --- prerequisites -------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $root 'third_party\openrgb\OpenRGB.exe'))) {
    Step 'Copying the pinned OpenRGB build'
    & (Join-Path $root 'tools\Get-PinnedOpenRgb.ps1')
}
if (-not (Get-CimInstance Win32_SystemDriver -Filter "Name='PawnIO'")) {
    Write-Warning 'PawnIO driver not found: RAM, motherboard SMBus and GPU I2C devices will not be detected.'
}
if (Get-Process AmbiBox -ErrorAction SilentlyContinue) {
    throw 'AmbiBox is running and would fight for the strip COM port. Close it first.'
}

# --- 1. publish ----------------------------------------------------------------------------
& (Join-Path $root 'tools\Build-Frontend.ps1')
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$publishDir = Join-Path $root 'artifacts\publish\app'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
# Both executables share one self-contained folder; their common libraries have the same (central) versions.
foreach ($project in 'RGBcontroller.Service', 'RGBcontroller.Agent', 'RGBcontroller.UI') {
    Step "Publishing $project (Release, win-x64, self-contained, ReadyToRun)"
    & $dotnet publish (Join-Path $root "src\$project\$project.csproj") `
        -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o $publishDir --nologo --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $project failed ($LASTEXITCODE)." }
}
if (-not (Test-Path (Join-Path $publishDir 'openrgb\OpenRGB.exe'))) { throw 'Published output is missing openrgb\OpenRGB.exe.' }

# --- 2. data folder and existing OpenRGB autostart -------------------------------------------
# Only the Service (SYSTEM) and administrators write config; users may read config and logs.
$dataDir = Join-Path $env:ProgramData 'RGBcontroller'
New-Item -ItemType Directory -Force $dataDir | Out-Null
# Set the explicit ACL on the folder only, then make every existing child inherit it. (Applying the folder ACL
# with /T to files strips them: (OI)(CI) grants do not apply to files, so they end up with no access at all.)
takeown /F $dataDir /R /A /D Y | Out-Null
icacls $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed ($LASTEXITCODE)." }
if (Get-ChildItem $dataDir -Force) {
    icacls (Join-Path $dataDir '*') /reset /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls /reset failed ($LASTEXITCODE)." }
}
New-Item -ItemType Directory -Force $stateDir | Out-Null
$task = Get-ScheduledTask -TaskName $OpenRgbTaskName -ErrorAction SilentlyContinue
if (-not (Test-Path $stateFile)) {
    $state = [ordered]@{
        installedAt     = (Get-Date).ToString('o')
        openRgbTask     = $OpenRgbTaskName
        openRgbTaskWas  = if ($task) { [string]$task.State } else { 'Missing' }
    }
    $state | ConvertTo-Json | Set-Content $stateFile -Encoding UTF8
    Write-Host "    Saved previous state: task '$OpenRgbTaskName' was $($state.openRgbTaskWas)"
}
if ($task) {
    Step "Disabling (not deleting) scheduled task '$OpenRgbTaskName' and stopping its OpenRGB"
    Stop-ScheduledTask -TaskName $OpenRgbTaskName -ErrorAction SilentlyContinue
    Disable-ScheduledTask -TaskName $OpenRgbTaskName | Out-Null
}

# --- 3. stop an older build and copy ---------------------------------------------------------
$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Step 'Stopping the running RGBcontroller service (lights go black)'
    Stop-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
foreach ($p in Get-Process OpenRGB -ErrorAction SilentlyContinue) {
    Write-Host "    Stopping OpenRGB pid $($p.Id)"
    Stop-Process -Id $p.Id -Force
}
foreach ($p in Get-Process RGBcontroller.Agent -ErrorAction SilentlyContinue) {
    Write-Host "    Stopping the Agent pid $($p.Id) (it is started again below)"
    Stop-Process -Id $p.Id -Force
}
foreach ($p in Get-Process RGBcontroller.UI -ErrorAction SilentlyContinue) {
    Write-Host "    Closing the UI pid $($p.Id)"
    Stop-Process -Id $p.Id -Force
}

Step "Copying build to $InstallDir"
robocopy $publishDir $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)." }

# --- 4. register ---------------------------------------------------------------------------
$exe = Join-Path $InstallDir 'RGBcontroller.Service.exe'
if (-not $service) {
    Step "Registering service '$serviceName' (LocalSystem, automatic start)"
    New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName 'RGBcontroller' `
        -Description 'RGB lighting and Adalight strip controller (supervises a private OpenRGB instance).' `
        -StartupType Automatic | Out-Null
}
sc.exe failure $serviceName reset= 86400 actions= restart/1000/restart/5000/restart/30000 | Out-Null
sc.exe failureflag $serviceName 1 | Out-Null

# --- 5. start ------------------------------------------------------------------------------
Step 'Starting RGBcontroller'
Start-Service $serviceName
Start-Sleep -Seconds 10
$log = Get-ChildItem (Join-Path $env:ProgramData 'RGBcontroller\logs') -Filter 'service-*.log' |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    Write-Host "--- last lines of $($log.FullName)"
    Get-Content $log.FullName -Tail 25
}

# --- 6. Agent: per-user autostart at logon, not elevated ----------------------------------------
$agent = Join-Path $InstallDir 'RGBcontroller.Agent.exe'
Step "Registering the Agent to start at logon (HKCU Run '$agentRunValue') and starting it"
Set-ItemProperty -Path $runKey -Name $agentRunValue -Value "`"$agent`""
# explorer.exe starts it in this user's normal (unelevated) context, like the logon autostart will.
Start-Process explorer.exe -ArgumentList "`"$agent`""

# --- 7. Shortcuts to the UI: Desktop and Start menu of the current user --------------------------------
$ui = Join-Path $InstallDir 'RGBcontroller.UI.exe'
Step 'Creating the RGBcontroller shortcuts on the Desktop and in the Start menu'
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) {
    $link = $shell.CreateShortcut((Join-Path $folder 'RGBcontroller.lnk'))
    $link.TargetPath = $ui
    $link.WorkingDirectory = $InstallDir
    $link.IconLocation = "$ui,0"
    $link.Description = 'RGB lighting and ambilight control'
    $link.Save()
}

Write-Host ''
Write-Host "Installed. Undo everything with: .\uninstall-dev.ps1" -ForegroundColor Green
