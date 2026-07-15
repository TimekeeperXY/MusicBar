namespace MusicBar.Services;

internal sealed class MediaPositionTracker
{
    private string? _trackKey;
    private TimeSpan _reportedPosition;
    private DateTimeOffset _reportedUpdatedAt;
    private TimeSpan _anchorPosition;
    private DateTimeOffset _anchorCapturedAt;
    private bool _wasPlaying;

    public TimeSpan Update(
        string trackKey,
        TimeSpan reportedPosition,
        DateTimeOffset reportedUpdatedAt,
        TimeSpan startTime,
        TimeSpan endTime,
        bool isPlaying,
        double playbackRate,
        DateTimeOffset now)
    {
        var trackChanged = !string.Equals(trackKey, _trackKey, StringComparison.Ordinal);
        // Some players, notably NetEase Cloud Music, refresh LastUpdatedTime on every
        // GSMTC read while leaving Position frozen. A timestamp-only change is not a
        // new playback anchor; accepting it would reset local projection every poll.
        var reportedPositionChanged = reportedPosition != _reportedPosition;
        var projectedBeforeUpdate = Project(now, playbackRate);

        if (trackChanged || reportedPositionChanged)
        {
            _anchorPosition = MediaSessionService.EstimatePosition(
                reportedPosition, reportedUpdatedAt, startTime, endTime,
                isPlaying, playbackRate, now);
            _anchorCapturedAt = now;
        }
        else if (_wasPlaying != isPlaying)
        {
            _anchorPosition = projectedBeforeUpdate;
            _anchorCapturedAt = now;
        }

        _trackKey = trackKey;
        _reportedPosition = reportedPosition;
        _reportedUpdatedAt = reportedUpdatedAt;
        _wasPlaying = isPlaying;

        var position = Project(now, playbackRate);
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

    public void Reset()
    {
        _trackKey = null;
        _reportedPosition = TimeSpan.Zero;
        _reportedUpdatedAt = default;
        _anchorPosition = TimeSpan.Zero;
        _anchorCapturedAt = default;
        _wasPlaying = false;
    }

    public void Calibrate(TimeSpan actualPosition, DateTimeOffset now)
    {
        _anchorPosition = actualPosition < TimeSpan.Zero ? TimeSpan.Zero : actualPosition;
        _anchorCapturedAt = now;
    }

    private TimeSpan Project(DateTimeOffset now, double playbackRate)
    {
        if (!_wasPlaying || playbackRate <= 0 || _anchorCapturedAt == default)
        {
            return _anchorPosition;
        }

        var elapsed = now - _anchorCapturedAt;
        return elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(1)
            ? _anchorPosition + TimeSpan.FromTicks((long)(elapsed.Ticks * playbackRate))
            : _anchorPosition;
    }
}
