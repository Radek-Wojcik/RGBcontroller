<#
.SYNOPSIS
    Testuje sterownik LED (protokół Adalight) przez port COM.

.DESCRIPTION
    Tryby (-Test):
      Hello   - otwiera port i nasłuchuje powitania "Ada" wysyłanego przez firmware
      Colors  - cała taśma: czerwony, zielony, niebieski, biały, potem zgaszenie
      Chase   - jedna dioda biegnie od początku do końca taśmy (sprawdza liczbę i kierunek)
      Rate    - wysyła klatki najszybciej jak się da i mierzy osiągalne FPS
      Off     - gasi taśmę

.EXAMPLE
    .\Test-Adalight.ps1 -Port COM3 -Leds 113 -Test Colors
#>
param(
    [string]$Port = 'COM3',
    [int]$Leds = 113,
    [int]$Baud = 115200,
    [ValidateSet('Hello', 'Colors', 'Chase', 'Rate', 'Off')]
    [string]$Test = 'Colors',
    [ValidateRange(0, 255)]
    [int]$Brightness = 64,
    [int]$ChaseDelayMs = 40,
    [int]$RateSeconds = 5
)

$ErrorActionPreference = 'Stop'

function New-Frame([int]$count) {
    $n = $count - 1
    $hi = ($n -shr 8) -band 0xFF
    $lo = $n -band 0xFF
    $frame = New-Object byte[] (6 + 3 * $count)
    $frame[0] = [byte][char]'A'; $frame[1] = [byte][char]'d'; $frame[2] = [byte][char]'a'
    $frame[3] = $hi; $frame[4] = $lo; $frame[5] = $hi -bxor $lo -bxor 0x55
    # przecinek: zwróć byte[] w całości, inaczej PowerShell rozpakuje tablicę do object[]
    # i Set-Led będzie pisać do kopii (do taśmy pójdą same zera)
    , $frame
}

function Set-Led([byte[]]$frame, [int]$i, [int]$r, [int]$g, [int]$b) {
    $o = 6 + 3 * $i
    $frame[$o] = $r; $frame[$o + 1] = $g; $frame[$o + 2] = $b
}

function Set-All([byte[]]$frame, [int]$r, [int]$g, [int]$b) {
    for ($i = 0; $i -lt $Leds; $i++) { Set-Led $frame $i $r $g $b }
}

$sp = New-Object System.IO.Ports.SerialPort $Port, $Baud, 'None', 8, 'One'
$sp.ReadTimeout = 500
$sp.WriteTimeout = 2000
$sp.Open()
try {
    $frame = New-Frame $Leds
    $v = $Brightness

    switch ($Test) {
        'Hello' {
            Write-Host "Nasłuchuję na $Port ($Baud bodów) przez 3 s..."
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $buf = ''
            while ($sw.ElapsedMilliseconds -lt 3000) {
                if ($sp.BytesToRead) { $buf += $sp.ReadExisting() }
                Start-Sleep -Milliseconds 50
            }
            if ($buf) { Write-Host "Odebrano: '$($buf -replace "`r|`n", '\n')'" } else { Write-Host 'Brak powitania (to normalne dla części firmware).' }
        }
        'Colors' {
            foreach ($c in @(@($v, 0, 0, 'czerwony'), @(0, $v, 0, 'zielony'), @(0, 0, $v, 'niebieski'), @($v, $v, $v, 'biały'))) {
                Write-Host "-> $($c[3])"
                Set-All $frame $c[0] $c[1] $c[2]
                # kilka powtórzeń na wypadek zgubionej pierwszej klatki
                1..3 | ForEach-Object { $sp.Write($frame, 0, $frame.Length); Start-Sleep -Milliseconds 100 }
                Start-Sleep -Milliseconds 1500
            }
            Set-All $frame 0 0 0
            $sp.Write($frame, 0, $frame.Length)
        }
        'Chase' {
            Write-Host "Dioda biegnie po $Leds LED-ach (pierwsze 3 na zielono, ostatnie 3 na czerwono jako znaczniki)."
            for ($i = 0; $i -lt $Leds; $i++) {
                Set-All $frame 0 0 0
                0..2 | ForEach-Object { Set-Led $frame $_ 0 $v 0; Set-Led $frame ($Leds - 1 - $_) $v 0 0 }
                Set-Led $frame $i $v $v $v
                $sp.Write($frame, 0, $frame.Length)
                Start-Sleep -Milliseconds $ChaseDelayMs
            }
            Set-All $frame 0 0 0
            $sp.Write($frame, 0, $frame.Length)
        }
        'Rate' {
            $count = 0
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($sw.Elapsed.TotalSeconds -lt $RateSeconds) {
                $h = ($count * 4) % 360
                # prosta tęcza, żeby było widać, czy klatki dochodzą płynnie
                for ($i = 0; $i -lt $Leds; $i++) {
                    $hue = ($h + $i * 360 / $Leds) % 360
                    $x = 1 - [math]::Abs((($hue / 60) % 2) - 1)
                    $rgb = switch ([int][math]::Floor($hue / 60)) {
                        0 { 1, $x, 0 } 1 { $x, 1, 0 } 2 { 0, 1, $x } 3 { 0, $x, 1 } 4 { $x, 0, 1 } default { 1, 0, $x }
                    }
                    Set-Led $frame $i ([int]($rgb[0] * $v)) ([int]($rgb[1] * $v)) ([int]($rgb[2] * $v))
                }
                $sp.Write($frame, 0, $frame.Length)
                $sp.BaseStream.Flush()
                $count++
            }
            $fps = $count / $sw.Elapsed.TotalSeconds
            $theory = $Baud / 10 / $frame.Length
            Write-Host ("Wysłano {0} klatek w {1:N1} s = {2:N1} FPS (teoretyczne maksimum łącza: {3:N1} FPS, klatka {4} B)" -f $count, $sw.Elapsed.TotalSeconds, $fps, $theory, $frame.Length)
            Set-All $frame 0 0 0
            $sp.Write($frame, 0, $frame.Length)
        }
        'Off' {
            Set-All $frame 0 0 0
            1..3 | ForEach-Object { $sp.Write($frame, 0, $frame.Length); Start-Sleep -Milliseconds 50 }
        }
    }
}
finally {
    Start-Sleep -Milliseconds 100
    $sp.Close()
}
