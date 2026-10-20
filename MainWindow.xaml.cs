// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DIsland.Interop;
using DIsland.Services;
using Microsoft.Win32;

namespace DIsland;

public partial class MainWindow : Window
{
    // Design-space width of the root; the window is this times the Size setting. The root's height
    // starts at 300 (XAML) and grows if the expanded content needs more (UpdateWindowHeight).
    private const double BaseWidth = 460;

    private const double IslandTopMargin = 8;
    private const double ExpandedWidth = 420;
    private const double IslandExpandedRadius = 32;
    private const double NotchCompactRadius = 14;
    private const double NotchExpandedRadius = 26;
    private const double NotchShoulder = 8;
    private const double BaseStiffness = 420;
    private const int LeavePollMs = 50;

    private static readonly Color DefaultMediaAccent = Color.FromRgb(0x30, 0xD1, 0x58);

    private readonly AppSettings _settings;
    private readonly StateController _state = new();
    private readonly TimerService _timer = new();
    private readonly MediaService _media = new();
    private readonly VolumeService _volume = new();
    private readonly BrightnessService _brightness = new();
    private readonly VolumeKeyHook _volumeKeys = new();
    private readonly BatteryService _battery = new();
    private readonly WallpaperEngineService _wallpaper = new();
    private readonly DeviceService _devices = new();
    private readonly KeyboardWatcher _keyboard = new();
    private readonly FocusService _focus = new();
    private readonly ClipboardWatcher _clipboard = new();
    private readonly PrivacyService _privacy = new();
    private readonly StopwatchService _stopwatch = new();
    private readonly AlarmService _alarms;
    private readonly StatusService _status = new();
    private System.Media.SoundPlayer? _alarmSound;
    private readonly ShelfService _shelf = new();
    private readonly NotificationService _notifications = new();
    private readonly CalendarService _calendar;
    private readonly WeatherService _weather;
    private string? _weatherKey;
    private readonly Hotkey _toggleHotkey = new(1);
    private string? _toggleHotkeyText;
    private ClipboardEvent? _lastCopy;
    private readonly WindowEventWatcher _windows;
    private readonly BackgroundDetector _background;

    // Liquid Glass: the island excludes itself from capture, then blurs a live copy of the screen behind it.
    private const double MaxGlassBlur = 40;
    private readonly ScreenCapture _capture = new();
    private readonly DispatcherTimer _glassTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private WriteableBitmap? _glassBitmap;
    private bool _excludedFromCapture;
    private bool _captureUnsupported;

    // Short grace period so a brief pause between tracks doesn't flap the wallpaper's audio.
    private readonly DispatcherTimer _wallpaperUnmuteTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _wallpaperMutedByUs;

    private readonly Spring _width = new(150);
    private readonly Spring _height = new(34);
    private readonly Spring _radius = new(17);
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private double _lastFrameTime;
    private double _lastAppliedFrame;
    private bool _animating;

    private readonly FrameworkElement[] _views;
    private FrameworkElement? _currentView;

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _leavePoll = new() { Interval = TimeSpan.FromMilliseconds(LeavePollMs) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _msOutside;
    private bool _hoverSuppressed;
    private bool _equalizerRunning;
    private bool _osdDimmed;
    private Artwork? _shownArt;
    private WallpaperTheme? _shownWallpaperTheme;

    private readonly SolidColorBrush _pillBrush = new(Colors.Black);
    private readonly DropShadowEffect _shadow = new() { BlurRadius = 22, ShadowDepth = 3, Direction = 270, Color = Colors.Black };
    private Color? _accent;

    public MainWindow(AppSettings settings)
    {
        StartupMark("window constructor");
        _settings = settings;
        _weather = new WeatherService(settings);
        _calendar = new CalendarService(settings);
        _alarms = new AlarmService(settings);
        InitializeComponent();
        _windows = new WindowEventWatcher(TimeSpan.FromMilliseconds(30));
        _background = new BackgroundDetector(GetIslandScreenRect, _windows);
        _windows.Changed += () =>
        {
            KeepOnTop(); // cheap check; only acts if something now covers the island
            UpdateWinUiBehind();
            if (_gpuActive) _gpuBlur?.SyncWindows();
        };

        _views = new FrameworkElement[]
        {
            ClockView, MediaView, TimerView, StopwatchView, OsdView, BatteryView, TimerDoneView, InfoView, NotificationView,
            ExpandedView,
        };
        foreach (var view in _views)
        {
            view.Opacity = 0;
            view.IsHitTestVisible = false;
            view.RenderTransform = new ScaleTransform(1, 1);
            view.RenderTransformOrigin = view == ExpandedView ? new Point(0.5, 0) : new Point(0.5, 0.5);
        }
        PillShape.Fill = _pillBrush;

        _state.Changed += Render;
        _settings.Changed += ApplySettings;

        _timer.Tick += OnTimerTick;
        _timer.StateChanged += OnTimerStateChanged;
        _timer.Finished += OnTimerFinished;
        _media.Changed += OnMediaChanged;
        _media.TimelineChanged += () =>
        {
            if (!_state.IsExpanded) return;
            var showProgress = _media.Current is { Duration.Ticks: > 0 };
            if (showProgress != (ProgressRow.Visibility == Visibility.Visible)) Render(); // row appears/disappears
            else UpdateProgress();
        };
        _volume.Changed += ShowVolume;
        _brightness.Changed += ShowBrightness;
        _volumeKeys.VolumeKey += OnVolumeKey;
        _battery.Alert += OnBatteryAlert;
        _devices.Changed += OnDeviceChanged;
        _keyboard.LockToggled += OnLockToggled;
        _keyboard.PasteShortcut += OnPasteShortcut;
        _focus.Changed += OnFocusChanged;
        _clipboard.Copied += OnCopied;
        _privacy.Changed += OnPrivacyChanged;
        _toggleHotkey.Pressed += () => SetIslandHidden(!IslandHidden);
        _weather.Changed += () =>
        {
            UpdateWeather();
            UpdateClock(); // the idle pill may show the temperature
        };
        _wallpaper.Changed += OnWallpaperChanged;
        _background.Changed += ApplyTheme;
        _wallpaperUnmuteTimer.Tick += (_, _) =>
        {
            _wallpaperUnmuteTimer.Stop();
            ReleaseWallpaperMute();
        };

        _clockTimer.Tick += (_, _) =>
        {
            UpdateClock();
            // Relative times ("in 5 min", "3m") on an open page.
            if (!_state.IsExpanded || _animating) return;
            if (_page == ExpandedPage.Calendar) BuildCalendar();
            else if (_page == ExpandedPage.Notifications) BuildNotificationList();
        };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            SetExpanded(true);
        };
        _leavePoll.Tick += OnLeavePoll;
        _progressTimer.Tick += (_, _) => UpdateProgress();

        Root.MouseEnter += OnRootMouseEnter;
        Root.MouseLeave += OnRootMouseLeave;
        Root.MouseLeftButtonUp += OnRootMouseLeftButtonUp;
        Root.PreviewMouseWheel += OnRootPreviewMouseWheel;
        WheelScrolling.Attach(AppsPage, pixelsPerNotch: 36);
        WheelScrolling.Attach(ShelfScroller, pixelsPerNotch: 36);
        WheelScrolling.Attach(NotificationsScroller, pixelsPerNotch: 36);
        WheelScrolling.Attach(CalendarScroller, pixelsPerNotch: 36);
        InitShelf();
        InitEnergySaver();
        _stopwatch.Tick += OnStopwatchTick;
        _stopwatch.StateChanged += OnStopwatchStateChanged;
        _alarms.Ring += OnAlarmRing;
        _status.Changed += OnStatusChanged;
        _state.Changed += UpdateSecondActivity;
        _state.Changed += () =>
        {
            // The alarm rings while its alert is up: clicked away or timed out stops the sound.
            if (_alarmSound is not null && _state.Transient != IslandView.TimerDone) StopAlarmSound();
        };
        _notifications.Arrived += OnNotificationArrived;
        _notifications.Changed += BuildNotificationList;
        _calendar.Changed += BuildCalendar;
        _calendar.Reminder += OnCalendarReminder;
        Root.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenSettings();
        };

        _glassTimer.Tick += (_, _) => CaptureGlass();
        InitMotionArtwork();
        LocationChanged += (_, _) => SyncGpuBounds();
        SizeChanged += (_, _) => SyncGpuBounds();
        DpiChanged += (_, _) => SyncGpuBounds();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        ApplySettings();
    }

    /// <summary>The gear button or a right-click on the island asked for the settings window.</summary>
    public event Action? SettingsRequested;

    /// <summary>Hidden with the shortcut or the tray menu: nothing shows, alerts included.</summary>
    public bool IslandHidden { get; private set; }

    public void SetIslandHidden(bool hidden)
    {
        if (hidden == IslandHidden) return;
        IslandHidden = hidden;
        if (hidden)
        {
            _hoverTimer.Stop();
            SetExpanded(false);
            _state.DismissTransient();
            _gpuBlur?.SetVisible(false);
            Hide();
            UpdateEqualizer(); // stops the bars' meter loop while nothing can be seen
        }
        else
        {
            Show();
            _gpuActive = false; // make UpdateGlass show the blur window again
            UpdateGlass();
            KeepOnTop(force: true);
            Render(); // whatever changed while hidden
            UpdateEqualizer();
        }
    }

    /// <summary>(Re)registers the hide/show shortcut when its setting changes.</summary>
    private void UpdateToggleHotkey()
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return; // registered once the window exists
        if (_settings.ToggleHotkey == _toggleHotkeyText) return;
        _toggleHotkeyText = _settings.ToggleHotkey;
        _settings.HotkeyProblem = _toggleHotkey.Set(_settings.ToggleHotkey);
    }

    private bool IsNotch => _settings.Mode == IslandMode.Notch;
    private bool IsGlass => _settings.Material == IslandMaterial.LiquidGlass;
    private double PillTop => IsNotch ? 0 : IslandTopMargin;
    private double SpeedFactor => Math.Clamp(_settings.AnimationSpeed, 0.25, 4);
    private double Scale => Math.Clamp(_settings.IslandScale, 0.5, 2);
    private double Roundness => Math.Clamp(_settings.CornerRoundness, 0, 1);

    // ---------------------------------------------------------------- settings

    private void ApplySettings()
    {
        // Scaling stiffness by speed² and damping by speed keeps the same curve shape, just faster.
        var stiffness = BaseStiffness * SpeedFactor * SpeedFactor;
        var dampingRatio = 1.0 - 0.55 * Math.Clamp(_settings.Bounciness, 0, 1);
        var damping = 2 * dampingRatio * Math.Sqrt(stiffness);
        foreach (var spring in new[] { _width, _height, _radius })
        {
            spring.Stiffness = stiffness;
            spring.Damping = damping;
        }

        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _settings.HoverDelayMs));
        _volumeKeys.Enabled = _settings.HideVolumeFlyout;

        Root.LayoutTransform = Math.Abs(Scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(Scale, Scale);
        Width = BaseWidth * Scale;
        UpdateWindowHeight();
        FontFamily = new FontFamily($"{_settings.FontFamily}, Segoe UI");

        foreach (var view in _views) view.Margin = new Thickness(0, PillTop, 0, 0);

        if (_settings.AdaptiveBackground) { if (IsLoaded) _background.Start(); }
        else _background.Stop();

        if (!_settings.DeviceAlerts) _devices.Stop();
        else if (IsLoaded) _devices.Start();
        if (!_settings.FocusAlerts) _focus.Stop();
        else if (IsLoaded) _focus.Start();
        if (!_settings.PrivacyIndicator) _privacy.Stop();
        else if (IsLoaded) _privacy.Start();
        if (IsLoaded) UpdatePrivacyIndicators();
        UpdateWeatherService();
        UpdateNotificationAndCalendarServices();
        _pageNavKey = null; // navigation style may have changed
        if (IsLoaded) BuildPageNav();
        UpdateToggleHotkey();
        _keyboard.LockKeysEnabled = _settings.LockKeyAlerts;
        _keyboard.PasteEnabled = _settings.PasteAlerts;
        _keyboard.Update();
        _clipboard.Enabled = _settings.CopyAlerts;
        _clipboard.ReadText = _settings.ClipboardPreview;
        _clipboard.Update();

        BuildQuickApps();
        UpdateClock();
        UpdateEqualizer(); // real waveform setting
        _alarms.Reschedule();
        if (_settings.StatusAlerts) { if (IsLoaded) _status.Start(); }
        else _status.Dispose();
        if (IsLoaded) UpdateWinUiBehind(); // material / engine may have changed
        ApplyTheme();
        UpdateWallpaperMute();
        UpdateMotionArtwork(_media.Current);

        Reposition();
        if (IsLoaded) Render();
        ApplyGeometry();
    }

    // ---------------------------------------------------------------- window setup

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetExStyle(hwnd);
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        style &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetExStyle(hwnd, style);
        // Hardware rendering measured ~40% cheaper in CPU than software for this window's animations.
        if (HwndSource.FromHwnd(hwnd) is { } source)
        {
            source.AddHook(WndProc);
            _keyboard.Attach(source);
            _clipboard.Attach(source);
            _toggleHotkey.Attach(source);
            UpdateToggleHotkey();
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupMark("loaded");
        KeepOnTop(force: true);
        UpdateWinUiBehind();
        Reposition();
        ApplyTheme();
        UpdateClock();
        _clockTimer.Start();
        SetEqualizerRunning(false);
        Render();

        // What the island shows straight away (volume / battery alerts, wallpaper theme, glass).
        _volume.Start();
        _battery.Start();
        _wallpaper.Start();
        _alarms.Start();
        if (_settings.AdaptiveBackground) _background.Start();
        BuildShelf();
        BuildNotificationList();
        BuildCalendar();
        UpdatePageVisibility();
        StartupMark("core services + first render");
        _ = StartSecondaryServicesAsync();
#if DEBUG
        if (Environment.GetEnvironmentVariable("DISLAND_SNAPSHOT") is { Length: > 0 } snapshotPath)
            _ = SaveSnapshotAsync(snapshotPath);
        if (Environment.GetEnvironmentVariable("DISLAND_MOTION_TEST") is { Length: > 0 } motionTest)
        {
            // Debug aid: "artist|album|output.txt" → look up motion artwork and report the result.
            var parts = motionTest.Split('|');
            _ = Task.Run(async () =>
            {
                string result;
                try
                {
                    using var motion = new MotionArtworkService();
                    var sw = Stopwatch.StartNew();
                    var file = await motion.GetAsync(parts[0], parts[1], "", CancellationToken.None);
                    result = file is null ? $"none ({sw.ElapsedMilliseconds} ms)"
                        : $"{file} {new System.IO.FileInfo(file).Length / 1024} KB ({sw.ElapsedMilliseconds} ms)";
                }
                catch (Exception ex)
                {
                    result = ex.ToString();
                }
                System.IO.File.WriteAllText(parts[2], result);
            });
        }
        if (Environment.GetEnvironmentVariable("DISLAND_MOTION_PLAY") is { Length: > 0 } motionFile)
        {
            // Debug aid: play a cached motion-art file in the expanded island's banner.
            _leavePoll.Tick += (_, _) => _msOutside = 0;
            _debugForceBanner = true;
            SetExpanded(true);
            MotionBanner.Source = new Uri(motionFile);
            MotionBanner.Play();
        }
        if (Environment.GetEnvironmentVariable("DISLAND_ACTIVITIES") is { Length: > 0 } activities)
        {
            // Debug aid: "timer,stopwatch" starts those activities (to see the second-activity bubble).
            if (activities.Contains("timer")) _timer.Start(TimeSpan.FromMinutes(5));
            if (activities.Contains("stopwatch")) _stopwatch.Start();
        }
        if (Environment.GetEnvironmentVariable("DISLAND_ICS") is { Length: > 0 } ics)
        {
            // Debug aid: use this iCal link for this run only (in memory, never saved by this hook).
            _settings.CalendarIcsUrls = new List<string> { ics };
            UpdateNotificationAndCalendarServices();
        }
        if (Environment.GetEnvironmentVariable("DISLAND_NAV") is { Length: > 0 } nav
            && Enum.TryParse<PageNavigation>(nav, true, out var navigation))
        {
            // Debug aid: navigation style for this run only (not saved).
            _settings.PageNavigation = navigation;
            _pageNavKey = null;
        }
        if (Environment.GetEnvironmentVariable("DISLAND_EXPAND") is { Length: > 0 } expand)
        {
            // Debug aid: open the island and keep it open (for snapshots of the expanded view);
            // a page name ("Apps") opens that page.
            _leavePoll.Tick += (_, _) => _msOutside = 0;
            SetExpanded(true);
            if (Enum.TryParse<ExpandedPage>(expand, true, out var page)) ShowPage(page, animate: false);
        }
        if (Environment.GetEnvironmentVariable("DISLAND_DEVICE") is { Length: > 0 } deviceName)
        {
            // Debug aid: "name" or "-name" fakes a device connecting / disconnecting after 1 s.
            _ = Task.Delay(1000).ContinueWith(_ => OnDeviceChanged(new DeviceEvent(deviceName.TrimStart('-'),
                    DeviceKind.Headphones, !deviceName.StartsWith('-'))),
                TaskScheduler.FromCurrentSynchronizationContext());
        }
        if (Environment.GetEnvironmentVariable("DISLAND_BENCH") is { Length: > 0 })
        {
            // Debug aid: expand/collapse continuously so animation cost can be measured.
            var bench = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            bench.Tick += (_, _) => SetExpanded(!_state.IsExpanded);
            _leavePoll.Tick += (_, _) => _msOutside = 0; // don't auto-collapse during the benchmark
            bench.Start();
        }
        if (Environment.GetEnvironmentVariable("DISLAND_STATS") is { Length: > 0 } statsPath)
        {
            // Debug aid: managed heap numbers after startup settles.
            _ = Task.Delay(10000).ContinueWith(_ =>
            {
                var before = GC.GetTotalMemory(false);
                var after = GC.GetTotalMemory(true);
                var info = GC.GetGCMemoryInfo();
                System.IO.File.WriteAllText(statsPath,
                    $"managed before GC {before / 1048576.0:N1} MB, after full GC {after / 1048576.0:N1} MB, " +
                    $"GC committed {info.TotalCommittedBytes / 1048576.0:N1} MB, gen0/1/2 collections " +
                    $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
            }, TaskScheduler.Default);
        }
#endif
        StartupMark("before media");
        await _media.StartAsync();
        StartupMark("media started");
        InitUpdates();
    }

    // ---------------------------------------------------------------- updates

    private void InitUpdates()
    {
        if (App.Updates is not { } updates) return;

        // "D-Island updated" once, on the first start after an update.
        var current = UpdateService.CurrentVersion;
        if (Version.TryParse(_settings.LastRunVersion, out var last) && last < current)
        {
            _ = Task.Delay(3000).ContinueWith(_ => ShowInfo(TransientPriority.Calendar, 300, "", "D-Island updated",
                    $"Version {current}", detailBrush: (Brush)FindResource("Green"), seconds: 5),
                TaskScheduler.FromCurrentSynchronizationContext());
        }
        if (_settings.LastRunVersion != current.ToString())
        {
            _settings.LastRunVersion = current.ToString();
            _settings.Save();
        }

        updates.ReadyToInstall += OnUpdateReady;
        updates.Available += info => ShowInfo(TransientPriority.Device, 320, "", "Update available",
            $"Version {info.Version}", detailBrush: (Brush)FindResource("Green"), seconds: 5);
        updates.Start();
    }

    /// <summary>
    /// A verified update is ready. Install at a quiet moment only: not while a timer, stopwatch or alarm is
    /// running, the island is open, or an alert is showing. The updater asks again every 10 minutes.
    /// </summary>
    private void OnUpdateReady(UpdateInfo info)
    {
        if (_timer.IsActive || _stopwatch.IsActive || _alarmSound is not null || _state.IsExpanded
            || _state.Transient is not null || _animating)
            return;
        ShowInfo(TransientPriority.TimerDone, 320, "", "Updating D-Island", $"to {info.Version}",
            detailBrush: (Brush)FindResource("Green"), seconds: 4);
        _ = Task.Delay(2500).ContinueWith(_ => App.Updates?.Install(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Debug: DISLAND_STARTUP_LOG=&lt;txt&gt; records wall time and this process's CPU time per startup phase.</summary>
    [Conditional("DEBUG")]
    private static void StartupMark(string phase)
    {
        if (Environment.GetEnvironmentVariable("DISLAND_STARTUP_LOG") is not { Length: > 0 } path) return;
        using var self = Process.GetCurrentProcess();
        System.IO.File.AppendAllText(path,
            $"{(DateTime.Now - self.StartTime).TotalMilliseconds,6:0} ms wall  {self.TotalProcessorTime.TotalMilliseconds,6:0} ms CPU  {phase}\n");
    }

    /// <summary>
    /// Everything that isn't needed for the first frame starts a moment later and one after another,
    /// so the island appears quickly instead of competing with a burst of device enumeration, COM
    /// setup, network requests and WinRT activation at startup.
    /// </summary>
    private async Task StartSecondaryServicesAsync()
    {
        await Task.Delay(1200);
        if (!IsLoaded) return;
        async Task Next(Action start, string name = "")
        {
#if DEBUG
            // Debug aid: DISLAND_SKIP=brightness,devices,… leaves services out (to measure each one's startup cost).
            if (name.Length > 0 && (Environment.GetEnvironmentVariable("DISLAND_SKIP") ?? "").Contains(name)) return;
#endif
            start();
            await Dispatcher.Yield(DispatcherPriority.Background); // let input and rendering run in between
        }
        StartupMark("secondary services: begin");
        await Next(() =>
        {
            if (_settings.PrivacyIndicator) _privacy.Start();
            UpdatePrivacyIndicators();
        }, "privacy");
        StartupMark("privacy");
        await Next(() => { if (_settings.FocusAlerts) _focus.Start(); }, "focus");
        StartupMark("focus");
        await Next(() => { if (_settings.StatusAlerts) _status.Start(); }, "status");
        StartupMark("status");
        await Next(() => { if (_settings.DeviceAlerts) _devices.Start(); }, "devices");
        StartupMark("devices");
        await Next(UpdateNotificationAndCalendarServices, "notifications");
        StartupMark("notifications + calendar");
        await Next(UpdateWeatherService, "weather");
        StartupMark("weather");
        await Next(() => _ = Task.Run(_brightness.Start), "brightness"); // WMI, on a worker thread
        await Task.Delay(3000);
        StartupMark("+3 s (async work settled)");
    }

#if DEBUG
    private bool _debugForceBanner;

    /// <summary>
    /// Debug aid: renders the island's own visual tree to a PNG. Liquid Glass excludes the window from
    /// screen capture, so ordinary screenshots can't show it.
    /// </summary>
    private async Task SaveSnapshotAsync(string path)
    {
        var delay = int.TryParse(Environment.GetEnvironmentVariable("DISLAND_SNAPSHOT_DELAY"), out var ms) ? ms : 3000;
        await Task.Delay(delay);
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)(Width * dpi.DpiScaleX), (int)(Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }
#endif

    private void OnClosed(object? sender, EventArgs e)
    {
        _settings.Changed -= ApplySettings;
        _wallpaperUnmuteTimer.Stop();
        ReleaseWallpaperMute(); // don't leave the wallpaper silenced after exit
        _wallpaper.Dispose();
        _background.Dispose();
        _volumeKeys.Dispose();
        _battery.Dispose();
        _devices.Dispose();
        _keyboard.Dispose();
        _focus.Dispose();
        _clipboard.Dispose();
        _privacy.Dispose();
        _weather.Dispose();
        _alarms.Dispose();
        _status.Dispose();
        _meterTimer.Stop();
        _meter.Dispose();
        _alarmSound?.Stop();
        _notifications.Dispose();
        _calendar.Dispose();
        _toggleHotkey.Dispose();
        _volume.Dispose();
        _brightness.Dispose();
        _media.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _clockTimer.Stop();
        _leavePoll.Stop();
        _progressTimer.Stop();
        if (_animating) CompositionTarget.Rendering -= OnRendering;
        _glassTimer.Stop();
        _capture.Dispose();
        _gpuBlur?.Dispose();
        _windows.Dispose();
        _motionCancel?.Cancel();
        MotionBanner.Close();
        _motionArt.Dispose();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(Reposition);

    private void Reposition()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = _settings.TopOffset;
    }

    // ---------------------------------------------------------------- rendering

    private void Render()
    {
        // Hidden (shortcut / tray): skip layout and animation entirely; catch up when shown.
        if (IslandHidden) return;
        FrameworkElement view;
        double width, height, radius;

        if (_state.IsExpanded)
        {
            UpdateExpandedSections();
            view = ExpandedView;
            width = ExpandedWidth;
            height = MeasureExpandedHeight();
            radius = IsNotch ? NotchExpandedRadius : IslandExpandedRadius;
        }
        else
        {
            view = ViewFor(_state.CurrentView);
            width = view.Width;
            height = view.Height;
            radius = IsNotch ? Math.Min(NotchCompactRadius, height / 2) : height / 2;
        }

        SwitchTo(view);
        AnimateTo(width, height, radius * Roundness);
    }

    private FrameworkElement ViewFor(IslandView view) => view switch
    {
        IslandView.Media => MediaView,
        IslandView.Timer => TimerView,
        IslandView.Stopwatch => StopwatchView,
        IslandView.Volume or IslandView.Brightness => OsdView,
        IslandView.Battery => BatteryView,
        IslandView.TimerDone => TimerDoneView,
        IslandView.Info => InfoView,
        IslandView.Notification => NotificationView,
        _ => ClockView,
    };

    private void UpdateExpandedSections()
    {
        var media = _media.Current;
        MediaSection.Visibility = media is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!AvailablePages().Contains(_page)) ShowPage(ExpandedPage.Home, animate: false);
        UpdatePageVisibility();
        ArtBanner.Visibility = _page == ExpandedPage.Home && _settings.ShowArtBanner && media is not null
                               && (media.Art is not null || _motionReady)
            ? Visibility.Visible
            : Visibility.Collapsed;
#if DEBUG
        if (_debugForceBanner) ArtBanner.Visibility = Visibility.Visible;
#endif
        ClockSection.Visibility = media is null ? Visibility.Visible : Visibility.Collapsed;
        ProgressRow.Visibility = media is { Duration.Ticks: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        TimerPresets.Visibility = _timer.IsActive ? Visibility.Collapsed : Visibility.Visible;
        TimerActivePanel.Visibility = _timer.IsActive ? Visibility.Visible : Visibility.Collapsed;
        StopwatchPanel.Visibility = _stopwatch.IsActive ? Visibility.Visible : Visibility.Collapsed;
        StopwatchStartButton.Visibility = _stopwatch.IsActive ? Visibility.Collapsed : Visibility.Visible;
        if (_stopwatch.IsActive) StopwatchTextLarge.Text = StopwatchService.Format(_stopwatch.Elapsed, tenths: true);
        UpdateProgress();
    }

    // ---------------------------------------------------------------- expanded pages

    private enum ExpandedPage
    {
        Home,
        Notifications,
        Calendar,
        Shelf,
        Weather,
        Apps,
        Wallpaper,
    }

    private ExpandedPage _page = ExpandedPage.Home;
    private FrameworkElement? _leavingPage;
    private string? _pageNavKey;
    private int _wheelDelta;
    private long _lastWheelEvent;
    private int _lastWheelDeltaValue;
    private double _wheelSpeed;
    private long _lastWheelFlip;
    private bool _wheelGestureScrolled;
    private bool _wheelGestureFlipped;
    private double _wheelPeak;
    private bool _wheelMomentumFaded;

    private List<ExpandedPage> AvailablePages()
    {
        var pages = new List<ExpandedPage> { ExpandedPage.Home };
        if (_settings.NotificationsEnabled) pages.Add(ExpandedPage.Notifications);
        if (_settings.CalendarEnabled) pages.Add(ExpandedPage.Calendar);
        if (_settings.ShelfEnabled) pages.Add(ExpandedPage.Shelf);
        if (_settings.WeatherEnabled) pages.Add(ExpandedPage.Weather);
        pages.Add(ExpandedPage.Apps);
        if (_settings.WallpaperEngineControls && _wallpaper.IsRunning) pages.Add(ExpandedPage.Wallpaper);
        return pages;
    }

    private FrameworkElement PageElement(ExpandedPage page) => page switch
    {
        ExpandedPage.Notifications => NotificationsPage,
        ExpandedPage.Calendar => CalendarPage,
        ExpandedPage.Shelf => ShelfPage,
        ExpandedPage.Weather => WeatherPage,
        ExpandedPage.Apps => AppsPage,
        ExpandedPage.Wallpaper => WallpaperPage,
        _ => HomePage,
    };

    private static string PageGlyph(ExpandedPage page) => page switch
    {
        ExpandedPage.Notifications => "",
        ExpandedPage.Calendar => "",
        ExpandedPage.Shelf => "",
        ExpandedPage.Weather => "",
        ExpandedPage.Apps => "",
        ExpandedPage.Wallpaper => "",
        _ => "",
    };

    private static string PageTitle(ExpandedPage page) => page switch
    {
        ExpandedPage.Notifications => "Notifications",
        ExpandedPage.Calendar => "Calendar",
        ExpandedPage.Shelf => "Shelf",
        ExpandedPage.Weather => "Weather",
        ExpandedPage.Apps => "Apps",
        ExpandedPage.Wallpaper => "Wallpaper",
        _ => "Home",
    };

    /// <summary>Only the current page (and one sliding out) is visible; the rest are collapsed.</summary>
    private void UpdatePageVisibility()
    {
        foreach (var page in Enum.GetValues<ExpandedPage>())
        {
            var element = PageElement(page);
            element.Visibility = page == _page || element == _leavingPage ? Visibility.Visible : Visibility.Collapsed;
        }
        BuildPageNav();
    }

    /// <summary>Tabs (labels) or dots, per the setting; rebuilt only when the pages or selection change.</summary>
    private void BuildPageNav()
    {
        var pages = AvailablePages();
        var dots = _settings.PageNavigation == PageNavigation.Dots;
        var key = $"{dots}|{_page}|{string.Join(',', pages)}";
        if (key == _pageNavKey) return;
        _pageNavKey = key;

        PageTabs.Children.Clear();
        // Dots sit centered; the nav row leaves room on the right for the gear, so balance it on the left.
        PageTabs.HorizontalAlignment = dots ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        PageTabs.Margin = dots ? new Thickness(34, 0, 0, 0) : new Thickness(-8, 0, 0, 0);
        var secondary = (Brush)FindResource("Secondary");
        foreach (var page in pages)
        {
            var selected = page == _page;
            // Tabs: an icon each, plus the label on the selected one (seven labels don't fit the width).
            var button = new Button
            {
                Style = (Style)FindResource("IconButton"),
                ToolTip = selected ? null : PageTitle(page),
                Padding = dots ? new Thickness(5, 6, 5, 6) : new Thickness(7, 3, selected ? 9 : 7, 3),
                Content = dots
                    ? new Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                    }
                    : new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            new TextBlock
                            {
                                Style = (Style)FindResource("Glyph"),
                                Text = PageGlyph(page),
                                FontSize = 13,
                                Foreground = selected ? Brushes.White : secondary,
                            },
                            new TextBlock
                            {
                                Text = PageTitle(page),
                                FontSize = 12,
                                FontWeight = FontWeights.SemiBold,
                                Margin = new Thickness(6, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center,
                                Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
                            },
                        },
                    },
            };
            if (!dots && selected) button.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            var target = page;
            button.Click += (_, _) => ShowPage(target, animate: true);
            PageTabs.Children.Add(button);
        }
    }

    /// <summary>Switches page, sliding in the direction of travel; the island resizes to the new page.</summary>
    private void ShowPage(ExpandedPage page, bool animate)
    {
        if (page == _page) return;
        var pages = AvailablePages();
        var direction = pages.IndexOf(page) >= pages.IndexOf(_page) ? 1 : -1;
        var from = PageElement(_page);
        var to = PageElement(page);
        _page = page;

        // A page still sliding out from an earlier switch is dropped at once.
        if (_leavingPage is { } stale && stale != to) FinishLeaving(stale);
        _leavingPage = null;

        to.Margin = new Thickness(0);
        to.IsHitTestVisible = true;
        if (animate && _state.IsExpanded)
        {
            // The outgoing page keeps drawing but takes no layout space (negative bottom margin), so
            // the island sizes to the incoming page while both are on screen.
            _leavingPage = from;
            from.Margin = new Thickness(0, 0, 0, -from.ActualHeight);
            from.IsHitTestVisible = false;
            SlidePage(from, 0, -36 * direction, 1, 0, 140, () =>
            {
                if (_leavingPage == from) FinishLeaving(from);
            });
            SlidePage(to, 36 * direction, 0, 0, 1, 220, null);
        }
        else
        {
            FinishLeaving(from);
            to.Opacity = 1;
            to.RenderTransform = Transform.Identity;
        }

        SuspendTextShadow();
        UpdateWallpaperThumb();
        UpdateMotionPlayback();
        if (_state.IsExpanded) Render(); // new height and banner
        else UpdatePageVisibility();
    }

    private void FinishLeaving(FrameworkElement page)
    {
        if (page == _leavingPage) _leavingPage = null;
        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = 1;
        page.RenderTransform = Transform.Identity;
        page.Margin = new Thickness(0);
        if (page != PageElement(_page)) page.Visibility = Visibility.Collapsed;
    }

    private void SlidePage(FrameworkElement page, double fromX, double toX, double fromOpacity, double toOpacity,
        double ms, Action? completed)
    {
        var time = TimeSpan.FromMilliseconds(ms / SpeedFactor);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new TranslateTransform(fromX, 0);
        page.RenderTransform = slide;
        var move = new DoubleAnimation(fromX, toX, time) { EasingFunction = ease };
        var fade = new DoubleAnimation(fromOpacity, toOpacity, time) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(move, FrameRateCap);
        Timeline.SetDesiredFrameRate(fade, FrameRateCap);
        if (completed is not null) fade.Completed += (_, _) => completed();
        slide.BeginAnimation(TranslateTransform.XProperty, move);
        page.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Mouse wheel / touchpad over the expanded island flips pages, unless the list under the cursor
    /// can still scroll that way (then it scrolls).
    /// <list type="bullet">
    /// <item>A gesture that scrolled a list never falls through into a flip when the list reaches its
    /// end; scrolling again (after a short pause) flips.</item>
    /// <item>Mouse wheel (whole notches): one page per notch.</item>
    /// <item>Touchpad (small deltas): one page per swipe. A new swipe is told apart from the previous
    /// swipe's momentum by a pause, a change of direction, or a jump in speed after the momentum has
    /// faded (momentum only decays), so quick successive swipes each flip, and one swipe flips once.</item>
    /// </list>
    /// (The first version allowed one flip per 400 ms stream of events, so quick flicks, and swipes
    /// started during momentum, merged into one gesture and "stopped switching".)
    /// </summary>
    private void OnRootPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_state.IsExpanded) return;
        var now = Environment.TickCount64;
        var delta = e.Delta;
        var mouse = delta % Mouse.MouseWheelDeltaForOneLine == 0;
        var gap = now - _lastWheelEvent;
        var speed = Math.Abs(delta);
        // A touchpad swipe ramps up fast at its start (8 → 30 → 60 → 90…); only once its momentum has
        // clearly faded (below 40% of the swipe's peak) can a speed-up mean a new swipe. Without that
        // check the ramp itself read as a second swipe and one swipe flipped two pages.
        var freshSwipe = !mouse && _wheelMomentumFaded && speed >= 30 && speed >= 2 * _wheelSpeed
                         && now - _lastWheelFlip > 250;
        var newGesture = gap > (mouse ? 300 : 220)
                         || Math.Sign(delta) != Math.Sign(_lastWheelDeltaValue)
                         || freshSwipe;
        _wheelSpeed = newGesture ? speed : 0.7 * _wheelSpeed + 0.3 * speed;
        _lastWheelEvent = now;
        _lastWheelDeltaValue = delta;
        if (newGesture)
        {
            _wheelGestureScrolled = false;
            _wheelGestureFlipped = false;
            _wheelDelta = 0;
            _wheelPeak = speed;
            _wheelMomentumFaded = false;
        }
        else
        {
            _wheelPeak = Math.Max(_wheelPeak, speed);
            if (_wheelSpeed < 0.4 * _wheelPeak) _wheelMomentumFaded = true;
        }

        for (var d = e.OriginalSource as DependencyObject; d is not null && d != Root; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ScrollViewer { ScrollableHeight: > 0 } scroller
                && (delta < 0 ? scroller.VerticalOffset < scroller.ScrollableHeight - 0.5 : scroller.VerticalOffset > 0.5))
            {
                _wheelGestureScrolled = true; // reaching the end mid-gesture won't flip
                return; // let it scroll
            }
        }

        e.Handled = true;
        if (_wheelGestureScrolled) return;
        if (!mouse && _wheelGestureFlipped) return;      // touchpad: one page per swipe
        if (mouse && now - _lastWheelFlip < 100) return; // mouse: one page per notch, ignoring double-reported notches

        _wheelDelta += delta;
        if (Math.Abs(_wheelDelta) < Mouse.MouseWheelDeltaForOneLine) return;
        var step = _wheelDelta < 0 ? 1 : -1;
        _wheelDelta = 0;

        var pages = AvailablePages();
        var index = pages.IndexOf(_page) + step;
        if (index < 0 || index >= pages.Count) return;
        _wheelGestureFlipped = true;
        _lastWheelFlip = now;
        ShowPage(pages[index], animate: true);
    }

    private double MeasureExpandedHeight()
    {
        // Content changed deep inside a page (e.g. forecast rows added) only invalidates that panel;
        // its ancestors keep a cached size until the next layout pass. Run it now, then measure.
        ExpandedView.UpdateLayout();
        ExpandedView.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
        return ExpandedView.DesiredSize.Height - PillTop;
    }

    private void SwitchTo(FrameworkElement target)
    {
        if (target == _currentView) return;
        var time = 1 / SpeedFactor;

        if (_currentView is { } previous)
        {
            previous.IsHitTestVisible = false;
            AnimateView(previous, opacity: 0, fromScale: null, toScale: 0.96, durationMs: 100 * time, delayMs: 0);
        }

        // The text shadow lives on the visible view only (an effect costs per pixel of its element),
        // and is dropped while views fade/scale: it can't be seen in motion, and re-blurring every
        // frame measured about a third of the animation cost. It returns once things settle.
        SetTextShadow(null);
        _currentView = target;
        SuspendTextShadow();
        target.IsHitTestVisible = true;
        UpdateEqualizer();
        AnimateView(target, opacity: 1, fromScale: 0.94, toScale: 1, durationMs: 200 * time, delayMs: 35 * time);
    }

    private void AnimateView(FrameworkElement view, double opacity, double? fromScale, double toScale,
        double durationMs, double delayMs)
    {
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(opacity, duration) { BeginTime = begin, EasingFunction = ease };
        var scale = new DoubleAnimation(toScale, duration) { BeginTime = begin, EasingFunction = ease };
        if (fromScale is { } from) scale.From = from;
        Timeline.SetDesiredFrameRate(fade, FrameRateCap); // same cap as the spring
        Timeline.SetDesiredFrameRate(scale, FrameRateCap);
        view.BeginAnimation(OpacityProperty, fade);
        var transform = (ScaleTransform)view.RenderTransform;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    // Collapsed states fit in a short window. A transparent WPF window is redrawn and pushed to the
    // screen as a whole every animation frame, so keeping it short while collapsed cuts that work
    // (and the GPU blur area, which follows the window) by about 3×.
    private const double CompactWindowHeight = 90;

    /// <summary>Grows the window before the pill grows; shrinks it only once the pill has settled small.</summary>
    private void UpdateWindowHeight()
    {
        var needed = PillTop + Math.Max(_height.Value, _height.Target) + 30; // overshoot + shadow room

        // Expanded content can outgrow the base height (quick apps + wallpaper controls). The root
        // only ever grows, in 20-unit steps: resizing the window rebuilds the GPU blur's layers, so
        // it must not follow the spring frame by frame.
        if (needed > Root.Height) Root.Height = Math.Ceiling(needed / 20) * 20;

        var height = (needed > CompactWindowHeight ? Root.Height : CompactWindowHeight) * Scale;
        if (Math.Abs(Height - height) > 0.5) Height = height;
    }

    private void AnimateTo(double width, double height, double radius)
    {
        _width.Target = width;
        _height.Target = height;
        _radius.Target = radius;
        UpdateWindowHeight();
        if (_animating) return;

        _animating = true;
        _lastFrameTime = _frameClock.Elapsed.TotalSeconds;
        _lastAppliedFrame = 0;
        SetTextShadow(null); // shadow returns when settled
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _frameClock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - _lastFrameTime, 1.0 / 20);
        _lastFrameTime = now;

        _width.Step(dt);
        _height.Step(dt);
        _radius.Step(dt);

        var settled = _width.IsSettled && _height.IsSettled && _radius.IsSettled;
        // Cap shape updates at 60 fps (30 under Energy Saver): on 120/144 Hz displays this halves the
        // window redraws while the motion looks the same at this size. (Physics still steps every tick.)
        if (!settled && now - _lastAppliedFrame < 1.0 / FrameRateCap - 0.002) return;
        _lastAppliedFrame = now;

        if (settled)
        {
            _width.Snap();
            _height.Snap();
            _radius.Snap();
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
            UpdateWindowHeight();
            if (_shadowRestore?.IsEnabled != true) RestoreTextShadow();
        }

        ApplyGeometry();
    }

    private void ApplyGeometry()
    {
        var w = Math.Max(0, _width.Value);
        var h = Math.Max(0, _height.Value);
        var r = Math.Clamp(_radius.Value, 0, Math.Min(w, h) / 2);

        var shape = IsNotch ? BuildNotch(w, h, r) : BuildPill(w, h, r);
        PillShape.Data = shape;
        GlassSheen.Data = shape;
        GlassLayer.Clip = shape;
        ContentLayer.Clip = shape;
        UpdateGpuShape();

        // The second-activity bubble sits just right of the pill, as tall as it; the privacy dot after it.
        var right = (BaseWidth + w) / 2;
        if (ActivityBubble.Visibility == Visibility.Visible)
        {
            var size = Math.Max(26, h);
            ActivityBubble.Width = ActivityBubble.Height = size;
            ActivityBubble.Margin = new Thickness(right + 7, PillTop + (h - size) / 2, 0, 0);
            right += 7 + size;
        }
        if (PrivacyDot.Visibility == Visibility.Visible)
            PrivacyDot.Margin = new Thickness(right + 7, PillTop + h / 2 - PrivacyDot.Height / 2, 0, 0);
    }

    // ---------------------------------------------------------------- mic / camera in use

    private static readonly Brush CameraBrush = FrozenBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush MicrophoneBrush = FrozenBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OnPrivacyChanged(SensorUse old, SensorUse now)
    {
        UpdatePrivacyIndicators();

        // Announce a start (not a stop), like the iPhone; camera wins when both start together.
        var cameraStarted = now.CameraApps.Except(old.CameraApps).ToList();
        var micStarted = now.MicrophoneApps.Except(old.MicrophoneApps).ToList();
        if (cameraStarted.Count > 0)
            ShowInfo(TransientPriority.Device, 340, "", "Camera in use", string.Join(", ", cameraStarted),
                glyphBrush: CameraBrush, detailBrush: CameraBrush);
        else if (micStarted.Count > 0)
            ShowInfo(TransientPriority.Device, 340, "", "Microphone in use", string.Join(", ", micStarted),
                glyphBrush: MicrophoneBrush, detailBrush: MicrophoneBrush);
    }

    /// <summary>Dot beside the collapsed pill; chip with the app names in the expanded island's nav row.</summary>
    private void UpdatePrivacyIndicators()
    {
        var use = _privacy.Current;
        var active = use.CameraInUse || use.MicrophoneInUse;
        var brush = use.CameraInUse ? CameraBrush : MicrophoneBrush;

        PrivacyDot.Fill = brush;
        PrivacyDot.Visibility = active && !_state.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        PrivacyChip.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PrivacyChipDot.Fill = brush;
        var what = use.CameraInUse && use.MicrophoneInUse ? "Camera & mic"
            : use.CameraInUse ? "Camera"
            : "Mic";
        PrivacyChipText.Text = $"{what} · {string.Join(", ", use.CameraApps.Concat(use.MicrophoneApps).Distinct())}";
        ApplyGeometry();
    }

    private static Geometry BuildPill(double w, double h, double r)
    {
        var geometry = new RectangleGeometry(new Rect((BaseWidth - w) / 2, IslandTopMargin, w, h), r, r);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Flush with the top edge: square top, rounded bottom corners, and small concave "shoulders"
    /// that flare into the screen edge so it reads as part of the bezel.
    /// </summary>
    private Geometry BuildNotch(double w, double h, double r)
    {
        var x = (BaseWidth - w) / 2;
        var s = Math.Min(NotchShoulder * Math.Max(Roundness, 0.25), h / 2);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x - s, 0), isFilled: true, isClosed: true);
            ctx.QuadraticBezierTo(new Point(x, 0), new Point(x, s), true, true);
            ctx.LineTo(new Point(x, h - r), true, true);
            ctx.ArcTo(new Point(x + r, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            ctx.LineTo(new Point(x + w - r, h), true, true);
            ctx.ArcTo(new Point(x + w, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            ctx.LineTo(new Point(x + w, s), true, true);
            ctx.QuadraticBezierTo(new Point(x + w, 0), new Point(x + w + s, 0), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------- Liquid Glass blur engines

    /// <summary>
    /// Picks and drives the blur engine. GPU (DWM shared visual + DirectComposition blur) is preferred:
    /// near-zero CPU and the island stays capturable. Screen capture is the fallback; it needs the
    /// island excluded from capture so the copy shows what's behind it rather than the island.
    /// </summary>
    private void UpdateGlass()
    {
        var wantBlur = IsGlass && IsLoaded && _settings.GlassBlur > 0;

        // Auto: the GPU engine can't draw WinUI 3 windows (their thumbnails come out empty), so while
        // one is the top window behind the island the screen-capture engine takes over.
        var gpu = wantBlur && _settings.GlassEngine != GlassEngine.ScreenCapture
                  && !(_settings.GlassEngine == GlassEngine.Auto && _winUiBehind)
                  && GpuBlurVerified() && EnsureGpuBlur();
        if (gpu != _gpuActive)
        {
            _gpuBlur?.SetVisible(gpu);
            if (gpu) KeepOnTop(force: true);
        }
        _gpuActive = gpu;
        if (gpu) UpdateGpuShape();

        var capture = wantBlur && !gpu && _settings.GlassEngine != GlassEngine.Gpu && !_captureUnsupported;
        SetExcludedFromCapture(capture);
        capture &= _excludedFromCapture;

        GlassLayer.Visibility = capture ? Visibility.Visible : Visibility.Collapsed;
        if (capture)
        {
            ((BlurEffect)GlassImage.Effect).Radius = Math.Clamp(_settings.GlassBlur, 0, 1) * MaxGlassBlur;
            CaptureGlass();
            _glassTimer.Start();
        }
        else
        {
            _glassTimer.Stop();
            GlassImage.Source = null;
            _glassBitmap = null;
        }
    }

    private void SetExcludedFromCapture(bool exclude)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (exclude == _excludedFromCapture || hwnd == IntPtr.Zero) return;
        var ok = NativeMethods.SetWindowDisplayAffinity(hwnd,
            exclude ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
        if (exclude && !ok) _captureUnsupported = true; // older than Windows 10 2004
        _excludedFromCapture = exclude && ok;
    }

    // ---- GPU engine

    private GpuBlurBackdrop? _gpuBlur;
    private bool _gpuActive;
    private bool _gpuFailed;
    private bool _gpuProbeRunning;
    private double _glassStrength = 1;

    /// <summary>"10.0.26200.6899": DWM changes ship in cumulative updates, so the revision is part of the key.</summary>
    private static string WindowsBuildKey()
    {
        var revision = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", 0);
        var v = Environment.OSVersion.Version;
        return $"v4|{v.Major}.{v.Minor}.{v.Build}.{revision}"; // bump when the engine's API usage changes
    }

    /// <summary>True when the probe passed on this build; starts the probe (in a child process) if never run.</summary>
    private bool GpuBlurVerified()
    {
        if (_gpuFailed) return false;
        var key = WindowsBuildKey();
        if (_settings.GpuBlurProbe == $"{key}:ok") return true;
        if (_settings.GpuBlurProbe == $"{key}:fail") return false;
        if (!_gpuProbeRunning) RunGpuProbe(key);
        return false;
    }

    private async void RunGpuProbe(string key)
    {
        _gpuProbeRunning = true;
        var ok = await Task.Run(() =>
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--probe-gpu-blur")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (probe is null) return false;
                if (!probe.WaitForExit(15000))
                {
                    probe.Kill();
                    return false;
                }
                return probe.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        });
        _gpuProbeRunning = false;
        _settings.GpuBlurProbe = $"{key}:{(ok ? "ok" : "fail")}";
        _settings.Save();
        UpdateGlass();
    }

    // ---------------------------------------------------------------- Energy Saver

    private bool _energySaver;

    /// <summary>
    /// Like the iPhone's Low Power Mode: while Windows' Energy Saver is on, island animations run at
    /// 30 fps instead of 60 and the music bars refresh at half rate. Everything else is unchanged.
    /// </summary>
    private int FrameRateCap => _energySaver ? 30 : 60;

    private void InitEnergySaver()
    {
        _energySaver = StatusService.EnergySaverOn;
#if DEBUG
        // Debug aid: DISLAND_ENERGYSAVER=1 runs the island's Energy Saver mode without changing Windows' setting.
        if (Environment.GetEnvironmentVariable("DISLAND_ENERGYSAVER") == "1")
        {
            _energySaver = true;
            _meterTimer.Interval = TimeSpan.FromMilliseconds(66);
            return;
        }
#endif
        try
        {
            Windows.System.Power.PowerManager.EnergySaverStatusChanged += (_, _) => Dispatcher.InvokeAsync(() =>
            {
                _energySaver = StatusService.EnergySaverOn;
                _meterTimer.Interval = TimeSpan.FromMilliseconds(_energySaver ? 66 : 33);
                if (_equalizerRunning) // restart the animated bars at the new rate
                {
                    SetEqualizerRunning(false);
                    UpdateEqualizer();
                }
            });
        }
        catch (Exception) { /* not available on this Windows */ }
        _meterTimer.Interval = TimeSpan.FromMilliseconds(_energySaver ? 66 : 33);
    }

    private bool _winUiBehind;

    /// <summary>
    /// Is the top window behind the island a WinUI 3 window (Apple Music, new Notepad, …)? The GPU
    /// engine's thumbnails of those are empty (checked with every source window and flag value), so
    /// Auto switches to the capture engine while one is there. Only matters while Liquid Glass is on.
    /// </summary>
    private void UpdateWinUiBehind()
    {
        if (!IsGlass || _settings.GlassEngine != GlassEngine.Auto) return;
        var island = new WindowInteropHelper(this).Handle;
        if (island == IntPtr.Zero || !NativeMethods.GetWindowRect(island, out var bounds)) return;

        var winUi = false;
        var className = new System.Text.StringBuilder(64);
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == island || hwnd == _gpuBlur?.Handle || !NativeMethods.IsShownOnScreen(hwnd)) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect) || !NativeMethods.Intersects(rect, bounds)) return true;
            className.Clear();
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            winUi = className.ToString() == "WinUIDesktopWin32WindowClass";
            return false; // only the top one decides
        }, IntPtr.Zero);

        if (winUi == _winUiBehind) return;
        _winUiBehind = winUi;
        UpdateGlass();
    }

    private bool EnsureGpuBlur()
    {
        if (_gpuBlur is not null) return true;
        try
        {
            _gpuBlur = new GpuBlurBackdrop();
            var hwnd = new WindowInteropHelper(this).Handle;
            _gpuBlur.Exclude(hwnd);
            // Not owner/owned: changing an already-shown window's owner leaves Windows' topmost
            // bookkeeping inconsistent, and the island could end up below ordinary app windows.
            // The pair is ordered explicitly instead (see KeepOnTop).
            KeepOnTop(force: true);
            SyncGpuBounds();
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _gpuBlur?.Dispose();
            _gpuBlur = null;
            _gpuFailed = true; // fall back to capture for this session
            return false;
        }
    }

    /// <summary>
    /// Keeps the island at the top of the always-on-top layer, with the GPU blur window directly
    /// beneath it. Called on window events; does nothing unless another visible window actually
    /// overlaps the island from above (so it never fights over z-order needlessly).
    /// </summary>
    private void KeepOnTop(bool force = false)
    {
        var island = new WindowInteropHelper(this).Handle;
        if (island == IntPtr.Zero) return;
        if (!force && !IsCoveredFromAbove(island)) return;

        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE
                           | NativeMethods.SWP_NOOWNERZORDER;
        NativeMethods.SetWindowPos(island, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
        // insertAfter = island places the blur window immediately below it.
        if (_gpuBlur is not null) NativeMethods.SetWindowPos(_gpuBlur.Handle, island, 0, 0, 0, 0, flags);
    }

    /// <summary>Is any other visible window above the island in z-order and overlapping it?</summary>
    private static bool IsCoveredFromAbove(IntPtr island)
    {
        if (!NativeMethods.GetWindowRect(island, out var mine)) return false;
        for (var w = NativeMethods.GetWindow(island, NativeMethods.GW_HWNDPREV); w != IntPtr.Zero;
             w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDPREV))
        {
            if (NativeMethods.IsShownOnScreen(w) && NativeMethods.GetWindowRect(w, out var r) && NativeMethods.Intersects(r, mine))
                return true;
        }
        return false;
    }

    private void SyncGpuBounds()
    {
        if (_gpuBlur is null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        _gpuBlur.SetBounds(rect);
        UpdateGpuShape();
    }

    /// <summary>Clip/blur/opacity of the GPU backdrop, in its window pixels. Called every animation frame.</summary>
    private void UpdateGpuShape()
    {
        if (!_gpuActive || _gpuBlur is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var f = dpi.DpiScaleX * Scale;
        var w = Math.Max(0, _width.Value);
        var h = Math.Max(0, _height.Value);
        var r = Math.Clamp(_radius.Value, 0, Math.Min(w, h) / 2);
        var extra = IsNotch ? r : 0; // square top corners in notch mode
        _gpuBlur.SetShape(
            (float)((BaseWidth - w) / 2 * f), (float)((PillTop - extra) * f),
            (float)(w * f), (float)((h + extra) * f), (float)(r * f),
            (float)(Math.Clamp(_settings.GlassBlur, 0, 1) * MaxGlassBlur * f),
            (float)_glassStrength);
    }

    /// <summary>Copies the screen behind the pill (plus room for the blur to spread) into the glass image.</summary>
    private void CaptureGlass()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var window)) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var f = dpi.DpiScaleX * Scale; // design units → screen pixels
        var pad = ((BlurEffect)GlassImage.Effect).Radius + 4;
        var w = Math.Max(0, _width.Value);
        var h = Math.Max(0, _height.Value);

        var left = Math.Max(0, (BaseWidth - w) / 2 - pad);
        var top = Math.Max(0, PillTop - pad);
        var right = Math.Min(BaseWidth, (BaseWidth + w) / 2 + pad);
        var bottom = Math.Min(Root.Height, PillTop + h + pad);

        var px = (int)Math.Floor(left * f);
        var py = (int)Math.Floor(top * f);
        var pw = (int)Math.Ceiling(right * f) - px;
        var ph = (int)Math.Ceiling(bottom * f) - py;
        // Full rate only while the island is animating; half rate is plenty for what's behind it.
        _glassTimer.Interval = TimeSpan.FromMilliseconds(_animating ? 33 : 66);
        if (!_capture.Capture(window.Left + px, window.Top + py, pw, ph)) return;

        if (_glassBitmap is null || _glassBitmap.PixelWidth != pw || _glassBitmap.PixelHeight != ph)
        {
            _glassBitmap = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgr32, null);
            GlassImage.Source = _glassBitmap;
        }
        _glassBitmap.WritePixels(new Int32Rect(0, 0, pw, ph), _capture.Bits, _capture.BufferSize, _capture.Stride);

        Canvas.SetLeft(GlassImage, px / f);
        Canvas.SetTop(GlassImage, py / f);
        GlassImage.Width = pw / f;
        GlassImage.Height = ph / f;
    }

    /// <summary>The island's target rectangle in screen pixels, for background detection.</summary>
    private NativeMethods.RECT? GetIslandScreenRect()
    {
        if (!IsLoaded || PresentationSource.FromVisual(this) is null) return null;
        var w = _width.Target;
        var x = (BaseWidth - w) / 2;
        var topLeft = Root.PointToScreen(new Point(x, PillTop));
        var bottomRight = Root.PointToScreen(new Point(x + w, PillTop + _height.Target));
        return new NativeMethods.RECT
        {
            Left = (int)topLeft.X,
            Top = (int)topLeft.Y,
            Right = (int)bottomRight.X,
            Bottom = (int)bottomRight.Y,
        };
    }

    // ---------------------------------------------------------------- theme & material

    /// <summary>Accent color per the user's chosen source; null means "neutral" (white bars, black background).</summary>
    private Color? ResolveAccent() => _settings.AccentSource switch
    {
        AccentSource.Custom => ParseColor(_settings.CustomAccent),
        AccentSource.MediaApp => _media.Current?.Accent,
        AccentSource.Monochrome => null,
        _ => _wallpaper.IsRunning ? _wallpaper.Theme?.Accent : null, // Auto, Wallpaper
    };

    private static Color? ParseColor(string? text)
    {
        try { return ColorConverter.ConvertFromString(text ?? "") is Color c ? c : null; }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// 1 when the material should be shown in full; the "desktop opacity" when adaptive and only the
    /// wallpaper is behind (unless expanded and the user wants it solid while expanded).
    /// </summary>
    private double MaterialStrength =>
        !_settings.AdaptiveBackground || _background.AppBehind || (_settings.SolidWhenExpanded && _state.IsExpanded)
            ? 1
            : Math.Clamp(_settings.DesktopOpacity, 0, 1);

    /// <summary>Specular rim: bright top-left edge fading around to a faint bottom-right glint.</summary>
    private static Brush CreateGlassRim(double shine)
    {
        byte A(double a) => (byte)Math.Round(255 * Math.Clamp(a * shine, 0, 1));
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.75), 0xFF, 0xFF, 0xFF), 0));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.12), 0xFF, 0xFF, 0xFF), 0.35));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.05), 0xFF, 0xFF, 0xFF), 0.65));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.4), 0xFF, 0xFF, 0xFF), 1));
        rim.Freeze();
        return rim;
    }

    /// <summary>Top sheen: a soft highlight across the upper part of the glass.</summary>
    private static Brush CreateGlassSheen(double shine)
    {
        byte A(double a) => (byte)Math.Round(255 * Math.Clamp(a * shine, 0, 1));
        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.22), 0xFF, 0xFF, 0xFF), 0));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.05), 0xFF, 0xFF, 0xFF), 0.4));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.6));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.06), 0xFF, 0xFF, 0xFF), 1));
        sheen.Freeze();
        return sheen;
    }

    private readonly DropShadowEffect _textShadow = new()
    {
        BlurRadius = 6,
        ShadowDepth = 0,
        Opacity = 0.7,
        Color = Colors.Black,
        RenderingBias = RenderingBias.Performance,
    };

    /// <summary>
    /// Applies material (fill, blur, rim, sheen, outline, shadow) and accents. Called whenever settings,
    /// wallpaper, media app, expansion or what's behind the island changes.
    /// </summary>
    private string? _themeKey;
    private bool _useTextShadow;
    private DispatcherTimer? _shadowRestore;

    /// <summary>
    /// Puts the shadow on the visible view's text. For the expanded view that is its content panel,
    /// not the whole view: the view also holds the (possibly video) art banner, and an effect over it
    /// would re-blur on every video frame.
    /// </summary>
    private void SetTextShadow(Effect? effect)
    {
        if (_currentView is null) return;
        var target = _currentView == ExpandedView ? ExpandedContent : _currentView;
        if (target.Effect != effect) target.Effect = effect;
    }

    private void SuspendTextShadow()
    {
        SetTextShadow(null);
        if (!_useTextShadow) return;
        _shadowRestore ??= CreateShadowRestoreTimer();
        _shadowRestore.Stop();
        _shadowRestore.Interval = TimeSpan.FromMilliseconds(260 / SpeedFactor); // after the view fade-in
        _shadowRestore.Start();
    }

    private DispatcherTimer CreateShadowRestoreTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background);
        timer.Tick += (_, _) =>
        {
            if (_animating) return; // keep waiting; the spring's settle calls RestoreTextShadow
            timer.Stop();
            RestoreTextShadow();
        };
        return timer;
    }

    private void RestoreTextShadow()
    {
        var settled = !_animating && _shadowRestore?.IsEnabled != true;
        SetTextShadow(_useTextShadow && settled ? _textShadow : null);
    }

    private void ApplyTheme()
    {
        var accent = ResolveAccent();
        _accent = accent;
        var strength = MaterialStrength;
        var glass = IsGlass;

        // The blur engine has its own state (probe finishing, window loaded); always let it update.
        _glassStrength = strength;
        UpdateGlass();

        // ApplyTheme is called on many events (media updates, window events); skip rebuilding brushes
        // and restarting animations when nothing that affects the look has changed.
        var s = _settings;
        var key = string.Join('|', accent, strength, s.Material, s.BackgroundTint, s.BackgroundOpacity, s.GlassTint,
            s.GlassShine, s.OutlineWidth, s.OutlineOpacity, s.ShadowStrength, s.TextShadow, s.AccentSource,
            _media.Current?.Accent, _osdDimmed);
        if (key == _themeKey) return;
        _themeKey = key;

        // Background fill
        var tint = Math.Clamp(_settings.BackgroundTint, 0, 0.6);
        var baseColor = accent is { } a ? ColorTools.Mix(Colors.Black, a, tint) : Colors.Black;
        var materialAlpha = _settings.Material switch
        {
            IslandMaterial.Translucent => Math.Clamp(_settings.BackgroundOpacity, 0.15, 1),
            IslandMaterial.LiquidGlass => Math.Clamp(_settings.GlassTint, 0, 0.9),
            _ => 1,
        };
        // Never fully 0: Windows passes clicks through fully transparent pixels of a layered window.
        baseColor.A = (byte)Math.Max(2, Math.Round(255 * materialAlpha * strength));
        _pillBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(baseColor, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase() });

        // Live blur of what's behind; adaptive strength fades it toward the sharp background.
        var shine = Math.Clamp(_settings.GlassShine, 0, 1);
        GlassLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(strength, TimeSpan.FromMilliseconds(450)));
        GlassSheen.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        GlassSheen.Fill = CreateGlassSheen(shine);
        GlassSheen.BeginAnimation(OpacityProperty, new DoubleAnimation(strength, TimeSpan.FromMilliseconds(450)));

        // Text shadow wherever the background can be see-through.
        var seeThrough = glass || _settings.Material == IslandMaterial.Translucent || strength < 1;
        _useTextShadow = _settings.TextShadow && seeThrough;
        RestoreTextShadow();

        // Outline: glass gets the specular rim; other materials an optional flat outline.
        var outline = Math.Clamp(_settings.OutlineWidth, 0, 4);
        if (glass && (shine > 0 || outline > 0))
        {
            PillShape.Stroke = CreateGlassRim(Math.Max(shine, outline > 0 ? 0.3 : 0) * Math.Max(strength, 0.35));
            PillShape.StrokeThickness = Math.Max(1, outline);
        }
        else if (outline > 0)
        {
            var c = accent ?? Colors.White;
            c.A = (byte)Math.Round(255 * Math.Clamp(_settings.OutlineOpacity, 0, 1) * Math.Max(strength, 0.35));
            var stroke = new SolidColorBrush(c);
            stroke.Freeze();
            PillShape.Stroke = stroke;
            PillShape.StrokeThickness = outline;
        }
        else
        {
            PillShape.Stroke = null;
            PillShape.StrokeThickness = 0;
        }

        // Shadow
        var shadow = Math.Clamp(_settings.ShadowStrength, 0, 1) * strength;
        _shadow.Opacity = shadow;
        PillShape.Effect = shadow > 0.01 ? _shadow : null;

        // Accents
        var levelBrush = accent is { } la ? new SolidColorBrush(la) : Brushes.White;
        levelBrush.Freeze();
        ProgressFill.Background = levelBrush;
        if (!_osdDimmed) OsdFill.Background = levelBrush;

        var mediaColor = _settings.AccentSource == AccentSource.Monochrome
            ? Colors.White
            : accent ?? _media.Current?.Accent ?? DefaultMediaAccent;
        var mediaBrush = new SolidColorBrush(mediaColor);
        mediaBrush.Freeze();
        foreach (var bar in Equalizer.Children.OfType<Shape>()) bar.Fill = mediaBrush;
        MediaAppDot.Fill = mediaBrush;
    }

    // ---------------------------------------------------------------- hover / click

    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        if (_settings.ExpandOnHover && !_state.IsExpanded && !_hoverSuppressed) _hoverTimer.Start();
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        _hoverSuppressed = false;
    }

    private void OnRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _hoverTimer.Stop();

        if (!_state.IsExpanded && _state.Transient == IslandView.TimerDone)
        {
            _state.DismissTransient(); // also silences a ringing alarm
            return;
        }

        // Like tapping on iPhone: a click on the collapsed music activity (or its bubble) opens the
        // music app instead of expanding; hovering still expands.
        if (!_state.IsExpanded && _settings.ClickOpensMediaApp && _state.Transient is null
            && ((_currentView == MediaView && e.OriginalSource is DependencyObject source && !IsInside(source, ActivityBubble))
                || (_state.SecondaryView == IslandView.Media && e.OriginalSource is DependencyObject b && IsInside(b, ActivityBubble))))
        {
            _hoverSuppressed = true;
            OpenMediaApp();
            return;
        }

        if (_state.IsExpanded)
        {
            // Don't let the hover timer immediately re-open what the user just closed.
            _hoverSuppressed = true;
            SetExpanded(false);
        }
        else
        {
            SetExpanded(true);
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d == container) return true;
        return false;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        _hoverTimer.Stop();
        SetExpanded(false);
        SettingsRequested?.Invoke();
    }

    private void SetExpanded(bool expanded)
    {
        _msOutside = 0;
        if (expanded)
        {
            _leavePoll.Start();
            _progressTimer.Start();
        }
        else
        {
            _leavePoll.Stop();
            _progressTimer.Stop();
        }
        var changed = _state.IsExpanded != expanded;
        // Opening while (or just after) an alert is shown goes to that alert's page.
        if (expanded && changed && _alertPage is { } alertPage && DateTime.Now < _alertPageUntil
            && AvailablePages().Contains(alertPage))
            ShowPage(alertPage, animate: false);
        _alertPage = null;
        if (!expanded && changed && (_calendarSelected is not null || _calendarMonth != new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)))
        {
            // Next time the calendar opens on this month's upcoming list.
            _calendarSelected = null;
            _calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            BuildCalendar();
        }
        _state.IsExpanded = expanded;
        if (changed) UpdatePrivacyIndicators(); // the dot hides while expanded (the chip shows instead)
        UpdateWallpaperThumb();
        UpdateMotionPlayback();
        if (changed && _settings.SolidWhenExpanded && _settings.AdaptiveBackground) ApplyTheme();
    }

    /// <summary>
    /// The window is mostly transparent click-through space, so WM_MOUSELEAVE isn't always reliable;
    /// poll the cursor against the pill's target rectangle while expanded.
    /// </summary>
    private void OnLeavePoll(object? sender, EventArgs e)
    {
        if (IsCursorOverPill())
        {
            _msOutside = 0;
            return;
        }

        _msOutside += LeavePollMs;
        if (_msOutside >= _settings.CollapseDelayMs) SetExpanded(false);
    }

    private bool IsCursorOverPill()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return true;
        var p = Root.PointFromScreen(new Point(pt.X, pt.Y)); // design units, scale and DPI undone
        var w = _width.Target;
        var x = (BaseWidth - w) / 2;
        const double slop = 6;
        return p.X >= x - slop && p.X <= x + w + slop && p.Y >= 0 && p.Y <= PillTop + _height.Target + slop;
    }

    // ---------------------------------------------------------------- clock

    private string? _clockWeatherKey;

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var seconds = _settings.ShowSeconds;

        // Wake exactly at the next second/minute boundary instead of every second: without seconds
        // shown, the clock does one update per minute.
        var next = seconds
            ? now.AddTicks(TimeSpan.TicksPerSecond - now.Ticks % TimeSpan.TicksPerSecond)
            : now.AddTicks(TimeSpan.TicksPerMinute - now.Ticks % TimeSpan.TicksPerMinute);
        _clockTimer.Interval = next - now + TimeSpan.FromMilliseconds(15);

        var time = _settings.ClockFormat switch
        {
            ClockFormat.TwelveHour => now.ToString(seconds ? "h:mm:ss tt" : "h:mm tt"),
            ClockFormat.TwentyFourHour => now.ToString(seconds ? "HH:mm:ss" : "HH:mm"),
            _ => now.ToString(seconds ? "T" : "t"),
        };

        var minimal = _settings.IdleStyle == IdleStyle.Minimal;
        var idleText = _settings.ShowDateWhenIdle ? $"{now:ddd d MMM}   {time}" : time;
        var weather = _settings.WeatherEnabled && _settings.WeatherInIdlePill ? _weather.Current : null;
        var weatherKey = weather is null ? null : $"{weather.Kind}|{weather.IsDay}|{Degrees(weather.Temperature)}";
        var visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        if (idleText == ClockText.Text && visibility == ClockRow.Visibility && time == BigTime.Text
            && weatherKey == _clockWeatherKey)
            return;

        ClockRow.Visibility = visibility;
        ClockText.Text = idleText;
        BigTime.Text = time;
        DateText.Text = now.ToString("dddd, MMMM d");

        // "| icon 26°" after the time; the icon is only rebuilt when the condition changes.
        if (weatherKey != _clockWeatherKey)
        {
            if (weather is not null && _clockWeatherKey?.StartsWith($"{weather.Kind}|{weather.IsDay}|") != true)
                ClockWeatherIcon.Child = WeatherIcons.Create(weather.Kind, weather.IsDay, 18);
            _clockWeatherKey = weatherKey;
            ClockWeatherTemp.Text = weather is null ? "" : Degrees(weather.Temperature);
            ClockWeather.Visibility = weather is null ? Visibility.Collapsed : Visibility.Visible;
        }

        // Size the idle pill to its text so seconds/date never clip.
        double width, height;
        if (minimal)
        {
            (width, height) = (110, 26);
        }
        else
        {
            ClockRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            (width, height) = (Math.Max(150, Math.Ceiling(ClockRow.DesiredSize.Width) + 44), 34);
        }

        if (Math.Abs(ClockView.Width - width) < 0.5 && Math.Abs(ClockView.Height - height) < 0.5) return;
        ClockView.Width = width;
        ClockView.Height = height;
        if (IsLoaded && _currentView == ClockView && !_state.IsExpanded) Render();
    }

    // ---------------------------------------------------------------- volume / brightness OSD

    private void ShowVolume(float level, bool muted)
    {
        var glyph = muted || level <= 0.001 ? ""
            : level < 0.34 ? ""
            : level < 0.67 ? ""
            : "";
        ShowOsd(IslandView.Volume, level, glyph, dimmed: muted);
    }

    private void ShowBrightness(int level) => ShowOsd(IslandView.Brightness, level / 100.0, "", dimmed: false);

    private void ShowOsd(IslandView kind, double level, string glyph, bool dimmed)
    {
        level = Math.Clamp(level, 0, 1);
        OsdIcon.Text = glyph;
        OsdPercent.Text = ((int)Math.Round(level * 100)).ToString();
        _osdDimmed = dimmed;
        OsdFill.Background = dimmed
            ? (Brush)FindResource("Secondary")
            : _accent is { } accent ? new SolidColorBrush(accent) : Brushes.White;

        var fill = (ScaleTransform)OsdFill.RenderTransform;
        if (_currentView == OsdView)
        {
            fill.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(level, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase() });
        }
        else
        {
            fill.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            fill.ScaleX = level;
        }

        _state.ShowTransient(kind, TransientPriority.Osd, TimeSpan.FromSeconds(_settings.OsdSeconds));
    }

    private void OnVolumeKey(int vk)
    {
        switch (vk)
        {
            case NativeMethods.VK_VOLUME_UP: _volume.StepUp(); break;
            case NativeMethods.VK_VOLUME_DOWN: _volume.StepDown(); break;
            case NativeMethods.VK_VOLUME_MUTE: _volume.ToggleMute(); break;
        }
        // Show even when nothing changed (e.g. already at 100%), like the native flyout does.
        ShowVolume(_volume.Level, _volume.Muted);
    }

    // ---------------------------------------------------------------- battery

    private const double BatteryFillMax = 22;

    private void OnBatteryAlert(BatteryAlert alert, int percent)
    {
        var green = (Brush)FindResource("Green");
        var orange = (Brush)FindResource("Orange");
        var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        var low = percent <= BatteryService.LowThreshold;

        var (title, accent, priority) = alert switch
        {
            BatteryAlert.PluggedIn => ("Charging", green, TransientPriority.Battery),
            BatteryAlert.Unplugged => ("On battery", low ? red : (Brush)Brushes.White, TransientPriority.Battery),
            BatteryAlert.Low => ("Low battery", orange, TransientPriority.LowBattery),
            _ => ("Battery critically low", red, TransientPriority.LowBattery),
        };

        BatteryTitle.Text = title;
        BatteryBolt.Visibility = alert == BatteryAlert.PluggedIn ? Visibility.Visible : Visibility.Collapsed;
        BatteryPercent.Text = $"{percent}%";
        BatteryPercent.Foreground = accent;
        BatteryFill.Background = accent;

        var target = Math.Max(2, BatteryFillMax * percent / 100.0);
        if (alert == BatteryAlert.PluggedIn)
        {
            // Fill sweeps up to the current level as the pill opens.
            BatteryFill.BeginAnimation(WidthProperty, new DoubleAnimation(2, target, TimeSpan.FromMilliseconds(700))
            {
                BeginTime = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            BatteryFill.BeginAnimation(WidthProperty, null);
            BatteryFill.Width = target;
        }

        _state.ShowTransient(IslandView.Battery, priority, TimeSpan.FromSeconds(_settings.AlertSeconds));
    }

    // ---------------------------------------------------------------- info alerts

    /// <summary>Fills in and shows the shared info pill, unless a higher-priority alert is on screen.</summary>
    private void ShowInfo(int priority, double width, string glyph, string title, string? detail,
        Brush? detailBrush = null, Brush? glyphBrush = null, double? seconds = null)
    {
        // Hidden: nothing to show, and animating an invisible window would only cost CPU.
        if (IslandHidden || !_state.CanShowTransient(priority)) return;
        InfoGlyph.Text = glyph;
        InfoGlyph.Foreground = glyphBrush ?? Brushes.White;
        InfoTitle.Text = title;
        InfoDetail.Text = detail ?? "";
        InfoDetail.Foreground = detailBrush ?? (Brush)FindResource("Secondary");
        InfoView.Width = width;
        _state.ShowTransient(IslandView.Info, priority, TimeSpan.FromSeconds(seconds ?? _settings.AlertSeconds));
    }

    private void OnLockToggled(LockKey key, bool on) =>
        ShowInfo(TransientPriority.LockKey, 230,
            "", // keyboard
            key == LockKey.CapsLock ? "Caps Lock" : "Num Lock",
            on ? "On" : "Off",
            detailBrush: on ? (Brush)FindResource("Green") : null,
            seconds: Math.Min(_settings.AlertSeconds, 1.5));

    private void OnFocusChanged(bool on) =>
        ShowInfo(TransientPriority.Focus, 280, "", "Do Not Disturb", on ? "On" : "Off",
            detailBrush: on ? new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)) : null,
            glyphBrush: on ? new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)) : null);

    private void OnCopied(ClipboardEvent copy)
    {
        _lastCopy = copy;
        var (glyph, detail) = copy.Kind switch
        {
            ClipboardKind.Files => ("", copy.Preview),
            ClipboardKind.Image => ("", "Image"),
            ClipboardKind.Text => ("", copy.Preview),
            _ => ("", null),
        };
        ShowInfo(TransientPriority.Clipboard, 340, glyph, "Copied", detail, seconds: Math.Min(_settings.AlertSeconds, 1.5));
    }

    private void OnPasteShortcut() =>
        ShowInfo(TransientPriority.Clipboard, 340, "", "Pasted",
            _lastCopy?.Kind == ClipboardKind.Image ? "Image" : _lastCopy?.Preview,
            seconds: Math.Min(_settings.AlertSeconds, 1.5));

    private void OnDeviceChanged(DeviceEvent device)
    {
        var glyph = device.Kind switch
        {
            DeviceKind.Headphones => "",
            DeviceKind.Speaker => "",
            DeviceKind.Mouse => "",
            DeviceKind.Keyboard => "",
            DeviceKind.GameController => "",
            DeviceKind.Phone => "",
            DeviceKind.Storage => "",
            DeviceKind.Monitor => "",
            DeviceKind.Camera => "",
            _ => "",
        };
        var secondary = (Brush)FindResource("Secondary");
        var status = device.Connected ? device.Battery is { } battery ? $"Connected · {battery}%" : "Connected" : "Disconnected";
        ShowInfo(TransientPriority.Device, 340, glyph, device.Name, status,
            detailBrush: device.Connected ? (Brush)FindResource("Green") : secondary,
            glyphBrush: device.Connected ? null : secondary);
    }

    // ---------------------------------------------------------------- notifications & calendar

    private ExpandedPage? _alertPage;
    private DateTime _alertPageUntil;

    /// <summary>Clicking / hovering the island soon after an alert opens the alert's page.</summary>
    private void SetAlertPage(ExpandedPage page, double seconds)
    {
        if (_state.IsExpanded) return;
        _alertPage = page;
        _alertPageUntil = DateTime.Now.AddSeconds(seconds + 3);
    }

    private void UpdateNotificationAndCalendarServices()
    {
        if (!IsLoaded) return;
#if DEBUG
        var skip = Environment.GetEnvironmentVariable("DISLAND_SKIP") ?? "";
        if (!skip.Contains("notif-only"))
#endif
        if (_settings.NotificationsEnabled) _notifications.Start();
        else _notifications.Stop();
#if DEBUG
        if (skip.Contains("calendar-only")) return;
#endif

        if (!_settings.CalendarEnabled)
        {
            _calendar.Stop();
            _calendarKey = null;
            return;
        }
        var key = $"{string.Join('\n', _settings.CalendarIcsUrls)}|{_settings.WindowsCalendar}|{_settings.CalendarReminderMinutes}";
        if (key == _calendarKey) return;
        var first = _calendarKey is null;
        _calendarKey = key;
        if (first) _calendar.Start();
        else _calendar.Refresh();
    }

    private string? _calendarKey;

    private void OnNotificationArrived(IslandNotification n)
    {
        // Windows holds toasts back during Do Not Disturb; so does the island.
        if (!_settings.NotificationsEnabled || IslandHidden || _focus.IsOn) return;
        if (!_state.CanShowTransient(TransientPriority.Notification)) return;

        NotifIcon.Background = n.AppIcon is { } icon
            ? new ImageBrush(icon) { Stretch = Stretch.UniformToFill }
            : (Brush)FindResource("ArtPlaceholder");
        var hide = _settings.NotificationsHideText;
        NotifTitle.Text = hide ? n.AppName : n.Title;
        NotifBody.Text = hide ? "New notification" : FirstLine(n.Body);
        NotifBody.Visibility = NotifBody.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        NotifApp.Text = hide ? "now" : n.AppName;
        var seconds = Math.Clamp(_settings.NotificationSeconds, 2, 15);
        SetAlertPage(ExpandedPage.Notifications, seconds);
        _state.ShowTransient(IslandView.Notification, TransientPriority.Notification, TimeSpan.FromSeconds(seconds));
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static string TimeAgo(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        return age.TotalMinutes < 1 ? "now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
            : time.LocalDateTime.ToString("ddd");
    }

    private void OnNotificationsClearClick(object sender, RoutedEventArgs e) => _notifications.ClearAll();

    private void OnIdentitySetupClick(object sender, RoutedEventArgs e)
    {
        SetExpanded(false);
        PackageIdentity.RunSetup();
    }

    /// <summary>The Notifications page: newest first; click opens the app, ✕ dismisses.</summary>
    private void BuildNotificationList()
    {
        var items = _notifications.Items;
        var secondary = (Brush)FindResource("Secondary");
        NotificationList.Children.Clear();
        NotificationsToolbar.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationsScroller.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationsCount.Text = items.Count == 1 ? "1 notification" : $"{items.Count} notifications";
        NotificationsSetup.Visibility = PackageIdentity.HasIdentity ? Visibility.Collapsed : Visibility.Visible;
        NotificationsEmpty.Text = _notifications.Problem ?? (items.Count == 0 ? "No notifications" : "");
        NotificationsEmpty.Visibility = NotificationsEmpty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var n in items)
        {
            var dismiss = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 24,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "Dismiss",
                Content = new TextBlock { Style = (Style)FindResource("Glyph"), Text = "", FontSize = 9, Foreground = secondary },
            };
            var id = n.Id;
            dismiss.Click += (_, _) => _notifications.Dismiss(id);

            var text = new StackPanel { Margin = new Thickness(10, 0, 6, 0) };
            text.Children.Add(new TextBlock
            {
                Text = $"{n.AppName} · {TimeAgo(n.Time)}",
                FontSize = 11,
                Foreground = secondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!_settings.NotificationsHideText)
            {
                text.Children.Add(new TextBlock { Text = n.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (n.Body.Length > 0)
                    text.Children.Add(new TextBlock
                    {
                        Text = n.Body,
                        FontSize = 12,
                        Foreground = secondary,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxHeight = 34, // two lines
                    });
            }

            var row = new Grid
            {
                Margin = new Thickness(0, 0, 0, 10),
                Background = Brushes.Transparent,
                Cursor = n.AppId is { Length: > 0 } ? Cursors.Hand : null,
                ToolTip = n.AppId is { Length: > 0 } ? $"Open {n.AppName}" : null,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
                Background = n.AppIcon is { } image ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : (Brush)FindResource("ArtPlaceholder"),
            };
            Grid.SetColumn(text, 1);
            Grid.SetColumn(dismiss, 2);
            row.Children.Add(icon);
            row.Children.Add(text);
            row.Children.Add(dismiss);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (n.AppId is not { Length: > 0 } appId) return;
                _hoverSuppressed = true;
                SetExpanded(false);
                QuickAppsService.Launch(appId);
            };
            NotificationList.Children.Add(row);
        }

        if (IsLoaded && _state.IsExpanded && _page == ExpandedPage.Notifications) Render();
    }

    private void OnCalendarReminder(CalendarItem item)
    {
        if (IslandHidden) return;
        var minutes = Math.Max(1, (int)Math.Round((item.Start - DateTime.Now).TotalMinutes));
        SetAlertPage(ExpandedPage.Calendar, _settings.AlertSeconds + 3);
        var color = item.Color is { } c ? new SolidColorBrush(c) : (Brush)FindResource("Orange");
        ShowInfo(TransientPriority.Calendar, 340, "", item.Title, $"in {minutes} min",
            detailBrush: color, glyphBrush: color, seconds: _settings.AlertSeconds + 3);
    }

    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime? _calendarSelected;

    /// <summary>Whether the event covers any part of <paramref name="day"/> (all-day ends are exclusive).</summary>
    private static bool OccursOn(CalendarItem item, DateTime day) =>
        item.Start < day.AddDays(1) && (item.End > day || item.Start.Date == day);

    private void OnCalendarPrevMonth(object sender, RoutedEventArgs e) => ShiftCalendarMonth(-1);
    private void OnCalendarNextMonth(object sender, RoutedEventArgs e) => ShiftCalendarMonth(1);

    private void ShiftCalendarMonth(int months)
    {
        _calendarMonth = _calendarMonth.AddMonths(months);
        BuildCalendar();
    }

    /// <summary>
    /// The month grid: weekday initials (in the culture's week order), the month's dates with the
    /// neighbouring months' days dimmed, today filled with the accent, a dot under days with events.
    /// Clicking a day lists its events; clicking it again goes back to the upcoming list.
    /// </summary>
    private void BuildMonth()
    {
        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        var secondary = (Brush)FindResource("Secondary");
        var accent = _accent is { } a ? new SolidColorBrush(a) : (Brush)FindResource("Orange");
        var today = DateTime.Today;
        var month = _calendarMonth;
        CalendarMonthTitle.Text = month.ToString(month.Year == today.Year ? "MMMM" : "MMMM yyyy");

        var firstDay = culture.FirstDayOfWeek;
        if (CalendarWeekdays.Children.Count == 0)
        {
            for (var i = 0; i < 7; i++)
                CalendarWeekdays.Children.Add(new TextBlock
                {
                    Text = culture.ShortestDayNames[((int)firstDay + i) % 7],
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = secondary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 3),
                });
        }

        // Days that have events (multi-day events mark every day they cover).
        var busy = new HashSet<DateTime>();
        foreach (var item in _calendar.Items)
        {
            var last = (item.End > item.Start ? item.End.AddTicks(-1) : item.Start).Date;
            for (var d = item.Start.Date; d <= last && busy.Count < 400; d = d.AddDays(1)) busy.Add(d);
        }

        CalendarDays.Children.Clear();
        var start = month.AddDays(-(((int)month.DayOfWeek - (int)firstDay + 7) % 7));
        var weeks = (int)Math.Ceiling(((month - start).Days + DateTime.DaysInMonth(month.Year, month.Month)) / 7.0);
        for (var i = 0; i < weeks * 7; i++)
        {
            var date = start.AddDays(i);
            var inMonth = date.Month == month.Month;
            var isToday = date == today;
            var isSelected = date == _calendarSelected;

            var cell = new Grid { Height = 26, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            cell.Children.Add(new Ellipse
            {
                Width = 22,
                Height = 22,
                Fill = isToday ? accent : isSelected ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) : null,
                Stroke = isSelected && isToday ? Brushes.White : null,
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
            });
            cell.Children.Add(new TextBlock
            {
                Text = date.Day.ToString(),
                FontSize = 11,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = isToday ? Brushes.White : inMonth ? Brushes.White : secondary,
                Opacity = inMonth || isToday ? 1 : 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3.5, 0, 0),
            });
            if (busy.Contains(date) && !isToday)
                cell.Children.Add(new Ellipse
                {
                    Width = 3.5,
                    Height = 3.5,
                    Fill = inMonth ? accent : secondary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 0.5),
                });
            cell.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true; // don't collapse the island
                _calendarSelected = _calendarSelected == date ? null : date;
                if (date.Month != _calendarMonth.Month || date.Year != _calendarMonth.Year)
                    _calendarMonth = new DateTime(date.Year, date.Month, 1);
                BuildCalendar();
            };
            CalendarDays.Children.Add(cell);
        }
    }

    /// <summary>The Calendar page: grouped by day, with "now" / "in N min" and a Join button for online meetings.</summary>
    private void BuildCalendar()
    {
        var secondary = (Brush)FindResource("Secondary");
        var now = DateTime.Now;
        BuildMonth();
        CalendarList.Children.Clear();

        // A picked day lists everything on it (past events too); otherwise the upcoming week.
        var items = _calendarSelected is { } selected
            ? _calendar.Items.Where(i => OccursOn(i, selected)).ToList()
            : _calendar.Items.Where(i => (i.End > now || (i.AllDay && OccursOn(i, now.Date)))
                                         && i.Start < now.Date.AddDays(CalendarService.DaysAhead)).ToList();

        string? empty = null;
        if (!_calendar.HasSources)
            empty = PackageIdentity.HasIdentity || !_settings.WindowsCalendar
                ? "Add a calendar in Settings > Calendar"
                : "Add an iCal link in Settings > Calendar, or set up access to the Windows calendar there";
        else if (items.Count == 0)
            empty = _calendarSelected is { } day0 ? $"Nothing on {day0:dddd d MMM}" : "Nothing in the next 7 days";
        if (_calendar.Problem is { } problem) empty = empty is null ? problem : $"{empty}\n{problem}";
        CalendarEmpty.Text = empty ?? "";
        CalendarEmpty.Visibility = empty is null ? Visibility.Collapsed : Visibility.Visible;
        CalendarScroller.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        DateTime? day = null;
        foreach (var item in items.Take(40))
        {
            var itemDay = _calendarSelected
                          ?? (item.AllDay || item.Start < now ? (item.Start < now.Date ? now.Date : item.Start.Date) : item.Start.Date);
            if (itemDay != day)
            {
                day = itemDay;
                CalendarList.Children.Add(new TextBlock
                {
                    Text = itemDay == now.Date ? "TODAY" : itemDay == now.Date.AddDays(1) ? "TOMORROW" : itemDay.ToString("dddd d MMM").ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = secondary,
                    Margin = new Thickness(0, CalendarList.Children.Count == 0 ? 0 : 8, 0, 5),
                });
            }

            var color = item.Color is { } c ? new SolidColorBrush(c) : (Brush)FindResource("Orange");
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = color, Margin = new Thickness(0, 1, 10, 1) });

            var time = item.AllDay ? "All day" : $"{item.Start:t} – {item.End:t}";
            var text = new StackPanel { Children =
            {
                new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    Text = item.Location is { } location && item.JoinUrl is null ? $"{time}  ·  {location}" : time,
                    FontSize = 11,
                    Foreground = secondary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            } };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (item.IsNow(now))
                right.Children.Add(new TextBlock { Text = "Now", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
            else if (!item.AllDay && item.Start > now && item.Start - now < TimeSpan.FromHours(1))
                right.Children.Add(new TextBlock { Text = $"in {(int)Math.Ceiling((item.Start - now).TotalMinutes)} min", FontSize = 11, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
            if (item.JoinUrl is { } url && (item.IsNow(now) || (item.Start > now && item.Start - now < TimeSpan.FromHours(12))))
            {
                var join = new Button { Style = (Style)FindResource("ChipButton"), Content = "Join", Margin = new Thickness(8, 0, 0, 0), ToolTip = url };
                join.Click += (_, _) =>
                {
                    _hoverSuppressed = true;
                    SetExpanded(false);
                    OpenPath(url);
                };
                right.Children.Add(join);
            }
            Grid.SetColumn(right, 2);
            row.Children.Add(right);
            CalendarList.Children.Add(row);
        }

        if (IsLoaded && _state.IsExpanded && _page == ExpandedPage.Calendar) Render();
    }

    // ---------------------------------------------------------------- file shelf

    private string? _shelfKey;
    private bool _draggingOut;
    private Point? _dragStart;

    private void InitShelf()
    {
        _shelf.Changed += BuildShelf;
        Root.AllowDrop = true;
        Root.DragEnter += OnShelfDragOver;
        Root.DragOver += OnShelfDragOver;
        Root.DragLeave += OnShelfDragLeave;
        Root.Drop += OnShelfDrop;
        StartDragOnMove(ShelfDragAll, () => _shelf.Items.ToList());
    }

    /// <summary>Files dragged over the island: open it on the Shelf page and offer to keep them.</summary>
    private void OnShelfDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_draggingOut || !_settings.ShelfEnabled || IslandHidden || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        // "Link" is honest (the shelf keeps a reference, not a copy); fall back to Copy if not offered.
        e.Effects = e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.Copy;

        _hoverTimer.Stop();
        if (!_state.IsExpanded) SetExpanded(true);
        if (_page != ExpandedPage.Shelf) ShowPage(ExpandedPage.Shelf, animate: true);
        if (ShelfDropOverlay.Visibility != Visibility.Visible)
        {
            ShelfDropRect.Stroke = _accent is { } accent ? new SolidColorBrush(accent) : Brushes.White;
            ShelfDropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void OnShelfDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires moving between elements inside the island; only react on really leaving.
        if (!IsCursorOverPill()) ShelfDropOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnShelfDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ShelfDropOverlay.Visibility = Visibility.Collapsed;
        if (_draggingOut || !_settings.ShelfEnabled || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        _shelf.Add(paths);
    }

    private void OnShelfClearClick(object sender, RoutedEventArgs e) => _shelf.Clear();

    /// <summary>Rebuilds the shelf tiles; thumbnails load afterwards at background priority.</summary>
    private void BuildShelf()
    {
        var f = Scale * VisualTreeHelper.GetDpi(this).DpiScaleX;
        var key = $"{f}\n{string.Join('\n', _shelf.Items)}";
        if (key == _shelfKey) return;
        _shelfKey = key;

        var items = _shelf.Items;
        ShelfItems.Children.Clear();
        ShelfEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfToolbar.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShelfScroller.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShelfCount.Text = items.Count == 1 ? "1 item" : $"{items.Count} items";

        const double tileWidth = 76, thumb = 44;
        var pixels = (int)Math.Round(thumb * f);
        var secondary = (Brush)FindResource("Secondary");
        foreach (var path in items)
        {
            var exists = System.IO.File.Exists(path) || System.IO.Directory.Exists(path);
            var image = new Image { Width = thumb, Height = thumb, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            var name = new TextBlock
            {
                Text = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } fileName ? fileName : path,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                MaxWidth = tileWidth - 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = secondary,
            };
            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 20,
                Height = 20,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x3A, 0x3A, 0x3C)),
                Visibility = Visibility.Hidden,
                ToolTip = "Remove from the shelf",
                Content = new TextBlock { Style = (Style)FindResource("Glyph"), Text = "", FontSize = 8 },
            };
            remove.Click += (_, _) => _shelf.Remove(path);

            var tile = new Grid
            {
                Width = tileWidth,
                Height = thumb + 34,
                Background = Brushes.Transparent, // hit-testable for dragging
                Opacity = exists ? 1 : 0.4,
                ToolTip = exists ? path : $"{path}\n(missing: moved or deleted)",
                Cursor = Cursors.Hand,
                Children =
                {
                    new StackPanel
                    {
                        Margin = new Thickness(0, 4, 0, 0),
                        Children =
                        {
                            // Generic document glyph behind the thumbnail: shows for missing files and
                            // until the thumbnail has loaded.
                            new Grid
                            {
                                Width = thumb,
                                Height = thumb,
                                Children =
                                {
                                    new TextBlock { Style = (Style)FindResource("Glyph"), Text = "", FontSize = 26, Foreground = secondary },
                                    image,
                                },
                            },
                            name,
                        },
                    },
                    remove,
                },
            };
            tile.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
            tile.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
            tile.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2 && exists) OpenPath(path);
            };
            tile.MouseLeftButtonUp += (_, e) => e.Handled = true; // a click on a tile doesn't collapse the island
            StartDragOnMove(tile, () => new List<string> { path });
            ShelfItems.Children.Add(tile);

            // Thumbnails (video, large photos) can take a moment; don't hold up the layout for them.
            if (exists)
            {
                var glyph = (UIElement)((Grid)image.Parent).Children[0];
                Dispatcher.InvokeAsync(() =>
                {
                    image.Source = QuickAppsService.GetFileImage(path, pixels);
                    if (image.Source is not null) glyph.Visibility = Visibility.Collapsed;
                }, DispatcherPriority.Background);
            }
        }

        if (IsLoaded && _state.IsExpanded && _page == ExpandedPage.Shelf) Render();
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    /// <summary>Starts a file drag from <paramref name="source"/> once the mouse moves far enough with the button down.</summary>
    private void StartDragOnMove(UIElement source, Func<List<string>> paths)
    {
        source.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(Root);
        source.PreviewMouseMove += (_, e) =>
        {
            if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _draggingOut) return;
            var delta = e.GetPosition(Root) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _dragStart = null;
            DragOut(source, paths());
        };
        source.PreviewMouseLeftButtonUp += (_, _) => _dragStart = null;
    }

    /// <summary>
    /// Drags files out of the shelf to Explorer, an app, a chat… A successful drop removes them from
    /// the shelf (unless the setting keeps them); files that were moved away are removed regardless.
    /// </summary>
    private void DragOut(UIElement source, List<string> paths)
    {
        paths = paths.Where(p => System.IO.File.Exists(p) || System.IO.Directory.Exists(p)).ToList();
        if (paths.Count == 0) return;
        _draggingOut = true;
        DragDropEffects result;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
            result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _draggingOut = false;
        }
        if (result != DragDropEffects.None && !_settings.ShelfKeepAfterDrag)
            foreach (var path in paths) _shelf.Remove(path);
        _shelf.RemoveMissing();
    }

    // ---------------------------------------------------------------- weather

    /// <summary>Starts / stops the service, and refetches when the location or unit changes.</summary>
    private void UpdateWeatherService()
    {
        if (!IsLoaded) return;
        if (!_settings.WeatherEnabled)
        {
            _weather.Stop();
            _weatherKey = null;
            return;
        }
        var s = _settings;
        var key = $"{s.WeatherUseDeviceLocation}|{s.WeatherLatitude}|{s.WeatherLongitude}|{s.TemperatureUnit}";
        if (key == _weatherKey) return;
        var first = _weatherKey is null;
        _weatherKey = key;
        if (first)
        {
            UpdateWeather(); // "Loading weather…" until the first report
            _weather.Start();
        }
        else
        {
            _weather.Refresh();
        }
    }

    private static string Degrees(double value) => $"{Math.Round(value):0}°";

    /// <summary>Fills the Weather page from the latest report.</summary>
    private void UpdateWeather()
    {
        var report = _weather.Current;
        WeatherProblemText.Text = _weather.Problem ?? "";
        WeatherProblemText.Visibility = _weather.Problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (report is null)
        {
            WeatherNowIcon.Child = null;
            WeatherNowTemp.Text = "";
            WeatherCondition.Text = _weather.Problem is null ? "Loading weather…" : "No weather";
            WeatherDetail.Text = WeatherPlaceText.Text = "";
            WeatherHours.Children.Clear();
            WeatherDays.Children.Clear();
            if (_state.IsExpanded && _page == ExpandedPage.Weather) Render();
            return;
        }

        var secondary = (Brush)FindResource("Secondary");
        WeatherNowIcon.Child = WeatherIcons.Create(report.Kind, report.IsDay, 50);
        WeatherNowTemp.Text = Degrees(report.Temperature);
        WeatherCondition.Text = report.Condition;
        var today = report.Days.FirstOrDefault();
        WeatherDetail.Text = today is null
            ? $"Feels like {Degrees(report.FeelsLike)}"
            : $"H {Degrees(today.Max)}  L {Degrees(today.Min)}  ·  Feels like {Degrees(report.FeelsLike)}";
        WeatherPlaceText.Text = report.Place;

        // Every second hour for the next 12: time, icon, temperature.
        WeatherHours.Children.Clear();
        var hourFormat = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H') ? "HH:mm" : "h tt";
        foreach (var hour in report.Hours.Where((_, i) => i % 2 == 0).Take(6))
        {
            WeatherHours.Children.Add(new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = hour.Time.ToString(hourFormat), FontSize = 11, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Center },
                    Centered(WeatherIcons.Create(hour.Kind, hour.IsDay, 22), new Thickness(0, 5, 0, 5)),
                    new TextBlock { Text = Degrees(hour.Temperature), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
        }

        // Days: name, icon, low, a range bar placed within the week's span (star columns, no measuring), high.
        WeatherDays.Children.Clear();
        var weekMin = report.Days.Min(d => d.Min);
        var span = Math.Max(1, report.Days.Max(d => d.Max) - weekMin);
        var fill = _accent is { } accent ? new SolidColorBrush(accent) : (Brush)FindResource("Orange");
        foreach (var day in report.Days)
        {
            var row = new Grid { Height = 24 };
            foreach (var width in new[] { 64.0, 30, 38 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

            var name = new TextBlock
            {
                Text = day.Date.Date == DateTime.Today ? "Today" : day.Date.ToString("ddd"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var icon = Centered(WeatherIcons.Create(day.Kind, true, 18), new Thickness(0));
            var low = new TextBlock { Text = Degrees(day.Min), FontSize = 13, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var high = new TextBlock { Text = Degrees(day.Max), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

            var bar = new Grid { Height = 4, Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            var before = (day.Min - weekMin) / span;
            var width2 = Math.Max(0.04, (day.Max - day.Min) / span);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(before, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width2, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - before - width2), GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) };
            Grid.SetColumnSpan(track, 3);
            var range = new Border { CornerRadius = new CornerRadius(2), Background = fill };
            Grid.SetColumn(range, 1);
            bar.Children.Add(track);
            bar.Children.Add(range);

            Grid.SetColumn(icon, 1);
            Grid.SetColumn(low, 2);
            Grid.SetColumn(bar, 3);
            Grid.SetColumn(high, 4);
            row.Children.Add(name);
            row.Children.Add(icon);
            row.Children.Add(low);
            row.Children.Add(bar);
            row.Children.Add(high);
            WeatherDays.Children.Add(row);
        }

        if (_state.IsExpanded && _page == ExpandedPage.Weather) Render(); // height may change
    }

    private static FrameworkElement Centered(FrameworkElement element, Thickness margin)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
        element.Margin = margin;
        return element;
    }

    // ---------------------------------------------------------------- quick apps

    private string? _quickAppsKey;

    /// <summary>Rebuilds the Apps page's tiles (icon + name) when the app list or icon size changes.</summary>
    private void BuildQuickApps()
    {
        var apps = _settings.QuickApps.Take(AppSettings.MaxQuickApps).ToList();
        var dipSize = Math.Clamp(_settings.QuickAppIconSize, 16, 48);
        var f = Scale * VisualTreeHelper.GetDpi(this).DpiScaleX; // design units → screen pixels
        var key = $"{dipSize}|{f}\n{string.Join('\n', apps)}"; // icons are rendered for the size
        if (key == _quickAppsKey) return;
        _quickAppsKey = key;

        // Draw icons 1:1 in screen pixels at a standard icon size: any scaling on the way to the
        // screen makes them soft.
        var pixels = QuickAppsService.SnapSize(dipSize * f);
        var shown = pixels / f;
        var tileWidth = Math.Round(Math.Max(dipSize + 24, 74));
        var tileHeight = Math.Round(dipSize + 36); // icon + name

        QuickAppsRow.Children.Clear();
        QuickAppsRow.UseLayoutRounding = true;
        foreach (var id in apps)
        {
            if (QuickAppsService.GetIcon(id, pixels) is not { } icon) continue; // uninstalled
            var image = new Image { Source = icon, Width = shown, Height = shown, Stretch = Stretch.Fill };
            var button = AppTile(image, QuickAppsService.GetName(id) ?? "", tileWidth, tileHeight);
            button.Click += (_, _) =>
            {
                _hoverSuppressed = true; // don't re-open under the cursor as the island shrinks
                SetExpanded(false);
                QuickAppsService.Launch(id);
            };
            QuickAppsRow.Children.Add(button);
        }

        // "+" to pick apps in Settings.
        if (QuickAppsRow.Children.Count < AppSettings.MaxQuickApps)
        {
            var add = AppTile(new Grid
                {
                    Width = shown,
                    Height = shown,
                    Children = { new TextBlock { Style = (Style)FindResource("Glyph"), Text = "", FontSize = Math.Max(14, dipSize * 0.5), Foreground = (Brush)FindResource("Secondary") } },
                }, "Add", tileWidth, tileHeight);
            add.Click += (_, _) => OpenSettings();
            QuickAppsRow.Children.Add(add);
        }

        // WrapPanel can't center its rows; give it exactly a whole number of tiles' width instead.
        var perRow = Math.Clamp((int)((ExpandedWidth - 36) / tileWidth), 1, Math.Max(1, QuickAppsRow.Children.Count));
        QuickAppsRow.Width = perRow * tileWidth;
        if (IsLoaded && _state.IsExpanded) Render();
    }

    private Button AppTile(FrameworkElement icon, string name, double width, double height)
    {
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        return new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = width,
            Height = height,
            ToolTip = name,
            Content = new StackPanel
            {
                Children =
                {
                    icon,
                    new TextBlock
                    {
                        Text = name,
                        FontSize = 11,
                        Margin = new Thickness(0, 5, 0, 0),
                        MaxWidth = width - 8,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = (Brush)FindResource("Secondary"),
                    },
                },
            },
        };
    }

    // ---------------------------------------------------------------- timer

    private void OnTimerTick()
    {
        var text = TimerService.Format(_timer.Remaining);
        TimerTextSmall.Text = text;
        TimerTextLarge.Text = text;
    }

    private void OnTimerStateChanged()
    {
        TimerPauseGlyph.Text = _timer.IsPaused ? "" : "";
        TimerTextSmall.Opacity = _timer.IsPaused ? 0.55 : 1;
        TimerTextLarge.Opacity = _timer.IsPaused ? 0.55 : 1;
        OnTimerTick();
        _state.TimerActive = _timer.IsActive;
        if (_state.IsExpanded) Render();
    }

    private void OnTimerFinished()
    {
        TimerDoneGlyph.Text = ""; // the view is shared with alarms
        TimerDoneText.Text = "Timer finished";
        SystemSounds.Exclamation.Play();
        _state.ShowTransient(IslandView.TimerDone, TransientPriority.TimerDone, TimeSpan.FromSeconds(5));
    }

    private void OnTimerPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var minutes))
            _timer.Start(TimeSpan.FromMinutes(minutes));
    }

    private void OnTimerAddClick(object sender, RoutedEventArgs e) => _timer.Add(TimeSpan.FromMinutes(1));
    private void OnTimerPauseClick(object sender, RoutedEventArgs e) => _timer.TogglePause();
    private void OnTimerCancelClick(object sender, RoutedEventArgs e) => _timer.Cancel();

    // ---------------------------------------------------------------- stopwatch & alarms

    private void OnStopwatchTick()
    {
        // Tenths only in the expanded view; the compact pill changes once a second.
        StopwatchTextSmall.Text = StopwatchService.Format(_stopwatch.Elapsed, tenths: false);
        if (_state.IsExpanded) StopwatchTextLarge.Text = StopwatchService.Format(_stopwatch.Elapsed, tenths: true);
    }

    private void OnStopwatchStateChanged()
    {
        StopwatchPauseGlyph.Text = _stopwatch.IsRunning ? "" : "";
        StopwatchTextSmall.Opacity = _stopwatch.IsRunning ? 1 : 0.55;
        StopwatchTextLarge.Opacity = _stopwatch.IsRunning ? 1 : 0.55;
        StopwatchTextLarge.Text = StopwatchService.Format(_stopwatch.Elapsed, tenths: true);
        OnStopwatchTick();
        _state.StopwatchActive = _stopwatch.IsActive;
        if (_state.IsExpanded) Render();
    }

    private void OnStopwatchStartClick(object sender, RoutedEventArgs e) => _stopwatch.Start();
    private void OnStopwatchPauseClick(object sender, RoutedEventArgs e) => _stopwatch.TogglePause();
    private void OnStopwatchResetClick(object sender, RoutedEventArgs e) => _stopwatch.Reset();

    /// <summary>Rings until clicked (or a minute passes): looping alarm sound and the alert pill.</summary>
    private void OnAlarmRing(Alarm alarm)
    {
        TimerDoneGlyph.Text = "";
        TimerDoneText.Text = string.IsNullOrWhiteSpace(alarm.Label) ? $"Alarm  {alarm.Time}" : $"{alarm.Label}  {alarm.Time}";
        try
        {
            var file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
            _alarmSound?.Stop();
            _alarmSound = System.IO.File.Exists(file) ? new System.Media.SoundPlayer(file) : null;
            if (_alarmSound is not null) _alarmSound.PlayLooping();
            else SystemSounds.Exclamation.Play();
        }
        catch (Exception) { SystemSounds.Exclamation.Play(); }

        if (IslandHidden) SetIslandHidden(false); // an alarm must be seen
        _state.ShowTransient(IslandView.TimerDone, TransientPriority.Alarm, TimeSpan.FromMinutes(1));
    }

    private void StopAlarmSound()
    {
        _alarmSound?.Stop();
        _alarmSound = null;
    }

    // ---------------------------------------------------------------- status alerts

    private void OnStatusChanged(StatusChange change)
    {
        if (!_settings.StatusAlerts) return;
        var green = (Brush)FindResource("Green");
        var red = FrozenBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        var secondary = (Brush)FindResource("Secondary");
        switch (change.Kind)
        {
            case StatusKind.MicMuted:
                ShowInfo(TransientPriority.LockKey, 250, "", "Microphone", "Muted", detailBrush: red, glyphBrush: red, seconds: 1.8);
                break;
            case StatusKind.MicUnmuted:
                ShowInfo(TransientPriority.LockKey, 250, "", "Microphone", "On", detailBrush: green, seconds: 1.8);
                break;
            case StatusKind.EnergySaverOn:
                ShowInfo(TransientPriority.Battery, 260, "", "Energy Saver", "On", detailBrush: green, glyphBrush: green);
                break;
            case StatusKind.EnergySaverOff:
                ShowInfo(TransientPriority.Battery, 260, "", "Energy Saver", "Off");
                break;
            case StatusKind.WifiConnected:
                ShowInfo(TransientPriority.Device, 320, "", change.Detail ?? "Wi-Fi", "Connected", detailBrush: green);
                break;
            case StatusKind.WifiDisconnected:
                ShowInfo(TransientPriority.Device, 320, "", change.Detail ?? "Wi-Fi", "Disconnected", glyphBrush: secondary);
                break;
            case StatusKind.VpnConnected:
                ShowInfo(TransientPriority.Device, 320, "", change.Detail ?? "VPN", "VPN on", detailBrush: green);
                break;
            case StatusKind.VpnDisconnected:
                ShowInfo(TransientPriority.Device, 320, "", change.Detail ?? "VPN", "VPN off", glyphBrush: secondary);
                break;
            case StatusKind.Offline:
                ShowInfo(TransientPriority.Device, 280, "", "No internet", null, glyphBrush: red);
                break;
        }
    }

    // ---------------------------------------------------------------- second activity bubble

    /// <summary>
    /// The second running activity (music while a timer runs, …) as a bubble beside the collapsed pill:
    /// album art for music, a timer / stopwatch glyph otherwise. Hidden while expanded or during alerts.
    /// </summary>
    private Artwork? _bubbleArt;

    private void UpdateSecondActivity()
    {
        var secondary = _settings.ShowSecondActivity && !_state.IsExpanded && _state.Transient is null
            ? _state.SecondaryView
            : null;
        var show = secondary is not null;
        var wasShown = ActivityBubble.Visibility == Visibility.Visible;

        if (secondary == IslandView.Media)
        {
            // Build the brush only when the artwork changes (state changes are frequent; animated
            // artwork would restart on every new brush).
            if (!ReferenceEquals(_bubbleArt, _shownArt))
            {
                _bubbleArt = _shownArt;
                ActivityBubbleArt.Fill = _shownArt?.CreateBrush();
            }
            var art = ActivityBubbleArt.Fill;
            ActivityBubbleArt.Visibility = art is null ? Visibility.Collapsed : Visibility.Visible;
            ActivityBubbleGlyph.Text = ""; // music note, if no art
            ActivityBubbleGlyph.Foreground = (Brush)FindResource("Secondary");
            ActivityBubbleGlyph.Visibility = art is null ? Visibility.Visible : Visibility.Collapsed;
            ActivityBubble.ToolTip = _media.Current?.Title;
        }
        else if (secondary is { } activity)
        {
            ActivityBubbleArt.Visibility = Visibility.Collapsed;
            ActivityBubbleGlyph.Visibility = Visibility.Visible;
            ActivityBubbleGlyph.Text = "";
            ActivityBubbleGlyph.Foreground = (Brush)FindResource(activity == IslandView.Timer ? "Orange" : "Cyan");
            ActivityBubble.ToolTip = activity == IslandView.Timer ? TimerService.Format(_timer.Remaining) : "Stopwatch";
        }

        if (show == wasShown) return;
        ActivityBubble.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            // Pops out of the pill's edge, like the iPhone's island splitting in two.
            var time = TimeSpan.FromMilliseconds(320 / SpeedFactor);
            var pop = new DoubleAnimation(0.2, 1, time) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(pop, FrameRateCap);
            var scale = (ScaleTransform)ActivityBubble.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        ApplyGeometry(); // position, and move the privacy dot past it
    }

    /// <summary>Opens the app that's playing (click on the collapsed music activity, when enabled).</summary>
    private void OpenMediaApp()
    {
        if (_media.Current?.AppId is not { Length: > 0 } appId) return;
        if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Desktop players (Spotify.exe, …): bring the running window forward. The click on the
            // island is the user input that lets this process hand over the foreground.
            foreach (var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(appId)))
            {
                var hwnd = process.MainWindowHandle;
                process.Dispose();
                if (hwnd == IntPtr.Zero) continue;
                if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9 /* SW_RESTORE */);
                SetForegroundWindow(hwnd);
                return;
            }
        }
        QuickAppsService.Launch(appId); // Store apps (Apple Music, …) by AppUserModelID
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    // ---------------------------------------------------------------- media

    private void OnMediaChanged()
    {
        var media = _media.Current;
        if (media is not null)
        {
            var title = string.IsNullOrWhiteSpace(media.Title) ? "Unknown title" : media.Title;

            MediaTitleSmall.Text = title;
            MediaTitleLarge.Text = title;
            MediaArtistLarge.Text = string.IsNullOrEmpty(media.Album) ? media.Artist : $"{media.Artist} — {media.Album}";
            MediaAppName.Text = media.AppName;
            if (!ReferenceEquals(media.Art, _shownArt))
            {
                // Only rebuild brushes on new artwork, so animated art doesn't restart on every update.
                _shownArt = media.Art;
                SetArt(MediaArtSmall, MediaArtSmallFallback, media.Art);
                SetArt(MediaArtLarge, MediaArtLargeFallback, media.Art);
                ArtBanner.Background = media.Art?.CreateBrush();
            }
            PlayPauseGlyph.Text = media.IsPlaying ? "" : "";
            PrevButton.IsEnabled = media.CanPrevious;
            NextButton.IsEnabled = media.CanNext;
            ProgressHit.Cursor = media.CanSeek ? Cursors.Hand : null;
        }

        _state.MediaPlaying = media?.IsPlaying == true;
        UpdateSecondActivity(); // the bubble may show this artwork
        UpdateMotionArtwork(media);
        ApplyTheme();
        UpdateEqualizer();
        UpdateWallpaperMute();
        if (_state.IsExpanded) Render();
    }

    /// <summary>
    /// Animate the bars only while they're on screen: any running WPF animation keeps the whole window
    /// re-rendering every frame, which competes with Wallpaper Engine for the GPU.
    /// </summary>
    private void UpdateEqualizer() =>
        SetEqualizerRunning(_media.Current?.IsPlaying == true && _currentView == MediaView && !IslandHidden);

    private void UpdateProgress()
    {
        if (_media.Current is not { Duration.Ticks: > 0 } media) return;

        var position = media.EstimatePosition(DateTimeOffset.Now);
        ElapsedText.Text = FormatMediaTime(position);
        RemainingText.Text = "-" + FormatMediaTime(media.Duration - position);
        ((ScaleTransform)ProgressFill.RenderTransform).ScaleX = position / media.Duration;
    }

    private void OnProgressMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // don't collapse the island
        if (_media.Current is not { CanSeek: true, Duration.Ticks: > 0 } media || ProgressHit.ActualWidth <= 0) return;

        var fraction = Math.Clamp(e.GetPosition(ProgressHit).X / ProgressHit.ActualWidth, 0, 1);
        ((ScaleTransform)ProgressFill.RenderTransform).ScaleX = fraction;
        _media.Seek(media.Duration * fraction);
    }

    private static string FormatMediaTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void SetArt(Border target, TextBlock fallback, Artwork? art)
    {
        target.Background = art is null
            ? (Brush)FindResource("ArtPlaceholder")
            : art.CreateBrush();
        fallback.Visibility = art is null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- real waveform: bars follow the output level (left / right peaks), 30 fps while visible.
    private readonly AudioMeter _meter = new();
    private readonly DispatcherTimer _meterTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly double[] _barLevels = new double[4];
    private bool _meterRunning;

    /// <summary>
    /// One frame of the real waveform. Peaks are mapped to a -48..0 dB scale; each bar gets its own
    /// mix of left / right and its own attack and decay plus a little phase wobble, so four bars from a
    /// stereo peak meter read like a spectrum (as the iPhone's waveform does, it isn't a true one).
    /// </summary>
    private void OnMeterTick(object? sender, EventArgs e)
    {
        var (left, right) = _meter.Read();
        static double Db(float peak) => peak <= 0.0001f ? 0 : Math.Clamp((20 * Math.Log10(peak) + 48) / 48, 0, 1);
        var l = Db(left);
        var r = Db(right);
        var t = _frameClock.Elapsed.TotalSeconds;
        var i = 0;
        foreach (UIElement bar in Equalizer.Children)
        {
            var mix = i switch { 0 => l, 1 => 0.6 * l + 0.4 * r, 2 => 0.4 * l + 0.6 * r, _ => r };
            var wobble = 0.82 + 0.18 * Math.Sin(t * (7.3 + 2.9 * i) + i * 1.7);
            var target = Math.Clamp(mix * wobble, 0, 1);
            // Fast attack, slower release, different per bar.
            var rate = target > _barLevels[i] ? 0.65 : 0.18 + 0.05 * i;
            _barLevels[i] += (target - _barLevels[i]) * rate;
            ((ScaleTransform)bar.RenderTransform).ScaleY = 0.22 + 0.78 * _barLevels[i];
            i++;
        }
    }

    private void SetMeterRunning(bool running)
    {
        if (running == _meterRunning) return;
        _meterRunning = running;
        if (running)
        {
            _meterTimer.Tick -= OnMeterTick;
            _meterTimer.Tick += OnMeterTick;
            _meterTimer.Start();
        }
        else
        {
            _meterTimer.Stop();
        }
    }

    private void SetEqualizerRunning(bool running)
    {
        // Real waveform: a meter-driven loop instead of the looping animation.
        var real = running && _settings.RealWaveform;
        var meterWasRunning = _meterRunning;
        SetMeterRunning(real);
        if (meterWasRunning && !real)
        {
            Array.Clear(_barLevels);
            foreach (UIElement bar in Equalizer.Children)
                if (bar.RenderTransform is ScaleTransform rest) rest.ScaleY = 0.35;
        }
        if (real)
        {
            foreach (UIElement bar in Equalizer.Children)
            {
                if (bar.RenderTransform is not ScaleTransform scale)
                {
                    bar.RenderTransform = scale = new ScaleTransform(1, 0.35);
                    bar.RenderTransformOrigin = new Point(0.5, 0.5);
                }
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            _equalizerRunning = false; // the animated variant is off
            return;
        }

        if (running == _equalizerRunning) return;
        _equalizerRunning = running;

        var i = 0;
        foreach (UIElement bar in Equalizer.Children)
        {
            if (bar.RenderTransform is not ScaleTransform scale)
            {
                scale = new ScaleTransform(1, 0.35);
                bar.RenderTransform = scale;
                bar.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            if (running)
            {
                // Different periods per bar so they never line up.
                var period = TimeSpan.FromMilliseconds(380 + 110 * (i % 3) + 45 * i);
                var bars = new DoubleAnimation(0.3, 1.0, period)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                // Runs for as long as music plays: 30 fps looks the same for bars this size and
                // halves the redraws of the window.
                Timeline.SetDesiredFrameRate(bars, FrameRateCap / 2);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, bars);
            }
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = 0.35;
            }
            i++;
        }
    }

    // ---------------------------------------------------------------- motion artwork

    private readonly MotionArtworkService _motionArt = new();
    private CancellationTokenSource? _motionCancel;
    private string? _motionKey;
    private bool _motionReady;

    private void InitMotionArtwork()
    {
        MotionBanner.MediaOpened += (_, _) =>
        {
            _motionReady = true;
            MotionBanner.Visibility = Visibility.Visible;
            // The large artwork square mirrors the same video (no second decoder).
            MediaArtLarge.Background = new VisualBrush(MotionBanner) { Stretch = Stretch.UniformToFill };
            MediaArtLargeFallback.Visibility = Visibility.Collapsed;
            if (_state.IsExpanded) Render(); // the banner may have just become visible
            UpdateMotionPlayback();
        };
        MotionBanner.MediaEnded += (_, _) =>
        {
            MotionBanner.Position = TimeSpan.Zero; // loop
            MotionBanner.Play();
        };
        MotionBanner.MediaFailed += (_, e) =>
        {
            App.LogError(e.ErrorException);
            ClearMotion();
        };
    }

    /// <summary>Starts a lookup when the album changes (cached albums resolve immediately).</summary>
    private void UpdateMotionArtwork(MediaInfo? media)
    {
        var key = media is null || !_settings.MotionArtwork
            ? null
            : $"{media.Artist}|{media.Album}|{(string.IsNullOrEmpty(media.Album) ? media.Title : "")}";
        if (key == _motionKey) return;

        _motionKey = key;
        _motionCancel?.Cancel();
        ClearMotion();
        if (key is null || media is null) return;

        var cancel = new CancellationTokenSource();
        _motionCancel = cancel;
        _ = LoadMotionAsync(media, key, cancel.Token);
    }

    private async Task LoadMotionAsync(MediaInfo media, string key, CancellationToken cancel)
    {
        string? file;
        try
        {
            file = await _motionArt.GetAsync(media.Artist, media.Album, media.Title, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancel.IsCancellationRequested || key != _motionKey || file is null) return;

        MotionBanner.Source = new Uri(file);
        MotionBanner.Play(); // opens the file; MediaOpened then pauses it unless the island is expanded
    }

    /// <summary>Decode video only while it can be seen.</summary>
    private void UpdateMotionPlayback()
    {
        if (!_motionReady) return;
        if (_state.IsExpanded && ArtBanner.Visibility == Visibility.Visible) MotionBanner.Play();
        else MotionBanner.Pause();
    }

    private void ClearMotion()
    {
        var wasShowing = _motionReady;
        _motionReady = false;
        MotionBanner.Stop();
        MotionBanner.Close();
        MotionBanner.Source = null;
        MotionBanner.Visibility = Visibility.Collapsed;
        if (wasShowing) SetArt(MediaArtLarge, MediaArtLargeFallback, _shownArt);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _media.Previous();
    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _media.TogglePlayPause();
    private void OnNextClick(object sender, RoutedEventArgs e) => _media.Next();

    // ---------------------------------------------------------------- Wallpaper Engine

    private void OnWallpaperChanged()
    {
        WallpaperPauseGlyph.Text = _wallpaper.IsPaused ? "" : "";
        WallpaperMuteGlyph.Text = _wallpaper.IsMuted ? "" : "";
        WallpaperTitle.Text = _wallpaper.Theme?.Title ?? "Wallpaper Engine";
        if (!ReferenceEquals(_wallpaper.Theme, _shownWallpaperTheme))
        {
            _shownWallpaperTheme = _wallpaper.Theme;
            UpdateWallpaperThumb();
        }
        if (!_wallpaper.IsRunning) _wallpaperMutedByUs = false;
        ApplyTheme();
        UpdateWallpaperMute();
        if (_state.IsExpanded) Render();
    }

    /// <summary>
    /// A still frame of the wallpaper preview, only while its row can be seen. (An animated 30px
    /// thumbnail kept WPF redrawing the whole expanded window at full frame rate: ~20% of a core.)
    /// </summary>
    private void UpdateWallpaperThumb()
    {
        var visible = _state.IsExpanded && _page == ExpandedPage.Wallpaper && _settings.WallpaperEngineControls
                      && _wallpaper.IsRunning;
        var preview = visible ? _wallpaper.Theme?.Preview : null;
        WallpaperThumb.Background = preview is null
            ? (Brush)FindResource("ArtPlaceholder")
            : new ImageBrush(preview.Still) { Stretch = Stretch.UniformToFill };
        WallpaperThumbFallback.Visibility = preview is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateWallpaperMute()
    {
        var shouldMute = _settings.MuteWallpaperDuringMedia && _media.Current?.IsPlaying == true;
        if (shouldMute)
        {
            _wallpaperUnmuteTimer.Stop();
            if (_wallpaperMutedByUs || _wallpaper.IsMuted || !_wallpaper.IsRunning) return;
            _wallpaperMutedByUs = true;
            _wallpaper.SetMuted(true);
        }
        else if (_wallpaperMutedByUs && !_wallpaperUnmuteTimer.IsEnabled)
        {
            _wallpaperUnmuteTimer.Start();
        }
    }

    /// <summary>Unmutes only if the island muted it, never overriding a mute the user chose.</summary>
    private void ReleaseWallpaperMute()
    {
        if (!_wallpaperMutedByUs) return;
        _wallpaperMutedByUs = false;
        _wallpaper.SetMuted(false);
    }

    private void OnWallpaperPauseClick(object sender, RoutedEventArgs e) => _wallpaper.TogglePause();

    private void OnWallpaperMuteClick(object sender, RoutedEventArgs e)
    {
        _wallpaperMutedByUs = false; // the user has taken over
        _wallpaperUnmuteTimer.Stop();
        _wallpaper.ToggleMute();
    }

    private void OnWallpaperNextClick(object sender, RoutedEventArgs e) => _wallpaper.Next();
}
