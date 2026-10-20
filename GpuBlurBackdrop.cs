// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Runtime.InteropServices;
using DIsland.Interop;
using Vortice.DirectComposition;

namespace DIsland;

/// <summary>
/// GPU Liquid Glass backdrop that needs no screen capture. For every window behind the island
/// (wallpaper and desktop included), DWM provides a live thumbnail as a DirectComposition visual
/// (the private <c>DwmpCreateSharedThumbnailVisual</c>, dwmapi ordinal 147). The thumbnails are
/// stacked in screen z-order, blurred with a DirectComposition Gaussian blur and clipped to the
/// island's rounded shape, all on the GPU, in a window directly beneath the island. The island's
/// own windows are simply never added, so the island stays visible in screenshots.
/// <para>
/// The API is private, so a probe in a child process verifies it on each Windows build first
/// (<see cref="ProbeExitCode"/>). A wrong signature would crash the child, never the island.
/// </para>
/// </summary>
internal sealed class GpuBlurBackdrop : IDisposable
{
    private const string ClassName = "DIslandGpuBlurBackdrop";
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint DWM_TNP_RECTDESTINATION = 0x1;
    private const uint DWM_TNP_RECTSOURCE = 0x2;
    private const uint DWM_TNP_VISIBLE = 0x8;
    private const int MaxWindows = 24;

    /// <summary>Flags for DwmpCreateSharedThumbnailVisual (2 per the reference gist). Debug: DISLAND_THUMB_FLAGS overrides.</summary>
    private static readonly uint ThumbnailFlags =
#if DEBUG
        uint.TryParse(Environment.GetEnvironmentVariable("DISLAND_THUMB_FLAGS"), out var debugFlags) ? debugFlags :
#endif
        2;

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new System.Text.StringBuilder(256);
        NativeMethods.GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugLog(string line)
    {
        if (Environment.GetEnvironmentVariable("DISLAND_BLUR_LOG") is { Length: > 0 } path)
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
    }

    private static WndProcDelegate? _wndProc; // must outlive every window of the class

    private readonly IntPtr _hwnd;
    private readonly IDCompositionDesktopDevice _device;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual2 _root;
    private readonly IDCompositionVisual2 _blurContainer;
    private readonly IDCompositionVisual3? _root3;
    private readonly IDCompositionRectangleClip _clip;
    private readonly IDCompositionGaussianBlurEffect _blur;
    private readonly CreateSharedThumbnailVisual _createThumbnail;
    private readonly List<Layer> _layers = new();
    private readonly HashSet<IntPtr> _excluded = new();
    private NativeMethods.RECT _bounds;
    private bool _visible = true;
    private (float X, float Y, float W, float H, float R, float Blur, float Opacity) _shape;

    private sealed record Layer(IntPtr Hwnd, IntPtr Thumbnail, IDCompositionVisual2 Visual)
    {
        public NativeMethods.RECT Rect { get; set; }
    }

    public GpuBlurBackdrop()
    {
        _createThumbnail = LoadPrivateApi();

        RegisterWindowClass();
        _hwnd = CreateWindowEx(
            (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)
            | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT | WS_EX_NOREDIRECTIONBITMAP,
            ClassName, "D-Island Glass", NativeMethods.WS_POPUP, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the glass window.");
        _excluded.Add(_hwnd);

        // No rendering device: nothing here draws into surfaces (only DWM thumbnails plus an effect),
        // and skipping Direct3D avoids a whole extra graphics-driver instance in the process.
        var iid = typeof(IDCompositionDesktopDevice).GUID;
        Marshal.ThrowExceptionForHR(DCompositionCreateDevice3(IntPtr.Zero, ref iid, out var devicePtr));
        _device = new IDCompositionDesktopDevice(devicePtr);

        _device.CreateTargetForHwnd(_hwnd, true, out _target).CheckError();
        _root = _device.CreateVisual();
        _blurContainer = _device.CreateVisual();
        _clip = _device.CreateRectangleClip();
        using (var device3 = _device.QueryInterface<IDCompositionDevice3>())
            _blur = device3.CreateGaussianBlurEffect();

        _blurContainer.SetEffect(_blur);
        _root.AddVisual(_blurContainer, false, null);
        _root.SetClip(_clip);
        _root3 = _root.QueryInterfaceOrNull<IDCompositionVisual3>();
        _target.SetRoot(_root);
        _device.Commit();

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Windows that must never appear in the blur (the island itself).</summary>
    public void Exclude(IntPtr hwnd) => _excluded.Add(hwnd);

    /// <summary>Matches the island window's screen rectangle.</summary>
    public void SetBounds(NativeMethods.RECT r)
    {
        if (r.Equals(_bounds)) return;
        _bounds = r;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOOWNERZORDER);
        // Every thumbnail's source/destination rectangle depends on the bounds.
        ClearLayers();
        SyncWindows();
    }

    public void SetVisible(bool visible)
    {
        if (visible == _visible) return;
        _visible = visible;
        NativeMethods.ShowWindow(_hwnd, visible ? NativeMethods.SW_SHOWNOACTIVATE : 0 /* SW_HIDE */);
        if (visible) SyncWindows();
    }

    /// <summary>
    /// Mirrors the windows behind the island: creates thumbnails for new ones, moves changed ones,
    /// rebuilds when the stacking order changes. Called on window events, never per frame.
    /// </summary>
    public void SyncWindows()
    {
        if (!_visible || _bounds.Right <= _bounds.Left) return;

        // EnumWindows is top-to-bottom; stack bottom-to-top.
        var behind = new List<(IntPtr Hwnd, NativeMethods.RECT Rect)>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (_excluded.Contains(hwnd) || !NativeMethods.IsShownOnScreen(hwnd)) return true;
#if DEBUG
            // Debug aid: DISLAND_BLUR_ONLY=<window class> mirrors just that class (plus the desktop).
            if (Environment.GetEnvironmentVariable("DISLAND_BLUR_ONLY") is { Length: > 0 } only
                && ClassOf(hwnd) is var cls && cls != only && cls != "Progman" && cls != "WorkerW")
                return true;
#endif
            if (!NativeMethods.GetWindowRect(hwnd, out var rect) || !NativeMethods.Intersects(rect, _bounds)) return true;
            behind.Add((hwnd, rect));
            return behind.Count < MaxWindows;
        }, IntPtr.Zero);
        behind.Reverse();

        var sameOrder = behind.Count == _layers.Count && behind.Select(b => b.Hwnd).SequenceEqual(_layers.Select(l => l.Hwnd));
        if (!sameOrder)
        {
            ClearLayers();
            foreach (var (hwnd, rect) in behind) AddLayer(hwnd, rect);
        }
        else
        {
            // DwmUpdateThumbnailProperties doesn't affect shared thumbnail visuals, so a moved or
            // resized window gets a fresh thumbnail at the same stacking position.
            for (var i = 0; i < behind.Count; i++)
            {
                if (!behind[i].Rect.Equals(_layers[i].Rect)) ReplaceLayer(i, behind[i].Rect);
            }
        }
        _device.Commit();
    }

    private void ReplaceLayer(int index, NativeMethods.RECT rect)
    {
        var old = _layers[index];
        if (CreateThumbnail(old.Hwnd, rect, out var visualPtr, out var thumbnail) != 0 || visualPtr == IntPtr.Zero)
            return;
        var visual = new IDCompositionVisual2(visualPtr);

        // Above the layer below it, or (for the bottom layer) behind the layer above it.
        if (index > 0) _blurContainer.AddVisual(visual, true, _layers[index - 1].Visual);
        else if (_layers.Count > 1) _blurContainer.AddVisual(visual, false, _layers[1].Visual);
        else _blurContainer.AddVisual(visual, false, null);

        _blurContainer.RemoveVisual(old.Visual);
        old.Visual.Dispose();
        DwmUnregisterThumbnail(old.Thumbnail);
        _layers[index] = new Layer(old.Hwnd, thumbnail, visual) { Rect = rect };
    }

    /// <summary>Shape, blur radius and opacity, in backdrop-window pixels. Called every animation frame.</summary>
    public void SetShape(float x, float y, float width, float height, float radius, float blurRadius, float opacity)
    {
        var shape = (x, y, width, height, radius, blurRadius, opacity);
        if (shape == _shape) return; // skip redundant commits while idle
        _shape = shape;

        _clip.SetLeft(x);
        _clip.SetTop(y);
        _clip.SetRight(x + width);
        _clip.SetBottom(y + height);
        _clip.SetTopLeftRadiusX(radius);
        _clip.SetTopLeftRadiusY(radius);
        _clip.SetTopRightRadiusX(radius);
        _clip.SetTopRightRadiusY(radius);
        _clip.SetBottomLeftRadiusX(radius);
        _clip.SetBottomLeftRadiusY(radius);
        _clip.SetBottomRightRadiusX(radius);
        _clip.SetBottomRightRadiusY(radius);
        // Standard deviation ≈ radius / 3 matches WPF's BlurEffect radius.
        _blur.SetStandardDeviation(Math.Max(0.01f, blurRadius / 3f));
        _root3?.SetOpacity(Math.Clamp(opacity, 0, 1));
        _device.Commit();
    }

    public void Dispose()
    {
        ClearLayers();
        _blur.Dispose();
        _clip.Dispose();
        _root3?.Dispose();
        _blurContainer.Dispose();
        _root.Dispose();
        _target.Dispose();
        _device.Dispose();
        DestroyWindow(_hwnd);
    }

    /// <summary>
    /// Only the part of the window that overlaps the island: the API rejects destinations outside
    /// the host window (E_INVALIDARG), and drawing less is cheaper anyway. Source coordinates are
    /// relative to the source window's top-left; destination to the backdrop window's.
    /// </summary>
    private ThumbnailProperties Properties(NativeMethods.RECT windowRect)
    {
        var left = Math.Max(windowRect.Left, _bounds.Left);
        var top = Math.Max(windowRect.Top, _bounds.Top);
        var right = Math.Min(windowRect.Right, _bounds.Right);
        var bottom = Math.Min(windowRect.Bottom, _bounds.Bottom);
        return new ThumbnailProperties
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE,
            rcSource = new NativeMethods.RECT
            {
                Left = left - windowRect.Left,
                Top = top - windowRect.Top,
                Right = right - windowRect.Left,
                Bottom = bottom - windowRect.Top,
            },
            rcDestination = new NativeMethods.RECT
            {
                Left = left - _bounds.Left,
                Top = top - _bounds.Top,
                Right = right - _bounds.Left,
                Bottom = bottom - _bounds.Top,
            },
            fVisible = 1,
        };
    }

    /// <summary>
    /// A thumbnail visual of <paramref name="hwnd"/>; 0 on success. Note: WinUI 3 windows (Apple Music,
    /// new Notepad) come out empty whatever the source (top-level or its DesktopChildSiteBridge child)
    /// or flags; MainWindow switches Auto to the capture engine while one is on top behind the island.
    /// </summary>
    private int CreateThumbnail(IntPtr hwnd, NativeMethods.RECT rect, out IntPtr visualPtr, out IntPtr thumbnail)
    {
        var props = Properties(rect);
        var hr = _createThumbnail(_hwnd, hwnd, ThumbnailFlags, ref props, _device.NativePointer, out visualPtr, out thumbnail);
        DebugLog($"thumbnail {hwnd:X} [{ClassOf(hwnd)}] rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) hr=0x{hr:X8}");
        return hr;
    }

    private void AddLayer(IntPtr hwnd, NativeMethods.RECT rect)
    {
        var hr = CreateThumbnail(hwnd, rect, out var visualPtr, out var thumbnail);
        if (hr != 0 || visualPtr == IntPtr.Zero)
            return; // e.g. a window that refuses thumbnails; just leave it out
        var visual = new IDCompositionVisual2(visualPtr);
        // With no reference visual, insertAbove=false places it in front of all siblings (true = behind all).
        _blurContainer.AddVisual(visual, false, null);
        _layers.Add(new Layer(hwnd, thumbnail, visual) { Rect = rect });
    }

    private void ClearLayers()
    {
        foreach (var layer in _layers)
        {
            _blurContainer.RemoveVisual(layer.Visual);
            layer.Visual.Dispose();
            DwmUnregisterThumbnail(layer.Thumbnail);
        }
        _layers.Clear();
    }

    // ---------------------------------------------------------------- probe

    /// <summary>
    /// Runs in a throwaway child process: builds the backdrop over a real screen area with real
    /// window thumbnails. 0 = works.
    /// </summary>
    public static int ProbeExitCode()
    {
        try
        {
            using var backdrop = new GpuBlurBackdrop();
            backdrop.SetBounds(new NativeMethods.RECT { Left = 0, Top = 0, Right = 200, Bottom = 100 });
            backdrop.SetShape(0, 0, 200, 50, 25, 12, 1);
            backdrop.SetVisible(false);
            return backdrop._layers.Count > 0 ? 0 : 2; // the desktop alone must produce a layer
        }
        catch (Exception)
        {
            return 1;
        }
    }

    // ---------------------------------------------------------------- interop

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSharedThumbnailVisual(IntPtr hwndDestination, IntPtr hwndSource, uint flags,
        ref ThumbnailProperties properties, IntPtr dcompDevice, out IntPtr visual, out IntPtr thumbnail);

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint dwFlags;
        public NativeMethods.RECT rcDestination;
        public NativeMethods.RECT rcSource;
        public byte opacity;
        public int fVisible;
        public int fSourceClientAreaOnly;
    }

    /// <summary>Exported by ordinal only (names are stripped from dwmapi.dll).</summary>
    private static CreateSharedThumbnailVisual LoadPrivateApi()
    {
        if (Environment.OSVersion.Version.Build < 17763)
            throw new PlatformNotSupportedException("Needs Windows 10 1809 or later.");
        var create = GetProcAddress(LoadLibrary("dwmapi.dll"), new IntPtr(147));
        if (create == IntPtr.Zero) throw new PlatformNotSupportedException("Shared thumbnail visual API not found.");
        return Marshal.GetDelegateForFunctionPointer<CreateSharedThumbnailVisual>(create);
    }

    private static void RegisterWindowClass()
    {
        if (_wndProc is not null) return;
        _wndProc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) => msg switch
    {
        NativeMethods.WM_NCHITTEST => new IntPtr(NativeMethods.HTTRANSPARENT),
        NativeMethods.WM_MOUSEACTIVATE => new IntPtr(NativeMethods.MA_NOACTIVATE),
        _ => DefWindowProc(hwnd, msg, wParam, lParam),
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice3(IntPtr renderingDevice, ref Guid iid, out IntPtr device);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
