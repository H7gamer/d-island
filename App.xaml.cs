// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.IO;
using System.Windows;
using System.Windows.Threading;
using DIsland.Services;

namespace DIsland;

public partial class App : Application
{
    private const string MutexName = @"Local\DIsland.SingleInstance";
    private const string OpenSettingsEventName = @"Local\DIsland.OpenSettings";

    private Mutex? _singleInstance;
    private EventWaitHandle? _openSettingsSignal;
    private RegisteredWaitHandle? _openSettingsWait;
    private AppSettings? _settings;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;

    /// <summary>The updater, shared by the island (alerts, installing) and Settings (status, "Check now").</summary>
    public static UpdateService? Updates { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--probe-gpu-blur", StringComparer.OrdinalIgnoreCase))
        {
            // Throwaway child process; see GpuBlurBackdrop.ProbeExitCode.
            Shutdown(GpuBlurBackdrop.ProbeExitCode());
            return;
        }

        _singleInstance = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Launching again while running opens the settings of the running instance.
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEventName))
                signal.Set();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

        _settings = AppSettings.Load();
        Updates = new UpdateService(_settings);
        _window = new MainWindow(_settings);
        _window.SettingsRequested += () => Dispatcher.InvokeAsync(OpenSettings, DispatcherPriority.Background);
        _window.Show();

        // Defer until the tray menu has fully closed; opening a window mid-dismissal lets the menu's
        // focus handling push it behind other windows.
        _tray = new TrayIcon(_settings,
            () => Dispatcher.InvokeAsync(OpenSettings, DispatcherPriority.Background),
            () => _window.IslandHidden,
            hidden => _window.SetIslandHidden(hidden),
            Shutdown);

        _openSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OpenSettingsEventName);
        _openSettingsWait = ThreadPool.RegisterWaitForSingleObject(_openSettingsSignal,
            (_, _) => Dispatcher.InvokeAsync(OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) OpenSettings();
#if DEBUG
        if (Environment.GetEnvironmentVariable("DISLAND_SETTINGS_SNAPSHOT") is { Length: > 0 } settingsShot)
        {
            // Debug aid: open Settings and render it to a PNG after 3 s (works without a visible screen).
            OpenSettings();
            if (Environment.GetEnvironmentVariable("DISLAND_SETTINGS_SCROLL") == "end")
                _ = Task.Delay(2000).ContinueWith(_ => (_settingsWindow!.FindName("SettingsScroller") as System.Windows.Controls.ScrollViewer)?.ScrollToEnd(),
                    TaskScheduler.FromCurrentSynchronizationContext());
            _ = Task.Delay(3000).ContinueWith(_ =>
            {
                var window = _settingsWindow!;
                var content = (System.Windows.FrameworkElement)window.Content;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                    dc.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                }
                bitmap.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = System.IO.File.Create(settingsShot);
                encoder.Save(file);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
#endif
    }

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        BringToFront(_settingsWindow);
    }

    private WhatsNewWindow? _whatsNewWindow;

    /// <summary>
    /// What's new since <paramref name="updatedFrom"/> (after an update), or in this version (from
    /// Settings, null). Nothing to show (a version without notes) shows nothing.
    /// </summary>
    public void ShowWhatsNew(Version? updatedFrom)
    {
        var releases = WhatsNew.Since(updatedFrom);
        if (releases.Count == 0) releases = WhatsNew.Since(null).Take(1).ToList();
        if (releases.Count == 0) return;
        _whatsNewWindow?.Close();
        _whatsNewWindow = new WhatsNewWindow(releases, updatedFrom);
        _whatsNewWindow.Closed += (_, _) => _whatsNewWindow = null;
        BringToFront(_whatsNewWindow);
    }

    /// <summary>
    /// Activate() alone is ignored by Windows' foreground lock when we're a background app, which
    /// leaves the window buried behind whatever is maximized. A brief Topmost toggle always raises it.
    /// </summary>
    private static void BringToFront(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        e.Handled = true; // keep the island alive; the failure is in the log
    }

    public static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DIsland");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:u}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _openSettingsWait?.Unregister(null);
        _openSettingsSignal?.Dispose();
        _settingsWindow?.Close();
        _whatsNewWindow?.Close();
        _tray?.Dispose();
        _window?.Close();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
