<#
.SYNOPSIS
    Mierzy zużycie CPU i RAM programów z zasobnika (OpenRGB, FanControl, ...) i zapisuje wynik do CSV.

.DESCRIPTION
    Co IntervalSeconds odczytuje czas procesora każdego pasującego procesu (NtQuerySystemInformation,
    jak Menedżer zadań), więc działa bez uprawnień administratora, także dla procesów uruchomionych
    z najwyższymi uprawnieniami, i niezależnie od języka systemu (w przeciwieństwie do typeperf/Get-Counter).

    CPU_calosc_pct  = udział w całym procesorze (jak w Menedżerze zadań, 100% = wszystkie wątki)
    CPU_rdzen_pct   = udział względem jednego wątku (jak w typeperf/Process Explorer, 100% = 1 wątek)

    Pliki wynikowe (w OutDir):
      <Label>-probki.csv        - każda próbka dla każdego procesu
      <Label>-podsumowanie.csv  - średnia, p95, maksimum, liczba instancji

.EXAMPLE
    .\Measure-TrayTools.ps1 -Label przed
    # wyłącz plugin Effects, ustaw kolor statyczny, potem:
    .\Measure-TrayTools.ps1 -Label po

.EXAMPLE
    .\Measure-TrayTools.ps1 -Label test -DurationSeconds 30
#>
param(
    [string]$Label = 'pomiar',
    [int]$DurationSeconds = 600,
    [double]$IntervalSeconds = 1,
    [string[]]$ProcessNames = @('OpenRGB', 'FanControl', 'AmbiBox', 'EarTrumpet', 'TranslucentTB'),
    [string]$OutDir = $(if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path })
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::CurrentCulture
$cores = [Environment]::ProcessorCount

# NtQuerySystemInformation zwraca czasy CPU wszystkich procesów bez uprawnień administratora
# (to samo źródło co Menedżer zadań), także dla procesów uruchomionych z najwyższymi uprawnieniami.
if (-not ('TrayMeter.NtProc' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace TrayMeter {
    public class ProcInfo {
        public string ProcessName; public int Id; public double CpuMs;
        public long WorkingSet64; public long PrivateMemorySize64;
    }
    public static class NtProc {
        [DllImport("ntdll.dll")]
        static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);
        public static List<ProcInfo> Snapshot() {
            int len = 1 << 20;
            while (true) {
                IntPtr buf = Marshal.AllocHGlobal(len);
                try {
                    int ret;
                    int st = NtQuerySystemInformation(5, buf, len, out ret);
                    if (st == unchecked((int)0xC0000004)) { len = Math.Max(len * 2, ret + 65536); continue; }
                    if (st != 0) throw new InvalidOperationException("NtQuerySystemInformation: 0x" + st.ToString("X8"));
                    var list = new List<ProcInfo>();
                    long off = 0;
                    while (true) {
                        IntPtr p = new IntPtr(buf.ToInt64() + off);
                        IntPtr namePtr = Marshal.ReadIntPtr(p, 64);
                        int nameLen = Marshal.ReadInt16(p, 56);
                        string name = namePtr == IntPtr.Zero ? "Idle" : Marshal.PtrToStringUni(namePtr, nameLen / 2);
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                        list.Add(new ProcInfo {
                            ProcessName = name,
                            Id = (int)Marshal.ReadIntPtr(p, 80).ToInt64(),
                            CpuMs = (Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48)) / 10000.0,
                            WorkingSet64 = Marshal.ReadIntPtr(p, 144).ToInt64(),
                            PrivateMemorySize64 = Marshal.ReadIntPtr(p, 200).ToInt64()
                        });
                        int next = Marshal.ReadInt32(p, 0);
                        if (next == 0) break;
                        off += next;
                    }
                    return list;
                } finally { Marshal.FreeHGlobal(buf); }
            }
        }
    }
}
'@
}

function Get-Tracked {
    [TrayMeter.NtProc]::Snapshot() | Where-Object { $ProcessNames -contains $_.ProcessName }
}

function Get-CpuMs($p) { $p.CpuMs }

function Get-Percentile([double[]]$values, [double]$q) {
    if ($values.Count -eq 0) { return $null }
    $sorted = @($values | Sort-Object)
    $sorted[[math]::Min($sorted.Count - 1, [math]::Ceiling($q * $sorted.Count) - 1)]
}

function Format-Num($value, [int]$digits = 2) {
    if ($null -eq $value) { return '' }
    ([math]::Round([double]$value, $digits)).ToString($culture)
}

$startProcs = @(Get-Tracked)
$found = $startProcs | Select-Object -ExpandProperty ProcessName -Unique
$missing = $ProcessNames | Where-Object { $found -notcontains $_ }
Write-Host "Pomiar '$Label': $DurationSeconds s co $IntervalSeconds s, $cores wątków logicznych."
Write-Host "Znalezione: $($found -join ', ')"
if ($missing) { Write-Host "Nie działają: $($missing -join ', ')" -ForegroundColor DarkGray }
Write-Host 'Nie używaj komputera podczas pomiaru (bezczynność).'

# pid -> ostatni odczyt czasu CPU w ms
$prev = @{}
foreach ($p in $startProcs) { $prev[$p.Id] = Get-CpuMs $p }

$samples = New-Object System.Collections.Generic.List[object]
$sw = [Diagnostics.Stopwatch]::StartNew()
$lastT = 0.0
$tick = 1
$deniedPids = @{}

while ($true) {
    $target = $tick * $IntervalSeconds
    if ($target -gt $DurationSeconds) { break }
    $wait = $target - $sw.Elapsed.TotalSeconds
    if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }

    $t = $sw.Elapsed.TotalSeconds
    $dt = $t - $lastT
    $lastT = $t
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $seen = @{}

    foreach ($p in Get-Tracked) {
        $cpuMs = Get-CpuMs $p
        if ($null -eq $cpuMs) { $deniedPids[$p.Id] = $p.ProcessName }

        $corePct = $null
        if ($null -ne $cpuMs -and $prev.ContainsKey($p.Id) -and $null -ne $prev[$p.Id]) {
            $corePct = [math]::Max(0, ($cpuMs - $prev[$p.Id]) / ($dt * 1000) * 100)
        }
        $prev[$p.Id] = $cpuMs
        $seen[$p.Id] = $true

        $samples.Add([pscustomobject]@{
            Czas       = $stamp
            Sekunda    = [math]::Round($t, 1)
            Proces     = $p.ProcessName
            PID        = $p.Id
            CpuRdzen   = $corePct
            CpuCalosc  = if ($null -ne $corePct) { $corePct / $cores } else { $null }
            WorkingMB  = $p.WorkingSet64 / 1MB
            PrivateMB  = $p.PrivateMemorySize64 / 1MB
        })
    }

    # zapomnij procesy, które się zakończyły
    foreach ($id in @($prev.Keys)) { if (-not $seen.ContainsKey($id)) { $prev.Remove($id) } }

    Write-Progress -Activity "Pomiar '$Label'" -Status ("{0:N0} / {1} s" -f $t, $DurationSeconds) `
        -PercentComplete ([math]::Min(100, $t / $DurationSeconds * 100))
    $tick++
}
Write-Progress -Activity "Pomiar '$Label'" -Completed

if ($deniedPids.Count) {
    $names = ($deniedPids.Values | Sort-Object -Unique) -join ', '
    Write-Warning "Brak czasu CPU dla: $names."
}

# --- próbki ---
$samplesPath = Join-Path $OutDir "$Label-probki.csv"
$samples | ForEach-Object {
    [pscustomobject][ordered]@{
        Czas           = $_.Czas
        Sekunda        = Format-Num $_.Sekunda 1
        Proces         = $_.Proces
        PID            = $_.PID
        CPU_calosc_pct = Format-Num $_.CpuCalosc 3
        CPU_rdzen_pct  = Format-Num $_.CpuRdzen 2
        RAM_WS_MB      = Format-Num $_.WorkingMB 1
        RAM_Private_MB = Format-Num $_.PrivateMB 1
    }
} | Export-Csv -Path $samplesPath -NoTypeInformation -Encoding UTF8 -UseCulture

# --- podsumowanie ---
function New-SummaryRow($name, $group, [double[]]$cpu, [double]$sampleCount) {
    $maxInstances = ($group | Group-Object Sekunda | Measure-Object Count -Maximum).Maximum
    [pscustomobject][ordered]@{
        Proces               = $name
        Probki_z_CPU         = $cpu.Count
        CPU_srednio_pct      = if ($cpu.Count) { ($cpu | Measure-Object -Average).Average } else { $null }
        CPU_p95_pct          = Get-Percentile $cpu 0.95
        CPU_max_pct          = if ($cpu.Count) { ($cpu | Measure-Object -Maximum).Maximum } else { $null }
        RAM_WS_srednio_MB    = ($group | Measure-Object WorkingMB -Average).Average
        Max_instancji        = $maxInstances
        Obecny_w_probkach_pct = 100 * ($group | Select-Object -ExpandProperty Sekunda -Unique).Count / $sampleCount
    }
}

$sampleCount = [double]($tick - 1)
$summary = @()
foreach ($g in $samples | Group-Object Proces | Sort-Object Name) {
    # suma po instancjach w danej sekundzie (duplikaty liczą się razem)
    $cpu = @($g.Group | Where-Object { $null -ne $_.CpuCalosc } | Group-Object Sekunda |
        ForEach-Object { ($_.Group | Measure-Object CpuCalosc -Sum).Sum })
    $summary += New-SummaryRow $g.Name $g.Group $cpu $sampleCount
}
$totalCpu = @($samples | Where-Object { $null -ne $_.CpuCalosc } | Group-Object Sekunda |
    ForEach-Object { ($_.Group | Measure-Object CpuCalosc -Sum).Sum })
if ($samples.Count) {
    $total = New-SummaryRow 'RAZEM' $samples $totalCpu $sampleCount
    $total.RAM_WS_srednio_MB = ($samples | Group-Object Sekunda |
        ForEach-Object { ($_.Group | Measure-Object WorkingMB -Sum).Sum } | Measure-Object -Average).Average
    $total.Max_instancji = $null
    $summary += $total
}

$summaryOut = $summary | ForEach-Object {
    [pscustomobject][ordered]@{
        Proces                = $_.Proces
        Probki_z_CPU          = $_.Probki_z_CPU
        CPU_srednio_pct       = Format-Num $_.CPU_srednio_pct 3
        CPU_p95_pct           = Format-Num $_.CPU_p95_pct 3
        CPU_max_pct           = Format-Num $_.CPU_max_pct 3
        RAM_WS_srednio_MB     = Format-Num $_.RAM_WS_srednio_MB 1
        Max_instancji         = $_.Max_instancji
        Obecny_w_probkach_pct = Format-Num $_.Obecny_w_probkach_pct 0
    }
}
$summaryPath = Join-Path $OutDir "$Label-podsumowanie.csv"
$summaryOut | Export-Csv -Path $summaryPath -NoTypeInformation -Encoding UTF8 -UseCulture

Write-Host ''
Write-Host "CPU w % całego procesora (100% = $cores wątków):"
$summaryOut | Format-Table -AutoSize | Out-Host
$dupes = $summary | Where-Object { $_.Max_instancji -gt 1 }
if ($dupes) { Write-Warning "Więcej niż jedna instancja: $(($dupes.Proces) -join ', ')" }
Write-Host "Zapisano: $samplesPath"
Write-Host "          $summaryPath"
