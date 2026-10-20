// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace DIsland.Services;

/// <summary>
/// One system-wide keyboard shortcut (RegisterHotKey), written as text like "Ctrl+Alt+I" so it can
/// live in settings. Registration fails if another app already owns the combination.
/// </summary>
public sealed class Hotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly int _id;
    private HwndSource? _source;
    private bool _registered;

    public Hotkey(int id) => _id = id;

    public event Action? Pressed;

    public void Attach(HwndSource source)
    {
        _source = source;
        source.AddHook(WndProc);
    }

    /// <summary>Registers <paramref name="text"/> (empty = none). Returns an error message, or null on success.</summary>
    public string? Set(string? text)
    {
        Unregister();
        if (_source is null || string.IsNullOrWhiteSpace(text)) return null;
        if (!TryParse(text, out var modifiers, out var key)) return $"\"{text}\" isn't a valid shortcut.";
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_source.Handle, _id, ToNative(modifiers) | MOD_NOREPEAT, vk);
        return _registered ? null : $"{text} is already used by another app. Pick another shortcut.";
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private void Unregister()
    {
        if (!_registered || _source is null) return;
        UnregisterHotKey(_source.Handle, _id);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    /// <summary>"Ctrl+Alt+I" from modifiers and key, in a fixed order.</summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(new KeyConverter().ConvertToInvariantString(key) ?? key.ToString());
        return string.Join('+', parts);
    }

    public static bool TryParse(string text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    try
                    {
                        if (new KeyConverter().ConvertFromInvariantString(part) is Key k) key = k;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                    break;
            }
        }
        // A bare key would swallow ordinary typing system-wide; require a modifier.
        return key != Key.None && modifiers != ModifierKeys.None;
    }

    private static uint ToNative(ModifierKeys m) =>
        (m.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0) | (m.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0)
        | (m.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0) | (m.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
