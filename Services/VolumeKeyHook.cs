// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Runtime.InteropServices;
using System.Windows.Threading;
using DIsland.Interop;

namespace DIsland.Services;

/// <summary>
/// Optional low-level keyboard hook that swallows the volume keys so the Windows volume flyout never
/// appears; the app applies the change itself via <see cref="VolumeKey"/>. Works on Windows 10 and 11,
/// unlike window-hunting tricks that depend on the flyout's internal window classes.
/// </summary>
public sealed class VolumeKeyHook : IDisposable
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // keep the delegate alive while hooked
    private IntPtr _hook;

    public VolumeKeyHook()
    {
        _proc = HookProc;
    }

    /// <summary>Virtual-key code of a pressed volume key, raised on the UI thread.</summary>
    public event Action<int>? VolumeKey;

    public bool Enabled
    {
        get => _hook != IntPtr.Zero;
        set
        {
            if (value == Enabled) return;
            if (value)
            {
                _hook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            }
            else
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    public void Dispose() => Enabled = false;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
            if (vk is NativeMethods.VK_VOLUME_MUTE or NativeMethods.VK_VOLUME_DOWN or NativeMethods.VK_VOLUME_UP)
            {
                var msg = (int)wParam;
                if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
                    _ui.InvokeAsync(() => VolumeKey?.Invoke(vk)); // return fast; hooks have a timeout
                return new IntPtr(1); // swallow down and up
            }
        }
        return CallNext(nCode, wParam, lParam);
    }

    private IntPtr CallNext(int nCode, IntPtr wParam, IntPtr lParam) =>
        NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
}
