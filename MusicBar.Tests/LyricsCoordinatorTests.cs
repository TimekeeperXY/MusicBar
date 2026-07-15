using MusicBar.Models;
using MusicBar.Services.Lyrics;

namespace MusicBar.Tests;

public sealed class LyricsCoordinatorTests
{
    [Fact]
    public async Task UpdateTrackAsync_FallsThroughUntilAProviderReturnsLyrics()
    {
        var calls = new List<string>();
        var miss = new FakeProvider("native", 100, calls, null);
        var document = new LyricsDocument("online", LyricsSourceKind.Online,
            [new LyricsLine(TimeSpan.Zero, "歌词")]);
        var hit = new FakeProvider("online", 200, calls, document);
        using var coordinator = new LyricsCoordinator([hit, miss]);

        await coordinator.UpdateTrackAsync(Track());

        Assert.Equal(["native", "online"], calls);
        Assert.Equal(LyricsResolutionState.Found, coordinator.State);
        Assert.Equal("歌词", coordinator.GetDisplayLine(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task UpdateTrackAsync_DoesNotRepeatLookupForSameTrack()
    {
        var calls = new List<string>();
        var provider = new FakeProvider("provider", 100, calls, null);
        using var coordinator = new LyricsCoordinator([provider]);
        var track = Track();

        await coordinator.UpdateTrackAsync(track);
        await coordinator.UpdateTrackAsync(track);

        Assert.Single(calls);
    }

    [Fact]
    public async Task AdjustOffset_ShiftsTheDisplayedLyric()
    {
        var document = new LyricsDocument("test", LyricsSourceKind.Online,
        [
            new LyricsLine(TimeSpan.Zero, "first"),
            new LyricsLine(TimeSpan.FromSeconds(5), "second")
        ]);
        var provider = new FakeProvider("provider", 100, [], document);
        using var coordinator = new LyricsCoordinator([provider]);
        await coordinator.UpdateTrackAsync(Track());

        coordinator.AdjustOffset(TimeSpan.FromSeconds(3));

        Assert.Equal("second", coordinator.GetDisplayLine(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task UpdateTrackAsync_ShowsOnlineTimeoutInsteadOfNotFound()
    {
        using var coordinator = new LyricsCoordinator(
            [new HangingProvider()], TimeSpan.FromMilliseconds(40));

        await coordinator.UpdateTrackAsync(Track());

        Assert.Equal(LyricsResolutionState.OnlineUnavailable, coordinator.State);
        Assert.Contains("连接超时", coordinator.GetDisplayLine(TimeSpan.Zero));
    }

    [Fact]
    public async Task UpdateTrackAsync_WithMinimumPriority_OnlyUsesOnlineProvider()
    {
        var calls = new List<string>();
        var native = new FakeProvider("native", 100, calls, null);
        var online = new FakeProvider("online", 200, calls, Document());
        using var coordinator = new LyricsCoordinator([native, online]);

        await coordinator.UpdateTrackAsync(Track(), force: true, minimumPriority: 200);

        Assert.Equal(["online"], calls);
        Assert.Equal(LyricsResolutionState.Found, coordinator.State);
    }

    private static LyricsDocument Document() => new(
        "online", LyricsSourceKind.Online,
        [new LyricsLine(TimeSpan.Zero, "歌词")]);

    private static LyricsTrack Track() =>
        new("Song", "Artist", "Album", TimeSpan.FromMinutes(3), "player");

    private sealed class FakeProvider(
        string name,
        int priority,
        List<string> calls,
        LyricsDocument? result) : ILyricsProvider
    {
        public string Name => name;
        public int Priority => priority;
        public bool CanHandle(LyricsTrack track) => true;
        public Task<LyricsDocument?> GetLyricsAsync(LyricsTrack track, CancellationToken cancellationToken)
        {
            calls.Add(Name);
            return Task.FromResult(result);
        }
    }

    private sealed class HangingProvider : ILyricsProvider
    {
        public string Name => "slow-online";
        public int Priority => 200;
        public bool CanHandle(LyricsTrack track) => true;

        public async Task<LyricsDocument?> GetLyricsAsync(
            LyricsTrack track,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return null;
        }
    }
}
