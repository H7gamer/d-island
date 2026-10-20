// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace DIsland.Services;

public sealed record MediaInfo(
    string AppId,
    string AppName,
    Color Accent,
    string Title,
    string Artist,
    string Album,
    Artwork? Art,
    bool IsPlaying,
    bool CanPrevious,
    bool CanNext,
    bool CanSeek,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedAt)
{
    /// <summary>Apps only report position on changes; extrapolate while playing.</summary>
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        var position = Position;
        if (IsPlaying)
        {
            var elapsed = now - PositionUpdatedAt;
            if (elapsed > TimeSpan.Zero && elapsed < Duration) position += elapsed;
        }
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return position > Duration ? Duration : position;
    }
}

/// <summary>
/// Tracks system media sessions (GSMTC): Spotify, Apple Music, browsers, Media Player, etc.
/// Prefers whichever session is actually playing, and sticks with the last one shown when all are
/// paused. Raises <see cref="Changed"/> on the UI thread.
/// </summary>
public sealed class MediaService : IDisposable
{
    private static readonly Color SpotifyGreen = Color.FromRgb(0x1D, 0xB9, 0x54);
    private static readonly Color AppleMusicRed = Color.FromRgb(0xFA, 0x2D, 0x48);
    private static readonly Color DefaultAccent = Color.FromRgb(0x30, 0xD1, 0x58);

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly List<Session> _watched = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Session? _session;
    private string? _artKey;
    private Artwork? _art;
    private int _version;

    public MediaInfo? Current { get; private set; }

    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception)
        {
            return; // GSMTC unavailable (e.g. stripped-down Windows editions).
        }

        _manager.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(SelectSession);
        _manager.SessionsChanged += (_, _) => _ui.InvokeAsync(WatchSessions);
        WatchSessions();
    }

    public async void TogglePlayPause()
    {
        if (_session is not { } s) return;
        try { await s.TryTogglePlayPauseAsync(); } catch (Exception) { }
    }

    public async void Next()
    {
        if (_session is not { } s) return;
        try { await s.TrySkipNextAsync(); } catch (Exception) { }
    }

    public async void Previous()
    {
        if (_session is not { } s) return;
        try { await s.TrySkipPreviousAsync(); } catch (Exception) { }
    }

    public async void Seek(TimeSpan position)
    {
        if (_session is not { } s) return;
        try
        {
            var start = s.GetTimelineProperties()?.StartTime ?? TimeSpan.Zero;
            await s.TryChangePlaybackPositionAsync((start + position).Ticks);
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        UnwatchAll();
        UnsubscribeSelected();
        _session = null;
        _manager = null;
    }

    /// <summary>
    /// Every session's playback state is watched, so e.g. Spotify starting while a paused browser tab
    /// is the system's "current" session still brings Spotify onto the island.
    /// </summary>
    private void WatchSessions()
    {
        if (_manager is null) return;
        UnwatchAll();
        try
        {
            foreach (var s in _manager.GetSessions())
            {
                s.PlaybackInfoChanged += OnAnyPlaybackInfoChanged;
                _watched.Add(s);
            }
        }
        catch (Exception)
        {
        }
        SelectSession();
    }

    private void UnwatchAll()
    {
        foreach (var s in _watched) s.PlaybackInfoChanged -= OnAnyPlaybackInfoChanged;
        _watched.Clear();
    }

    private void OnAnyPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) =>
        _ui.InvokeAsync(SelectSession);

    private void SelectSession()
    {
        if (_manager is null) return;

        Session? next = null;
        try { next = PickSession(); }
        catch (Exception) { /* a session went away mid-query; treat as none */ }

        UnsubscribeSelected();
        _session = next;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnSelectedChanged;
            _session.TimelinePropertiesChanged += OnSelectedTimelineChanged;
        }
        _ = RefreshAsync();
    }

    private Session? PickSession()
    {
        var sessions = _manager!.GetSessions().Where(s => !IsIgnored(s)).ToList();
        if (sessions.Count == 0) return null;

        var current = _manager.GetCurrentSession();
        if (current is not null && IsIgnored(current)) current = null;
        if (current is not null && IsPlaying(current)) return current;

        var shown = _session is null
            ? null
            : sessions.FirstOrDefault(s => s.SourceAppUserModelId == _session.SourceAppUserModelId);
        if (shown is not null && IsPlaying(shown)) return shown;

        return sessions.FirstOrDefault(IsPlaying) ?? shown ?? current ?? sessions[0];
    }

    /// <summary>Wallpaper audio (Wallpaper Engine) isn't "now playing" music.</summary>
    private static bool IsIgnored(Session s)
    {
        try { return (s.SourceAppUserModelId ?? "").Contains("wallpaper", StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return true; }
    }

    private static bool IsPlaying(Session s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing; }
        catch (Exception) { return false; }
    }

    private void UnsubscribeSelected()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnSelectedChanged;
        _session.TimelinePropertiesChanged -= OnSelectedTimelineChanged;
    }

    private void OnSelectedChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        _ui.InvokeAsync(() => _ = RefreshAsync());

    /// <summary>
    /// Timeline updates can be frequent (browsers send them constantly), so they only update the
    /// position in place and raise <see cref="TimelineChanged"/>; no metadata or artwork re-query.
    /// </summary>
    private void OnSelectedTimelineChanged(Session sender, TimelinePropertiesChangedEventArgs args) =>
        _ui.InvokeAsync(() =>
        {
            if (!ReferenceEquals(sender, _session) || Current is null)
            {
                _ = RefreshAsync();
                return;
            }
            try
            {
                var timeline = sender.GetTimelineProperties();
                var duration = timeline.EndTime - timeline.StartTime;
                Current = Current with
                {
                    Position = timeline.Position - timeline.StartTime,
                    Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                    PositionUpdatedAt = timeline.LastUpdatedTime,
                };
                TimelineChanged?.Invoke();
            }
            catch (Exception)
            {
                // Session closing; the next event resyncs.
            }
        });

    /// <summary>Only the playback position/duration changed (lightweight; see <see cref="Changed"/> for everything else).</summary>
    public event Action? TimelineChanged;

    /// <summary>Runs on the UI thread; a version counter drops results superseded by a newer refresh.</summary>
    private async Task RefreshAsync()
    {
        var version = ++_version;
        var session = _session;
        if (session is null)
        {
            Current = null;
            Changed?.Invoke();
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _version) return;
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var appId = session.SourceAppUserModelId ?? "";

            var title = props.Title ?? "";
            var artist = props.Artist ?? "";
            var album = props.AlbumTitle ?? "";
            if (IsAppleMusic(appId) && album.Length == 0)
            {
                // Apple Music for Windows reports "Artist — Album" in the artist field.
                var split = artist.IndexOf(" — ", StringComparison.Ordinal);
                if (split > 0)
                {
                    album = artist[(split + 3)..];
                    artist = artist[..split];
                }
            }

            // Apps often publish the title first and the thumbnail a moment later, so it's part of the key.
            var key = $"{appId}|{title}|{artist}|{album}|{props.Thumbnail is not null}";
            var art = _art;
            if (key != _artKey)
            {
                art = await LoadThumbnailAsync(props.Thumbnail);
                if (version != _version) return;
                _art = art;
                _artKey = key;
            }

            var controls = playback?.Controls;
            var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;
            var position = timeline is null ? TimeSpan.Zero : timeline.Position - timeline.StartTime;

            Current = new MediaInfo(
                appId,
                FriendlyAppName(appId),
                AccentFor(appId),
                title,
                artist,
                album,
                art,
                playback?.PlaybackStatus == PlaybackStatus.Playing,
                controls?.IsPreviousEnabled ?? false,
                controls?.IsNextEnabled ?? false,
                controls?.IsPlaybackPositionEnabled ?? false,
                position,
                duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                timeline?.LastUpdatedTime ?? DateTimeOffset.Now);
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // The session can close between the event and the query; the next event will resync.
        }
    }

    private static bool IsSpotify(string appId) => appId.Contains("Spotify", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppleMusic(string appId) =>
        appId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
        || appId.Contains("iTunes", StringComparison.OrdinalIgnoreCase);

    private static Color AccentFor(string appId) =>
        IsSpotify(appId) ? SpotifyGreen
        : IsAppleMusic(appId) ? AppleMusicRed
        : DefaultAccent;

    private static string FriendlyAppName(string appId)
    {
        if (IsSpotify(appId)) return "Spotify";
        if (IsAppleMusic(appId)) return "Apple Music";
        if (appId.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase)) return "Media Player";
        if (appId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (appId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (appId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";

        // "Foo.exe", "Publisher.App_hash!App" → "Foo", "App"
        var name = appId.Split('!')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length > 0 ? name : "Media";
    }

    private static async Task<Artwork?> LoadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var winStream = await reference.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            return await Artwork.DecodeAsync(bytes, maxSize: 480); // widest use: the expanded banner
        }
        catch (Exception)
        {
            return null;
        }
    }
}
