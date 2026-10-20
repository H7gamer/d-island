// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Windows.Threading;
using DIsland.Interop;
using Microsoft.Win32;

namespace DIsland.Services;

public enum BatteryAlert
{
    PluggedIn,
    Unplugged,
    Low,      // crossed 20% on battery
    Critical, // crossed 10% on battery
}

/// <summary>
/// Polls GetSystemPowerStatus (plus an immediate re-check on power status broadcasts) and raises
/// <see cref="Alert"/> on plug/unplug and once per discharge when crossing 20% and 10%.
/// </summary>
public sealed class BatteryService : IDisposable
{
    public const int LowThreshold = 20;
    public const int CriticalThreshold = 10;

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    // Power broadcasts (PowerModeChanged) report plug/unplug and level changes immediately;
    // this poll is only a safety net, so it can be slow.
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
    private bool _initialized;
    private bool _lastOnAc;
    private bool _warnedLow;
    private bool _warnedCritical;

    public BatteryService()
    {
        _poll.Tick += (_, _) => Poll();
    }

    /// <summary>Alert kind and battery percent, raised on the UI thread.</summary>
    public event Action<BatteryAlert, int>? Alert;

    public void Start()
    {
        Poll();
        _poll.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _poll.Stop();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) _ui.InvokeAsync(Poll);
    }

    private void Poll()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status)) return;

        // BatteryFlag 128 = no system battery, 255 = unknown.
        var hasBattery = status.BatteryFlag != 128 && status.BatteryFlag != 255 && status.BatteryLifePercent <= 100;
        if (!hasBattery) return;

        var onAc = status.ACLineStatus == 1;
        int percent = status.BatteryLifePercent;

        if (!_initialized)
        {
            // Don't fire alerts for the state we started in.
            _initialized = true;
            _lastOnAc = onAc;
            _warnedLow = !onAc && percent <= LowThreshold;
            _warnedCritical = !onAc && percent <= CriticalThreshold;
            return;
        }

        if (onAc != _lastOnAc)
        {
            _lastOnAc = onAc;
            // An unplug at low charge already shows the level in red; don't stack a second low alert on it.
            _warnedLow = !onAc && percent <= LowThreshold;
            _warnedCritical = !onAc && percent <= CriticalThreshold;
            Alert?.Invoke(onAc ? BatteryAlert.PluggedIn : BatteryAlert.Unplugged, percent);
            return;
        }

        if (onAc)
        {
            _warnedLow = _warnedCritical = false;
            return;
        }

        if (percent <= CriticalThreshold && !_warnedCritical)
        {
            _warnedCritical = _warnedLow = true;
            Alert?.Invoke(BatteryAlert.Critical, percent);
        }
        else if (percent <= LowThreshold && !_warnedLow)
        {
            _warnedLow = true;
            Alert?.Invoke(BatteryAlert.Low, percent);
        }

        if (percent > LowThreshold) _warnedLow = false;
        if (percent > CriticalThreshold) _warnedCritical = false;
    }
}
