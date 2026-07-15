using MusicBar.Models;

namespace MusicBar.Services.Lyrics;

public interface ILyricsProvider
{
    string Name { get; }
    int Priority { get; }
    bool CanHandle(LyricsTrack track);
    Task<LyricsDocument?> GetLyricsAsync(LyricsTrack track, CancellationToken cancellationToken);
}
