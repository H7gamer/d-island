# D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
# Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

# Checks that the running D-Island window stays above other windows that overlap it
# (a normal activated window, and an always-on-top one). Start the island first.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace ZT -Name U -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr h, System.Text.StringBuilder s, int n);
'@
$islandPid = (Get-Process DIsland).Id

function Get-Z([IntPtr]$hwnd) {
    $script:z = 0; $script:found = @{}
    [ZT.U]::EnumWindows({ param($h, $l)
        $script:z++
        [uint32]$p = 0; [void][ZT.U]::GetWindowThreadProcessId($h, [ref]$p)
        $sb = New-Object System.Text.StringBuilder 64; [void][ZT.U]::GetClassName($h, $sb, 64)
        if ($p -eq $islandPid -and $sb.ToString().StartsWith("HwndWrapper") -and -not $script:found.island) { $script:found.island = $script:z }
        if ($h -eq $hwnd) { $script:found.test = $script:z }
        $true }, [IntPtr]::Zero) | Out-Null
    $script:found
}

function Test-Window([string]$label, [bool]$topMost) {
    $form = New-Object System.Windows.Forms.Form
    $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(400, 0); $form.Size = New-Object System.Drawing.Size(700, 300)
    $form.TopMost = $topMost; $form.ShowInTaskbar = $false; $form.BackColor = 'SteelBlue'
    $form.Show(); $form.Activate()
    1..16 | ForEach-Object { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
    $z = Get-Z $form.Handle
    $form.Close()
    "{0,-34} island z={1,3}  test window z={2,3}  -> {3}" -f $label, $z.island, $z.test, ($(if ($z.island -lt $z.test) { 'island ON TOP' } else { 'island BEHIND' }))
}

Test-Window "normal window, activated" $false
Test-Window "always-on-top window, activated" $true
