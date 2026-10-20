// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace DIsland.Services;

/// <summary>What kind of thing a device is, for its icon.</summary>
public enum DeviceKind
{
    Other,
    Headphones,
    Speaker,
    Mouse,
    Keyboard,
    GameController,
    Phone,
    Storage,
    Monitor,
    Camera,
    Printer,
}

/// <summary>A device connected or disconnected; <see cref="Battery"/> in percent when the device reports it.</summary>
public sealed record DeviceEvent(string Name, DeviceKind Kind, bool Connected, int? Battery = null);

/// <summary>
/// Watches device containers (one per physical device, the unit "Devices and Printers" shows) and
/// reports when one connects or disconnects: USB devices, Bluetooth devices, monitors. Works for
/// unpackaged apps; no polling.
/// </summary>
public sealed class DeviceService : IDisposable
{
    private const string ConnectedKey = "System.Devices.Connected";
    private const string CategoryKey = "System.Devices.CategoryIds";
    private const string LocalMachineKey = "System.Devices.LocalMachine";

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, (string Name, DeviceKind Kind, bool Connected)> _devices = new();
    private readonly Dictionary<string, DateTime> _recent = new();
    private readonly HashSet<string> _batteryReported = new();
    private DeviceWatcher? _watcher;
    private bool _ready;

    /// <summary>Raised on the UI thread after the initial enumeration, never for the starting state.</summary>
    public event Action<DeviceEvent>? Changed;

    public void Start()
    {
        if (_watcher is not null) return;
        try
        {
            _watcher = DeviceInformation.CreateWatcher("", new[] { ConnectedKey, CategoryKey, LocalMachineKey },
                DeviceInformationKind.DeviceContainer);
            _watcher.Added += (_, info) => _ui.InvokeAsync(() => OnAdded(info));
            _watcher.Updated += (_, update) => _ui.InvokeAsync(() => OnUpdated(update));
            _watcher.Removed += (_, update) => _ui.InvokeAsync(() => OnRemoved(update.Id));
            _watcher.EnumerationCompleted += (_, _) => _ui.InvokeAsync(() => _ready = true);
            _watcher.Start();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _watcher = null;
        }
    }

    public void Stop()
    {
        if (_watcher is null) return;
        try
        {
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                _watcher.Stop();
        }
        catch (Exception) { /* already stopping */ }
        _watcher = null;
        _devices.Clear();
        _ready = false;
    }

    public void Dispose() => Stop();

    private void OnAdded(DeviceInformation info)
    {
        if (Get<bool?>(info.Properties, LocalMachineKey) == true) return; // the PC itself
        var categories = Get<string[]>(info.Properties, CategoryKey);
        if (IsIgnored(categories)) return;

        var connected = Get<bool?>(info.Properties, ConnectedKey) == true;
        var name = string.IsNullOrWhiteSpace(info.Name) ? "Device" : info.Name;
        _devices[info.Id] = (name, Classify(categories), connected);
        if (connected) Report(info.Id);
    }

    private void OnUpdated(DeviceInformationUpdate update)
    {
        if (!_devices.TryGetValue(update.Id, out var device)) return;
        if (!update.Properties.TryGetValue(ConnectedKey, out var value) || value is not bool connected) return;
        if (connected == device.Connected) return;
        _devices[update.Id] = device with { Connected = connected };
        Report(update.Id);
    }

    private void OnRemoved(string id)
    {
        if (!_devices.Remove(id, out var device) || !device.Connected) return;
        Raise(device.Name, device.Kind, connected: false);
    }

    private async void Report(string id)
    {
        var device = _devices[id];
        int? battery = null;
        if (device.Connected && _ready) battery = await BatteryAsync(id);
        Raise(device.Name, device.Kind, device.Connected, battery);
    }

    /// <summary>
    /// Bluetooth devices that report battery (headsets, earbuds, many mice / keyboards) carry it as a
    /// device property on one of the container's devices. Waits at most 1.5 s so the alert isn't late.
    /// </summary>
    private static async Task<int?> BatteryAsync(string containerId)
    {
        const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        try
        {
            var query = DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{containerId}\"",
                new[] { BatteryKey }, DeviceInformationKind.Device).AsTask();
            if (await Task.WhenAny(query, Task.Delay(1500)) != query) return null;
            return (await query)
                .Select(d => d.Properties.TryGetValue(BatteryKey, out var v) && v is byte b ? (int?)b : null)
                .FirstOrDefault(b => b is >= 0 and <= 100);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Raise(string name, DeviceKind kind, bool connected, int? battery = null)
    {
        if (!_ready) return;

        // A Bluetooth phone or headset shows up as several containers (one per profile) that all
        // change together; report the device once. Only one of them may carry the battery level, so
        // a repeat that brings the battery the first report lacked goes through (updating the alert).
        var key = $"{name}|{connected}";
        var now = DateTime.UtcNow;
        if (_recent.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(4)
            && (battery is null || _batteryReported.Contains(key)))
            return;
        _recent[key] = now;
        if (battery is not null) _batteryReported.Add(key);
        else _batteryReported.Remove(key);
        if (_recent.Count > 64)
        {
            foreach (var old in _recent.Where(r => now - r.Value > TimeSpan.FromMinutes(1)).Select(r => r.Key).ToList())
                _recent.Remove(old);
        }

        Changed?.Invoke(new DeviceEvent(name, kind, connected, battery));
    }

    /// <summary>Printers (network ones come and go) and virtual print queues aren't worth an alert.</summary>
    private static bool IsIgnored(string[]? categories) =>
        categories is not null && categories.Any(c => c.StartsWith("PrintFax", StringComparison.OrdinalIgnoreCase));

    private static DeviceKind Classify(string[]? categories)
    {
        if (categories is null) return DeviceKind.Other;
        bool Has(string prefix) => categories.Any(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        // Most specific first: a phone also lists Audio, a monitor may list Audio too.
        if (Has("Communication.Headset") || Has("Audio.Headphone") || Has("Audio.Headset")) return DeviceKind.Headphones;
        if (Has("Communication.Phone") || Has("Multimedia.PMP")) return DeviceKind.Phone;
        if (Has("Input.Mouse")) return DeviceKind.Mouse;
        if (Has("Input.Keyboard")) return DeviceKind.Keyboard;
        if (Has("Input.Gaming")) return DeviceKind.GameController;
        if (Has("Display")) return DeviceKind.Monitor;
        if (Has("Storage")) return DeviceKind.Storage;
        if (Has("Imaging.Camera") || Has("Imaging.Webcam")) return DeviceKind.Camera;
        if (Has("Audio")) return DeviceKind.Speaker;
        return DeviceKind.Other;
    }

    private static T? Get<T>(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) && value is T typed ? typed : default;
}
