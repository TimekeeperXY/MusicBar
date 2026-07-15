using MusicBar.Models;
using MusicBar.Services.Lyrics;

namespace MusicBar.Tests;

public sealed class CachedOnlineLyricsProviderTests
{
    [Fact]
    public async Task FirstLookupStoresOnlineLyrics_SecondLookupUsesDiskCache()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var track = Track();
            var online = new FakeOnlineProvider(Document());
            var first = new CachedOnlineLyricsProvider(online, new LyricsDiskCache(directory));

            var onlineResult = await first.GetLyricsAsync(track, CancellationToken.None);

            Assert.NotNull(onlineResult);
            Assert.Equal(1, online.CallCount);
            Assert.Single(Directory.GetFiles(directory, "*.json"));

            var unavailableOnline = new FakeOnlineProvider(null);
            var second = new CachedOnlineLyricsProvider(
                unavailableOnline, new LyricsDiskCache(directory));

            var cachedResult = await second.GetLyricsAsync(track, CancellationToken.None);

            Assert.NotNull(cachedResult);
            Assert.Equal(0, unavailableOnline.CallCount);
            Assert.Equal("第二句", cachedResult.GetLine(TimeSpan.FromSeconds(6)));
            Assert.Equal("你好", cachedResult.Lines[0].Translation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptCacheIsIgnoredAndOnlineLookupRepairsIt()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var track = Track();
            var cache = new LyricsDiskCache(directory);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(cache.GetCachePath(track), "{not-json");
            var online = new FakeOnlineProvider(Document());
            var provider = new CachedOnlineLyricsProvider(online, cache);

            var result = await provider.GetLyricsAsync(track, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(1, online.CallCount);
            Assert.Contains("schemaVersion", await File.ReadAllTextAsync(cache.GetCachePath(track)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CacheRejectsAMateriallyDifferentDuration()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var cache = new LyricsDiskCache(directory);
            await cache.StoreAsync(Track(), Document(), CancellationToken.None);
            var differentVersion = Track() with { Duration = TimeSpan.FromMinutes(4) };

            var result = await cache.TryGetAsync(differentVersion, CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Invalidate_RemovesCacheAndForcesANewOnlineLookup()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var track = Track();
            var cache = new LyricsDiskCache(directory);
            var initial = new CachedOnlineLyricsProvider(
                new FakeOnlineProvider(Document()), cache);
            await initial.GetLyricsAsync(track, CancellationToken.None);

            initial.Invalidate(track);
            var refreshedOnline = new FakeOnlineProvider(Document());
            var refreshed = new CachedOnlineLyricsProvider(refreshedOnline, cache);
            var result = await refreshed.GetLyricsAsync(track, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(1, refreshedOnline.CallCount);
            Assert.True(File.Exists(cache.GetCachePath(track)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory() => Path.Combine(
        Path.GetTempPath(), $"musicbar-cache-tests-{Guid.NewGuid():N}");

    private static LyricsTrack Track() =>
        new("测试歌曲", "测试歌手", "测试专辑", TimeSpan.FromMinutes(3), "player");

    private static LyricsDocument Document() => new(
        "LRCLIB #1",
        LyricsSourceKind.Online,
        [
            new LyricsLine(TimeSpan.FromSeconds(1), "第一句", "你好"),
            new LyricsLine(TimeSpan.FromSeconds(5), "第二句")
        ],
        0.95);

    private sealed class FakeOnlineProvider(LyricsDocument? result) : ILyricsProvider
    {
        public int CallCount { get; private set; }
        public string Name => "online";
        public int Priority => 200;
        public bool CanHandle(LyricsTrack track) => true;

        public Task<LyricsDocument?> GetLyricsAsync(
            LyricsTrack track,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
