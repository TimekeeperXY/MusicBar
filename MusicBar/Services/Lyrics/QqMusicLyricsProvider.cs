using MusicBar.Models;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MusicBar.Services.Lyrics;

public sealed class QqMusicLyricsProvider : ILyricsProvider
{
    private const double MinimumMatchConfidence = 0.78;
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;
    private readonly LyricsDiskCache _cache;
    private readonly LrcLyricsService _parser = new();

    public string Name => "QQ音乐";
    public int Priority => 125;

    public QqMusicLyricsProvider() : this(
        SharedClient,
        new LyricsDiskCache(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MusicBar", "lyrics-cache", "qq", "v1")))
    {
    }

    internal QqMusicLyricsProvider(
        HttpClient client,
        LyricsDiskCache? cache = null)
    {
        _client = client;
        _cache = cache ?? new LyricsDiskCache(Path.Combine(
            Path.GetTempPath(), "MusicBar.Tests", Guid.NewGuid().ToString("N")));
    }

    public bool CanHandle(LyricsTrack track)
    {
        var source = track.SourceAppId.ToLowerInvariant();
        return source.Contains("qqmusic") || source.Contains("qq音乐");
    }

    public async Task<LyricsDocument?> GetLyricsAsync(
        LyricsTrack track,
        CancellationToken cancellationToken)
    {
        var cached = await _cache.TryGetAsync(track, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var candidate = await FindSongAsync(track, cancellationToken);
        if (candidate is null || candidate.MatchConfidence < MinimumMatchConfidence)
        {
            return null;
        }

        var payload = await GetLyricPayloadAsync(candidate.Mid, cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Lyric))
        {
            return null;
        }

        var main = _parser.ParseText(
            WebUtility.HtmlDecode(payload.Lyric),
            $"QQ音乐 #{candidate.Mid}",
            LyricsSourceKind.NativePlayer,
            candidate.MatchConfidence);
        LyricsDocument result;
        if (main.Lines.Count == 0 || string.IsNullOrWhiteSpace(payload.Translation))
        {
            result = main;
        }
        else
        {
            var translated = _parser.ParseText(
                WebUtility.HtmlDecode(payload.Translation),
                "QQ音乐翻译",
                LyricsSourceKind.NativePlayer);
            var translations = translated.Lines
                .GroupBy(line => line.Time)
                .ToDictionary(group => group.Key, group => group.First().Text);
            result = main with
            {
                Lines = main.Lines.Select(line =>
                    translations.TryGetValue(line.Time, out var translation) &&
                    !string.Equals(line.Text, translation, StringComparison.Ordinal)
                        ? line with { Translation = translation }
                        : line).ToArray()
            };
        }

        try
        {
            await _cache.StoreAsync(track, result, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Native lyrics remain usable when the local cache is unavailable.
        }
        return result;
    }

    public void Invalidate(LyricsTrack track) => _cache.Remove(track);

    private async Task<QqMusicCandidate?> FindSongAsync(
        LyricsTrack track,
        CancellationToken cancellationToken)
    {
        var requestPayload = JsonSerializer.Serialize(new
        {
            comm = new { ct = "19", cv = "1859", uin = "0" },
            req = new
            {
                module = "music.search.SearchCgiService",
                method = "DoSearchForQQMusicDesktop",
                param = new
                {
                    query = $"{track.Title} {track.Artist}",
                    num_per_page = 10,
                    page_num = 1,
                    search_type = 0
                }
            }
        });
        using var request = CreateRequest(
            HttpMethod.Post,
            "https://u.y.qq.com/cgi-bin/musicu.fcg");
        request.Content = new StringContent(requestPayload, Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!TryGetSongList(document.RootElement, out var songs))
        {
            return null;
        }

        QqMusicCandidate? best = null;
        foreach (var song in songs.EnumerateArray())
        {
            var mid = GetString(song, "mid");
            var title = GetString(song, "title") ?? GetString(song, "name");
            if (string.IsNullOrWhiteSpace(mid) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var artists = song.TryGetProperty("singer", out var singers) &&
                singers.ValueKind == JsonValueKind.Array
                    ? string.Join("/", singers.EnumerateArray()
                        .Select(singer => GetString(singer, "name"))
                        .Where(name => !string.IsNullOrWhiteSpace(name)))
                    : string.Empty;
            var album = song.TryGetProperty("album", out var albumElement)
                ? GetString(albumElement, "name") ?? string.Empty
                : string.Empty;
            var duration = song.TryGetProperty("interval", out var interval) &&
                interval.TryGetDouble(out var seconds)
                    ? TimeSpan.FromSeconds(Math.Max(0, seconds))
                    : TimeSpan.Zero;
            var confidence = CalculateConfidence(
                track, title, artists, album, duration);
            var candidate = new QqMusicCandidate(
                mid, title, artists, album, duration, confidence);
            if (best is null || candidate.MatchConfidence > best.MatchConfidence)
            {
                best = candidate;
            }
        }
        return best;
    }

    private async Task<QqLyricPayload?> GetLyricPayloadAsync(
        string songMid,
        CancellationToken cancellationToken)
    {
        var url =
            "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg" +
            $"?songmid={Uri.EscapeDataString(songMid)}&format=json&nobase64=1&g_tk=5381";
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if ((root.TryGetProperty("retcode", out var retcode) && retcode.GetInt32() != 0) ||
            (root.TryGetProperty("code", out var code) && code.GetInt32() != 0))
        {
            return null;
        }
        return new QqLyricPayload(
            GetString(root, "lyric"),
            GetString(root, "trans"));
    }

    internal static double CalculateConfidence(
        LyricsTrack track,
        string title,
        string artists,
        string album,
        TimeSpan duration)
    {
        var titleScore = Similarity(track.Title, title);
        var artistScore = Similarity(track.Artist, artists);
        var albumScore = string.IsNullOrWhiteSpace(track.Album) ||
            string.IsNullOrWhiteSpace(album)
                ? 0.5
                : Similarity(track.Album, album);
        var durationDifference = track.Duration <= TimeSpan.Zero ||
            duration <= TimeSpan.Zero
                ? double.NaN
                : Math.Abs((track.Duration - duration).TotalSeconds);
        var durationScore = double.IsNaN(durationDifference)
            ? 0.5
            : durationDifference switch
            {
                <= 2 => 1,
                <= 5 => 0.85,
                <= 10 => 0.45,
                _ => 0
            };
        return (titleScore * 0.45) + (artistScore * 0.35) +
            (durationScore * 0.15) + (albumScore * 0.05);
    }

    private static bool TryGetSongList(JsonElement root, out JsonElement songs)
    {
        songs = default;
        return root.TryGetProperty("req", out var req) &&
            req.TryGetProperty("data", out var data) &&
            data.TryGetProperty("body", out var body) &&
            body.TryGetProperty("song", out var song) &&
            song.TryGetProperty("list", out songs) &&
            songs.ValueKind == JsonValueKind.Array;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double Similarity(string first, string second)
    {
        var left = Normalize(first);
        var right = Normalize(second);
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        return left.Contains(right, StringComparison.Ordinal) ||
            right.Contains(left, StringComparison.Ordinal)
                ? 0.84
                : 0;
    }

    private static string Normalize(string value) => new(
        value.Normalize().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        return request;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MusicBar/1.0.0");
        return client;
    }

    internal sealed record QqMusicCandidate(
        string Mid,
        string Title,
        string Artists,
        string Album,
        TimeSpan Duration,
        double MatchConfidence);

    private sealed record QqLyricPayload(string? Lyric, string? Translation);
}
