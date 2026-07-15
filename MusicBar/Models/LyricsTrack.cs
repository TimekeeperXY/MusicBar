using System.Text;

namespace MusicBar.Models;

public sealed record LyricsTrack(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string SourceAppId)
{
    public string StableKey => string.Join('|',
        Normalize(Title), Normalize(Artist), Normalize(Album));

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }
}
