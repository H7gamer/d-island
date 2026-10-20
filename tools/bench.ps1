# D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
# Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

# Measures D-Island: idle CPU, animation CPU (expand/collapse loop) and memory.
# Needs a Debug build (the DISLAND_BENCH hook is compiled only in Debug). Uses the real settings file.
# Usage: .\tools\bench.ps1 [-Label "my change"]
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\bin\Debug\net8.0-windows10.0.19041.0\DIsland.exe"),
    [string]$Label = "run"
)
Get-Process DIsland -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1

function Measure-Cpu($seconds) {
    $p = Get-Process DIsland
    $c0 = $p.TotalProcessorTime; $t0 = Get-Date
    Start-Sleep $seconds
    $p.Refresh()
    100 * ($p.TotalProcessorTime - $c0).TotalMilliseconds / ((Get-Date) - $t0).TotalMilliseconds
}

Start-Process $Exe; Start-Sleep 12
$idle = Measure-Cpu 20
$p = Get-Process DIsland; $ws = $p.WorkingSet64 / 1MB; $priv = $p.PrivateMemorySize64 / 1MB
Get-Process DIsland | Stop-Process -Force; Start-Sleep 1

$env:DISLAND_BENCH = "1"; Start-Process $Exe; $env:DISLAND_BENCH = $null
Start-Sleep 12
$anim = Measure-Cpu 20
Get-Process DIsland | Stop-Process -Force

"{0,-28} idle {1,5:N2}%  animating {2,5:N2}%  working set {3,6:N1} MB  private {4,6:N1} MB" -f $Label, $idle, $anim, $ws, $priv
# Run-to-run noise is large (±5 points animating): compare averages of 3 runs.
