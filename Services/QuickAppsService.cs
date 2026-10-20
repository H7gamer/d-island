// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DIsland.Services;

public sealed record InstalledApp(string Id, string Name);

/// <summary>
/// Apps from the Start menu's "All apps" list (shell:AppsFolder), which covers desktop and Store
/// apps alike. An app is identified by its parsing name: an AppUserModelID or a known-folder path.
/// </summary>
public static class QuickAppsService
{
    private static readonly Dictionary<string, ImageSource?> IconCache = new();

    /// <summary>All Start menu apps, sorted by name. Takes about a second, so it runs off the UI thread.</summary>
    public static Task<IReadOnlyList<InstalledApp>> ListAsync()
    {
        var result = new TaskCompletionSource<IReadOnlyList<InstalledApp>>();
        // Shell.Application is apartment-threaded; enumerate on a short-lived STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                var apps = new List<InstalledApp>();
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name;
                    string id = item.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) apps.Add(new InstalledApp(id, name));
                }
                result.SetResult(apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                result.SetResult(Array.Empty<InstalledApp>());
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    /// <summary>The app's display name, or null if it isn't installed (any more).</summary>
    public static string? GetName(string id)
    {
        if (SHCreateItemFromParsingName(AppsFolderPath(id), IntPtr.Zero, typeof(IShellItem).GUID, out var obj) != 0
            || obj is not IShellItem item)
            return null;
        try
        {
            if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var namePtr) != 0) return null;
            try { return Marshal.PtrToStringUni(namePtr); }
            finally { Marshal.FreeCoTaskMem(namePtr); }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>The app's icon at <paramref name="size"/> pixels (with alpha), cached; null if unavailable.</summary>
    public static ImageSource? GetIcon(string id, int size)
    {
        var key = $"{size}|{id}";
        if (IconCache.TryGetValue(key, out var cached)) return cached;
        var icon = LoadIcon(id, size);
        IconCache[key] = icon;
        return icon;
    }

    public static void Launch(string id)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppsFolderPath(id)) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static string AppsFolderPath(string id) => @"shell:AppsFolder\" + id;

    /// <summary>Sizes icon files usually contain; asking for another size makes the shell resample (soft).</summary>
    private static readonly int[] StandardSizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    /// <summary>
    /// Whole pixels for an icon of <paramref name="pixels"/>; a standard size when one is within a
    /// pixel and a half (drawn from the icon file as-is, sharpest), otherwise downscaled from the next one up.
    /// </summary>
    public static int SnapSize(double pixels)
    {
        var nearest = StandardSizes.MinBy(s => Math.Abs(s - pixels));
        return Math.Abs(nearest - pixels) <= 1.5 ? nearest : (int)Math.Round(pixels);
    }

    /// <summary>
    /// A file's or folder's shell image at <paramref name="size"/> pixels: a thumbnail for photos,
    /// videos and documents that have one, otherwise its icon. Not cached (files change).
    /// </summary>
    public static ImageSource? GetFileImage(string path, int size)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var obj) != 0
            || obj is not IShellItemImageFactory factory)
            return null;
        var hbitmap = IntPtr.Zero;
        try
        {
            var request = StandardSizes.FirstOrDefault(s => s >= size, 256);
            if (factory.GetImage(new NativeSize { cx = request, cy = request }, 0, out hbitmap) != 0) return null;
            return ToBitmapSource(hbitmap) is { } bitmap ? Resample(bitmap, size) : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    private static ImageSource? LoadIcon(string id, int size)
    {
        if (SHCreateItemFromParsingName(AppsFolderPath(id), IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var obj) != 0
            || obj is not IShellItemImageFactory factory)
            return null;

        var hbitmap = IntPtr.Zero;
        try
        {
            // Store apps (AUMID "package!app"): "icon only" crops their logo edge to edge (the Settings
            // gear loses its teeth), so take the regular padded logo, as Start shows it. Desktop apps:
            // icon only, at a standard size.
            const int SIIGBF_ICONONLY = 0x4;
            var packaged = id.Contains('!');
            var request = packaged ? size : StandardSizes.FirstOrDefault(s => s >= size, 256);
            if (factory.GetImage(new NativeSize { cx = request, cy = request }, packaged ? 0 : SIIGBF_ICONONLY, out hbitmap) != 0)
                return null;
            if (ToBitmapSource(hbitmap) is not { } bitmap) return null;
            // The padded logo draws noticeably smaller than desktop icons; trim its empty margin and
            // leave the small inset desktop icons have, so the row looks even.
            return packaged ? Resample(TrimTransparent(bitmap), size, inset: 0.06) : Resample(bitmap, size);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies the 32-bit icon bitmap with its alpha channel (Imaging.CreateBitmapSourceFromHBitmap
    /// drops alpha for these, giving black corners).
    /// </summary>
    private static BitmapSource? ToBitmapSource(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        int width = bm.bmWidth, height = bm.bmHeight;
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)height, pixels, ref info, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        // Photo thumbnails come back as 32-bit with every alpha byte 0 (no alpha at all): opaque.
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length && !anyAlpha; i += 4) anyAlpha = pixels[i] != 0;
        if (!anyAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        // Straight (not premultiplied) alpha: edge pixels have color brighter than their alpha.
        // Reading it as premultiplied gave noisy, broken-looking edges.
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// An app logo trimmed of its transparent margin and fitted (with a small inset) into a
    /// <paramref name="size"/>-pixel square. Store app logos carry wide empty margins (the Settings gear
    /// drew tiny next to other icons).
    /// </summary>
    public static BitmapSource FitLogo(BitmapSource logo, int size) => Resample(TrimTransparent(logo), size, inset: 0.06);

    /// <summary>The bitmap cropped to its visible (non-transparent) pixels.</summary>
    private static BitmapSource TrimTransparent(BitmapSource source)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] < 8) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
        if (right < left) return source; // fully transparent

        // Keep it square so the icon stays centered the way it was drawn.
        var side = Math.Max(right - left + 1, bottom - top + 1);
        var cx = (left + right + 1) / 2.0;
        var cy = (top + bottom + 1) / 2.0;
        var x0 = (int)Math.Clamp(Math.Round(cx - side / 2.0), 0, Math.Max(0, width - side));
        var y0 = (int)Math.Clamp(Math.Round(cy - side / 2.0), 0, Math.Max(0, height - side));
        var rect = new System.Windows.Int32Rect(x0, y0, Math.Min(side, width - x0), Math.Min(side, height - y0));
        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// High-quality fit into a <paramref name="size"/>-pixel square (centered, with an optional
    /// <paramref name="inset"/> as a fraction of the size), unless it already is one.
    /// </summary>
    private static BitmapSource Resample(BitmapSource source, int size, double inset = 0)
    {
        if (inset == 0 && source.PixelWidth == size && source.PixelHeight == size) return source;
        var box = size * (1 - 2 * inset);
        var scale = Math.Min(box / source.PixelWidth, box / source.PixelHeight);
        var (w, h) = (source.PixelWidth * scale, source.PixelHeight * scale);

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new System.Windows.Rect((size - w) / 2, (size - h) / 2, w, h));
        var result = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    // ---------------------------------------------------------------- interop

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [In] in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits,
        ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
