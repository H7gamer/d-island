// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Windows.Threading;

namespace DIsland.Services;

public sealed class TimerService
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _endUtc;
    private TimeSpan _pausedRemaining;

    public TimerService()
    {
        _tick.Tick += (_, _) => OnTick();
    }

    public bool IsActive { get; private set; }
    public bool IsPaused { get; private set; }

    public TimeSpan Remaining =>
        !IsActive ? TimeSpan.Zero
        : IsPaused ? _pausedRemaining
        : Max(TimeSpan.Zero, _endUtc - DateTime.UtcNow);

    /// <summary>Remaining time changed (fires a few times per second while running).</summary>
    public event Action? Tick;
    /// <summary>Started, paused, resumed, cancelled or finished.</summary>
    public event Action? StateChanged;
    public event Action? Finished;

    public void Start(TimeSpan duration)
    {
        _endUtc = DateTime.UtcNow + duration;
        IsActive = true;
        IsPaused = false;
        _tick.Start();
        StateChanged?.Invoke();
        Tick?.Invoke();
    }

    public void TogglePause()
    {
        if (!IsActive) return;
        if (IsPaused)
        {
            _endUtc = DateTime.UtcNow + _pausedRemaining;
            IsPaused = false;
            _tick.Start();
        }
        else
        {
            _pausedRemaining = Remaining;
            IsPaused = true;
            _tick.Stop();
        }
        StateChanged?.Invoke();
    }

    public void Add(TimeSpan amount)
    {
        if (!IsActive) return;
        if (IsPaused) _pausedRemaining += amount;
        else _endUtc += amount;
        Tick?.Invoke();
    }

    public void Cancel()
    {
        if (!IsActive) return;
        IsActive = false;
        IsPaused = false;
        _tick.Stop();
        StateChanged?.Invoke();
    }

    private void OnTick()
    {
        if (Remaining > TimeSpan.Zero)
        {
            Tick?.Invoke();
            return;
        }
        IsActive = false;
        _tick.Stop();
        StateChanged?.Invoke();
        Finished?.Invoke();
    }

    public static string Format(TimeSpan t)
    {
        var total = (int)Math.Ceiling(t.TotalSeconds);
        var h = total / 3600;
        var m = total / 60 % 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
