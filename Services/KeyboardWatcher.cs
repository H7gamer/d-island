// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DIsland.Services;

public enum LockKey
{
    CapsLock,
    NumLock,
}

/// <summary>
/// Watches a few keys system-wide: Caps Lock / Num Lock toggles, and the paste shortcuts (Ctrl+V,
/// Shift+Insert). Uses Raw Input (RIDEV_INPUTSINK) on the island window rather than a low-level
/// keyboard hook: raw input is delivered asynchronously, so a busy UI thread can never delay the
/// user's typing, and there's no cost between key presses. Every other key is ignored unread.
/// </summary>
public sealed class KeyboardWatcher : IDisposable
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_CAPITAL = 0x14;
    private const int VK_INSERT = 0x2D;
    private const int VK_V = 0x56;
    private const int VK_NUMLOCK = 0x90;
    private const int WM_INPUT = 0x00FF;
    private const int RIM_TYPEKEYBOARD = 1;
    private const ushort RI_KEY_BREAK = 1;
    private const uint RIDEV_INPUTSINK = 0x100;
    private const uint RIDEV_REMOVE = 0x1;
    private const uint RID_INPUT = 0x10000003;

    private HwndSource? _source;
    private bool _registered;
    private bool _pasteKeyDown;

    /// <summary>Key and its new state (true = on), raised on the UI thread.</summary>
    public event Action<LockKey, bool>? LockToggled;

    /// <summary>A paste shortcut was pressed (whether the focused app pastes is up to it).</summary>
    public event Action? PasteShortcut;

    public bool LockKeysEnabled { get; set; }
    public bool PasteEnabled { get; set; }

    public void Attach(HwndSource source)
    {
        _source = source;
        source.AddHook(WndProc);
        Update();
    }

    /// <summary>Registers for keyboard input only while something wants it.</summary>
    public void Update()
    {
        var want = LockKeysEnabled || PasteEnabled;
        if (_source is null || want == _registered) return;
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // generic desktop
            usUsage = 0x06,     // keyboard
            dwFlags = want ? RIDEV_INPUTSINK : RIDEV_REMOVE,
            hwndTarget = want ? _source.Handle : IntPtr.Zero,
        };
        if (RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) _registered = want;
        else App.LogError(new InvalidOperationException($"RegisterRawInputDevices failed ({Marshal.GetLastWin32Error()})"));
    }

    public void Dispose()
    {
        LockKeysEnabled = PasteEnabled = false;
        Update();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT || !_registered) return IntPtr.Zero;

        var size = (uint)Marshal.SizeOf<RAWINPUT>();
        if (GetRawInputData(lParam, RID_INPUT, out var input, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == unchecked((uint)-1)
            || input.header.dwType != RIM_TYPEKEYBOARD)
            return IntPtr.Zero;

        var vk = input.keyboard.VKey;
        var released = (input.keyboard.Flags & RI_KEY_BREAK) != 0;

        if (LockKeysEnabled && released && vk is VK_CAPITAL or VK_NUMLOCK)
        {
            // Act on release (holding the key repeats key-down). The toggle state is updated as the
            // key is processed; read it once this message is done.
            var key = vk == VK_CAPITAL ? LockKey.CapsLock : LockKey.NumLock;
            Dispatcher.CurrentDispatcher.InvokeAsync(
                () => LockToggled?.Invoke(key, (GetKeyState(vk) & 1) != 0), DispatcherPriority.Input);
        }
        else if (PasteEnabled && vk is VK_V or VK_INSERT)
        {
            if (released)
            {
                _pasteKeyDown = false;
            }
            else if (!_pasteKeyDown) // first key-down only, not auto-repeat
            {
                _pasteKeyDown = true;
                bool Down(int k) => GetAsyncKeyState(k) < 0;
                var paste = vk == VK_V
                    ? Down(VK_CONTROL) && !Down(VK_MENU) && !Down(VK_SHIFT)
                    : Down(VK_SHIFT) && !Down(VK_CONTROL) && !Down(VK_MENU);
                if (paste) Dispatcher.CurrentDispatcher.InvokeAsync(() => PasteShortcut?.Invoke(), DispatcherPriority.Input);
            }
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    /// <summary>Keyboard view of RAWINPUT, padded to the size of the union's larger members.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWKEYBOARD keyboard;
        private readonly long _pad0, _pad1;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, out RAWINPUT data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);
}
