using MusicBar.Models;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MusicBar.Services.Lyrics;

public sealed class LrclibLyricsProvider : ILyricsProvider
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;
    private readonly LrcLyricsService _parser = new();

    public string Name => "LRCLIB";
    public int Priority => 200;

    public LrclibLyricsProvider() : this(SharedClient)
    {
    }

    internal LrclibLyricsProvider(HttpClient client) => _client = client;

    public bool CanHandle(LyricsTrack track) =>
        !string.IsNullOrWhiteSpace(track.Title) &&
        !string.IsNullOrWhiteSpace(track.Artist) &&
        track.Duration > TimeSpan.Zero;

    public async Task<LyricsDocument?> GetLyricsAsync(
        LyricsTrack track,
        CancellationToken cancellationToken)
    {
        Exception? exactFailure = null;
        LrclibResult? result = null;
        try
        {
            result = await GetExactAsync(track, cancellationToken);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or OperationCanceledException)
        {
            // A slow or temporarily unavailable exact endpoint should not
            // prevent the broader search endpoint from finding the lyrics.
            exactFailure = exception;
        }

        result ??= await SearchAsync(track, cancellationToken);
        if (result is null && exactFailure is not null)
        {
            throw new HttpRequestException(
                "LRCLIB exact lookup failed and the fallback search returned no result.",
                exactFailure);
        }

        if (result is null || string.IsNullOrWhiteSpace(result.SyncedLyrics))
        {
            return null;
        }

        var confidence = CalculateConfidence(track, result);
        if (confidence < 0.72)
        {
            return null;
        }

        return _parser.ParseText(
            result.SyncedLyrics,
            $"LRCLIB #{result.Id}",
            LyricsSourceKind.Online,
            confidence);
    }

    private async Task<LrclibResult?> GetExactAsync(
        LyricsTrack track,
        CancellationToken cancellationToken)
    {
        var parameters = BuildMetadataQuery(track, includeAlbum: true, includeDuration: true);
        using var response = await _client.GetAsync($"api/get?{parameters}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LrclibResult>(cancellationToken);
    }

    private async Task<LrclibResult?> SearchAsync(
        LyricsTrack track,
        CancellationToken cancellationToken)
    {
        var parameters = BuildMetadataQuery(track, includeAlbum: false, includeDuration: false);
        var results = await _client.GetFromJsonAsync<LrclibResult[]>(
            $"api/search?{parameters}", cancellationToken) ?? [];

        return results
            .Where(result => !string.IsNullOrWhiteSpace(result.SyncedLyrics))
            .Select(result => new { Result = result, Score = CalculateConfidence(track, result) })
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault()?.Result;
    }

    private static string BuildMetadataQuery(
        LyricsTrack track,
        bool includeAlbum,
        bool includeDuration)
    {
        var values = new List<string>
        {
            $"track_name={Uri.EscapeDataString(track.Title)}",
            $"artist_name={Uri.EscapeDataString(track.Artist)}"
        };
        if (includeAlbum && !string.IsNullOrWhiteSpace(track.Album))
        {
            values.Add($"album_name={Uri.EscapeDataString(track.Album)}");
        }
        if (includeDuration)
        {
            values.Add($"duration={Math.Round(track.Duration.TotalSeconds)}");
        }
        return string.Join('&', values);
    }

    internal static double CalculateConfidence(LyricsTrack track, LrclibResult candidate)
    {
        var title = Similarity(track.Title, candidate.TrackName);
        var artist = Similarity(track.Artist, candidate.ArtistName);
        var album = string.IsNullOrWhiteSpace(track.Album) || string.IsNullOrWhiteSpace(candidate.AlbumName)
            ? 0.5
            : Similarity(track.Album, candidate.AlbumName);
        var difference = Math.Abs(track.Duration.TotalSeconds - candidate.Duration);
        var duration = difference switch
        {
            <= 2 => 1,
            <= 5 => 0.8,
            <= 10 => 0.35,
            _ => 0
        };
        return (title * 0.4) + (artist * 0.3) + (duration * 0.2) + (album * 0.1);
    }

    private static double Similarity(string first, string second)
    {
        var left = Normalize(first);
        var right = Normalize(second);
        if (left == right) return 1;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)) return 0.82;
        return 0;
    }

    private static string Normalize(string value) => new(
        value.Normalize().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net/"),
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicBar/0.2.0-alpha (Windows)");
        return client;
    }

    internal sealed class LrclibResult
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("trackName")] public string TrackName { get; init; } = string.Empty;
        [JsonPropertyName("artistName")] public string ArtistName { get; init; } = string.Empty;
        [JsonPropertyName("albumName")] public string AlbumName { get; init; } = string.Empty;
        [JsonPropertyName("duration")] public double Duration { get; init; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; init; }
    }
}
