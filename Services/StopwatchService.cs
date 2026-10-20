// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Diagnostics;
using System.Windows.Threading;

namespace DIsland.Services;

/// <summary>A stopwatch: start / pause / reset. Ticks every 100 ms only while running.</summary>
public sealed class StopwatchService
{
    private readonly Stopwatch _watch = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public StopwatchService()
    {
        _tick.Tick += (_, _) => Tick?.Invoke();
    }

    /// <summary>Started and not reset (running or paused).</summary>
    public bool IsActive { get; private set; }
    public bool IsRunning => _watch.IsRunning;
    public TimeSpan Elapsed => _watch.Elapsed;

    public event Action? Tick;
    public event Action? StateChanged;

    public void Start()
    {
        IsActive = true;
        _watch.Start();
        _tick.Start();
        StateChanged?.Invoke();
        Tick?.Invoke();
    }

    public void TogglePause()
    {
        if (!IsActive) return;
        if (_watch.IsRunning)
        {
            _watch.Stop();
            _tick.Stop();
        }
        else
        {
            _watch.Start();
            _tick.Start();
        }
        StateChanged?.Invoke();
        Tick?.Invoke();
    }

    public void Reset()
    {
        _watch.Reset();
        _tick.Stop();
        IsActive = false;
        StateChanged?.Invoke();
    }

    /// <summary>"1:02:03", "02:03" (compact) or with tenths ("02:03.4").</summary>
    public static string Format(TimeSpan t, bool tenths)
    {
        var h = (int)t.TotalHours;
        var text = h > 0 ? $"{h}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        return tenths ? $"{text}.{t.Milliseconds / 100}" : text;
    }
}
