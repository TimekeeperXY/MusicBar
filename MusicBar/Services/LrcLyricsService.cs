using MusicBar.Models;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MusicBar.Services;

public sealed partial class LrcLyricsService
{
    public string? LoadedPath { get; private set; }
    public LyricsDocument? Document { get; private set; }

    public void Load(string path)
    {
        Document = Parse(path);
        LoadedPath = path;
    }

    public LyricsDocument Parse(string path)
    {
        var document = ParseText(
            File.ReadAllText(path),
            Path.GetFileName(path),
            LyricsSourceKind.Manual);
        return document;
    }

    public LyricsDocument ParseText(
        string content,
        string source,
        LyricsSourceKind sourceKind,
        double matchConfidence = 1)
    {
        var parsed = new List<LyricsLine>();
        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var matches = TimestampRegex().Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var lyric = TimestampRegex().Replace(rawLine, string.Empty).Trim();
            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fractionText = match.Groups[3].Value.PadRight(3, '0')[..3];
                var milliseconds = int.Parse(fractionText, CultureInfo.InvariantCulture);
                parsed.Add(new LyricsLine(new TimeSpan(0, 0, minutes, seconds, milliseconds), lyric));
            }
        }

        return new LyricsDocument(
            source,
            sourceKind,
            parsed.OrderBy(line => line.Time).ToArray(),
            matchConfidence);
    }

    public string GetLine(TimeSpan position) =>
        Document?.GetLine(position) ?? "右键可加载本地 LRC 歌词";

    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();
}
