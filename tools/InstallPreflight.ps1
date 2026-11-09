<#
.SYNOPSIS
    Install-time checks for RGBcontroller: PawnIO, WebView2 and other programs that would fight for the same
    hardware (an existing OpenRGB or AmbiBox autostart). Used by the installer; safe to run by hand.

.DESCRIPTION
    -Check     (default) Read-only. Lists autostart entries of OpenRGB and AmbiBox (scheduled tasks, Run keys,
               Startup folders) and the state of PawnIO and WebView2. Exit code 0 when PawnIO and WebView2 are
               present, 2 when PawnIO is missing, 3 when only WebView2 is missing. With -OutFile the findings go to
               a text file the installer shows to the user.
    -Disable   Turns the listed autostart entries off WITHOUT deleting them: tasks are disabled, Run values and
               Startup shortcuts are moved aside and recorded in the state file. Call only after the user agreed.
    -Restore   Puts everything -Disable changed back (used by the uninstaller).

    Nothing here modifies an OpenRGB configuration or any file of OpenRGB/AmbiBox themselves (SPEC 12 rule 7).

.EXAMPLE
    .\tools\InstallPreflight.ps1 -Check
    .\tools\InstallPreflight.ps1 -Disable
    .\tools\InstallPreflight.ps1 -Restore
#>
[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    [Parameter(ParameterSetName = 'Check')][switch]$Check,
    [Parameter(ParameterSetName = 'Disable')][switch]$Disable,
    [Parameter(ParameterSetName = 'Restore')][switch]$Restore,
    [string]$OutFile,
    [string]$StateFile = (Join-Path $env:ProgramData 'RGBcontroller\autostart-state.json'),
    # Overridable so the self-test can use scratch locations.
    [string[]]$RunKeyPaths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'),
    [string[]]$StartupFolders = @(
        [Environment]::GetFolderPath('CommonStartup'),
        [Environment]::GetFolderPath('Startup')),
    [switch]$SkipTasks,
    [switch]$SkipDriverChecks
)

$ErrorActionPreference = 'Stop'

# What counts as a competitor. Our own files never match (the pinned copy lives in ...\RGBcontroller\openrgb).
$programs = [ordered]@{ OpenRGB = 'OpenRGB'; AmbiBox = 'AmbiBox' }
function Test-Match([string]$text) {
    if (-not $text) { return $null }
    if ($text -match 'RGBcontroller') { return $null }
    foreach ($p in $programs.GetEnumerator()) { if ($text -match $p.Value) { return $p.Key } }
    return $null
}

function Get-Findings {
    $found = @()
    if (-not $SkipTasks) {
        foreach ($t in Get-ScheduledTask -ErrorAction SilentlyContinue) {
            $cmd = ($t.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join ' | '
            $program = Test-Match $cmd
            if ($program) {
                $found += [pscustomobject]@{
                    Program = $program; Kind = 'Task'; Name = $t.TaskName; Location = $t.TaskPath
                    Command = $cmd; Enabled = ($t.State -ne 'Disabled')
                }
            }
        }
    }

    foreach ($path in $RunKeyPaths) {
        if (-not (Test-Path $path)) { continue }
        $key = Get-Item $path
        foreach ($name in $key.GetValueNames()) {
            $value = [string]$key.GetValue($name)
            $program = Test-Match "$name $value"
            if ($program) {
                $found += [pscustomobject]@{ Program = $program; Kind = 'RunKey'; Name = $name; Location = $path; Command = $value; Enabled = $true }
            }
        }
    }

    $shell = $null
    foreach ($folder in $StartupFolders) {
        if (-not (Test-Path $folder)) { continue }
        foreach ($file in Get-ChildItem $folder -Filter '*.lnk' -ErrorAction SilentlyContinue) {
            if (-not $shell) { $shell = New-Object -ComObject WScript.Shell }
            $target = $shell.CreateShortcut($file.FullName).TargetPath
            $program = Test-Match "$($file.BaseName) $target"
            if ($program) {
                $found += [pscustomobject]@{ Program = $program; Kind = 'StartupFolder'; Name = $file.Name; Location = $folder; Command = $target; Enabled = $true }
            }
        }
    }

    return $found
}

function Get-PawnIoState {
    if ($SkipDriverChecks) { return 'Running' }
    $driver = Get-CimInstance Win32_SystemDriver -Filter "Name='PawnIO'" -ErrorAction SilentlyContinue
    if (-not $driver) { return 'Missing' }
    return [string]$driver.State
}

function Get-WebView2Version {
    if ($SkipDriverChecks) { return '0.0' }
    foreach ($path in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') {
        $v = (Get-ItemProperty $path -ErrorAction SilentlyContinue).pv
        if ($v -and $v -ne '0.0.0.0') { return [string]$v }
    }

    return $null
}

# Windows PowerShell 5.1 hands a JSON array over as one object; the ForEach-Object round trip flattens it.
function Read-State { if (Test-Path $StateFile) { @(Get-Content $StateFile -Raw | ConvertFrom-Json | ForEach-Object { $_ }) } else { @() } }
function Write-State($entries) {
    New-Item -ItemType Directory -Force (Split-Path $StateFile) | Out-Null
    ConvertTo-Json -InputObject @($entries) -Depth 5 | Set-Content $StateFile -Encoding UTF8
}

if ($Disable) {
    $state = [System.Collections.Generic.List[object]]::new()
    foreach ($e in Read-State) { $state.Add($e) }
    foreach ($f in Get-Findings | Where-Object { $_.Enabled }) {
        switch ($f.Kind) {
            'Task' {
                Disable-ScheduledTask -TaskName $f.Name -TaskPath $f.Location | Out-Null
                $state.Add(@{ Kind = 'Task'; Name = $f.Name; Location = $f.Location })
            }
            'RunKey' {
                $value = (Get-ItemProperty -Path $f.Location -Name $f.Name).($f.Name)
                $kind = (Get-Item $f.Location).GetValueKind($f.Name).ToString()
                Remove-ItemProperty -Path $f.Location -Name $f.Name
                $state.Add(@{ Kind = 'RunKey'; Name = $f.Name; Location = $f.Location; Value = $value; ValueKind = $kind })
            }
            'StartupFolder' {
                $aside = Join-Path (Split-Path $StateFile) 'disabled-startup'
                New-Item -ItemType Directory -Force $aside | Out-Null
                $from = Join-Path $f.Location $f.Name
                $to = Join-Path $aside $f.Name
                Move-Item $from $to -Force
                $state.Add(@{ Kind = 'StartupFolder'; Name = $f.Name; Location = $f.Location; MovedTo = $to })
            }
        }

        Write-Host "Disabled $($f.Kind) '$($f.Name)' ($($f.Program))"
    }

    Write-State $state
    exit 0
}

if ($Restore) {
    $left = @()
    foreach ($e in Read-State) {
        try {
            switch ($e.Kind) {
                'Task' { Enable-ScheduledTask -TaskName $e.Name -TaskPath $e.Location | Out-Null }
                'RunKey' {
                    if (-not (Test-Path $e.Location)) { New-Item -Path $e.Location -Force | Out-Null }
                    New-ItemProperty -Path $e.Location -Name $e.Name -Value $e.Value -PropertyType $e.ValueKind -Force | Out-Null
                }
                'StartupFolder' { Move-Item $e.MovedTo (Join-Path $e.Location $e.Name) -Force }
            }

            Write-Host "Restored $($e.Kind) '$($e.Name)'"
        }
        catch {
            Write-Warning "Could not restore $($e.Kind) '$($e.Name)': $($_.Exception.Message)"
            $left += $e
        }
    }

    if ($left.Count -gt 0) { Write-State $left } elseif (Test-Path $StateFile) { Remove-Item $StateFile -Force }
    exit 0
}

# --- Check (default) -----------------------------------------------------------------------
$findings = @(Get-Findings)
$pawn = Get-PawnIoState
$webView = Get-WebView2Version
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("PawnIO driver: $pawn")
$lines.Add("WebView2 runtime: $(if ($webView) { $webView } else { 'Missing' })")
foreach ($f in $findings) {
    $lines.Add("$($f.Program) autostart [$($f.Kind), $(if ($f.Enabled) { 'enabled' } else { 'already disabled' })]: $($f.Name) -> $($f.Command)")
}

$lines | ForEach-Object { Write-Host $_ }
if ($OutFile) {
    # Only entries the installer would switch off are listed for consent.
    $consent = $findings | Where-Object { $_.Enabled } | ForEach-Object { "$($_.Program) ($($_.Kind)): $($_.Name)" }
    Set-Content -Path $OutFile -Value $consent -Encoding UTF8
}

if ($pawn -eq 'Missing') { exit 2 }
if (-not $webView) { exit 3 }
exit 0
