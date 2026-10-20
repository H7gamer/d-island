// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Runtime.InteropServices;
using DIsland.Interop;

namespace DIsland.Services;

/// <summary>
/// Output level of the default playback device (what you hear), for the equalizer bars. Read on
/// demand only while the bars are on screen; the device is re-acquired when it changes or fails.
/// </summary>
public sealed class AudioMeter : IDisposable
{
    private const int CLSCTX_ALL = 0x17;

    private IMMDeviceEnumerator? _enumerator;
    private IAudioMeterInformation? _meter;
    private string? _deviceId;
    private readonly float[] _channels = new float[2];

    /// <summary>Left and right peak (0..1) since the last call; both 0 if unavailable.</summary>
    public (float Left, float Right) Read()
    {
        try
        {
            EnsureMeter();
            if (_meter is null) return (0, 0);
            if (_meter.GetMeteringChannelCount(out var count) == 0 && count >= 2
                && _meter.GetChannelsPeakValues(2, _channels) == 0)
                return (_channels[0], _channels[1]);
            return _meter.GetPeakValue(out var peak) == 0 ? (peak, peak) : (0, 0);
        }
        catch (Exception)
        {
            Release(); // device removed; try again next read
            return (0, 0);
        }
    }

    private int _readsSinceCheck;

    /// <summary>Follows the default device (checked about once a second), re-activating only on change.</summary>
    private void EnsureMeter()
    {
        if (_meter is not null && ++_readsSinceCheck < 30) return;
        _readsSinceCheck = 0;
        _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) != 0 || device is null)
        {
            Release();
            return;
        }
        try
        {
            device.GetId(out var id);
            if (_meter is not null && id == _deviceId) return;
            Release();
            var iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) == 0 && obj is IAudioMeterInformation meter)
            {
                _meter = meter;
                _deviceId = id;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void Release()
    {
        if (_meter is not null) Marshal.ReleaseComObject(_meter);
        _meter = null;
        _deviceId = null;
    }

    public void Dispose()
    {
        Release();
        if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator);
        _enumerator = null;
    }
}
