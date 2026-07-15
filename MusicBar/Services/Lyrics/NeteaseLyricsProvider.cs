using MusicBar.Models;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicBar.Services.Lyrics;

public sealed class NeteaseLyricsProvider : ILyricsProvider
{
    private static readonly HttpClient Client = CreateClient();
    private readonly LrcLyricsService _parser = new();

    public string Name => "网易云音乐";
    public int Priority => 100;

    public bool CanHandle(LyricsTrack track)
    {
        var source = track.SourceAppId.ToLowerInvariant();
        return source.Contains("cloudmusic") || source.Contains("netease");
    }

    public async Task<LyricsDocument?> GetLyricsAsync(
        LyricsTrack track,
        CancellationToken cancellationToken)
    {
        var trackId = await FindTrackIdAsync(track, cancellationToken);
        if (trackId is null)
        {
            return null;
        }

        using var response = await Client.GetAsync(
            $"api/song/lyric?id={Uri.EscapeDataString(trackId)}&lv=1&kv=1&tv=-1",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<NeteaseLyricsResponse>(cancellationToken);
        if (payload?.Code != 200 || string.IsNullOrWhiteSpace(payload.Lrc?.Lyric))
        {
            return null;
        }

        var main = _parser.ParseText(
            payload.Lrc.Lyric,
            $"网易云音乐 #{trackId}",
            LyricsSourceKind.NativePlayer,
            1);
        if (string.IsNullOrWhiteSpace(payload.TranslatedLyrics?.Lyric))
        {
            return main;
        }

        var translated = _parser.ParseText(
            payload.TranslatedLyrics.Lyric,
            "网易云音乐翻译",
            LyricsSourceKind.NativePlayer);
        var translations = translated.Lines
            .GroupBy(line => line.Time)
            .ToDictionary(group => group.Key, group => group.First().Text);
        var merged = main.Lines
            .Select(line => translations.TryGetValue(line.Time, out var translation)
                ? line with { Translation = translation }
                : line)
            .ToArray();
        return main with { Lines = merged };
    }

    private static async Task<string?> FindTrackIdAsync(
        LyricsTrack target,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase", "CloudMusic", "webdata", "file", "playingList");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("list", out var list))
            {
                return null;
            }

            string? bestId = null;
            var bestScore = 0d;
            foreach (var item in list.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.TryGetProperty("track", out var track))
                {
                    continue;
                }

                var title = GetString(track, "name");
                var durationMilliseconds = GetNumber(track, "duration");
                var artists = track.TryGetProperty("artists", out var artistArray)
                    ? string.Join('/', artistArray.EnumerateArray().Select(artist => GetString(artist, "name")))
                    : string.Empty;
                var score = CalculateLocalMatch(target, title, artists, durationMilliseconds / 1000d);
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestId = GetString(item, "id");
            }

            return bestScore >= 0.82 ? bestId : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static double CalculateLocalMatch(
        LyricsTrack target,
        string title,
        string artist,
        double durationSeconds)
    {
        var titleScore = Normalize(target.Title) == Normalize(title) ? 1 : 0;
        var targetArtist = Normalize(target.Artist);
        var candidateArtist = Normalize(artist);
        var artistScore = targetArtist == candidateArtist ||
                          targetArtist.Contains(candidateArtist, StringComparison.Ordinal) ||
                          candidateArtist.Contains(targetArtist, StringComparison.Ordinal)
            ? 1 : 0;
        var difference = Math.Abs(target.Duration.TotalSeconds - durationSeconds);
        var durationScore = target.Duration <= TimeSpan.Zero
            ? 0.5
            : difference <= 2 ? 1 : difference <= 5 ? 0.8 : difference <= 10 ? 0.3 : 0;
        return (titleScore * 0.5) + (artistScore * 0.3) + (durationScore * 0.2);
    }

    private static string Normalize(string value) => new(
        value.Normalize().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString()
            : string.Empty;

    private static double GetNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) ? number : 0;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://music.163.com/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MusicBar/0.2.0-alpha");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
    }

    private sealed class NeteaseLyricsResponse
    {
        [JsonPropertyName("code")] public int Code { get; init; }
        [JsonPropertyName("lrc")] public LyricsPayload? Lrc { get; init; }
        [JsonPropertyName("tlyric")] public LyricsPayload? TranslatedLyrics { get; init; }
    }

    private sealed class LyricsPayload
    {
        [JsonPropertyName("lyric")] public string? Lyric { get; init; }
    }
}
