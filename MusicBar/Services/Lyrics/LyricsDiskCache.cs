using MusicBar.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MusicBar.Services.Lyrics;

internal sealed class LyricsDiskCache
{
    private const int SchemaVersion = 1;
    private const int MaximumCacheBytes = 10 * 1024 * 1024;
    private const int MaximumLineCount = 20_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _cacheDirectory;

    public LyricsDiskCache(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MusicBar", "lyrics-cache", "v1");
    }

    public async Task<LyricsDocument?> TryGetAsync(
        LyricsTrack track,
        CancellationToken cancellationToken)
    {
        var path = GetCachePath(track);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > MaximumCacheBytes)
            {
                return null;
            }

            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var entry = await JsonSerializer.DeserializeAsync<CacheEntry>(
                stream, JsonOptions, cancellationToken);
            return ValidateAndCreateDocument(track, entry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A partial or user-modified cache file must never break lyric lookup.
            return null;
        }
    }

    public async Task StoreAsync(
        LyricsTrack track,
        LyricsDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Lines.Count == 0 || document.Lines.Count > MaximumLineCount)
        {
            return;
        }

        Directory.CreateDirectory(_cacheDirectory);
        var path = GetCachePath(track);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var entry = new CacheEntry
        {
            SchemaVersion = SchemaVersion,
            CachedAtUtc = DateTimeOffset.UtcNow,
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            DurationMilliseconds = checked((long)Math.Round(track.Duration.TotalMilliseconds)),
            Source = document.Source,
            MatchConfidence = document.MatchConfidence,
            Lines = document.Lines.Select(line => new CacheLine
            {
                TimeMilliseconds = checked((long)Math.Round(line.Time.TotalMilliseconds)),
                Text = line.Text,
                Translation = line.Translation
            }).ToArray()
        };

        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, entry, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stale temporary file is harmless and can be overwritten later.
            }
        }
    }

    public void Remove(LyricsTrack track)
    {
        File.Delete(GetCachePath(track));
    }

    internal string GetCachePath(LyricsTrack track)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(track.StableKey));
        return Path.Combine(_cacheDirectory, $"{Convert.ToHexStringLower(hash)}.json");
    }

    private static LyricsDocument? ValidateAndCreateDocument(
        LyricsTrack requestedTrack,
        CacheEntry? entry)
    {
        if (entry is null || entry.SchemaVersion != SchemaVersion ||
            entry.Lines is not { Length: > 0 and <= MaximumLineCount } ||
            string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Artist))
        {
            return null;
        }

        var cachedTrack = new LyricsTrack(
            entry.Title, entry.Artist, entry.Album ?? string.Empty,
            TimeSpan.FromMilliseconds(Math.Max(0, entry.DurationMilliseconds)), string.Empty);
        if (!string.Equals(requestedTrack.StableKey, cachedTrack.StableKey, StringComparison.Ordinal))
        {
            return null;
        }

        if (requestedTrack.Duration > TimeSpan.Zero && cachedTrack.Duration > TimeSpan.Zero &&
            Math.Abs((requestedTrack.Duration - cachedTrack.Duration).TotalSeconds) > 10)
        {
            return null;
        }

        var lines = entry.Lines
            .Where(line => line.TimeMilliseconds >= 0 && !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => new LyricsLine(
                TimeSpan.FromMilliseconds(line.TimeMilliseconds),
                line.Text,
                line.Translation))
            .OrderBy(line => line.Time)
            .ToArray();
        if (lines.Length == 0)
        {
            return null;
        }

        return new LyricsDocument(
            string.IsNullOrWhiteSpace(entry.Source) ? "在线歌词缓存" : entry.Source,
            LyricsSourceKind.Online,
            lines,
            Math.Clamp(entry.MatchConfidence, 0, 1));
    }

    private sealed class CacheEntry
    {
        public int SchemaVersion { get; init; }
        public DateTimeOffset CachedAtUtc { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public string? Album { get; init; }
        public long DurationMilliseconds { get; init; }
        public string Source { get; init; } = string.Empty;
        public double MatchConfidence { get; init; }
        public CacheLine[] Lines { get; init; } = [];
    }

    private sealed class CacheLine
    {
        public long TimeMilliseconds { get; init; }
        public string Text { get; init; } = string.Empty;
        public string? Translation { get; init; }
    }
}
