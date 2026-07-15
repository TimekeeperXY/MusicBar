using MusicBar.Models;
using System.Collections.Concurrent;

namespace MusicBar.Services.Lyrics;

public sealed class ManualLyricsProvider : ILyricsProvider
{
    private readonly ConcurrentDictionary<string, LyricsDocument> _documents = new();

    public string Name => "手动 LRC";
    public int Priority => 0;

    public bool CanHandle(LyricsTrack track) => _documents.ContainsKey(track.StableKey);

    public Task<LyricsDocument?> GetLyricsAsync(LyricsTrack track, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _documents.TryGetValue(track.StableKey, out var document);
        return Task.FromResult(document);
    }

    public void Set(LyricsTrack track, LyricsDocument document) =>
        _documents[track.StableKey] = document;
}
