// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DIsland.Interop;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace DIsland.Services;

public enum StatusKind
{
    MicMuted,
    MicUnmuted,
    EnergySaverOn,
    EnergySaverOff,
    WifiConnected,
    WifiDisconnected,
    VpnConnected,
    VpnDisconnected,
    Offline,
}

/// <summary>A status change: kind plus detail (network name, VPN name).</summary>
public sealed record StatusChange(StatusKind Kind, string? Detail);

/// <summary>
/// Small system state changes worth a glance, all event-driven: microphone mute (the default
/// recording device's endpoint volume callback), Energy Saver (PowerManager), and Wi-Fi / VPN /
/// offline transitions (NetworkInformation, debounced since a switch fires several events).
/// </summary>
public sealed class StatusService : IDisposable
{
    private const int CLSCTX_ALL = 0x17;

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _networkSettle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly MuteCallback _muteCallback;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _mic;
    private bool? _micMuted;
    private bool? _energySaver;
    private string? _wifi;
    private string? _vpn;
    private bool _online;
    private bool _started;

    public StatusService()
    {
        _muteCallback = new MuteCallback(muted => _ui.InvokeAsync(() => OnMicMute(muted)));
        _networkSettle.Tick += (_, _) =>
        {
            _networkSettle.Stop();
            ReadNetwork(report: true);
        };
    }

    /// <summary>Raised on the UI thread.</summary>
    public event Action<StatusChange>? Changed;

    /// <summary>Windows' Energy Saver is on (the island lightens its own work then).</summary>
    public static bool EnergySaverOn
    {
        get
        {
            try { return PowerManager.EnergySaverStatus == EnergySaverStatus.On; }
            catch (Exception) { return false; }
        }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out var device) == 0 && device is not null)
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) == 0 && obj is IAudioEndpointVolume mic)
                {
                    _mic = mic;
                    _micMuted = mic.GetMute(out var muted) == 0 && muted;
                    mic.RegisterControlChangeNotify(_muteCallback);
                }
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception) { /* no microphone */ }

        try
        {
            _energySaver = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
            PowerManager.EnergySaverStatusChanged += OnEnergySaverChanged;
        }
        catch (Exception) { /* not available */ }

        ReadNetwork(report: false);
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        try { PowerManager.EnergySaverStatusChanged -= OnEnergySaverChanged; } catch (Exception) { }
        _networkSettle.Stop();
        if (_mic is not null)
        {
            try { _mic.UnregisterControlChangeNotify(_muteCallback); } catch (Exception) { }
            Marshal.ReleaseComObject(_mic);
            _mic = null;
        }
        if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator);
        _enumerator = null;
    }

    private void OnMicMute(bool muted)
    {
        if (muted == _micMuted) return; // volume changes notify too
        _micMuted = muted;
        Changed?.Invoke(new StatusChange(muted ? StatusKind.MicMuted : StatusKind.MicUnmuted, null));
    }

    private void OnEnergySaverChanged(object? sender, object e) => _ui.InvokeAsync(() =>
    {
        var on = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        if (on == _energySaver) return;
        _energySaver = on;
        Changed?.Invoke(new StatusChange(on ? StatusKind.EnergySaverOn : StatusKind.EnergySaverOff, null));
    });

    private void OnNetworkStatusChanged(object sender) => _ui.InvokeAsync(() =>
    {
        _networkSettle.Stop();
        _networkSettle.Start();
    });

    /// <summary>Current Wi-Fi network, VPN and connectivity; reports what changed since the last read.</summary>
    private void ReadNetwork(bool report)
    {
        string? wifi = null;
        var online = false;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            online = profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            foreach (var p in NetworkInformation.GetConnectionProfiles())
            {
                if (!p.IsWlanConnectionProfile || p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None) continue;
                wifi = p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? p.ProfileName;
                break;
            }
        }
        catch (Exception) { /* keep defaults */ }

        // VPN: an operational PPP / tunnel adapter that isn't one of Windows' built-in transition tunnels.
        string? vpn = null;
        try
        {
            vpn = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                                     && (n.NetworkInterfaceType == NetworkInterfaceType.Ppp
                                         || (n.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                                             && !n.Description.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
                                             && !n.Description.Contains("6to4", StringComparison.OrdinalIgnoreCase)
                                             && !n.Description.Contains("ISATAP", StringComparison.OrdinalIgnoreCase))
                                         || n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                                         || n.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                                         || n.Description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)))
                ?.Name;
        }
        catch (Exception) { /* keep null */ }

        var oldWifi = _wifi;
        var oldVpn = _vpn;
        var wasOnline = _online;
        _wifi = wifi;
        _vpn = vpn;
        _online = online;
        if (!report) return;

        if (vpn != oldVpn)
            Changed?.Invoke(vpn is null ? new StatusChange(StatusKind.VpnDisconnected, oldVpn) : new StatusChange(StatusKind.VpnConnected, vpn));
        if (wifi != oldWifi)
            Changed?.Invoke(wifi is null ? new StatusChange(StatusKind.WifiDisconnected, oldWifi) : new StatusChange(StatusKind.WifiConnected, wifi));
        else if (wasOnline && !online && wifi is null)
            Changed?.Invoke(new StatusChange(StatusKind.Offline, null));
    }

    [ComVisible(true)]
    private sealed class MuteCallback : IAudioEndpointVolumeCallback
    {
        private readonly Action<bool> _onMute;
        public MuteCallback(Action<bool> onMute) => _onMute = onMute;

        public int OnNotify(IntPtr notifyData)
        {
            if (notifyData == IntPtr.Zero) return 0;
            _onMute(Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData).bMuted != 0);
            return 0;
        }
    }
}
