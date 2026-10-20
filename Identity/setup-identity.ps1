# D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
# Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

# Gives DIsland.exe a package identity (needed for reading notifications and the Windows calendar) by
# registering a *sparse* package: only a manifest, whose "external location" is the folder holding the
# exe. Nothing is copied or installed besides that registration.
#
#   1. Gets makeappx/signtool from Microsoft's Microsoft.Windows.SDK.BuildTools NuGet package (cached).
#   2. Creates a self-signed code-signing certificate "CN=D-Island Dev" in the current user's store.
#   3. Trusts it machine-wide (Local Machine\Trusted People): the ONLY step that needs admin, so it runs
#      in a separate elevated process and Windows shows one UAC prompt.
#   4. Packs and signs the manifest, and registers it with the exe's folder as external location.
#
# Run again after moving the exe to another folder. Undo: Get-AppxPackage DIsland | Remove-AppxPackage
# (and remove the certificate from certlm.msc > Trusted People, and certmgr.msc > Personal).
param(
    [string]$ExeDir = (Split-Path $PSScriptRoot -Parent),
    [switch]$Restart
)
$ErrorActionPreference = 'Stop'
$publisher = 'CN=D-Island Dev'
$toolsVersion = '10.0.26100.1742'
$work = Join-Path $env:LOCALAPPDATA 'DIsland\Identity'
$log = Join-Path $work 'setup.log'
New-Item -ItemType Directory -Force $work | Out-Null
Start-Transcript -Path $log -Force | Out-Null

try {
    if (-not (Test-Path (Join-Path $ExeDir 'DIsland.exe'))) { throw "DIsland.exe not found in $ExeDir" }

    # ---- 1. packaging tools
    $toolsDir = Join-Path $work "BuildTools.$toolsVersion"
    if (-not (Test-Path $toolsDir)) {
        Write-Host "Downloading Microsoft.Windows.SDK.BuildTools $toolsVersion..."
        $nupkg = Join-Path $work 'buildtools.zip'
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$toolsVersion/microsoft.windows.sdk.buildtools.$toolsVersion.nupkg" -OutFile $nupkg -UseBasicParsing
        Expand-Archive $nupkg $toolsDir -Force
        Remove-Item $nupkg
    }
    $makeappx = Get-ChildItem $toolsDir -Recurse -Filter makeappx.exe | Where-Object FullName -like '*\x64\*' | Select-Object -First 1 -ExpandProperty FullName
    $signtool = Get-ChildItem $toolsDir -Recurse -Filter signtool.exe | Where-Object FullName -like '*\x64\*' | Select-Object -First 1 -ExpandProperty FullName
    if (-not $makeappx -or -not $signtool) { throw 'makeappx/signtool not found in the BuildTools package' }

    # ---- 2. signing certificate (current user)
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
    if (-not $cert) {
        Write-Host 'Creating the signing certificate...'
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
            -FriendlyName 'D-Island package signing' -CertStoreLocation Cert:\CurrentUser\My `
            -NotAfter (Get-Date).AddYears(10) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }

    # ---- 3. trust it (elevated, once)
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint
    if (-not $trusted) {
        Write-Host 'Trusting the certificate (Windows will ask for permission)...'
        $cer = Join-Path $work 'D-Island.cer'
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        $import = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
        $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
            -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $import
        if ($p.ExitCode -ne 0 -or -not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint)) {
            throw 'The certificate was not trusted (permission declined?)'
        }
    }

    # ---- 4. pack, sign, register
    $stage = Join-Path $work 'package'
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force (Join-Path $stage 'Assets') | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'AppxManifest.xml') $stage

    # Logos: the same black pill with a green dot as the tray icon.
    Add-Type -AssemblyName System.Drawing
    foreach ($logo in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
        $size = $logo[1]
        $bmp = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $h = $size * 0.44; $w = $size * 0.88; $x = ($size - $w) / 2; $y = ($size - $h) / 2
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc($x, $y, $h, $h, 90, 180); $path.AddArc($x + $w - $h, $y, $h, $h, 270, 180); $path.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 16, 16, 16))), $path)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 48, 209, 88))), $x + $w - $h * 0.85, $y + $h * 0.3, $h * 0.4, $h * 0.4)
        $bmp.Save((Join-Path $stage "Assets\$($logo[0])"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    }

    $msix = Join-Path $work 'DIsland.msix'
    & $makeappx pack /d $stage /p $msix /nv /o | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }

    Get-AppxPackage -Name DIsland | Remove-AppxPackage -ErrorAction SilentlyContinue
    Add-AppxPackage -Path $msix -ExternalLocation $ExeDir
    $pkg = Get-AppxPackage -Name DIsland
    if (-not $pkg) { throw 'The package did not register' }
    Write-Host "Registered $($pkg.PackageFullName) for $ExeDir"

    if ($Restart) {
        # Identity applies to processes started after registration.
        Get-Process DIsland -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 800
        Start-Process (Join-Path $ExeDir 'DIsland.exe')
    }
    Write-Host 'OK'
    $code = 0
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)"
    $code = 1
}
finally {
    Stop-Transcript | Out-Null
}
exit $code
