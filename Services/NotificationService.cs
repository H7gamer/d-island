// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DIsland.Services;

public sealed record IslandNotification(
    uint Id,
    string AppName,
    string? AppId,
    string Title,
    string Body,
    DateTimeOffset Time)
{
    public ImageSource? AppIcon { get; init; }
}

/// <summary>
/// Other apps' notifications (the ones in Windows' notification center) via UserNotificationListener.
/// Needs package identity and the user's consent (Windows asks once). Uses the NotificationChanged event
/// where Windows allows it for desktop apps, else checks every 2 seconds.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, ImageSource?> _icons = new();
    private UserNotificationListener? _listener;
    private bool _subscribed;
    private bool _loaded;
    private bool _syncing;
    private int _syncCount;
    private bool _resync;
    private List<IslandNotification> _items = new();

    public NotificationService()
    {
        _poll.Tick += async (_, _) => await SyncAsync();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<IslandNotification> Items => _items;

    /// <summary>Why notifications aren't available (no identity, access denied); null when working.</summary>
    public string? Problem { get; private set; }

    /// <summary>A notification that arrived after start (UI thread).</summary>
    public event Action<IslandNotification>? Arrived;

    /// <summary>The list changed (arrived, dismissed, cleared), UI thread.</summary>
    public event Action? Changed;

    public async void Start()
    {
        if (_listener is not null) return;
        if (!PackageIdentity.HasIdentity)
        {
            Problem = "Needs a one-time setup (Settings > Notifications)";
            Changed?.Invoke();
            return;
        }
        try
        {
            Mark("notifications: start");
            var listener = UserNotificationListener.Current;
            // Must be asked from the UI thread; Windows shows its consent prompt the first time.
            var access = await listener.RequestAccessAsync();
            Mark("notifications: access");
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Problem = "Notification access is off (Windows Settings > Privacy & security > Notifications)";
                Changed?.Invoke();
                return;
            }
            _listener = listener;
            try
            {
                listener.NotificationChanged += (_, _) => _ui.InvokeAsync(async () => await SyncAsync());
                _subscribed = true;
            }
            catch (Exception)
            {
                // Not available to this kind of app: fall back to checking every 2 s.
                _poll.Start();
            }
            Problem = null;
            Mark("notifications: subscribed");
            await SyncAsync();
            Mark("notifications: first sync");
            _loaded = true;
#if DEBUG
            if (Environment.GetEnvironmentVariable("DISLAND_NOTIF_LOG") is { Length: > 0 } log)
                File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} mode={Mode} items={_items.Count}\n");
#endif
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Problem = "Notifications unavailable";
            Changed?.Invoke();
        }
    }

    public void Stop()
    {
        _poll.Stop();
        _listener = null; // the event handler can't be removed reliably; the null check stops it
        _subscribed = false;
        _loaded = false;
        _items = new();
    }

    public void Dispose() => Stop();

    public void Dismiss(uint id)
    {
        try { _listener?.RemoveNotification(id); }
        catch (Exception) { /* already gone */ }
        _ = SyncAsync();
    }

    public void ClearAll()
    {
        try { _listener?.ClearNotifications(); }
        catch (Exception) { /* nothing to clear */ }
        _ = SyncAsync();
    }

    /// <summary>Re-reads the notification list; new ids since the last read are reported as arrivals.</summary>
    private async Task SyncAsync()
    {
        if (_listener is null) return;
        if (_syncing)
        {
            _resync = true;
            return;
        }
        _syncing = true;
        try
        {
            do
            {
                _resync = false;
                var current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                Mark($"notifications: list of {current.Count} read (sync #{++_syncCount})");
                var known = _items.ToDictionary(n => n.Id);
                var items = new List<IslandNotification>();
                var arrived = new List<IslandNotification>();
                foreach (var n in current)
                {
                    if (known.TryGetValue(n.Id, out var existing))
                    {
                        items.Add(existing);
                        continue;
                    }
                    if (await ReadAsync(n) is not { } item) continue;
                    items.Add(item);
                    if (_loaded) arrived.Add(item);
                }
                items.Sort((a, b) => b.Time.CompareTo(a.Time));
                var changed = items.Count != _items.Count || items.Select(i => i.Id).Except(_items.Select(i => i.Id)).Any();
                _items = items;
                if (changed) Changed?.Invoke();
                foreach (var item in arrived.OrderBy(a => a.Time)) Arrived?.Invoke(item);
            } while (_resync);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task<IslandNotification?> ReadAsync(UserNotification n)
    {
        try
        {
            var binding = n.Notification.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                        ?? new List<string>();
            if (texts.Count == 0) return null;

            string appName = "App", appId = "";
            try
            {
                appName = n.AppInfo.DisplayInfo.DisplayName;
                appId = n.AppInfo.AppUserModelId;
            }
            catch (Exception) { /* some system notifications have no app info */ }

            return new IslandNotification(n.Id, appName, appId, texts[0], string.Join("\n", texts.Skip(1)), n.CreationTime)
            {
                AppIcon = await IconAsync(n, appId),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The app's logo (cached per app).</summary>
    private async Task<ImageSource?> IconAsync(UserNotification n, string appId)
    {
        if (_icons.TryGetValue(appId, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            var stream = await n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64)).OpenReadAsync();
            using var managed = stream.AsStreamForRead();
            var memory = new MemoryStream();
            await managed.CopyToAsync(memory);
            memory.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            // Store apps' logos (Settings, …) have wide transparent margins; fill the icon box like others.
            icon = QuickAppsService.FitLogo(new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0), 64);
        }
        catch (Exception)
        {
            // Fall back to the Start menu icon for the app id, if any.
            if (appId.Length > 0) icon = QuickAppsService.GetIcon(appId, 64);
        }
        _icons[appId] = icon;
        return icon;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void Mark(string phase)
    {
        if (Environment.GetEnvironmentVariable("DISLAND_STARTUP_LOG") is not { Length: > 0 } path) return;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        File.AppendAllText(path, $"{(DateTime.Now - self.StartTime).TotalMilliseconds,6:0} ms wall  {self.TotalProcessorTime.TotalMilliseconds,6:0} ms CPU  {phase}\n");
    }

    /// <summary>For the status line in Settings.</summary>
    public string Mode => _listener is null ? "off" : _subscribed ? "live" : "checking every 2 s";
}
