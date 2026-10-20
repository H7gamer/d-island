// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;
using Ical.Net.DataTypes;
using Windows.ApplicationModel.Appointments;

namespace DIsland.Services;

public sealed record CalendarItem(
    string Title,
    DateTime Start,
    DateTime End,
    bool AllDay,
    string? Location,
    string? JoinUrl,
    Color? Color)
{
    public bool IsNow(DateTime now) => !AllDay && Start <= now && now < End;
}

/// <summary>
/// Events for this month and the next week from two sources: iCal (.ics) links, the "secret address" Google,
/// Outlook and iCloud calendars offer, parsed with Ical.Net (recurrence, time zones, exceptions); and
/// the Windows calendar (AppointmentStore: accounts added to Windows / Outlook), which needs package
/// identity. Raises <see cref="Reminder"/> a few minutes before timed events.
/// </summary>
public sealed class CalendarService : IDisposable
{
    public const int DaysAhead = 7;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Regex JoinLink = new(
        @"https://(?:[\w-]+\.)*(?:teams\.microsoft\.com/l/meetup-join|teams\.live\.com/meet|zoom\.us/[jw]/|meet\.google\.com/|[\w-]+\.webex\.com/)[^\s""<>]*",
        RegexOptions.IgnoreCase); // interpreted: runs a few times per refresh, not worth compiling at startup

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(10) };
    private readonly DispatcherTimer _reminderTimer = new(DispatcherPriority.Normal);
    private readonly HashSet<string> _reminded = new();
    private readonly Dictionary<string, Color?> _calendarColors = new();
    private AppointmentStore? _store;
    private CancellationTokenSource? _pending;
    private bool _running;

    public CalendarService(AppSettings settings)
    {
        _settings = settings;
        _refresh.Tick += (_, _) => Refresh();
        _reminderTimer.Tick += (_, _) => CheckReminders();
    }

    public IReadOnlyList<CalendarItem> Items { get; private set; } = Array.Empty<CalendarItem>();

    /// <summary>Why some or all events are missing; null when every source loaded.</summary>
    public string? Problem { get; private set; }

    /// <summary>Whether any source is configured at all.</summary>
    public bool HasSources => _settings.CalendarIcsUrls.Count > 0 || (_settings.WindowsCalendar && PackageIdentity.HasIdentity);

    public event Action? Changed;

    /// <summary>A timed event starts within the reminder lead time.</summary>
    public event Action<CalendarItem>? Reminder;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _refresh.Start();
        Refresh();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _refresh.Stop();
        _reminderTimer.Stop();
        _pending?.Cancel();
        if (_store is not null)
        {
            _store.StoreChanged -= OnStoreChanged;
            _store = null;
        }
    }

    public void Dispose() => Stop();

    public async void Refresh()
    {
        if (!_running) return;
        _pending?.Cancel();
        var cancel = new CancellationTokenSource();
        _pending = cancel;

        // This whole month (for the month grid's event dots) plus at least the next week.
        var today = DateTime.Now.Date;
        var from = new DateTime(today.Year, today.Month, 1);
        var to = new DateTime(today.Year, today.Month, 1).AddMonths(1);
        if (to < today.AddDays(DaysAhead)) to = today.AddDays(DaysAhead);
        var items = new List<CalendarItem>();
        var problems = new List<string>();

        foreach (var url in _settings.CalendarIcsUrls.ToList())
        {
            try
            {
                items.AddRange(await LoadIcsAsync(url, from, to, cancel.Token));
            }
            catch (Exception) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                problems.Add($"Couldn't load {ShortName(url)}");
            }
        }

        if (_settings.WindowsCalendar && PackageIdentity.HasIdentity)
        {
            try
            {
                items.AddRange(await LoadWindowsAsync(from, to));
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                problems.Add("Couldn't read the Windows calendar (Settings > Privacy & security > Calendar)");
            }
        }
        if (cancel.IsCancellationRequested) return;

        // Past events of this month are kept (the month grid and a picked day show them); the page
        // filters to upcoming ones for its default list.
        Items = items
            .GroupBy(i => (i.Title, i.Start)) // the same meeting from two sources
            .Select(g => g.First())
            .OrderBy(i => i.AllDay ? i.Start.Date : i.Start).ThenBy(i => !i.AllDay)
            .ToList();
        Problem = problems.Count == 0 ? null : string.Join("\n", problems);
        ScheduleReminders();
        Changed?.Invoke();
    }

    private static string ShortName(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "a calendar";

    // ---------------------------------------------------------------- iCal

    private static async Task<List<CalendarItem>> LoadIcsAsync(string url, DateTime from, DateTime to, CancellationToken cancel)
    {
        // webcal:// is just https for calendar apps.
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        var text = await Http.GetStringAsync(url, cancel);

        // Parsing and expanding recurrences can take a moment for big calendars; off the UI thread.
        return await Task.Run(() =>
        {
            var calendar = Ical.Net.Calendar.Load(text);
            var color = ParseColor(calendar.Properties["X-APPLE-CALENDAR-COLOR"]?.Value as string);
            var items = new List<CalendarItem>();
            foreach (var occurrence in calendar.GetOccurrences(new CalDateTime(from.ToUniversalTime()), new CalDateTime(to.ToUniversalTime())))
            {
                if (occurrence.Source is not Ical.Net.CalendarComponents.CalendarEvent e) continue;
                if (string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
                var allDay = e.IsAllDay;
                // Via UTC: Ical.Net 4's AsSystemLocal ignores the event's TZID (8:00 New York came out
                // as 8:00 local); AsUtc converts correctly.
                var start = allDay ? occurrence.Period.StartTime.Date : occurrence.Period.StartTime.AsUtc.ToLocalTime();
                var end = occurrence.Period.EndTime is { } endTime
                    ? allDay ? endTime.Date : endTime.AsUtc.ToLocalTime()
                    : start + (occurrence.Period.Duration > TimeSpan.Zero ? occurrence.Period.Duration : allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
                items.Add(new CalendarItem(
                    string.IsNullOrWhiteSpace(e.Summary) ? "(No title)" : e.Summary.Trim(),
                    start, end, allDay,
                    string.IsNullOrWhiteSpace(e.Location) ? null : e.Location.Trim(),
                    FindJoinLink(e.Location, e.Description, e.Url?.ToString()),
                    color));
            }
            return items;
        }, cancel);
    }

    private static Color? ParseColor(string? text)
    {
        try { return text is { Length: > 0 } && ColorConverter.ConvertFromString(text.Length == 9 ? "#" + text[7..] + text[1..7] : text) is Color c ? c : null; }
        catch (FormatException) { return null; }
    }

    private static string? FindJoinLink(params string?[] texts) =>
        texts.Select(t => t is null ? null : JoinLink.Match(t)).FirstOrDefault(m => m?.Success == true)?.Value;

    // ---------------------------------------------------------------- Windows calendar

    private async Task<List<CalendarItem>> LoadWindowsAsync(DateTime from, DateTime to)
    {
        if (_store is null)
        {
            _store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly)
                     ?? throw new InvalidOperationException("No access to the Windows calendar");
            try
            {
                _store.ChangeTracker.Enable();
                _store.StoreChanged += OnStoreChanged;
            }
            catch (Exception) { /* change tracking unavailable: the 10-minute refresh covers it */ }
        }

        var options = new FindAppointmentsOptions();
        foreach (var property in new[]
                 {
                     AppointmentProperties.Subject, AppointmentProperties.StartTime, AppointmentProperties.Duration,
                     AppointmentProperties.AllDay, AppointmentProperties.Location, AppointmentProperties.OnlineMeetingLink,
                     AppointmentProperties.Details, AppointmentProperties.IsCanceledMeeting,
                 })
            options.FetchProperties.Add(property);

        var appointments = await _store.FindAppointmentsAsync(from, to - from, options);
        var items = new List<CalendarItem>();
        foreach (var a in appointments)
        {
            if (a.IsCanceledMeeting) continue;
            if (!_calendarColors.TryGetValue(a.CalendarId, out var color))
            {
                try
                {
                    var calendar = await _store.GetAppointmentCalendarAsync(a.CalendarId);
                    var c = calendar?.DisplayColor;
                    color = c is { } wc ? Color.FromRgb(wc.R, wc.G, wc.B) : null;
                }
                catch (Exception) { color = null; }
                _calendarColors[a.CalendarId] = color;
            }
            var start = a.AllDay ? a.StartTime.Date : a.StartTime.LocalDateTime;
            items.Add(new CalendarItem(
                string.IsNullOrWhiteSpace(a.Subject) ? "(No title)" : a.Subject.Trim(),
                start, start + a.Duration, a.AllDay,
                string.IsNullOrWhiteSpace(a.Location) ? null : a.Location.Trim(),
                a.OnlineMeetingLink is { Length: > 0 } link ? link : FindJoinLink(a.Location, a.Details),
                color));
        }
        return items;
    }

    private void OnStoreChanged(AppointmentStore sender, AppointmentStoreChangedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            sender.ChangeTracker.GetChangeReader().AcceptChanges(); // we re-read everything anyway
        }
        catch (Exception) { /* ignore */ }
        finally
        {
            deferral.Complete();
        }
        _ui.InvokeAsync(Refresh);
    }

    // ---------------------------------------------------------------- reminders

    /// <summary>Wakes exactly when the next reminder is due (no polling).</summary>
    private void ScheduleReminders()
    {
        _reminderTimer.Stop();
        if (_settings.CalendarReminderMinutes <= 0) return;
        var lead = TimeSpan.FromMinutes(_settings.CalendarReminderMinutes);
        var now = DateTime.Now;
        var next = Items
            .Where(i => !i.AllDay && i.Start > now && !_reminded.Contains(Key(i)))
            .Select(i => i.Start - lead)
            .DefaultIfEmpty(DateTime.MaxValue)
            .Min();
        if (next == DateTime.MaxValue) return;
        _reminderTimer.Interval = next <= now ? TimeSpan.FromMilliseconds(50) : next - now + TimeSpan.FromMilliseconds(200);
        _reminderTimer.Start();
    }

    private void CheckReminders()
    {
        _reminderTimer.Stop();
        var lead = TimeSpan.FromMinutes(_settings.CalendarReminderMinutes);
        var now = DateTime.Now;
        foreach (var item in Items.Where(i => !i.AllDay && i.Start > now && i.Start - lead <= now))
        {
            if (!_reminded.Add(Key(item))) continue;
            Reminder?.Invoke(item);
        }
        ScheduleReminders();
    }

    private static string Key(CalendarItem item) => $"{item.Title}|{item.Start:O}";
}
