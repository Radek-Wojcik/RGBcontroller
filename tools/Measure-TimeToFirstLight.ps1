<#
.SYNOPSIS
    Spike R5: time from the last boot to the first light of the strip and of each OpenRGB device.

.DESCRIPTION
    Reads the boot time and boot type (0x0 cold boot, 0x1 Fast Startup, 0x2 resume from hibernation) from the
    System event log (Kernel-General 12, Kernel-Boot 27) and the "First light" lines that the Service writes
    after its most recent start. Works without admin rights. Run it after logging in.

.EXAMPLE
    .\tools\Measure-TimeToFirstLight.ps1
#>
param(
    [string]$LogDir = (Join-Path $env:ProgramData 'RGBcontroller\logs')
)

$ErrorActionPreference = 'Stop'
$boot = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-General'; Id = 12 } -MaxEvents 1
$bootType = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Boot'; Id = 27 } -MaxEvents 1
$typeCode = [int]$bootType.Properties[0].Value
$typeName = switch ($typeCode) { 0 { 'cold boot' } 1 { 'Fast Startup' } 2 { 'resume from hibernation' } default { "type $typeCode" } }

$lines = Get-ChildItem $LogDir -Filter 'service-*.log' | Sort-Object LastWriteTime | ForEach-Object { Get-Content $_.FullName }
$startIndex = -1
for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match 'Lighting host starting') { $startIndex = $i } }
if ($startIndex -lt 0) { throw "No Service start found in $LogDir." }

function Get-Time([string]$line) { [DateTimeOffset]::ParseExact($line.Substring(0, 30), 'yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture) }

$serviceStart = Get-Time $lines[$startIndex]
$bootTime = [DateTimeOffset]$boot.TimeCreated
Write-Host ("Boot: {0:yyyy-MM-dd HH:mm:ss} ({1})" -f $bootTime, $typeName)
if ($serviceStart -lt $bootTime) { Write-Warning 'The latest Service start is older than the latest boot; the Service did not start at this boot.' }

$rows = @([pscustomobject]@{ Event = 'Service start'; SecondsAfterBoot = [math]::Round(($serviceStart - $bootTime).TotalSeconds, 1); SecondsAfterServiceStart = 0 })
for ($i = $startIndex + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'Lighting host starting') { break }
    if ($lines[$i] -match 'First light (.+?) \(?\d+ LEDs\)?|First light (Adalight strip on \S+)') {
        $t = Get-Time $lines[$i]
        $name = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
        $rows += [pscustomobject]@{
            Event = $name.Trim()
            SecondsAfterBoot = [math]::Round(($t - $bootTime).TotalSeconds, 1)
            SecondsAfterServiceStart = [math]::Round(($t - $serviceStart).TotalSeconds, 1)
        }
    }
}
$rows | Format-Table -AutoSize
