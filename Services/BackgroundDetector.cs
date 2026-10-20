// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Text;
using System.Windows.Threading;
using DIsland.Interop;

namespace DIsland.Services;

/// <summary>
/// Tells whether an app window is behind the island or only the desktop/wallpaper is, by walking
/// top-level windows and checking which visible, uncloaked app windows intersect the island.
/// </summary>
internal sealed class BackgroundDetector : IDisposable
{
    // Desktop, wallpaper host and taskbars don't count as "an app behind the island".
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    // Window events drive updates; this slow poll is only a safety net for missed events.
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private readonly Func<NativeMethods.RECT?> _area;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly StringBuilder _className = new(64);

    /// <param name="area">The island's current rectangle in screen pixels, or null if unknown.</param>
    public BackgroundDetector(Func<NativeMethods.RECT?> area, WindowEventWatcher windows)
    {
        _area = area;
        _poll.Tick += (_, _) => Poll();
        windows.Changed += () =>
        {
            if (_poll.IsEnabled) Poll();
        };
    }

    public bool AppBehind { get; private set; }

    public event Action? Changed;

    public bool IsRunning => _poll.IsEnabled;

    public void Start()
    {
        if (_poll.IsEnabled) return;
        _poll.Start();
        Poll();
    }

    /// <summary>Re-check now (e.g. after the island changed size).</summary>
    public void Refresh()
    {
        if (_poll.IsEnabled) Poll();
    }

    public void Stop() => _poll.Stop();

    public void Dispose() => _poll.Stop();

    private void Poll()
    {
        if (_area() is not { } area) return;

        var found = false;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!Covers(hwnd, area)) return true;
            found = true;
            return false; // stop enumerating
        }, IntPtr.Zero);

        if (found == AppBehind) return;
        AppBehind = found;
        Changed?.Invoke();
    }

    private bool Covers(IntPtr hwnd, NativeMethods.RECT area)
    {
        // Cheapest checks first; this runs for every top-level window on each window event.
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;

        // Tool windows and click-through windows are overlays (e.g. other widgets), not apps.
        var ex = NativeMethods.GetExStyle(hwnd);
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 || (ex & NativeMethods.WS_EX_TRANSPARENT) != 0) return false;

        if (!NativeMethods.TryGetFrameBounds(hwnd, out var r) || !NativeMethods.Intersects(r, area)) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownProcessId) return false;

        // Suspended UWP apps and windows on other virtual desktops are "visible" but cloaked.
        if (!NativeMethods.IsShownOnScreen(hwnd)) return false;

        _className.Clear();
        NativeMethods.GetClassName(hwnd, _className, _className.Capacity);
        return !ShellClasses.Contains(_className.ToString());
    }
}
