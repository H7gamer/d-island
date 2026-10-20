// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DIsland.Services;

/// <summary>
/// Looks up Apple Music "motion artwork" (the looping album videos) for the playing album and
/// caches it as a local MP4.
/// <para>
/// Apple's official Music API doesn't expose motion artwork to third-party apps, so this uses the
/// same unofficial route as the open-source downloaders: the anonymous token embedded in the
/// music.apple.com web player, and the catalog endpoint with <c>extend=editorialVideo</c>. It can
/// break whenever Apple changes the web player, and it is opt-in. Every result, including "this
/// album has none", is cached on disk so each album is looked up once.
/// </para>
/// </summary>
public sealed class MotionArtworkService : IDisposable
{
    private const string WebPlayer = "https://music.apple.com";
    private const string CatalogApi = "https://amp-api.music.apple.com/v1/catalog";
    private const long MaxCacheBytes = 200L * 1024 * 1024;
    // Measured while expanded: 15 fps ≈ 17% of a core, 24 fps ≈ 27%. Motion art is slow-moving.
    private const uint MotionFrameRate = 15;
    private static readonly TimeSpan NegativeCacheTime = TimeSpan.FromDays(7);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIsland", "MotionArt");

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _index;
    private string? _token;
    private DateTimeOffset _tokenExpiry;

    private sealed record CacheEntry(string? File, DateTimeOffset Checked);

    public MotionArtworkService()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        // The web player and its API expect a browser.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        _index = LoadIndex();
    }

    /// <summary>
    /// Path of a cached MP4 loop for this album, downloading it first if needed; null when the album
    /// has no motion artwork (or the lookup failed). Safe to call repeatedly: cached results return
    /// immediately.
    /// </summary>
    public async Task<string?> GetAsync(string artist, string album, string title, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(artist) || (string.IsNullOrWhiteSpace(album) && string.IsNullOrWhiteSpace(title)))
            return null;

        var key = Normalize($"{artist}|{(string.IsNullOrWhiteSpace(album) ? title : album)}");
        await _gate.WaitAsync(cancel);
        try
        {
            if (_index.TryGetValue(key, out var cached))
            {
                if (cached.File is not null && File.Exists(cached.File)) return cached.File;
                if (cached.File is null && DateTimeOffset.UtcNow - cached.Checked < NegativeCacheTime) return null;
            }

            string? file = null;
            try
            {
                file = await LookUpAndDownloadAsync(key, artist, album, title, cancel);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return null; // network/format failure: don't cache, try again next time
            }

            _index[key] = new CacheEntry(file, DateTimeOffset.UtcNow);
            SaveIndex();
            TrimCache();
            return file;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    // ---------------------------------------------------------------- lookup

    private async Task<string?> LookUpAndDownloadAsync(string key, string artist, string album, string title,
        CancellationToken cancel)
    {
        var storefront = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant();
        var albumId = await FindAlbumIdAsync(storefront, artist, album, title, cancel)
                      ?? (storefront != "us" ? await FindAlbumIdAsync("us", artist, album, title, cancel) : null);
        if (albumId is null) return null;

        var playlist = await GetMotionPlaylistAsync(storefront, albumId, cancel)
                       ?? (storefront != "us" ? await GetMotionPlaylistAsync("us", albumId, cancel) : null);
        if (playlist is null) return null;

        Directory.CreateDirectory(CacheDir);
        var file = Path.Combine(CacheDir, $"{albumId}.mp4");
        return await DownloadLoopAsync(playlist, file, cancel) ? file : null;
    }

    /// <summary>Apple's public iTunes Search API (no token) maps artist + album/title to an album id.</summary>
    private async Task<string?> FindAlbumIdAsync(string storefront, string artist, string album, string title,
        CancellationToken cancel)
    {
        var byAlbum = !string.IsNullOrWhiteSpace(album);
        var term = Uri.EscapeDataString($"{artist} {(byAlbum ? album : title)}");
        var url = $"https://itunes.apple.com/search?term={term}&entity={(byAlbum ? "album" : "song")}&limit=10&country={storefront}";
        using var response = await _http.GetAsync(url, cancel);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel));

        var wantArtist = Normalize(artist);
        var wantName = Normalize(byAlbum ? album : title);
        string? fallback = null;
        foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            if (!item.TryGetProperty("collectionId", out var idElement)) continue;
            var id = idElement.GetInt64().ToString(CultureInfo.InvariantCulture);
            var itemArtist = Normalize(item.TryGetProperty("artistName", out var a) ? a.GetString() : null);
            var itemName = Normalize(item.TryGetProperty(byAlbum ? "collectionName" : "trackName", out var n) ? n.GetString() : null);

            var artistMatches = itemArtist.Contains(wantArtist) || wantArtist.Contains(itemArtist);
            if (!artistMatches || itemArtist.Length == 0) continue;
            if (itemName == wantName) return id;
            if (fallback is null && (itemName.Contains(wantName) || wantName.Contains(itemName))) fallback = id;
        }
        return fallback;
    }

    /// <summary>The album's square motion-art HLS playlist URL, or null if it has none.</summary>
    private async Task<string?> GetMotionPlaylistAsync(string storefront, string albumId, CancellationToken cancel)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetTokenAsync(forceRefresh: attempt > 0, cancel);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{CatalogApi}/{storefront}/albums/{albumId}?extend=editorialVideo");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Origin", WebPlayer);
            using var response = await _http.SendAsync(request, cancel);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) continue; // stale token
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel));
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return null;
            if (!data[0].GetProperty("attributes").TryGetProperty("editorialVideo", out var video)) return null;

            foreach (var kind in new[] { "motionDetailSquare", "motionSquareVideo1x1" })
            {
                if (video.TryGetProperty(kind, out var entry) && entry.TryGetProperty("video", out var url)
                    && url.GetString() is { Length: > 0 } playlist)
                    return playlist;
            }
            return null;
        }
        throw new InvalidOperationException("Apple Music rejected the web-player token.");
    }

    // ---------------------------------------------------------------- token

    /// <summary>
    /// The web player ships an anonymous JWT (header kid "WebPlayKid") inside its main JS bundle.
    /// Cached in memory and on disk until shortly before it expires.
    /// </summary>
    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancel)
    {
        var tokenFile = Path.Combine(CacheDir, "token.txt");
        if (!forceRefresh)
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;
            if (File.Exists(tokenFile) && File.ReadAllText(tokenFile).Trim() is { Length: > 0 } saved
                && TryReadJwt(saved, out _, out var savedExpiry) && DateTimeOffset.UtcNow < savedExpiry)
            {
                (_token, _tokenExpiry) = (saved, savedExpiry);
                return saved;
            }
        }

        var html = await _http.GetStringAsync($"{WebPlayer}/us/browse", cancel);
        foreach (Match script in Regex.Matches(html, "src=\"(/assets/index[^\"]*\\.js)\""))
        {
            var js = await _http.GetStringAsync(WebPlayer + script.Groups[1].Value, cancel);
            foreach (Match jwt in Regex.Matches(js, @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+"))
            {
                if (!TryReadJwt(jwt.Value, out var kid, out var expiry) || kid != "WebPlayKid") continue;
                (_token, _tokenExpiry) = (jwt.Value, expiry);
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(tokenFile, jwt.Value);
                return jwt.Value;
            }
        }
        throw new InvalidOperationException("Couldn't find the Apple Music web-player token.");
    }

    private static bool TryReadJwt(string jwt, out string? kid, out DateTimeOffset expiry)
    {
        kid = null;
        expiry = DateTimeOffset.MinValue;
        try
        {
            var parts = jwt.Split('.');
            using var header = JsonDocument.Parse(Base64Url(parts[0]));
            kid = header.RootElement.TryGetProperty("kid", out var k) ? k.GetString() : null;
            using var payload = JsonDocument.Parse(Base64Url(parts[1]));
            expiry = payload.RootElement.TryGetProperty("exp", out var exp)
                ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).AddHours(-1)
                : DateTimeOffset.UtcNow.AddDays(1);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] Base64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    // ---------------------------------------------------------------- HLS â†’ MP4

    /// <summary>
    /// Picks the smallest H.264 variant at least 480px wide (sharp enough for the banner, cheap to
    /// decode, and H.264 needs no codec extension), then concatenates its fMP4 init segment and
    /// media segments into a single MP4 file.
    /// </summary>
    private async Task<bool> DownloadLoopAsync(string masterUrl, string file, CancellationToken cancel)
    {
        var master = await _http.GetStringAsync(masterUrl, cancel);
        var variants = ParseVariants(master, new Uri(masterUrl))
            .Where(v => v.Codecs.Contains("avc1", StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.Width)
            .ThenBy(v => v.Bandwidth) // Apple lists several bitrates per size; the banner needs the lightest
            .ToList();
        var variant = variants.FirstOrDefault(v => v.Width >= 480) ?? variants.LastOrDefault();
        if (variant is null) return false;

        var mediaUrl = variant.Uri;
        var media = await _http.GetStringAsync(mediaUrl, cancel);
        string? init = null;
        var segments = new List<Uri>();
        foreach (var raw in media.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var m = Regex.Match(line, "URI=\"([^\"]+)\"");
                if (m.Success) init = m.Groups[1].Value;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                segments.Add(new Uri(mediaUrl, line));
            }
        }
        if (init is null || segments.Count == 0) return false; // not fMP4 (e.g. MPEG-TS): can't play as one MP4

        var fragmented = file + ".fmp4";
        await using (var output = File.Create(fragmented))
        {
            foreach (var part in segments.Prepend(new Uri(mediaUrl, init)))
            {
                await using var stream = await _http.GetStreamAsync(part, cancel);
                await stream.CopyToAsync(output, cancel);
            }
        }

        try
        {
            return await ToRegularMp4Async(fragmented, file, variant.Width, cancel);
        }
        finally
        {
            File.Delete(fragmented);
        }
    }

    /// <summary>
    /// HLS loops are fragmented MP4, which WPF's MediaElement opens but won't play (no duration,
    /// position stuck at 0). Windows' built-in, hardware-accelerated transcoder rewrites the loop
    /// once as a regular MP4 (video only, same size).
    /// </summary>
    private static async Task<bool> ToRegularMp4Async(string source, string destination, int size, CancellationToken cancel)
    {
        var input = await Windows.Storage.StorageFile.GetFileFromPathAsync(source);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
        var output = await folder.CreateFileAsync(Path.GetFileName(destination) + ".part",
            Windows.Storage.CreationCollisionOption.ReplaceExisting);

        var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(
            Windows.Media.MediaProperties.VideoEncodingQuality.Auto);
        profile.Audio = null; // motion art has no audio track
        profile.Video.Width = (uint)size;
        profile.Video.Height = (uint)size;
        profile.Video.Bitrate = 1_200_000;
        // Every decoded frame makes WPF redraw the whole transparent island window, so the frame
        // rate is the main cost of playing it.
        profile.Video.FrameRate.Numerator = MotionFrameRate;
        profile.Video.FrameRate.Denominator = 1;

        var transcoder = new Windows.Media.Transcoding.MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareFileTranscodeAsync(input, output, profile);
        if (!prepared.CanTranscode)
        {
            await output.DeleteAsync();
            return false;
        }
        await prepared.TranscodeAsync().AsTask(cancel);
        File.Move(output.Path, destination, overwrite: true);
        return true;
    }

    private sealed record Variant(Uri Uri, int Width, long Bandwidth, string Codecs);

    private static IEnumerable<Variant> ParseVariants(string master, Uri baseUri)
    {
        var lines = master.Split('\n').Select(l => l.Trim()).ToArray();
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            var resolution = Regex.Match(lines[i], @"RESOLUTION=(\d+)x(\d+)");
            var codecs = Regex.Match(lines[i], "CODECS=\"([^\"]+)\"");
            var bandwidth = Regex.Match(lines[i], @"(?<![-_])BANDWIDTH=(\d+)");
            var uri = lines[i + 1];
            if (!resolution.Success || uri.StartsWith('#') || uri.Length == 0) continue;
            yield return new Variant(
                new Uri(baseUri, uri),
                int.Parse(resolution.Groups[1].Value, CultureInfo.InvariantCulture),
                bandwidth.Success ? long.Parse(bandwidth.Groups[1].Value, CultureInfo.InvariantCulture) : long.MaxValue,
                codecs.Success ? codecs.Groups[1].Value : "");
        }
    }

    // ---------------------------------------------------------------- cache

    /// <summary>Lowercase, drop bracketed edition notes and punctuation: "Positions (Deluxe)" â†’ "positions".</summary>
    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = Regex.Replace(s.ToLowerInvariant(), @"[\(\[].*?[\)\]]", " ");
        s = Regex.Replace(s, @"\s+-\s+(single|ep)$", " ");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormKC))
            sb.Append(char.IsLetterOrDigit(c) || c == '|' ? c : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string IndexFile => Path.Combine(CacheDir, "index.json");

    private static Dictionary<string, CacheEntry> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexFile))
                return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(IndexFile)) ?? new();
        }
        catch (Exception)
        {
            // Corrupt index: start over (files are re-found by album id on the next lookup).
        }
        return new Dictionary<string, CacheEntry>();
    }

    private void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(IndexFile, JsonSerializer.Serialize(_index));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Keeps the cache under 200 MB by deleting the least recently written loops.</summary>
    private void TrimCache()
    {
        try
        {
            var files = new DirectoryInfo(CacheDir).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > MaxCacheBytes) f.Delete();
            }
        }
        catch (Exception)
        {
        }
    }
}
