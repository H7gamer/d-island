// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using DIsland.Interop;

namespace DIsland.Services;

/// <summary>
/// Copies a screen rectangle into a reusable 32-bit DIB with GDI. Windows that excluded themselves
/// from capture (the island in Liquid Glass mode) don't appear in the copy, so it shows what's behind.
/// </summary>
internal sealed class ScreenCapture : IDisposable
{
    private IntPtr _dc;
    private IntPtr _dib;
    private IntPtr _previous;

    public IntPtr Bits { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;
    public int BufferSize => Stride * Height;

    public bool Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        EnsureBuffer(width, height);
        if (Bits == IntPtr.Zero) return false;

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false; // e.g. secure desktop / locked
        try
        {
            return NativeMethods.BitBlt(_dc, 0, 0, width, height, screen, x, y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose() => Free();

    private void EnsureBuffer(int width, int height)
    {
        if (width == Width && height == Height && _dib != IntPtr.Zero) return;
        Free();

        _dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        var header = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down rows, matching WPF's layout
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = NativeMethods.CreateDIBSection(_dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
        {
            Free();
            return;
        }
        _previous = NativeMethods.SelectObject(_dc, _dib);
        Bits = bits;
        Width = width;
        Height = height;
    }

    private void Free()
    {
        if (_dc != IntPtr.Zero)
        {
            if (_previous != IntPtr.Zero) NativeMethods.SelectObject(_dc, _previous);
            NativeMethods.DeleteDC(_dc);
        }
        if (_dib != IntPtr.Zero) NativeMethods.DeleteObject(_dib);
        _dc = _dib = _previous = Bits = IntPtr.Zero;
        Width = Height = 0;
    }
}
