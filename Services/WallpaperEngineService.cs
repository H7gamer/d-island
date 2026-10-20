// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DIsland.Services;

/// <summary>The current wallpaper's identity and colors, read from its project.json and preview.</summary>
public sealed record WallpaperTheme(string Title, Color? Accent, Artwork? Preview);

/// <summary>
/// Detects a running Wallpaper Engine, drives it through its documented command-line controls
/// (<c>wallpaper64.exe -control pause|play|mute|unmute|nextWallpaper</c>), and follows the selected
/// wallpaper (via Wallpaper Engine's config.json) to expose its theme.
/// Wallpaper Engine doesn't report pause/mute state back, so those reflect what the island last sent.
/// </summary>
public sealed class WallpaperEngineService : IDisposable
{
    private static readonly string[] ProcessNames = { "wallpaper64", "wallpaper32" };

    // Only looks for Wallpaper Engine while it isn't running; once found, its exit is an event.
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _configDebounce = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private Process? _process;
    private FileSystemWatcher? _configWatcher;
    private string? _exePath;
    private DateTime _configStamp;
    private string? _projectDir;
    private int _themeVersion;

    public WallpaperEngineService()
    {
        _poll.Tick += (_, _) => Poll();
        _configDebounce.Tick += (_, _) =>
        {
            _configDebounce.Stop();
            if (IsRunning) CheckSelectedWallpaper();
        };
    }

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsMuted { get; private set; }
    public WallpaperTheme? Theme { get; private set; }

    /// <summary>Running state, theme, or the island's view of paused/muted changed.</summary>
    public event Action? Changed;

    public void Start()
    {
        Poll();
        _poll.Start();
    }

    public void Dispose()
    {
        _poll.Stop();
        _configDebounce.Stop();
        _configWatcher?.Dispose();
        _process?.Dispose();
    }

    public void TogglePause()
    {
        if (Send(IsPaused ? "play" : "pause")) IsPaused = !IsPaused;
        Changed?.Invoke();
    }

    public void ToggleMute() => SetMuted(!IsMuted);

    public void SetMuted(bool muted)
    {
        if (muted == IsMuted) return;
        if (Send(muted ? "mute" : "unmute")) IsMuted = muted;
        Changed?.Invoke();
    }

    /// <summary>Only has an effect when a playlist is active in Wallpaper Engine.</summary>
    public void Next() => Send("nextWallpaper");

    private void Poll()
    {
        var running = _process is not null && !HasExited(_process);
        if (!running)
        {
            _process?.Dispose();
            _process = FindRunningProcess(out var path);
            running = _process is not null;
            if (running) _exePath = string.IsNullOrEmpty(path) ? FindInSteam() : path;
        }

        var changed = running != IsRunning;
        IsRunning = running;
        if (changed)
        {
            if (running)
            {
                WatchConfig();
                WatchExit();
            }
            else
            {
                IsPaused = false;
                IsMuted = false;
                Theme = null;
                _projectDir = null;
                _configStamp = default;
                _configWatcher?.Dispose();
                _configWatcher = null;
            }
        }

        // While running, exit and config changes are events; poll only as a slow safety net.
        _poll.Interval = TimeSpan.FromSeconds(running && _process is { EnableRaisingEvents: true } ? 30 : 5);
        if (running) CheckSelectedWallpaper();
        if (changed) Changed?.Invoke();
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (Exception) { return true; }
    }

    private void WatchExit()
    {
        try
        {
            _process!.EnableRaisingEvents = true;
            _process.Exited += (_, _) => _ui.InvokeAsync(Poll);
        }
        catch (Exception)
        {
            // Not allowed (e.g. elevated Wallpaper Engine): the 5s poll covers it.
        }
    }

    /// <summary>Wallpaper Engine rewrites config.json on wallpaper change; react immediately instead of polling.</summary>
    private void WatchConfig()
    {
        _configWatcher?.Dispose();
        _configWatcher = null;
        if (string.IsNullOrEmpty(_exePath)) return;
        try
        {
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(_exePath)!, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            FileSystemEventHandler onChange = (_, _) => _ui.InvokeAsync(() =>
            {
                _configDebounce.Stop();
                _configDebounce.Start();
            });
            watcher.Changed += onChange;
            watcher.Created += onChange;
            watcher.Renamed += (s, e) => onChange(s, e);
            watcher.EnableRaisingEvents = true;
            _configWatcher = watcher;
        }
        catch (Exception)
        {
            // Unwatchable folder: the safety poll still re-checks the config.
        }
    }

    /// <summary>A running Wallpaper Engine process (kept for exit events) and its exe path ("" if unreadable).</summary>
    private static Process? FindRunningProcess(out string? path)
    {
        path = null;
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            Process? keep = null;
            foreach (var p in processes)
            {
                if (keep is null)
                {
                    keep = p;
                    try { path = p.MainModule?.FileName ?? ""; }
                    catch (Exception) { path = ""; } // e.g. Wallpaper Engine running elevated
                }
                else
                {
                    p.Dispose();
                }
            }
            if (keep is not null) return keep;
        }
        return null;
    }

    private static string? FindInSteam()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is not string steam) return null;
            var exe = Path.Combine(steam, "steamapps", "common", "wallpaper_engine",
                Environment.Is64BitOperatingSystem ? "wallpaper64.exe" : "wallpaper32.exe");
            return File.Exists(exe) ? exe : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- theme

    /// <summary>Wallpaper Engine rewrites config.json when the wallpaper changes; re-read it on modification.</summary>
    private void CheckSelectedWallpaper()
    {
        if (string.IsNullOrEmpty(_exePath)) return;
        var config = Path.Combine(Path.GetDirectoryName(_exePath)!, "config.json");

        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(config); }
        catch (Exception) { return; }
        if (stamp == _configStamp) return;

        var dir = ReadSelectedWallpaperDir(config);
        if (dir is null) return; // possibly mid-write; try again next poll
        _configStamp = stamp;
        if (string.Equals(dir, _projectDir, StringComparison.OrdinalIgnoreCase)) return;

        _projectDir = dir;
        _ = LoadThemeAsync(dir);
    }

    /// <summary>config.json → {user} → general → wallpaperconfig → selectedwallpapers → Monitor0 → file.</summary>
    private static string? ReadSelectedWallpaperDir(string configPath)
    {
        try
        {
            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            foreach (var user in doc.RootElement.EnumerateObject())
            {
                if (user.Value.ValueKind != JsonValueKind.Object
                    || !user.Value.TryGetProperty("general", out var general)
                    || !general.TryGetProperty("wallpaperconfig", out var wallpaperConfig)
                    || !wallpaperConfig.TryGetProperty("selectedwallpapers", out var selected)
                    || selected.ValueKind != JsonValueKind.Object)
                    continue;

                JsonElement monitor = default;
                var found = selected.TryGetProperty("Monitor0", out monitor);
                if (!found)
                {
                    foreach (var m in selected.EnumerateObject())
                    {
                        monitor = m.Value;
                        found = true;
                        break;
                    }
                }

                if (found && monitor.ValueKind == JsonValueKind.Object
                    && monitor.TryGetProperty("file", out var file) && file.GetString() is { Length: > 0 } path)
                    return Path.GetDirectoryName(Path.GetFullPath(path));
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private async Task LoadThemeAsync(string dir)
    {
        var version = ++_themeVersion;
        var title = "Wallpaper";
        Color? scheme = null;
        string? previewName = null;

        try
        {
            var json = await File.ReadAllTextAsync(Path.Combine(dir, "project.json"));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } s) title = s;
            if (root.TryGetProperty("preview", out var p)) previewName = p.GetString();
            if (root.TryGetProperty("general", out var general)
                && general.TryGetProperty("properties", out var props)
                && props.TryGetProperty("schemecolor", out var schemeColor)
                && schemeColor.TryGetProperty("value", out var value))
                scheme = ParseSchemeColor(value.GetString());
        }
        catch (Exception)
        {
            // No/invalid project.json (e.g. a raw video file); fall back to defaults.
        }

        Artwork? preview = null;
        if (previewName is not null)
        {
            try
            {
                var previewPath = Path.Combine(dir, previewName);
                // Shown as a 30px thumbnail and sampled for its color: 128px is plenty.
                if (File.Exists(previewPath)) preview = await Artwork.DecodeAsync(await File.ReadAllBytesAsync(previewPath), maxSize: 128);
            }
            catch (Exception)
            {
            }
        }

        if (version != _themeVersion) return;

        // Many wallpapers leave the scheme color at black/white; derive one from the preview instead.
        var accent = scheme is { } sc && ColorTools.IsVivid(sc) ? sc
            : preview is not null ? ColorTools.Vibrant(preview.Still)
            : null;

        Theme = new WallpaperTheme(title, accent is { } a ? ColorTools.Readable(a) : null, preview);
        Changed?.Invoke();
    }

    /// <summary>Wallpaper Engine stores colors as "r g b" floats in 0..1.</summary>
    private static Color? ParseSchemeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;

        var channels = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            channels[i] = (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        }
        return Color.FromRgb(channels[0], channels[1], channels[2]);
    }

    private bool Send(string command)
    {
        if (!IsRunning || string.IsNullOrEmpty(_exePath)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(_exePath, $"-control {command}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
