# D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
# Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

# Visual check of Liquid Glass: puts a striped, text-covered window behind the running island
# (which must be at TopOffset 0, Liquid Glass, blur > 0), screenshots, moves the window, screenshots
# again. Blurred, smeared colour inside the pill = working; sharp stripes/text = blur not working;
# unchanged colours after the move = blur not following.
#
# Coordinates assume a 1920x1080 display at 125% scaling. This PowerShell is DPI-unaware, so window
# positions are in logical pixels (physical / 1.25) while CopyFromScreen uses physical pixels.
# Screenshots fail with "The handle is invalid" while the screen saver runs; wait and retry.
param([string]$OutDir = $env:TEMP)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

function Shot($name) {
    $bmp = New-Object System.Drawing.Bitmap 600, 90
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(660, 0, 0, 0, $bmp.Size)
    $big = New-Object System.Drawing.Bitmap $bmp, 1200, 180
    $path = Join-Path $OutDir "$name.png"; $big.Save($path); $g.Dispose(); $bmp.Dispose(); $big.Dispose()
    $path
}

$pattern = New-Object System.Drawing.Bitmap 600, 100
$gp = [System.Drawing.Graphics]::FromImage($pattern)
$colors = @('Red', 'Orange', 'Yellow', 'Lime', 'Cyan', 'Blue', 'Magenta', 'White')
for ($i = 0; $i -lt 30; $i++) {
    $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromName($colors[$i % 8]))
    $gp.FillRectangle($b, $i * 20, 0, 10, 100); $b.Dispose()
}
$font = New-Object System.Drawing.Font('Segoe UI', [single]18, [System.Drawing.FontStyle]::Bold)
$gp.DrawString("SHARP TEXT BEHIND THE GLASS", $font, [System.Drawing.Brushes]::Black, 120, 6); $gp.Dispose()

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'; $form.StartPosition = 'Manual'; $form.ShowInTaskbar = $false
$form.Location = New-Object System.Drawing.Point(468, 0); $form.Size = New-Object System.Drawing.Size(600, 100)
$form.BackgroundImage = $pattern
$form.Show(); 1..30 | ForEach-Object { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
Shot "glass-1"
$form.Location = New-Object System.Drawing.Point(538, 0); 1..20 | ForEach-Object { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
Shot "glass-2-moved"
$form.Close()
