// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DIsland.Services;

public enum IslandMode
{
    /// <summary>Floating pill just below the top edge.</summary>
    DynamicIsland,
    /// <summary>Attached to the top edge with flared shoulders, like a laptop notch.</summary>
    Notch,
}

public enum IslandMaterial
{
    Opaque,
    Translucent,
    /// <summary>Blurred backdrop with a light tint, specular rim and sheen.</summary>
    LiquidGlass,
}

public enum GlassEngine
{
    /// <summary>GPU when it has been verified on this Windows build, otherwise screen capture.</summary>
    Auto,
    /// <summary>DWM + DirectComposition only; no blur if unavailable. Island stays capturable.</summary>
    Gpu,
    /// <summary>Screen copy + blur; hides the island from screenshots while blurring.</summary>
    ScreenCapture,
}

public enum AccentSource
{
    /// <summary>Wallpaper Engine's theme when available; otherwise the media app's color for media bits.</summary>
    Auto,
    Wallpaper,
    MediaApp,
    Custom,
    Monochrome,
}

public enum ClockFormat
{
    System,
    TwelveHour,
    TwentyFourHour,
}

public enum IdleStyle
{
    Clock,
    /// <summary>A small empty pill when nothing is happening.</summary>
    Minimal,
}

public enum PageNavigation
{
    /// <summary>Labeled tabs across the top of the expanded island.</summary>
    Tabs,
    /// <summary>Page dots (no labels), like the iPhone home screen.</summary>
    Dots,
}

public sealed class AppSettings
{
    // ---- shape & material
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IslandMode Mode { get; set; } = IslandMode.DynamicIsland;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IslandMaterial Material { get; set; } = IslandMaterial.Opaque;

    /// <summary>Background opacity in Translucent mode (content stays opaque), 0.15..1.</summary>
    public double BackgroundOpacity { get; set; } = 0.7;

    /// <summary>Tint strength in Liquid Glass mode, 0..0.9.</summary>
    public double GlassTint { get; set; } = 0.12;

    /// <summary>How much of the frosted blur is mixed over the sharp background: 0 = clear glass, 1 = fully frosted.</summary>
    public double GlassBlur { get; set; } = 0.35;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GlassEngine GlassEngine { get; set; } = GlassEngine.Auto;

    /// <summary>Cached GPU blur probe result, "&lt;windows build&gt;:ok|fail"; re-probed after Windows updates.</summary>
    public string? GpuBlurProbe { get; set; }

    /// <summary>Liquid Glass edge highlight and top sheen, 0 = matte, 1 = very glossy.</summary>
    public double GlassShine { get; set; } = 0.3;

    /// <summary>Soft shadow behind text and icons so they stay readable over see-through materials.</summary>
    public bool TextShadow { get; set; } = true;

    public double IslandScale { get; set; } = 1.0;

    /// <summary>Multiplier on corner radius; 1 = fully rounded pill.</summary>
    public double CornerRoundness { get; set; } = 1.0;

    public double OutlineWidth { get; set; }
    public double OutlineOpacity { get; set; } = 0.25;
    public double ShadowStrength { get; set; }

    // ---- adaptive
    /// <summary>Fade the material over the bare desktop; use it fully when an app window is behind.</summary>
    public bool AdaptiveBackground { get; set; } = true;

    /// <summary>Material strength while only the desktop/wallpaper is behind the island, 0..1.</summary>
    public double DesktopOpacity { get; set; } = 0.15;

    public bool SolidWhenExpanded { get; set; } = true;

    // ---- colors
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AccentSource AccentSource { get; set; } = AccentSource.Auto;

    public string CustomAccent { get; set; } = "#0A84FF";

    /// <summary>How much of the accent is mixed into the dark background, 0..0.5.</summary>
    public double BackgroundTint { get; set; } = 0.18;

    // ---- text & clock
    public string FontFamily { get; set; } = "Segoe UI Variable Text";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ClockFormat ClockFormat { get; set; } = ClockFormat.System;

    public bool ShowSeconds { get; set; }
    public bool ShowDateWhenIdle { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IdleStyle IdleStyle { get; set; } = IdleStyle.Clock;

    // ---- interaction & animation
    public bool ExpandOnHover { get; set; } = true;
    public int HoverDelayMs { get; set; } = 120;
    public int CollapseDelayMs { get; set; } = 250;

    /// <summary>Spring speed multiplier; 1 = default, 2 = twice as fast.</summary>
    public double AnimationSpeed { get; set; } = 1.0;

    /// <summary>0 = no overshoot, 1 = very bouncy.</summary>
    public double Bounciness { get; set; } = 0.35;

    // ---- media & Wallpaper Engine
    public bool ShowArtBanner { get; set; } = true;

    /// <summary>
    /// Fetch Apple Music motion artwork (looping album videos) for the playing album, from any player.
    /// Opt-in: uses Apple's unofficial web-player token and private catalog endpoint.
    /// </summary>
    public bool MotionArtwork { get; set; }

    /// <summary>Show pause / mute / next wallpaper controls when Wallpaper Engine is running.</summary>
    public bool WallpaperEngineControls { get; set; } = true;

    /// <summary>Mute Wallpaper Engine's audio while a media app is playing.</summary>
    public bool MuteWallpaperDuringMedia { get; set; }

    /// <summary>Superseded by <see cref="AccentSource"/>; read once for migration, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ThemeFromWallpaper { get; set; }

    // ---- alerts, position, system
    public double OsdSeconds { get; set; } = 2;
    public double AlertSeconds { get; set; } = 3;

    /// <summary>Extra distance from the top of the screen, in DIPs (e.g. for a top-docked taskbar).</summary>
    public double TopOffset { get; set; }

    public bool HideVolumeFlyout { get; set; }

    /// <summary>System-wide shortcut that hides / shows the island, e.g. "Ctrl+Alt+I"; empty = none.</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+I";

    /// <summary>Why <see cref="ToggleHotkey"/> couldn't be registered (runtime only, shown in Settings).</summary>
    [JsonIgnore]
    public string? HotkeyProblem { get; set; }

    /// <summary>Alert when a USB / Bluetooth device or monitor connects or disconnects.</summary>
    public bool DeviceAlerts { get; set; } = true;

    public bool LockKeyAlerts { get; set; } = true;

    /// <summary>Do Not Disturb (Focus) turning on or off.</summary>
    public bool FocusAlerts { get; set; } = true;

    public bool CopyAlerts { get; set; } = true;

    /// <summary>On Ctrl+V / Shift+Insert (Windows has no paste event; the shortcut is the signal).</summary>
    public bool PasteAlerts { get; set; } = true;

    /// <summary>Show the first line of copied text in the alert.</summary>
    public bool ClipboardPreview { get; set; } = true;

    // ---- quick apps
    /// <summary>Launcher row in the expanded island: Start menu app ids (AppUserModelID or known-folder path).</summary>
    public List<string> QuickApps { get; set; } = new()
    {
        "Microsoft.Windows.Explorer",
        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
    };

    public const int MaxQuickApps = 16;

    // ---- updates
    /// <summary>Check the official GitHub releases once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Download, verify and install updates by themselves (installed copies only).</summary>
    public bool AutoInstallUpdates { get; set; } = true;

    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>The version that last ran, to say "Updated to …" once after an update.</summary>
    public string? LastRunVersion { get; set; }

    // ---- activities
    /// <summary>Clicking the collapsed music activity opens the music app (hover still expands), like tapping on iPhone.</summary>
    public bool ClickOpensMediaApp { get; set; }

    /// <summary>When two activities run (e.g. timer + music), show the second as a small bubble beside the island.</summary>
    public bool ShowSecondActivity { get; set; } = true;

    /// <summary>Equalizer bars follow the actual sound instead of a looping animation.</summary>
    public bool RealWaveform { get; set; } = true;

    public List<Alarm> Alarms { get; set; } = new();

    /// <summary>Microphone muted, Energy Saver, Wi-Fi / VPN connected or disconnected.</summary>
    public bool StatusAlerts { get; set; } = true;

    // ---- notes and to-do
    public bool TodoEnabled { get; set; } = true;
    public bool NotesEnabled { get; set; } = true;

    // ---- file shelf
    public bool ShelfEnabled { get; set; } = true;

    /// <summary>Keep files on the shelf after dragging them out (otherwise a successful drop removes them).</summary>
    public bool ShelfKeepAfterDrag { get; set; }

    // ---- calendar
    public bool CalendarEnabled { get; set; } = true;

    /// <summary>iCal (.ics / webcal) addresses: Google "Secret address in iCal format", Outlook "ICS" link, iCloud public link.</summary>
    public List<string> CalendarIcsUrls { get; set; } = new();

    /// <summary>Calendars added to Windows (Outlook / Calendar app accounts); needs the one-time identity setup.</summary>
    public bool WindowsCalendar { get; set; } = true;

    /// <summary>Alert this many minutes before a timed event; 0 = off.</summary>
    public int CalendarReminderMinutes { get; set; } = 5;

    // ---- notifications
    /// <summary>Show other apps' notifications in the island (needs the one-time identity setup).</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Only the app name in the alert, not the message text.</summary>
    public bool NotificationsHideText { get; set; }

    public double NotificationSeconds { get; set; } = 5;

    // ---- weather
    public bool WeatherEnabled { get; set; } = true;

    /// <summary>Windows location (rounded to ~1 km); otherwise the chosen city below.</summary>
    public bool WeatherUseDeviceLocation { get; set; } = true;

    public string? WeatherPlace { get; set; }

    /// <summary>Town name for "My location" (from OpenStreetMap) and the rounded coordinates it's for.</summary>
    public string? WeatherDevicePlace { get; set; }
    public string? WeatherDevicePlaceKey { get; set; }
    public double? WeatherLatitude { get; set; }
    public double? WeatherLongitude { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Auto;

    /// <summary>Weather icon and temperature next to the clock in the idle pill ("10:58 PM | ☁ 26°").</summary>
    public bool WeatherInIdlePill { get; set; } = true;

    // ---- expanded pages
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PageNavigation PageNavigation { get; set; } = PageNavigation.Tabs;

    /// <summary>Orange / green dot beside the island (and an alert) while an app uses the mic / camera.</summary>
    public bool PrivacyIndicator { get; set; } = true;

    /// <summary>Quick app icon size in design units (before the island's Size scaling), 16..48.</summary>
    public double QuickAppIconSize { get; set; } = 26;

    /// <summary>Raised (on the UI thread) after any setting is changed through the UI.</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Restores look-and-feel defaults; system integration (volume flyout, wallpaper muting) is left alone.</summary>
    public void ResetToDefaults()
    {
        var d = new AppSettings();
        Mode = d.Mode;
        Material = d.Material;
        BackgroundOpacity = d.BackgroundOpacity;
        GlassTint = d.GlassTint;
        GlassBlur = d.GlassBlur;
        GlassEngine = d.GlassEngine;
        GlassShine = d.GlassShine;
        TextShadow = d.TextShadow;
        IslandScale = d.IslandScale;
        CornerRoundness = d.CornerRoundness;
        OutlineWidth = d.OutlineWidth;
        OutlineOpacity = d.OutlineOpacity;
        ShadowStrength = d.ShadowStrength;
        AdaptiveBackground = d.AdaptiveBackground;
        DesktopOpacity = d.DesktopOpacity;
        SolidWhenExpanded = d.SolidWhenExpanded;
        AccentSource = d.AccentSource;
        CustomAccent = d.CustomAccent;
        BackgroundTint = d.BackgroundTint;
        FontFamily = d.FontFamily;
        ClockFormat = d.ClockFormat;
        ShowSeconds = d.ShowSeconds;
        ShowDateWhenIdle = d.ShowDateWhenIdle;
        IdleStyle = d.IdleStyle;
        ExpandOnHover = d.ExpandOnHover;
        HoverDelayMs = d.HoverDelayMs;
        CollapseDelayMs = d.CollapseDelayMs;
        AnimationSpeed = d.AnimationSpeed;
        Bounciness = d.Bounciness;
        ShowArtBanner = d.ShowArtBanner;
        WallpaperEngineControls = d.WallpaperEngineControls;
        OsdSeconds = d.OsdSeconds;
        AlertSeconds = d.AlertSeconds;
        TopOffset = d.TopOffset;
        QuickAppIconSize = d.QuickAppIconSize;
        PageNavigation = d.PageNavigation;
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DIsland", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                // Before materials existed, a lowered opacity meant "translucent"; keep that look.
                if (!json.Contains("\"Material\"") && json.Contains("\"BackgroundOpacity\"") && settings.BackgroundOpacity < 1)
                    settings.Material = IslandMaterial.Translucent;

                // The first Liquid Glass default tint (30%) made the glass look solid; move it to the new default.
                if (!json.Contains("\"GlassBlur\"") && Math.Abs(settings.GlassTint - 0.3) < 0.001)
                    settings.GlassTint = new AppSettings().GlassTint;

                if (settings.ThemeFromWallpaper == false && !json.Contains("\"AccentSource\""))
                    settings.AccentSource = AccentSource.MediaApp;
                settings.ThemeFromWallpaper = null;

                return settings;
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Non-fatal: the setting just won't persist.
        }
    }
}
