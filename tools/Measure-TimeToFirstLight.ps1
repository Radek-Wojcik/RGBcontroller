<#
.SYNOPSIS
    Spike R5: time from boot to the first light of the strip and of each OpenRGB device, for the last few boots.

.DESCRIPTION
    Pairs every Service start found in the logs with the boot it belongs to (System event log: Kernel-General 12
    for the boot time, Kernel-Boot 27 for the type: 0x0 cold boot, 0x1 Fast Startup, 0x2 resume from hibernation)
    and lists the "First light" lines that follow it. Starts that did not happen right after a boot (manual
    restarts, dev installs) are shown as such. Works without admin rights.

.EXAMPLE
    .\tools\Measure-TimeToFirstLight.ps1
    .\tools\Measure-TimeToFirstLight.ps1 -Last 5
#>
param(
    [int]$Last = 3,
    [string]$LogDir = (Join-Path $env:ProgramData 'RGBcontroller\logs'),
    # A Service start more than this long after the boot is not a boot-time start.
    [int]$MaxBootToStartSeconds = 300
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture

$boots = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-General'; Id = 12 } -MaxEvents 20 |
    ForEach-Object { [DateTimeOffset]$_.TimeCreated })
$bootTypes = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Boot'; Id = 27 } -MaxEvents 20 |
    ForEach-Object { [pscustomobject]@{ Time = [DateTimeOffset]$_.TimeCreated; Type = [int]$_.Properties[0].Value } })

function Get-Time([string]$line) { [DateTimeOffset]::ParseExact($line.Substring(0, 30), 'yyyy-MM-dd HH:mm:ss.fff zzz', $culture) }
function Get-BootTypeName([DateTimeOffset]$boot) {
    $t = $bootTypes | Where-Object { [math]::Abs(($_.Time - $boot).TotalSeconds) -lt 120 } | Select-Object -First 1
    if (-not $t) { return 'unknown' }
    switch ($t.Type) { 0 { 'cold boot / restart' } 1 { 'Fast Startup' } 2 { 'resume from hibernation' } default { "type $($t.Type)" } }
}

$lines = @(Get-ChildItem $LogDir -Filter 'service-*.log' | Sort-Object LastWriteTime | ForEach-Object { Get-Content $_.FullName -Encoding UTF8 })
$starts = @(for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match 'Lighting host starting') { $i } })
if ($starts.Count -eq 0) { throw "No Service start found in $LogDir." }

foreach ($startIndex in ($starts | Select-Object -Last $Last)) {
    $serviceStart = Get-Time $lines[$startIndex]
    $boot = $boots | Where-Object { $_ -le $serviceStart } | Sort-Object -Descending | Select-Object -First 1
    $afterBoot = $boot -and ($serviceStart - $boot).TotalSeconds -le $MaxBootToStartSeconds

    Write-Host ''
    if ($afterBoot) {
        Write-Host ("Boot {0:yyyy-MM-dd HH:mm:ss} ({1})" -f $boot, (Get-BootTypeName $boot)) -ForegroundColor Cyan
    }
    else {
        Write-Host ("Service start {0:yyyy-MM-dd HH:mm:ss} (not at boot: manual start or reinstall)" -f $serviceStart) -ForegroundColor DarkGray
    }

    $rows = @([pscustomobject]@{
        Event = 'Service start'
        SecondsAfterBoot = if ($afterBoot) { [math]::Round(($serviceStart - $boot).TotalSeconds, 1) } else { $null }
        SecondsAfterServiceStart = 0
    })
    for ($i = $startIndex + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'Lighting host starting') { break }
        if ($lines[$i] -match 'First light (.+?) \(\d+ LEDs\)|First light (Adalight strip on \S+)') {
            $t = Get-Time $lines[$i]
            $name = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
            $rows += [pscustomobject]@{
                Event = $name.Trim()
                SecondsAfterBoot = if ($afterBoot) { [math]::Round(($t - $boot).TotalSeconds, 1) } else { $null }
                SecondsAfterServiceStart = [math]::Round(($t - $serviceStart).TotalSeconds, 1)
            }
        }
    }
    $rows | Format-Table -AutoSize | Out-Host
}
