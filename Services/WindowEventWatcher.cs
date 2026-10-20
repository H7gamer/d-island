// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Windows.Threading;
using DIsland.Interop;

namespace DIsland.Services;

/// <summary>
/// Raises <see cref="Changed"/> (debounced, on the UI thread) when top-level windows move, resize,
/// show, hide, minimize, restore, (un)cloak or change z-order. Replaces polling with WinEvent hooks,
/// so the island does no work while nothing on screen changes.
/// </summary>
internal sealed class WindowEventWatcher : IDisposable
{
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_REORDER = 0x8004;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const uint GA_ROOT = 2;

    private readonly NativeMethods.WinEventDelegate _callback; // keep alive while hooked
    private readonly List<IntPtr> _hooks = new();
    private readonly DispatcherTimer _debounce;

    public WindowEventWatcher(TimeSpan debounce)
    {
        _callback = OnWinEvent;
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };

        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_REORDER);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
    }

    public event Action? Changed;

    public void Dispose()
    {
        foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        _debounce.Stop();
    }

    private void Hook(uint min, uint max)
    {
        // Out-of-context hooks are delivered on this (UI) thread's message loop; no injection.
        var hook = NativeMethods.SetWinEventHook(min, max, IntPtr.Zero, _callback, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Cheap filters first: only whole top-level windows (not cursors, carets, child controls).
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        if (eventType != EVENT_SYSTEM_FOREGROUND && NativeMethods.GetAncestor(hwnd, GA_ROOT) != hwnd) return;
        if (!_debounce.IsEnabled) _debounce.Start();
    }
}
