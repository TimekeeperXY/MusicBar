using MusicBar.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicBar.Services.Lyrics;

public sealed partial class SodaLyricsProvider : ILyricsProvider
{
    private static readonly byte[] RootRecordSignature =
        [0xd4, 0x72, 0x40, 0x96, 0xaa, 0x72, 0x65, 0x73, 0x6f, 0x75, 0x72, 0x63, 0x65, 0x49, 0x64];

    private readonly string _cachePath;

    public string Name => "汽水音乐";
    public int Priority => 150;

    public SodaLyricsProvider(string? cachePath = null)
    {
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SodaMusic", "LunaCacheV2", "entries.db");
    }

    public bool CanHandle(LyricsTrack track)
    {
        var source = track.SourceAppId.ToLowerInvariant();
        return (source.Contains("汽水") || source.Contains("sodamusic") ||
                source.Contains("qishui") || source.Contains("douyin")) &&
               File.Exists(_cachePath);
    }

    public async Task<LyricsDocument?> GetLyricsAsync(
        LyricsTrack track,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            return null;
        }

        byte[] bytes;
        await using (var stream = new FileStream(
            _cachePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length <= 0 || stream.Length > 512L * 1024 * 1024)
            {
                return null;
            }
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        var offsets = FindCandidateOffsets(bytes, track.Title, track.Artist, cancellationToken);
        SodaCandidate? best = null;
        Exception? lastDecodeError = null;
        SodaCandidate? lastDecodedCandidate = null;
        string? lastDecodedShape = null;
        foreach (var offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var root = new MsgpackrReader(bytes.AsMemory(offset)).Read()
                    as Dictionary<string, object?>;
                var candidate = ExtractCandidate(root);
                if (candidate is null)
                {
                    lastDecodedShape = DescribeShape(root);
                    continue;
                }

                var confidence = CalculateConfidence(track, candidate);
                lastDecodedCandidate = candidate with { Confidence = confidence };
                if (confidence >= 0.82 && (best is null || confidence > best.Confidence))
                {
                    best = candidate with { Confidence = confidence };
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException)
            {
                lastDecodeError = exception;
                // A cache page may contain stale or partial records; other candidates remain usable.
            }
        }

        if (best is null && Environment.GetEnvironmentVariable("MUSICBAR_SODA_DEBUG") == "1")
        {
            throw new InvalidDataException(
                $"Failed to match {offsets.Count} Soda cache candidate(s). " +
                $"Last decoded: {lastDecodedCandidate?.Title ?? "none"} / " +
                $"{string.Join(',', lastDecodedCandidate?.Artists ?? [])} / " +
                $"{lastDecodedCandidate?.Duration.TotalSeconds:F1}s / " +
                $"{lastDecodedCandidate?.Confidence:F2}. Shape: {lastDecodedShape}", lastDecodeError);
        }

        if (best is null || string.IsNullOrWhiteSpace(best.Content))
        {
            return null;
        }

        var lines = ParseKrc(best.Content);
        return lines.Count == 0 ? null : new LyricsDocument(
            $"汽水音乐 #{best.TrackId}",
            LyricsSourceKind.NativePlayer,
            lines,
            best.Confidence);
    }

    internal static IReadOnlyList<LyricsLine> ParseKrc(string content)
    {
        var lines = new List<LyricsLine>();
        foreach (var rawLine in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = KrcLineRegex().Match(rawLine);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var milliseconds))
            {
                continue;
            }

            var text = KrcCharacterRegex().Replace(match.Groups[3].Value, string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(new LyricsLine(TimeSpan.FromMilliseconds(milliseconds), text));
            }
        }
        return lines.OrderBy(line => line.Time).ToArray();
    }

    internal static double CalculateConfidence(LyricsTrack track, SodaCandidate candidate)
    {
        var title = Similarity(track.Title, candidate.Title);
        var artist = candidate.Artists.Count == 0 ? 0 :
            candidate.Artists.Max(value => Similarity(track.Artist, value));
        var durationDifference = track.Duration <= TimeSpan.Zero || candidate.Duration <= TimeSpan.Zero
            ? double.NaN
            : Math.Abs(track.Duration.TotalSeconds - candidate.Duration.TotalSeconds);
        var duration = double.IsNaN(durationDifference) ? 0.5 : durationDifference switch
        {
            <= 2 => 1,
            <= 5 => 0.8,
            <= 10 => 0.35,
            _ => 0
        };
        return (title * 0.55) + (artist * 0.35) + (duration * 0.10);
    }

    private static IReadOnlyList<int> FindCandidateOffsets(
        byte[] bytes,
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        var offsets = FindOffsetsForNeedle(bytes, Encoding.UTF8.GetBytes(title), cancellationToken);
        if (offsets.Count == 0 && !string.IsNullOrWhiteSpace(artist))
        {
            offsets = FindOffsetsForNeedle(bytes, Encoding.UTF8.GetBytes(artist), cancellationToken);
        }
        return offsets;
    }

    private static List<int> FindOffsetsForNeedle(
        byte[] bytes,
        byte[] needle,
        CancellationToken cancellationToken)
    {
        var results = new HashSet<int>();
        if (needle.Length < 2)
        {
            return [];
        }

        var source = bytes.AsSpan();
        var searchStart = 0;
        while (searchStart <= source.Length - needle.Length && results.Count < 80)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = source[searchStart..].IndexOf(needle);
            if (relative < 0)
            {
                break;
            }
            var occurrence = searchStart + relative;
            var recordStart = FindPrevious(source, RootRecordSignature, occurrence, 2 * 1024 * 1024);
            if (recordStart >= 0)
            {
                results.Add(recordStart);
            }
            searchStart = occurrence + needle.Length;
        }
        return results.ToList();
    }

    private static int FindPrevious(ReadOnlySpan<byte> source, ReadOnlySpan<byte> needle, int before, int limit)
    {
        var start = Math.Max(0, before - limit);
        for (var index = Math.Min(before, source.Length - needle.Length); index >= start; index--)
        {
            if (source[index] == needle[0] && source.Slice(index, needle.Length).SequenceEqual(needle))
            {
                return index;
            }
        }
        return -1;
    }

    private static SodaCandidate? ExtractCandidate(Dictionary<string, object?>? root)
    {
        var info = GetMap(root, "info");
        var detail = GetMap(info, "mediaDetail");
        var playable = GetMap(detail, "playable");
        var lyrics = GetMap(detail, "lyrics");
        var title = GetString(playable, "name");
        var content = GetString(lyrics, "content");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var artists = GetList(playable, "artists")
            .Select(value => GetString(value as Dictionary<string, object?>, "name"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        var durationMilliseconds = GetNumber(playable, "duration");

        return new SodaCandidate(
            GetString(info, "trackId") ?? string.Empty,
            title,
            artists,
            durationMilliseconds > 0 ? TimeSpan.FromMilliseconds(durationMilliseconds) : TimeSpan.Zero,
            content,
            0);
    }

    private static string DescribeShape(Dictionary<string, object?>? root)
    {
        var info = GetMap(root, "info");
        var detail = GetMap(info, "mediaDetail");
        var playable = GetMap(detail, "playable");
        var lyrics = GetMap(detail, "lyrics");
        static string Keys(Dictionary<string, object?>? value) =>
            value is null ? string.Empty : string.Join(',', value.Keys);
        static string TypeOf(Dictionary<string, object?>? value, string key) =>
            value is not null && value.TryGetValue(key, out var item)
                ? item?.GetType().FullName ?? "null"
                : "missing";
        return $"root=[{Keys(root)}] info=[{Keys(info)}] detail=[{Keys(detail)}] " +
               $"playable=[{Keys(playable)}]({TypeOf(detail, "playable")}) " +
               $"lyrics=[{Keys(lyrics)}]({TypeOf(detail, "lyrics")})";
    }

    private static Dictionary<string, object?>? GetMap(Dictionary<string, object?>? source, string key) =>
        source is not null && source.TryGetValue(key, out var value)
            ? value as Dictionary<string, object?>
            : null;

    private static List<object?> GetList(Dictionary<string, object?>? source, string key) =>
        source is not null && source.TryGetValue(key, out var value) && value is List<object?> list
            ? list
            : [];

    private static string? GetString(Dictionary<string, object?>? source, string key) =>
        source is not null && source.TryGetValue(key, out var value) ? value as string : null;

    private static double GetNumber(Dictionary<string, object?>? source, string key)
    {
        if (source is null || !source.TryGetValue(key, out var value)) return 0;
        return value switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number => number,
            ushort number => number,
            uint number => number,
            ulong number => number,
            float number => number,
            double number => number,
            _ => 0
        };
    }

    private static double Similarity(string first, string second)
    {
        var left = Normalize(first);
        var right = Normalize(second);
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)) return 0.82;
        return 0;
    }

    private static string Normalize(string value) => new(
        value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Where(char.IsLetterOrDigit).ToArray());

    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex KrcLineRegex();

    [GeneratedRegex(@"<\d+,\d+,\d+>", RegexOptions.CultureInvariant)]
    private static partial Regex KrcCharacterRegex();

    internal sealed record SodaCandidate(
        string TrackId,
        string Title,
        IReadOnlyList<string> Artists,
        TimeSpan Duration,
        string Content,
        double Confidence);
}
