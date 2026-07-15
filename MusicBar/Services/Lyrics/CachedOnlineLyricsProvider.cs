using MusicBar.Models;
using System.IO;

namespace MusicBar.Services.Lyrics;

internal sealed class CachedOnlineLyricsProvider : ILyricsProvider
{
    private readonly ILyricsProvider _onlineProvider;
    private readonly LyricsDiskCache _cache;

    public CachedOnlineLyricsProvider(
        ILyricsProvider onlineProvider,
        LyricsDiskCache? cache = null)
    {
        _onlineProvider = onlineProvider;
        _cache = cache ?? new LyricsDiskCache();
    }

    public string Name => $"{_onlineProvider.Name}（带缓存）";
    public int Priority => _onlineProvider.Priority;

    public bool CanHandle(LyricsTrack track) =>
        !string.IsNullOrWhiteSpace(track.Title) &&
        !string.IsNullOrWhiteSpace(track.Artist);

    public void Invalidate(LyricsTrack track) => _cache.Remove(track);

    public async Task<LyricsDocument?> GetLyricsAsync(
        LyricsTrack track,
        CancellationToken cancellationToken)
    {
        var cached = await _cache.TryGetAsync(track, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        if (!_onlineProvider.CanHandle(track))
        {
            return null;
        }

        var online = await _onlineProvider.GetLyricsAsync(track, cancellationToken);
        if (online is null || online.Lines.Count == 0)
        {
            return null;
        }

        try
        {
            await _cache.StoreAsync(track, online, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache persistence is best-effort; online lyrics remain usable.
        }
        return online;
    }
}
