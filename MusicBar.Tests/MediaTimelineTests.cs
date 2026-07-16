using MusicBar.Services;

namespace MusicBar.Tests;

public sealed class MediaTimelineTests
{
    [Fact]
    public void EstimatePosition_AdvancesFromLastSystemUpdateWhilePlaying()
    {
        var updated = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

        var position = MediaSessionService.EstimatePosition(
            TimeSpan.FromSeconds(30), updated, TimeSpan.Zero, TimeSpan.FromMinutes(4),
            true, 1, updated.AddSeconds(8));

        Assert.Equal(TimeSpan.FromSeconds(38), position);
    }

    [Fact]
    public void EstimatePosition_DoesNotAdvanceWhilePaused()
    {
        var updated = DateTimeOffset.UtcNow.AddMinutes(-1);

        var position = MediaSessionService.EstimatePosition(
            TimeSpan.FromSeconds(30), updated, TimeSpan.Zero, TimeSpan.FromMinutes(4),
            false, 1, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(30), position);
    }

    [Fact]
    public void EstimatePosition_ClampsToTrackEnd()
    {
        var updated = DateTimeOffset.UtcNow.AddSeconds(-20);

        var position = MediaSessionService.EstimatePosition(
            TimeSpan.FromSeconds(55), updated, TimeSpan.Zero, TimeSpan.FromMinutes(1),
            true, 1, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromMinutes(1), position);
    }

    [Fact]
    public void Tracker_AdvancesLocallyWhenPlayerTimelineStaysFrozen()
    {
        var tracker = new MediaPositionTracker();
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var unavailableSystemTimestamp = DateTimeOffset.MinValue;

        var first = tracker.Update(
            "song", TimeSpan.FromSeconds(12), unavailableSystemTimestamp,
            TimeSpan.Zero, TimeSpan.FromMinutes(4), true, 1, now);
        var second = tracker.Update(
            "song", TimeSpan.FromSeconds(12), unavailableSystemTimestamp,
            TimeSpan.Zero, TimeSpan.FromMinutes(4), true, 1, now.AddSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(12), first);
        Assert.Equal(TimeSpan.FromSeconds(19), second);
    }

    [Fact]
    public void Tracker_DoesNotResetWhenOnlyPlayerTimestampKeepsRefreshing()
    {
        var tracker = new MediaPositionTracker();
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        var first = tracker.Update(
            "netease-song", TimeSpan.FromSeconds(12), now,
            TimeSpan.Zero, TimeSpan.FromMinutes(4), true, 1, now);
        var second = tracker.Update(
            "netease-song", TimeSpan.FromSeconds(12), now.AddSeconds(1),
            TimeSpan.Zero, TimeSpan.FromMinutes(4), true, 1, now.AddSeconds(1));
        var third = tracker.Update(
            "netease-song", TimeSpan.FromSeconds(12), now.AddSeconds(2),
            TimeSpan.Zero, TimeSpan.FromMinutes(4), true, 1, now.AddSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(12), first);
        Assert.Equal(TimeSpan.FromSeconds(13), second);
        Assert.Equal(TimeSpan.FromSeconds(14), third);
    }

    [Fact]
    public void Tracker_FreezesProjectedPositionWhenPlaybackPauses()
    {
        var tracker = new MediaPositionTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.Update("song", TimeSpan.FromSeconds(10), default,
            TimeSpan.Zero, TimeSpan.FromMinutes(4), true, 1, now);

        var paused = tracker.Update("song", TimeSpan.FromSeconds(10), default,
            TimeSpan.Zero, TimeSpan.FromMinutes(4), false, 1, now.AddSeconds(5));
        var later = tracker.Update("song", TimeSpan.FromSeconds(10), default,
            TimeSpan.Zero, TimeSpan.FromMinutes(4), false, 1, now.AddSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(15), paused);
        Assert.Equal(paused, later);
    }

    [Fact]
    public void Tracker_CalibrateReplacesUnknownStartupPosition()
    {
        var tracker = new MediaPositionTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.Update("song", TimeSpan.Zero, default,
            TimeSpan.Zero, TimeSpan.Zero, true, 1, now);

        tracker.Calibrate(TimeSpan.FromSeconds(83), now.AddSeconds(2));
        var position = tracker.Update("song", TimeSpan.Zero, default,
            TimeSpan.Zero, TimeSpan.Zero, true, 1, now.AddSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(86), position);
    }

    [Fact]
    public void SessionLossGrace_DoesNotExpireDuringATransientStartupGap()
    {
        var missingSince = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);

        Assert.False(MediaSessionService.IsSessionLossGraceExpired(
            missingSince, missingSince.AddSeconds(3)));
        Assert.True(MediaSessionService.IsSessionLossGraceExpired(
            missingSince, missingSince.AddSeconds(4)));
    }

    [Theory]
    [InlineData("cloudmusic.exe", "cloudmusic")]
    [InlineData("网易云音乐", "cloudmusic")]
    [InlineData("QQMusic.exe", "QQMusic")]
    [InlineData("QQ音乐", "QQMusic")]
    [InlineData("SodaMusic.exe", "SodaMusic")]
    [InlineData("com.luna.music", "SodaMusic")]
    [InlineData("汽水音乐", "SodaMusic")]
    [InlineData("SpotifyAB.SpotifyMusic_xyz!Spotify", "Spotify")]
    [InlineData("AppleInc.AppleMusic_xyz!App", "AppleMusic")]
    [InlineData("chrome.exe", null)]
    public void KnownPlayerProcessMapping_DoesNotTreatBrowsersAsDesktopPlayers(
        string sourceAppId,
        string? expected)
    {
        Assert.Equal(expected, MediaSessionService.GetKnownPlayerProcessName(sourceAppId));
    }

    [Theory]
    [InlineData("cloudmusic.exe", true)]
    [InlineData("网易云音乐", true)]
    [InlineData("QQMusic.exe", true)]
    [InlineData("QQ音乐", true)]
    [InlineData("SodaMusic.exe", true)]
    [InlineData("汽水音乐", true)]
    [InlineData("SpotifyAB.SpotifyMusic_xyz!Spotify", true)]
    [InlineData("AppleInc.AppleMusic_xyz!App", true)]
    [InlineData("chrome.exe", false)]
    [InlineData("msedge.exe", false)]
    [InlineData("firefox.exe", false)]
    [InlineData("vlc.exe", false)]
    [InlineData("Zoom.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SupportedMusicSourceUsesExplicitDesktopPlayerAllowlist(
        string? sourceAppId,
        bool expected)
    {
        Assert.Equal(expected, MediaSessionService.IsSupportedMusicSource(sourceAppId));
    }
}
