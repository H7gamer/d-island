// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DIsland.Services;

/// <summary>
/// Whether this process runs with the sparse package's identity (Identity\setup-identity.ps1), which
/// Windows requires for UserNotificationListener and the AppointmentStore. Fixed for the process's life:
/// identity is assigned when the exe starts.
/// </summary>
public static class PackageIdentity
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    public static bool HasIdentity { get; } = Check();

    private static bool Check()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
    }

    public static string SetupScript => Path.Combine(AppContext.BaseDirectory, "Identity", "setup-identity.ps1");

    public static string SetupLog => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIsland", "Identity", "setup.log");

    /// <summary>
    /// Runs the setup script in a visible console (it downloads tools, then Windows asks for permission
    /// once), restarting the island afterwards so it starts with the identity.
    /// </summary>
    public static void RunSetup()
    {
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{SetupScript}\" -Restart")
        {
            UseShellExecute = true,
        })?.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);
}
