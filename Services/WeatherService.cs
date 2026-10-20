// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Geolocation;

namespace DIsland.Services;

public enum TemperatureUnit
{
    /// <summary>°F in the US and a few other regions, else °C (from Windows' region setting).</summary>
    Auto,
    Celsius,
    Fahrenheit,
}

public enum WeatherKind
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    Snow,
    Thunder,
}

public sealed record WeatherHour(DateTime Time, double Temperature, WeatherKind Kind, bool IsDay);

public sealed record WeatherDay(DateTime Date, double Min, double Max, WeatherKind Kind);

public sealed record WeatherReport(
    string Place,
    double Temperature,
    double FeelsLike,
    WeatherKind Kind,
    string Condition,
    bool IsDay,
    IReadOnlyList<WeatherHour> Hours,
    IReadOnlyList<WeatherDay> Days,
    bool Fahrenheit,
    DateTime Updated);

public sealed record Place(string Name, string Detail, double Latitude, double Longitude);

/// <summary>
/// Weather from Open-Meteo (free, no key). Location is Windows' (Wi-Fi based, rounded to ~1 km
/// before it leaves the machine) or a city chosen in Settings. Refreshes every 20 minutes, on resume
/// from sleep and when the network comes back; failures keep the last report.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(20) };
    private readonly AppSettings _settings;
    private CancellationTokenSource? _pending;
    private bool _running;

    static WeatherService()
    {
        // Identifies the app (and where to reach its author), as OpenStreetMap's Nominatim policy requires.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"D-Island/{UpdateService.CurrentVersion} (+https://github.com/H7gamer/d-island)");
    }

    public WeatherService(AppSettings settings)
    {
        _settings = settings;
        _refresh.Tick += (_, _) => Refresh();
    }

    public WeatherReport? Current { get; private set; }

    /// <summary>Why there's no (fresh) report, e.g. "Location is off"; null when fine.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised on the UI thread after each refresh attempt.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _refresh.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        Refresh();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _refresh.Stop();
        _pending?.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    public void Dispose() => Stop();

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // The network usually needs a few seconds after resume.
        if (e.Mode == PowerModes.Resume) _ui.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            Refresh();
        });
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) _ui.InvokeAsync(Refresh);
    }

    /// <summary>Fetches now (location / unit settings may have changed). Overlapping calls cancel the older one.</summary>
    public async void Refresh()
    {
        if (!_running) return;
        _pending?.Cancel();
        var cancel = new CancellationTokenSource();
        _pending = cancel;
        try
        {
            var report = await FetchAsync(cancel.Token);
            if (cancel.IsCancellationRequested) return;
            Current = report;
            Problem = null;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return;
        }
        catch (LocationException ex)
        {
            Problem = ex.Message;
        }
        catch (TransientException)
        {
            // Keep the last report; try again in a minute instead of waiting for the 20-minute refresh.
            Problem = Current is null ? "Finding your location…" : null;
            _ = _ui.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                Refresh();
            });
        }
        catch (Exception ex)
        {
            // Offline, DNS, timeouts: keep showing the last report.
            Problem = Current is null ? "Weather unavailable (no connection?)" : null;
            if (ex is not HttpRequestException and not TaskCanceledException) App.LogError(ex);
        }
        Changed?.Invoke();
    }

    public bool UseFahrenheit => _settings.TemperatureUnit switch
    {
        TemperatureUnit.Fahrenheit => true,
        TemperatureUnit.Celsius => false,
        _ => !RegionInfo.CurrentRegion.IsMetric,
    };

    private async Task<WeatherReport> FetchAsync(CancellationToken cancel)
    {
        var (latitude, longitude, place) = await LocateAsync(cancel);
        var fahrenheit = UseFahrenheit;
        // Invariant culture: a comma decimal separator would break the query.
        var coordinates = FormattableString.Invariant($"latitude={latitude:0.##}&longitude={longitude:0.##}");
        var url = $"https://api.open-meteo.com/v1/forecast?{coordinates}" +
            "&current=temperature_2m,apparent_temperature,weather_code,is_day" +
            "&hourly=temperature_2m,weather_code,is_day&forecast_hours=13" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=5&timezone=auto" +
            (fahrenheit ? "&temperature_unit=fahrenheit" : "");

        using var response = await Http.GetAsync(url, cancel);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel));
        var root = json.RootElement;

        var current = root.GetProperty("current");
        var code = current.GetProperty("weather_code").GetInt32();
        var isDay = current.GetProperty("is_day").GetInt32() == 1;

        var hourly = root.GetProperty("hourly");
        var hours = new List<WeatherHour>();
        var times = hourly.GetProperty("time");
        for (var i = 1; i < times.GetArrayLength(); i++) // [0] is the current hour
        {
            hours.Add(new WeatherHour(
                DateTime.Parse(times[i].GetString()!, CultureInfo.InvariantCulture),
                hourly.GetProperty("temperature_2m")[i].GetDouble(),
                Classify(hourly.GetProperty("weather_code")[i].GetInt32()),
                hourly.GetProperty("is_day")[i].GetInt32() == 1));
        }

        var daily = root.GetProperty("daily");
        var days = new List<WeatherDay>();
        var dates = daily.GetProperty("time");
        for (var i = 0; i < dates.GetArrayLength(); i++)
        {
            days.Add(new WeatherDay(
                DateTime.Parse(dates[i].GetString()!, CultureInfo.InvariantCulture),
                daily.GetProperty("temperature_2m_min")[i].GetDouble(),
                daily.GetProperty("temperature_2m_max")[i].GetDouble(),
                Classify(daily.GetProperty("weather_code")[i].GetInt32())));
        }

        return new WeatherReport(place,
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(),
            Classify(code), Describe(code), isDay, hours, days, fahrenheit, DateTime.Now);
    }

    private async Task<(double Latitude, double Longitude, string Place)> LocateAsync(CancellationToken cancel)
    {
        if (!_settings.WeatherUseDeviceLocation)
        {
            if (_settings.WeatherLatitude is { } lat && _settings.WeatherLongitude is { } lon)
                return (lat, lon, _settings.WeatherPlace ?? "");
            throw new LocationException("Choose a city for weather in Settings");
        }

        var access = await Geolocator.RequestAccessAsync().AsTask(cancel);
        if (access != GeolocationAccessStatus.Allowed)
            throw new LocationException("Location is off. Turn it on in Windows, or choose a city in Settings");
        var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
        Geoposition position;
        try
        {
            // An hour-old fix is fine for weather and avoids waking the Wi-Fi scan on every refresh.
            position = await locator.GetGeopositionAsync(TimeSpan.FromHours(1), TimeSpan.FromSeconds(15)).AsTask(cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Location service hiccups (timeouts, "pipe is being closed", Wi-Fi off): temporary, not a bug.
            throw new TransientException();
        }
        var p = position.Coordinate.Point.Position;
        // ~1 km is plenty for weather; don't send a precise location anywhere.
        var (latitude, longitude) = (Math.Round(p.Latitude, 2), Math.Round(p.Longitude, 2));
        return (latitude, longitude, await PlaceNameAsync(latitude, longitude, cancel) ?? "My location");
    }

    /// <summary>
    /// The town or city at the (already rounded) coordinates, from OpenStreetMap's Nominatim, for the
    /// weather page instead of "My location". Asked once per place and remembered in settings, as
    /// Nominatim's usage policy asks; null when it can't be reached (then it's tried on a later refresh).
    /// </summary>
    private async Task<string?> PlaceNameAsync(double latitude, double longitude, CancellationToken cancel)
    {
        var key = FormattableString.Invariant($"{latitude:0.##},{longitude:0.##}");
        if (_settings.WeatherDevicePlaceKey == key && !string.IsNullOrEmpty(_settings.WeatherDevicePlace))
            return _settings.WeatherDevicePlace;
        try
        {
            var url = FormattableString.Invariant(
                $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&zoom=10&addressdetails=1&lat={latitude:0.##}&lon={longitude:0.##}") +
                $"&accept-language={Uri.EscapeDataString(CultureInfo.CurrentUICulture.Name)},en";
            using var response = await Http.GetAsync(url, cancel);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel));
            if (!json.RootElement.TryGetProperty("address", out var address)) return null;
            string? Part(string name) => address.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s ? s : null;
            var name = Part("city") ?? Part("town") ?? Part("village") ?? Part("municipality") ?? Part("suburb")
                       ?? Part("county") ?? Part("state_district") ?? Part("state") ?? Part("country");
            if (name is null) return null;
            _settings.WeatherDevicePlaceKey = key;
            _settings.WeatherDevicePlace = name;
            _settings.Save();
            return name;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
                                   && !cancel.IsCancellationRequested)
        {
            return null; // offline or rate-limited: the forecast still works, with "My location"
        }
    }

    /// <summary>City search for Settings (Open-Meteo geocoding).</summary>
    public static async Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken cancel)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=6&language=en&name={Uri.EscapeDataString(query)}";
        using var response = await Http.GetAsync(url, cancel);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel));
        if (!json.RootElement.TryGetProperty("results", out var results)) return Array.Empty<Place>();
        return results.EnumerateArray().Select(r =>
        {
            string? Text(string name) => r.TryGetProperty(name, out var v) ? v.GetString() : null;
            var detail = string.Join(", ", new[] { Text("admin1"), Text("country") }.Where(s => !string.IsNullOrEmpty(s)));
            return new Place(Text("name") ?? "", detail, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
        }).ToList();
    }

    /// <summary>WMO weather interpretation codes, grouped.</summary>
    private static WeatherKind Classify(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        >= 51 and <= 57 => WeatherKind.Drizzle,
        >= 61 and <= 67 or >= 80 and <= 82 => WeatherKind.Rain,
        >= 71 and <= 77 or 85 or 86 => WeatherKind.Snow,
        >= 95 => WeatherKind.Thunder,
        _ => WeatherKind.Cloudy,
    };

    private static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        _ => code >= 96 ? "Thunderstorm with hail" : "Cloudy",
    };

    private sealed class LocationException : Exception
    {
        public LocationException(string message) : base(message) { }
    }

    /// <summary>A temporary failure: keep the last report and try again shortly.</summary>
    private sealed class TransientException : Exception
    {
    }
}
