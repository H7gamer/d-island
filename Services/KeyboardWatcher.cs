// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DIsland.Services;

public enum LockKey
{
    CapsLock,
    NumLock,
}

/// <summary>
/// Watches a few keys system-wide: Caps Lock / Num Lock toggles, the paste shortcuts (Ctrl+V,
/// Shift+Insert), the modifier keys, and the hide/show shortcut when another app owns it. Uses Raw Input (RIDEV_INPUTSINK) on the island window rather than a low-level
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
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int WM_INPUT = 0x00FF;
    private const int RIM_TYPEKEYBOARD = 1;
    private const ushort RI_KEY_BREAK = 1;
    private const ushort RI_KEY_E0 = 2;
    private const uint RIDEV_INPUTSINK = 0x100;
    private const uint RIDEV_REMOVE = 0x1;
    private const uint RID_INPUT = 0x10000003;

    private HwndSource? _source;
    private bool _registered;
    private bool _pasteKeyDown;
    private bool _shortcutKeyDown;

    /// <summary>Key and its new state (true = on), raised on the UI thread.</summary>
    public event Action<LockKey, bool>? LockToggled;

    /// <summary>A paste shortcut was pressed (whether the focused app pastes is up to it).</summary>
    public event Action? PasteShortcut;

    /// <summary><see cref="Shortcut"/> was pressed.</summary>
    public event Action? ShortcutPressed;

    public bool LockKeysEnabled { get; set; }
    public bool PasteEnabled { get; set; }

    /// <summary>
    /// A shortcut to recognise here, for when RegisterHotKey can't have it because another app (a
    /// graphics or keyboard utility, another overlay…) registered it first. Raw input still sees the
    /// keys then, so the shortcut keeps working; the other app may react to it too.
    /// </summary>
    public (ModifierKeys Modifiers, int VirtualKey)? Shortcut { get; set; }

    public void Attach(HwndSource source)
    {
        _source = source;
        source.AddHook(WndProc);
        Update();
    }

    /// <summary>Registers for keyboard input only while something wants it.</summary>
    public void Update()
    {
        var want = LockKeysEnabled || PasteEnabled || Shortcut is not null;
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
        Shortcut = null;
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
        TrackModifier(vk, input.keyboard.Flags, input.keyboard.MakeCode, released);

        if (Shortcut is { } shortcut && vk == shortcut.VirtualKey)
        {
            // When another app owns the shortcut, Windows turns the key-down into its hotkey message and
            // raw input never sees it; only the key-up comes through. So: act on the key-down when it
            // arrives, otherwise on a key-up whose key-down was swallowed (modifiers still held).
            var fire = false;
            if (released)
            {
                fire = !_shortcutKeyDown;
                _shortcutKeyDown = false;
            }
            else if (!_shortcutKeyDown) // first key-down only, not auto-repeat
            {
                _shortcutKeyDown = true;
                fire = true;
            }
            if (fire && ModifiersDown() == shortcut.Modifiers)
                Dispatcher.CurrentDispatcher.InvokeAsync(() => ShortcutPressed?.Invoke(), DispatcherPriority.Input);
        }

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

    // Modifier keys held (key → when pressed), per physical key (left/right), as seen in this raw input
    // stream. Raw input arrives asynchronously, so asking Windows (GetAsyncKeyState) when the
    // shortcut's key comes in can already see a quickly released modifier as up; the stream's own
    // order is exact. A key-up can go missing (e.g. Win+L locks before Win is released), so an entry
    // only counts while Windows agrees it's down, or for half a second after it was pressed.
    private readonly Dictionary<int, long> _modifiersHeld = new();

    private void TrackModifier(int vk, ushort flags, ushort makeCode, bool released)
    {
        if (vk is not (VK_CONTROL or VK_MENU or VK_SHIFT or VK_LWIN or VK_RWIN)) return;
        var id = vk << 16 | ((flags & RI_KEY_E0) != 0 ? 1 << 8 : 0) | makeCode; // left and right are separate keys
        if (released) _modifiersHeld.Remove(id);
        else _modifiersHeld.TryAdd(id, Environment.TickCount64); // auto-repeat keeps the first press time
    }

    /// <summary>The modifier keys held right now (either side).</summary>
    private ModifierKeys ModifiersDown()
    {
        var now = Environment.TickCount64;
        var modifiers = ModifierKeys.None;
        foreach (var (id, pressed) in _modifiersHeld)
        {
            if (now - pressed > 500 && GetAsyncKeyState(id >> 16) >= 0) continue; // stale: its key-up was missed
            modifiers |= (id >> 16) switch
            {
                VK_CONTROL => ModifierKeys.Control,
                VK_MENU => ModifierKeys.Alt,
                VK_SHIFT => ModifierKeys.Shift,
                _ => ModifierKeys.Windows,
            };
        }
        return modifiers;
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
