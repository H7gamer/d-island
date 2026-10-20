// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace DIsland.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer release exists (and isn't being installed automatically).</summary>
    Available,
    Downloading,
    /// <summary>Downloaded and verified; waiting for a good moment to install.</summary>
    ReadyToInstall,
    Installing,
    Failed,
}

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string InstallerUrl, string InstallerName, string ChecksumUrl);

/// <summary>
/// Updates from the official GitHub releases only (https://github.com/H7gamer/d-island).
/// <list type="bullet">
/// <item>Checks the public "latest release" API a minute after start and then once a day (no account,
/// no token; GitHub sees only the IP address and the "D-Island/version" user agent).</item>
/// <item>A release is used only if it has the installer and its ".sha256" file as assets of that
/// release, both served from github.com/H7gamer/d-island. The download is verified against the
/// checksum before anything runs.</item>
/// <item>Installed copies (the installer's uninstaller sits next to the exe) update themselves
/// silently; the installer restarts the island. Development builds only report the update.</item>
/// </list>
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "H7gamer/d-island";

    // Must stay above Http: static fields initialise in declaration order, and the client's user agent
    // needs the version (in the other order it was null, the user agent invalid, and the type unusable).
    public static Version CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static readonly Regex InstallerName = new(@"^D-Island-Setup-(\d+\.\d+\.\d+)\.exe$", RegexOptions.IgnoreCase);
    private static readonly HttpClient Http = CreateClient();

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(10) };
    private bool _busy;

    public UpdateService(AppSettings settings)
    {
        _settings = settings;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    /// <summary>Installed by the setup program (and so able to update itself), not a development build.</summary>
    public static bool IsInstalled =>
#if DEBUG
        Environment.GetEnvironmentVariable("DISLAND_UPDATE_INSTALLED") == "1" ||
#endif
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    // Debug aids: DISLAND_UPDATE_FEED=<url> replaces the GitHub "latest release" URL (and allows assets
    // from http://localhost), DISLAND_UPDATE_INSTALLED=1 treats this build as installed, and
    // DISLAND_UPDATE_DRYRUN=<txt> logs the install command instead of running it.
#if DEBUG
    private static string? DebugFeed => Environment.GetEnvironmentVariable("DISLAND_UPDATE_FEED") is { Length: > 0 } f ? f : null;
#else
    private static string? DebugFeed => null;
#endif

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateInfo? Latest { get; private set; }
    public string? Error { get; private set; }
    public string? DownloadedInstaller { get; private set; }

    /// <summary>State changes (UI thread).</summary>
    public event Action? Changed;

    /// <summary>A verified update is downloaded; the owner decides when to call <see cref="Install"/>.</summary>
    public event Action<UpdateInfo>? ReadyToInstall;

    /// <summary>A newer version exists but won't be installed automatically.</summary>
    public event Action<UpdateInfo>? Available;

    public void Start()
    {
        _timer.Start();
        // First check about a minute after start (startup stays light); TickAsync then runs every 10
        // minutes but only checks when a day has passed since the last check.
        var firstCheck = DebugFeed is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(3);
        _ = Task.Delay(firstCheck).ContinueWith(async _ => await TickAsync(),
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Stop() => _timer.Stop();

    private async Task TickAsync()
    {
        if (!_settings.CheckForUpdates || _busy) return;
        if (State == UpdateState.ReadyToInstall && Latest is { } ready)
        {
            ReadyToInstall?.Invoke(ready); // still waiting for a good moment
            return;
        }
        if (DebugFeed is null && _settings.LastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
        await CheckAsync(manual: false);
    }

    /// <summary>Checks now (Settings button, or the daily schedule) and, if allowed, downloads the update.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            SetState(UpdateState.Checking);
            var info = await FetchLatestAsync();
            _settings.LastUpdateCheck = DateTime.UtcNow;
            _settings.Save();
            Latest = info;
            if (info is null || info.Version <= CurrentVersion)
            {
                SetState(UpdateState.UpToDate);
                return;
            }

            if (!IsInstalled || !(_settings.AutoInstallUpdates || manual))
            {
                SetState(UpdateState.Available);
                Available?.Invoke(info);
                return;
            }

            SetState(UpdateState.Downloading);
            DownloadedInstaller = await DownloadVerifiedAsync(info);
            SetState(UpdateState.ReadyToInstall);
            ReadyToInstall?.Invoke(info);
        }
        catch (Exception ex)
        {
            Error = ex is HttpRequestException or OperationCanceledException or IOException ? "Couldn't reach GitHub" : ex.Message;
#if DEBUG
            if (Environment.GetEnvironmentVariable("DISLAND_UPDATE_LOG") is { Length: > 0 } log)
                File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} exception: {ex.GetType().Name}: {ex.Message}\n");
#endif
            SetState(UpdateState.Failed);
            if (ex is not HttpRequestException and not OperationCanceledException and not IOException and not UpdateException) App.LogError(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Starts the verified installer silently and ends this process; the installer restarts D-Island.</summary>
    public void Install()
    {
        if (State != UpdateState.ReadyToInstall || DownloadedInstaller is not { } installer || !File.Exists(installer)) return;
        SetState(UpdateState.Installing);
        // !identity: the notification setup stays registered across updates (same folder), so don't run it again.
        const string arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=\"!identity\"";
#if DEBUG
        if (Environment.GetEnvironmentVariable("DISLAND_UPDATE_DRYRUN") is { Length: > 0 } dryRun)
        {
            File.WriteAllText(dryRun, $"would run: \"{installer}\" {arguments}\n");
            return;
        }
#endif
        Process.Start(new ProcessStartInfo(installer, arguments)
        {
            UseShellExecute = true,
        })?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void SetState(UpdateState state)
    {
        if (state != UpdateState.Failed) Error = null;
        State = state;
#if DEBUG
        if (Environment.GetEnvironmentVariable("DISLAND_UPDATE_LOG") is { Length: > 0 } log)
            File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {state} {Latest?.Version} {Error}\n");
#endif
        Changed?.Invoke();
    }

    /// <summary>The latest published, non-prerelease release with an installer and its checksum file.</summary>
    private static async Task<UpdateInfo?> FetchLatestAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DebugFeed ?? $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json"); // API only; asset downloads are plain files
        using var response = await Http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        string? installerUrl = null, installerName = null, checksumUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!IsOfficialDownload(url)) continue;
            if (InstallerName.IsMatch(name))
            {
                installerName = name;
                installerUrl = url;
            }
        }
        if (installerName is null) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (asset.GetProperty("name").GetString() == installerName + ".sha256" && IsOfficialDownload(url)) checksumUrl = url;
        }
        if (checksumUrl is null) return null; // never install anything we can't verify

        return new UpdateInfo(version, tag, root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases",
            installerUrl!, installerName, checksumUrl);
    }

    private static bool IsOfficialDownload(string url) =>
        (DebugFeed is not null && url.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase)) ||
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads to %LocalAppData%\DIsland\Updates and checks the SHA-256 before keeping the file.</summary>
    private static async Task<string> DownloadVerifiedAsync(UpdateInfo info)
    {
        var expected = Regex.Match(await Http.GetStringAsync(info.ChecksumUrl), "[0-9a-fA-F]{64}").Value;
        if (expected.Length != 64) throw new UpdateException("The update's checksum file is invalid");

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIsland", "Updates");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder)) TryDelete(old); // earlier downloads
        var target = Path.Combine(folder, info.InstallerName);
        var partial = target + ".partial";

        using (var response = await Http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 400L * 1024 * 1024) throw new UpdateException("The update is unexpectedly large");
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(partial);
            // Copy by hand so a stalled connection fails after 30 s without data, not at the 10-minute timeout.
            var buffer = new byte[81920];
            using var stall = new CancellationTokenSource();
            while (true)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(30));
                int read = await source.ReadAsync(buffer, stall.Token);
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read));
                if (file.Length > 400L * 1024 * 1024) break;
            }
            if (file.Length > 400L * 1024 * 1024) { file.Close(); TryDelete(partial); throw new UpdateException("The update is unexpectedly large"); }
        }

        string actual;
        await using (var file = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partial);
            throw new UpdateException("The downloaded update didn't match its checksum and was deleted");
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* in use or gone */ }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"D-Island/{CurrentVersion}");
        return client;
    }

    private sealed class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }
}
