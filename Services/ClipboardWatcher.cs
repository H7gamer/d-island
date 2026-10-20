// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DIsland.Services;

public enum ClipboardKind
{
    Text,
    Image,
    Files,
    Other,
}

/// <summary>What was copied: kind plus a short description ("3 files", the first line of text).</summary>
public sealed record ClipboardEvent(ClipboardKind Kind, string? Preview);

/// <summary>
/// Reports clipboard changes via AddClipboardFormatListener (WM_CLIPBOARDUPDATE): no polling.
/// Content that password managers mark as private ("ExcludeClipboardContentFromMonitorProcessing")
/// is skipped entirely.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private static readonly uint ExcludeFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");

    private readonly DispatcherTimer _settle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
    private HwndSource? _source;
    private bool _listening;

    public ClipboardWatcher()
    {
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Read();
        };
    }

    /// <summary>Raised on the UI thread after a copy.</summary>
    public event Action<ClipboardEvent>? Copied;

    public bool Enabled { get; set; }

    /// <summary>Include a text preview (the first line) in <see cref="ClipboardEvent.Preview"/>.</summary>
    public bool ReadText { get; set; } = true;

    public void Attach(HwndSource source)
    {
        _source = source;
        source.AddHook(WndProc);
        Update();
    }

    public void Update()
    {
        if (_source is null || Enabled == _listening) return;
        if (Enabled ? AddClipboardFormatListener(_source.Handle) : RemoveClipboardFormatListener(_source.Handle))
            _listening = Enabled;
    }

    public void Dispose()
    {
        Enabled = false;
        Update();
        _settle.Stop();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _listening)
        {
            // Apps often write several formats in quick succession (and some clear first); read once
            // things have settled, and never inside the notification (the writer may still hold it open).
            _settle.Stop();
            _settle.Start();
        }
        return IntPtr.Zero;
    }

    private void Read()
    {
        if (IsClipboardFormatAvailable(ExcludeFormat)) return; // a password manager asked not to be watched

        try
        {
            ClipboardEvent? e = null;
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                e = new ClipboardEvent(ClipboardKind.Files, files.Count == 1
                    ? System.IO.Path.GetFileName(files[0]?.TrimEnd('\\'))
                    : $"{files.Count} items");
            }
            else if (Clipboard.ContainsImage())
            {
                e = new ClipboardEvent(ClipboardKind.Image, null);
            }
            else if (Clipboard.ContainsText())
            {
                string? preview = null;
                if (ReadText)
                {
                    var text = Clipboard.GetText().Trim();
                    if (text.Length == 0) return; // emptied, not copied
                    var line = text.Split('\n', 2)[0].Trim();
                    preview = line.Length > 60 ? line[..60] + "…" : line;
                }
                e = new ClipboardEvent(ClipboardKind.Text, preview);
            }
            else if (CountClipboardFormats() > 0)
            {
                e = new ClipboardEvent(ClipboardKind.Other, null);
            }
            if (e is not null) Copied?.Invoke(e);
        }
        catch (Exception)
        {
            // The clipboard is locked by another app (COMException / ExternalException); skip this one.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern int CountClipboardFormats();
}
