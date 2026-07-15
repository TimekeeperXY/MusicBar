using MusicBar.Models;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MusicBar.Services;

public sealed class MediaSessionService : IDisposable
{
    private static readonly TimeSpan SessionLossGracePeriod = TimeSpan.FromSeconds(4);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly MediaPositionTracker _positionTracker = new();
    private readonly NeteasePlaybackProbe _neteasePlaybackProbe = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Timer? _refreshTimer;
    private DateTimeOffset? _sessionMissingSince;
    private string? _neteaseWindowTrackKey;
    private DateTimeOffset _neteaseWindowTrackStartedAt;
    private bool _disposed;

    public event EventHandler<MediaSnapshot>? SnapshotChanged;

    public MediaSnapshot Current { get; private set; } = MediaSnapshot.Empty;

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnManagerSessionChanged;
        _manager.SessionsChanged += OnManagerSessionChanged;
        AttachBestSession(DateTimeOffset.Now);
        await RefreshAsync();
        _refreshTimer = new Timer(async _ => await RefreshAsync(), null, 750, 750);
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_session is null)
        {
            return;
        }

        if (Current.IsPlaying && Current.CanPause)
        {
            await _session.TryPauseAsync();
        }
        else if (Current.CanPlay)
        {
            await _session.TryPlayAsync();
        }
        else
        {
            await _session.TryTogglePlayPauseAsync();
        }

        await RefreshAsync();
    }

    public async Task PreviousAsync()
    {
        if (_session is not null)
        {
            await _session.TrySkipPreviousAsync();
        }
    }

    public async Task NextAsync()
    {
        if (_session is not null)
        {
            await _session.TrySkipNextAsync();
        }
    }

    public async Task CycleRepeatAsync()
    {
        if (_session is null || !Current.CanRepeat)
        {
            return;
        }

        var next = Current.RepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None
        };

        await _session.TryChangeAutoRepeatModeAsync(next);
        await RefreshAsync();
    }

    public void CalibratePosition(TimeSpan actualPosition) =>
        _positionTracker.Calibrate(actualPosition, DateTimeOffset.Now);

    private void OnManagerSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        object args) => _ = RefreshAsync();

    private void AttachBestSession(DateTimeOffset now)
    {
        if (_manager is null)
        {
            return;
        }

        var next = _manager.GetCurrentSession();
        if (next is null)
        {
            var sessions = _manager.GetSessions();
            next = sessions
                .FirstOrDefault(candidate => candidate.GetPlaybackInfo().PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                ?? sessions.FirstOrDefault();
        }

        if (next is null)
        {
            _sessionMissingSince ??= now;
            if (_session is not null &&
                (IsKnownPlayerProcessRunning(_session.SourceAppUserModelId) ||
                 !IsSessionLossGraceExpired(_sessionMissingSince, now)))
            {
                return;
            }

            DetachSession();
            return;
        }

        _sessionMissingSince = null;

        if (ReferenceEquals(next, _session))
        {
            return;
        }

        DetachSession();
        _session = next;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnSessionStateChanged;
            _session.PlaybackInfoChanged += OnSessionStateChanged;
            _session.TimelinePropertiesChanged += OnSessionStateChanged;
        }
    }

    private void OnSessionStateChanged(
        GlobalSystemMediaTransportControlsSession sender,
        object args) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_disposed || !await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            AttachBestSession(DateTimeOffset.Now);
            var session = _session;
            if (session is null)
            {
                if (await TryPublishNeteaseProgressAsync())
                {
                    return;
                }
                if (Current.HasSession)
                {
                    Publish(MediaSnapshot.Empty);
                }
                return;
            }

            var media = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var controls = playback.Controls;
            var artwork = await ReadArtworkAsync(media.Thumbnail);
            var isPlaying = playback.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
            var position = _positionTracker.Update(
                $"{media.Title}\u001f{media.Artist}",
                timeline.Position,
                timeline.LastUpdatedTime,
                timeline.StartTime,
                timeline.EndTime,
                isPlaying,
                playback.PlaybackRate ?? 1,
                DateTimeOffset.Now);
            if (IsNeteaseSource(session.SourceAppUserModelId))
            {
                var nativeState = await _neteasePlaybackProbe.TryGetStateAsync(CancellationToken.None);
                if (nativeState is not null)
                {
                    if (nativeState.DurationSeconds > 0)
                    {
                        duration = TimeSpan.FromSeconds(nativeState.DurationSeconds);
                    }
                    position = NeteasePlaybackProbe.CalculatePosition(
                        duration, nativeState.ProgressRatio);
                    isPlaying = nativeState.IsPlaying;
                }
            }

            Publish(new MediaSnapshot(
                true,
                string.IsNullOrWhiteSpace(media.Title) ? "未知歌曲" : media.Title,
                string.IsNullOrWhiteSpace(media.Artist) ? "未知歌手" : media.Artist,
                media.AlbumTitle ?? string.Empty,
                session.SourceAppUserModelId ?? string.Empty,
                artwork,
                isPlaying,
                controls.IsPlayEnabled || controls.IsPlayPauseToggleEnabled,
                controls.IsPauseEnabled || controls.IsPlayPauseToggleEnabled,
                controls.IsPreviousEnabled,
                controls.IsNextEnabled,
                controls.IsRepeatEnabled,
                controls.IsShuffleEnabled,
                playback.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None,
                playback.IsShuffleActive,
                position,
                duration));
        }
        catch
        {
            // A player can replace its media session while starting. Keep the last good snapshot;
            // the next serialized timer pass will attach the replacement session.
            await TryPublishNeteaseProgressAsync();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal static TimeSpan EstimatePosition(
        TimeSpan reportedPosition,
        DateTimeOffset lastUpdated,
        TimeSpan startTime,
        TimeSpan endTime,
        bool isPlaying,
        double playbackRate,
        DateTimeOffset now)
    {
        var position = reportedPosition;
        var elapsed = now - lastUpdated;
        if (isPlaying && playbackRate > 0 && elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(1))
        {
            position += TimeSpan.FromTicks((long)(elapsed.Ticks * playbackRate));
        }

        if (position < startTime)
        {
            return startTime;
        }
        if (endTime > startTime && position > endTime)
        {
            return endTime;
        }
        return position;
    }

    internal static bool IsSessionLossGraceExpired(
        DateTimeOffset? missingSince,
        DateTimeOffset now) =>
        missingSince is not null && now - missingSince.Value >= SessionLossGracePeriod;

    internal static string? GetKnownPlayerProcessName(string? sourceAppId)
    {
        var source = sourceAppId?.ToLowerInvariant() ?? string.Empty;
        if (source.Contains("cloudmusic") || source.Contains("netease")) return "cloudmusic";
        if (source.Contains("qqmusic")) return "QQMusic";
        if (source.Contains("sodamusic") || source.Contains("qishui")) return "SodaMusic";
        if (source.Contains("spotify")) return "Spotify";
        if (source.Contains("applemusic") || source.Contains("apple music")) return "AppleMusic";
        return null;
    }

    private async Task<bool> TryPublishNeteaseProgressAsync()
    {
        var windowTrack = _neteasePlaybackProbe.TryGetWindowTrack();
        if (windowTrack is null)
        {
            return false;
        }

        var nativeState = await _neteasePlaybackProbe.TryGetStateAsync(CancellationToken.None);
        var now = DateTimeOffset.Now;
        if (!string.Equals(_neteaseWindowTrackKey, windowTrack.Key, StringComparison.Ordinal))
        {
            _neteaseWindowTrackKey = windowTrack.Key;
            _neteaseWindowTrackStartedAt = now;
        }

        var currentMatches = Current.HasSession && IsNeteaseSource(Current.SourceAppId) &&
            string.Equals(Current.Title, windowTrack.Title, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Current.Artist, windowTrack.Artist, StringComparison.OrdinalIgnoreCase);
        var duration = nativeState?.DurationSeconds > 0
            ? TimeSpan.FromSeconds(nativeState.DurationSeconds)
            : currentMatches ? Current.Duration : TimeSpan.Zero;
        var position = nativeState is not null
            ? NeteasePlaybackProbe.CalculatePosition(duration, nativeState.ProgressRatio)
            : now - _neteaseWindowTrackStartedAt;
        var isPlaying = nativeState?.IsPlaying ?? true;

        if (currentMatches)
        {
            Publish(Current with
            {
                Duration = duration,
                Position = position,
                IsPlaying = isPlaying
            });
            return true;
        }

        Publish(new MediaSnapshot(
            true,
            windowTrack.Title,
            windowTrack.Artist,
            string.Empty,
            "cloudmusic",
            null,
            isPlaying,
            false,
            false,
            false,
            false,
            false,
            false,
            MediaPlaybackAutoRepeatMode.None,
            null,
            position,
            duration));
        return true;
    }

    private static bool IsNeteaseSource(string? sourceAppId) =>
        GetKnownPlayerProcessName(sourceAppId) == "cloudmusic";

    private static bool IsKnownPlayerProcessRunning(string? sourceAppId)
    {
        var processName = GetKnownPlayerProcessName(sourceAppId);
        if (processName is null)
        {
            return false;
        }

        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(processName);
            foreach (var process in processes)
            {
                process.Dispose();
            }
            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void Publish(MediaSnapshot snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private static async Task<ImageSource?> ReadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var randomAccessStream = await reference.OpenReadAsync();
            var length = checked((uint)randomAccessStream.Size);
            using var reader = new DataReader(randomAccessStream.GetInputStreamAt(0));
            await reader.LoadAsync(length);
            var bytes = new byte[length];
            reader.ReadBytes(bytes);
            using var memory = new MemoryStream(bytes, writable: false);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void DetachSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= OnSessionStateChanged;
        _session.PlaybackInfoChanged -= OnSessionStateChanged;
        _session.TimelinePropertiesChanged -= OnSessionStateChanged;
        _positionTracker.Reset();
        _session = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer?.Dispose();
        _neteasePlaybackProbe.Dispose();
        DetachSession();
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnManagerSessionChanged;
            _manager.SessionsChanged -= OnManagerSessionChanged;
        }
        _refreshGate.Dispose();
    }
}
