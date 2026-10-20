// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Runtime.InteropServices;
using System.Windows.Threading;
using DIsland.Interop;

namespace DIsland.Services;

/// <summary>
/// Master volume of the default render endpoint. Follows default-device switches and raises
/// <see cref="Changed"/> (level 0..1, muted) on the UI thread.
/// </summary>
public sealed class VolumeService : IDisposable
{
    private const int CLSCTX_ALL = 0x17;

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceNotificationClient _deviceClient;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private float _lastLevel = -1;
    private bool _lastMuted;

    public VolumeService()
    {
        _volumeCallback = new VolumeCallback((level, muted) => _ui.InvokeAsync(() => OnNotify(level, muted)));
        _deviceClient = new DeviceNotificationClient(() => _ui.InvokeAsync(AttachDefaultEndpoint));
    }

    public event Action<float, bool>? Changed;

    public float Level
    {
        get
        {
            if (_endpoint is null || _endpoint.GetMasterVolumeLevelScalar(out var level) != 0) return 0;
            return level;
        }
    }

    public bool Muted => _endpoint is not null && _endpoint.GetMute(out var muted) == 0 && muted;

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(_deviceClient);
            AttachDefaultEndpoint();
        }
        catch (Exception)
        {
            _enumerator = null; // No audio service; volume OSD simply stays off.
        }
    }

    public void StepUp()
    {
        if (_endpoint is null) return;
        var ctx = Guid.Empty;
        _endpoint.SetMute(false, ref ctx);
        _endpoint.VolumeStepUp(ref ctx);
    }

    public void StepDown()
    {
        if (_endpoint is null) return;
        var ctx = Guid.Empty;
        _endpoint.VolumeStepDown(ref ctx);
    }

    public void ToggleMute()
    {
        if (_endpoint is null) return;
        var ctx = Guid.Empty;
        _endpoint.SetMute(!Muted, ref ctx);
    }

    public void Dispose()
    {
        DetachEndpoint();
        if (_enumerator is not null)
        {
            _enumerator.UnregisterEndpointNotificationCallback(_deviceClient);
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    private void AttachDefaultEndpoint()
    {
        DetachEndpoint();
        if (_enumerator is null) return;

        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) != 0 || device is null)
                return; // No output device right now.

            var iid = typeof(IAudioEndpointVolume).GUID;
            var hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
            Marshal.ReleaseComObject(device);
            if (hr != 0 || obj is not IAudioEndpointVolume endpoint) return;

            _endpoint = endpoint;
            _endpoint.RegisterControlChangeNotify(_volumeCallback);
            _lastLevel = Level;
            _lastMuted = Muted;
        }
        catch (Exception)
        {
            _endpoint = null;
        }
    }

    private void DetachEndpoint()
    {
        if (_endpoint is null) return;
        try
        {
            _endpoint.UnregisterControlChangeNotify(_volumeCallback);
            Marshal.ReleaseComObject(_endpoint);
        }
        catch (Exception)
        {
            // Device already gone.
        }
        _endpoint = null;
    }

    private void OnNotify(float level, bool muted)
    {
        // Channel-balance changes also notify; only surface master level / mute changes.
        if (Math.Abs(level - _lastLevel) < 0.0005f && muted == _lastMuted) return;
        _lastLevel = level;
        _lastMuted = muted;
        Changed?.Invoke(level, muted);
    }

    [ComVisible(true)]
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly Action<float, bool> _onChange;

        public VolumeCallback(Action<float, bool> onChange) => _onChange = onChange;

        public int OnNotify(IntPtr notifyData)
        {
            if (notifyData == IntPtr.Zero) return 0;
            var data = Marshal.PtrToStructure<AudioVolumeNotificationData>(notifyData);
            _onChange(data.fMasterVolume, data.bMuted != 0);
            return 0;
        }
    }

    [ComVisible(true)]
    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly Action _onDefaultRenderChanged;

        public DeviceNotificationClient(Action onDefaultRenderChanged) => _onDefaultRenderChanged = onDefaultRenderChanged;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.eRender && role == ERole.eMultimedia) _onDefaultRenderChanged();
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
