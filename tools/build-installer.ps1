# D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
# Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

# Builds the D-Island installer: artifacts\D-Island-Setup-<version>.exe
#
#   1. Generates DIsland.ico (the black pill with a green dot, as in the tray) if it's missing.
#   2. Publishes a self-contained Release build (no .NET install needed on the target PC).
#   3. Compiles installer\DIsland.iss with Inno Setup 6 (winget install JRSoftware.InnoSetup).
#
# Usage: .\tools\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"

# ---- 1. icon (multi-size, PNG-compressed entries; Windows Vista and later read these)
$ico = Join-Path $root 'DIsland.ico'
if (-not (Test-Path $ico)) {
    Write-Host 'Generating DIsland.ico...'
    Add-Type -AssemblyName System.Drawing
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $frames = foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        $w = $size * 0.92; $h = $size * 0.46; $x = ($size - $w) / 2; $y = ($size - $h) / 2
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc($x, $y, $h, $h, 90, 180)
        $path.AddArc($x + $w - $h, $y, $h, $h, 270, 180)
        $path.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 18, 18, 20))), $path)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 255, 255, 255)), ([Math]::Max(1, $size / 22))
        $g.DrawPath($pen, $path)
        $dot = $h * 0.36
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 48, 209, 88))),
            $x + $w - $h * 0.5 - $dot / 2, $y + ($h - $dot) / 2, $dot, $dot)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , @($size, $ms.ToArray())
    }
    $out = New-Object System.IO.MemoryStream
    $wr = New-Object System.IO.BinaryWriter $out
    $wr.Write([uint16]0); $wr.Write([uint16]1); $wr.Write([uint16]$frames.Count)   # ICONDIR
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {                                                         # ICONDIRENTRY
        $s = if ($f[0] -ge 256) { 0 } else { $f[0] }
        $wr.Write([byte]$s); $wr.Write([byte]$s); $wr.Write([byte]0); $wr.Write([byte]0)
        $wr.Write([uint16]1); $wr.Write([uint16]32); $wr.Write([uint32]$f[1].Length); $wr.Write([uint32]$offset)
        $offset += $f[1].Length
    }
    foreach ($f in $frames) { $wr.Write([byte[]]$f[1]) }
    [System.IO.File]::WriteAllBytes($ico, $out.ToArray())
}

# ---- 1b. wizard artwork: the pill on the setup pages (small: top-right corner; large: finish page)
Add-Type -AssemblyName System.Drawing
function New-PillImage([int]$width, [int]$height, [string]$file, [bool]$dark) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    if ($dark) {
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $height),
            ([System.Drawing.Color]::FromArgb(255, 44, 44, 52)), ([System.Drawing.Color]::FromArgb(255, 14, 14, 16))
        $g.FillRectangle($bg, 0, 0, $width, $height)
    } else {
        $g.Clear([System.Drawing.Color]::White)
    }
    $w = [Math]::Min($width * 0.8, $height * 1.6); $h = $w * 0.32
    $x = ($width - $w) / 2; $y = if ($dark) { $height * 0.22 } else { ($height - $h) / 2 }
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $h, $h, 90, 180); $path.AddArc($x + $w - $h, $y, $h, $h, 270, 180); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 10, 12))), $path)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 255, 255, 255)), ([Math]::Max(1, $h / 16))), $path)
    $dot = $h * 0.34
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 48, 209, 88))), $x + $w - $h * 0.5 - $dot / 2, $y + ($h - $dot) / 2, $dot, $dot)
    if ($dark) {
        $font = New-Object System.Drawing.Font 'Segoe UI Semibold', ($width * 0.12), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $format = New-Object System.Drawing.StringFormat; $format.Alignment = 'Center'
        $g.TextRenderingHint = 'AntiAliasGridFit'
        $g.DrawString('D-Island', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($y + $h + $height * 0.06), $width, ($width * 0.2)), $format)
    }
    $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}
$installerDir = Join-Path $root 'installer'
New-PillImage 58 58 (Join-Path $installerDir 'wizard-small.bmp') $false
New-PillImage 116 116 (Join-Path $installerDir 'wizard-small-2x.bmp') $false
New-PillImage 164 314 (Join-Path $installerDir 'wizard-large.bmp') $true
New-PillImage 328 628 (Join-Path $installerDir 'wizard-large-2x.bmp') $true

# ---- 2. self-contained publish
$version = ([xml](Get-Content (Join-Path $root 'DIsland.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '1.0.0' }
$publish = Join-Path $root 'artifacts\publish'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Publishing D-Island $version (self-contained, win-x64)..."
dotnet publish (Join-Path $root 'DIsland.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=false -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# ---- 3. installer
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup --scope user' }
Write-Host 'Compiling the installer...'
& $iscc "/DAppVersion=$version" "/DPublishDir=$publish" "/DIconFile=$ico" (Join-Path $root 'installer\DIsland.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
# ---- 4. checksum file: the in-app updater only installs a release that has one, and verifies against it
$setup = Join-Path $root "artifacts\D-Island-Setup-$version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText("$setup.sha256", "$hash  D-Island-Setup-$version.exe`n")
Write-Host ("Built {0} ({1:N1} MB), SHA-256 {2}" -f $setup, ((Get-Item $setup).Length / 1MB), $hash)
Write-Host "Upload BOTH files to the GitHub release: D-Island-Setup-$version.exe and D-Island-Setup-$version.exe.sha256"
