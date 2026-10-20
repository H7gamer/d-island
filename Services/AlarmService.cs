// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DIsland.Services;

/// <summary>An alarm time; once (switches itself off after ringing) or every day.</summary>
public sealed class Alarm
{
    /// <summary>Local time of day, "HH:mm".</summary>
    public string Time { get; set; } = "07:00";
    public bool Enabled { get; set; } = true;
    public bool Daily { get; set; }
    public string Label { get; set; } = "";

    [JsonIgnore]
    public TimeSpan TimeOfDay => TimeSpan.TryParse(Time, out var t) ? t : TimeSpan.FromHours(7);
}

/// <summary>
/// Rings alarms from settings while the island runs. Waits for the next one with a timer capped at a
/// minute, so sleep, resume and clock changes can't make it miss or drift; an alarm missed by up to
/// 10 minutes (e.g. the PC was asleep) still rings on wake.
/// </summary>
public sealed class AlarmService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal);
    private readonly Dictionary<Alarm, DateTime> _rungAt = new();
    private DateTime _lastCheck = DateTime.Now;

    public AlarmService(AppSettings settings)
    {
        _settings = settings;
        _timer.Tick += (_, _) => Check();
    }

    /// <summary>An alarm is ringing (UI thread).</summary>
    public event Action<Alarm>? Ring;

    public void Start()
    {
        _lastCheck = DateTime.Now;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Schedule();
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Stop();
    }

    /// <summary>Alarms changed in Settings.</summary>
    public void Reschedule()
    {
        _lastCheck = DateTime.Now; // don't ring an alarm just set for a time already passed today
        Schedule();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ui.InvokeAsync(Check); // raised on a system thread
    }

    private void Check()
    {
        var now = DateTime.Now;
        foreach (var alarm in _settings.Alarms.Where(a => a.Enabled).ToList())
        {
            // The most recent occurrence of the alarm time up to now.
            var due = now.Date + alarm.TimeOfDay;
            if (due > now) due = due.AddDays(-1);
            if (due <= _lastCheck || now - due > TimeSpan.FromMinutes(10)) continue;
            if (_rungAt.TryGetValue(alarm, out var rung) && rung == due) continue;
            _rungAt[alarm] = due;
            if (!alarm.Daily)
            {
                alarm.Enabled = false;
                _settings.Save();
            }
            Ring?.Invoke(alarm);
        }
        _lastCheck = now;
        Schedule();
    }

    private void Schedule()
    {
        _timer.Stop();
        var now = DateTime.Now;
        var next = _settings.Alarms.Where(a => a.Enabled)
            .Select(a => now.Date + a.TimeOfDay is var today && today > now ? today : today.AddDays(1))
            .DefaultIfEmpty(DateTime.MaxValue)
            .Min();
        if (next == DateTime.MaxValue) return;
        var wait = next - now;
        _timer.Interval = wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait + TimeSpan.FromMilliseconds(100);
        _timer.Start();
    }
}
