using MusicBar.Models;

namespace MusicBar.Services.Lyrics;

public enum LyricsResolutionState
{
    Idle,
    Searching,
    Found,
    NotFound,
    OnlineUnavailable
}

public sealed class LyricsCoordinator : IDisposable
{
    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly TimeSpan _resolutionTimeout;
    private CancellationTokenSource? _resolutionCancellation;
    private string? _trackKey;
    private bool _disposed;

    public event EventHandler? StateChanged;

    public LyricsResolutionState State { get; private set; } = LyricsResolutionState.Idle;
    public LyricsDocument? Current { get; private set; }
    public LyricsTrack? CurrentTrack { get; private set; }

    public LyricsCoordinator(
        IEnumerable<ILyricsProvider> providers,
        TimeSpan? resolutionTimeout = null)
    {
        _providers = providers.OrderBy(provider => provider.Priority).ToArray();
        _resolutionTimeout = resolutionTimeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task UpdateTrackAsync(
        LyricsTrack track,
        bool force = false,
        int minimumPriority = int.MinValue)
    {
        if (_disposed || (!force && track.StableKey == _trackKey))
        {
            return;
        }

        _resolutionCancellation?.Cancel();
        _resolutionCancellation?.Dispose();
        _resolutionCancellation = new CancellationTokenSource(_resolutionTimeout);
        var cancellation = _resolutionCancellation;

        _trackKey = track.StableKey;
        CurrentTrack = track;
        Current = null;
        State = LyricsResolutionState.Searching;
        StateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            Exception? providerFailure = null;
            foreach (var provider in _providers)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (provider.Priority < minimumPriority || !provider.CanHandle(track))
                {
                    continue;
                }

                LyricsDocument? document;
                try
                {
                    document = await provider.GetLyricsAsync(track, cancellation.Token);
                }
                catch (Exception exception) when (!cancellation.IsCancellationRequested)
                {
                    providerFailure ??= exception;
                    continue;
                }

                if (document is null || document.Lines.Count == 0)
                {
                    continue;
                }

                if (!ReferenceEquals(cancellation, _resolutionCancellation))
                {
                    return;
                }

                Current = document;
                State = LyricsResolutionState.Found;
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (ReferenceEquals(cancellation, _resolutionCancellation))
            {
                State = providerFailure is null
                    ? LyricsResolutionState.NotFound
                    : LyricsResolutionState.OnlineUnavailable;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && ReferenceEquals(cancellation, _resolutionCancellation))
            {
                State = LyricsResolutionState.OnlineUnavailable;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            // A track change cancels the previous lookup; the newer lookup owns the visible state.
        }
    }

    public string GetDisplayLine(TimeSpan position) => State switch
    {
        LyricsResolutionState.Searching => "正在寻找歌词…",
        LyricsResolutionState.Found => Current?.GetLine(position) ?? "此歌曲暂无歌词",
        LyricsResolutionState.NotFound => "未找到歌词 · 右键可加载 LRC",
        LyricsResolutionState.OnlineUnavailable => "在线歌词连接超时 · 请稍后重试",
        _ => "右键可加载本地 LRC 歌词"
    };

    public void AdjustOffset(TimeSpan delta)
    {
        if (Current is null)
        {
            return;
        }
        Current = Current with { Offset = Current.Offset + delta };
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetOffset()
    {
        if (Current is null)
        {
            return;
        }
        Current = Current with { Offset = TimeSpan.Zero };
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _disposed = true;
        _resolutionCancellation?.Cancel();
        _resolutionCancellation?.Dispose();
    }
}
