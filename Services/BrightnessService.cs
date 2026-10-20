// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Management;
using System.Windows.Threading;

namespace DIsland.Services;

/// <summary>
/// Listens for WmiMonitorBrightnessEvent (internal laptop panels). On desktops with external
/// monitors the WMI class doesn't exist and this stays silent.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private ManagementEventWatcher? _watcher;

    /// <summary>Brightness 0..100, raised on the UI thread.</summary>
    public event Action<int>? Changed;

    /// <summary>Blocking WMI setup; call off the UI thread.</summary>
    public void Start()
    {
        try
        {
            var watcher = new ManagementEventWatcher(
                new ManagementScope(@"root\WMI"),
                new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += OnEventArrived;
            watcher.Start();
            _watcher = watcher;
        }
        catch (Exception)
        {
            // ManagementException "Not supported" on machines without a WMI-controllable panel.
        }
    }

    public void Dispose()
    {
        try
        {
            _watcher?.Stop();
            _watcher?.Dispose();
        }
        catch (Exception)
        {
        }
        _watcher = null;
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var level = Convert.ToInt32(e.NewEvent["Brightness"]);
            _ui.InvokeAsync(() => Changed?.Invoke(level));
        }
        catch (Exception)
        {
        }
    }
}
