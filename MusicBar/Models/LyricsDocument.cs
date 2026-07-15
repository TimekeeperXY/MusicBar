namespace MusicBar.Models;

public enum LyricsSourceKind
{
    NativePlayer,
    LocalFile,
    Online,
    Manual
}

public sealed record LyricsLine(TimeSpan Time, string Text, string? Translation = null);

public sealed record LyricsDocument(
    string Source,
    LyricsSourceKind SourceKind,
    IReadOnlyList<LyricsLine> Lines,
    double MatchConfidence = 1,
    TimeSpan Offset = default)
{
    public string GetLine(TimeSpan position)
    {
        if (Lines.Count == 0)
        {
            return "此歌曲暂无歌词";
        }

        var adjusted = position + Offset;
        var low = 0;
        var high = Lines.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (Lines[middle].Time <= adjusted)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (result < 0)
        {
            return "♪";
        }

        var line = Lines[result];
        return string.IsNullOrWhiteSpace(line.Translation)
            ? line.Text
            : $"{line.Text}  ·  {line.Translation}";
    }
}
