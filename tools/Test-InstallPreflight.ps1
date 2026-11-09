<#
.SYNOPSIS
    Self-test of InstallPreflight.ps1 on scratch locations (a temp Startup folder and a scratch HKCU key); touches no real
    autostart entry, no scheduled task and no file of OpenRGB or AmbiBox.
#>
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'InstallPreflight.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ('preflight-' + [guid]::NewGuid().ToString('N'))
$startup = Join-Path $work 'Startup'
$stateFile = Join-Path $work 'state.json'
$runKey = 'HKCU:\Software\RGBcontrollerPreflightTest\Run'
New-Item -ItemType Directory -Force $startup | Out-Null
New-Item -Path $runKey -Force | Out-Null
$failures = 0
function Assert($ok, $what) { if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what" -ForegroundColor Red; $script:failures++ } }
$common = @('-RunKeyPaths', $runKey, '-StartupFolders', $startup, '-SkipTasks', '-SkipDriverChecks', '-StateFile', $stateFile)

try {
    New-ItemProperty -Path $runKey -Name 'OpenRGB_Auto' -Value '"C:\Program Files\OpenRGB\OpenRGB.exe" --startminimized' -PropertyType String | Out-Null
    New-ItemProperty -Path $runKey -Name 'Other' -Value 'C:\Windows\notepad.exe' -PropertyType String | Out-Null
    New-ItemProperty -Path $runKey -Name 'RGBcontroller.Agent' -Value '"C:\Program Files\RGBcontroller\openrgb\OpenRGB.exe"' -PropertyType String | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $startup 'AmbiBox.lnk'))
    $link.TargetPath = 'C:\Windows\notepad.exe'
    $link.Save()

    $out = Join-Path $work 'consent.txt'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Check -OutFile $out @common | Out-Null
    $consent = @(Get-Content $out)
    Assert ($consent.Count -eq 2) 'check finds exactly the OpenRGB Run value and the AmbiBox shortcut'
    Assert (($consent -join ' ') -match 'OpenRGB_Auto' -and ($consent -join ' ') -match 'AmbiBox') 'check names both'
    Assert (-not (($consent -join ' ') -match 'RGBcontroller\.Agent|Other')) 'check ignores our own entry and unrelated programs'

    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Disable @common | Out-Null
    Assert (-not (Get-ItemProperty $runKey -Name 'OpenRGB_Auto' -ErrorAction SilentlyContinue)) 'disable removes the Run value'
    Assert ((Get-ItemProperty $runKey -Name 'Other').Other -eq 'C:\Windows\notepad.exe') 'disable leaves other values alone'
    Assert (-not (Test-Path (Join-Path $startup 'AmbiBox.lnk'))) 'disable moves the shortcut aside'
    Assert ((@(Get-Content $stateFile -Raw | ConvertFrom-Json | ForEach-Object { $_ })).Count -eq 2) 'disable records what it changed'

    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Check -OutFile $out @common | Out-Null
    Assert (@(Get-Content $out).Count -eq 0) 'nothing is left to ask about after disabling'

    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Restore @common | Out-Null
    Assert ((Get-ItemProperty $runKey -Name 'OpenRGB_Auto').OpenRGB_Auto -eq '"C:\Program Files\OpenRGB\OpenRGB.exe" --startminimized') 'restore brings the Run value back unchanged'
    Assert (Test-Path (Join-Path $startup 'AmbiBox.lnk')) 'restore brings the shortcut back'
    Assert (-not (Test-Path $stateFile)) 'restore removes the state file'

    # Exit codes the installer reacts to: 0 fine, 2 PawnIO missing, 3 WebView2 missing (PawnIO wins when both are).
    function ExitCodeOf([string]$simulate) {
        $extra = if ($simulate) { @('-Simulate', $simulate) } else { @() }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Check @common @extra | Out-Null
        return $LASTEXITCODE
    }
    Assert ((ExitCodeOf '') -eq 0) 'exit code 0 when PawnIO and WebView2 are present'
    Assert ((ExitCodeOf 'PawnIoMissing') -eq 2) 'exit code 2 when PawnIO is missing'
    Assert ((ExitCodeOf 'WebView2Missing') -eq 3) 'exit code 3 when only WebView2 is missing'
    Assert ((ExitCodeOf 'PawnIoMissing,WebView2Missing') -eq 2) 'PawnIO is reported first when both are missing'
}
finally {
    Remove-Item 'HKCU:\Software\RGBcontrollerPreflightTest' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) { Write-Host "$failures check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'All preflight checks passed.' -ForegroundColor Green

exit 0
