using MusicBar.Models;
using MusicBar.Services.Lyrics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MusicBar.Tests;

public sealed class LyricsMatchingTests
{
    [Fact]
    public void StableKey_NormalizesCaseSpacingAndPunctuation()
    {
        var first = new LyricsTrack("My Song!", "The Artist", "Album-A", TimeSpan.Zero, "a");
        var second = new LyricsTrack("my　song", "the-artist", "album a", TimeSpan.FromMinutes(3), "b");

        Assert.Equal(first.StableKey, second.StableKey);
    }

    [Fact]
    public void NeteaseMatch_AllowsMissingSystemDurationWhenTitleAndArtistMatch()
    {
        var track = new LyricsTrack("Susan 说", "陶喆", "", TimeSpan.Zero, "cloudmusic.exe");

        var score = NeteaseLyricsProvider.CalculateLocalMatch(track, "Susan 说", "陶喆", 261.8);

        Assert.True(score >= 0.82);
    }

    [Fact]
    public void LyricsDocument_CombinesOriginalAndTranslation()
    {
        var document = new LyricsDocument("test", LyricsSourceKind.NativePlayer,
            [new LyricsLine(TimeSpan.Zero, "Hello", "你好")]);

        Assert.Equal("Hello  ·  你好", document.GetLine(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void LrclibConfidence_RejectsWrongDurationAndMetadata()
    {
        var track = new LyricsTrack("Song A", "Artist A", "Album", TimeSpan.FromSeconds(200), "player");
        var candidate = new LrclibLyricsProvider.LrclibResult
        {
            TrackName = "Song B",
            ArtistName = "Artist B",
            AlbumName = "Elsewhere",
            Duration = 260
        };

        Assert.True(LrclibLyricsProvider.CalculateConfidence(track, candidate) < 0.72);
    }

    [Fact]
    public async Task LrclibLookup_UsesSearchWhenExactRequestTimesOut()
    {
        const string response = """
            [{
              "id": 42,
              "trackName": "Song",
              "artistName": "Artist",
              "albumName": "Album",
              "duration": 180,
              "syncedLyrics": "[00:01.00]fallback lyric"
            }]
            """;
        var handler = new ExactTimeoutThenSearchHandler(response);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://lrclib.test/"),
            Timeout = TimeSpan.FromSeconds(2)
        };
        var provider = new LrclibLyricsProvider(client);
        var track = new LyricsTrack(
            "Song", "Artist", "Album", TimeSpan.FromMinutes(3), "QQMusic.exe");

        var result = await provider.GetLyricsAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("fallback lyric", result.GetLine(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void QqMusicConfidence_PrefersExactVersionAndRejectsWrongArtist()
    {
        var track = new LyricsTrack(
            "陪你", "陶喆", "STUPID POP SONGS",
            TimeSpan.FromSeconds(304), "QQ音乐");

        var exact = QqMusicLyricsProvider.CalculateConfidence(
            track, "陪你", "陶喆", "STUPID POP SONGS", TimeSpan.FromSeconds(304));
        var wrongArtist = QqMusicLyricsProvider.CalculateConfidence(
            track, "陪你", "SpongeBaby组合", "翻唱集", TimeSpan.FromSeconds(298));

        Assert.True(exact >= 0.98);
        Assert.True(wrongArtist < 0.78);
    }

    [Fact]
    public async Task QqMusicLookup_UsesMatchedSongMidAndReturnsNativeSyncedLyrics()
    {
        var handler = new QqMusicHandler();
        using var client = new HttpClient(handler);
        var provider = new QqMusicLyricsProvider(client);
        var track = new LyricsTrack(
            "陪你", "陶喆", "STUPID POP SONGS",
            TimeSpan.FromSeconds(304), "QQ音乐");

        var result = await provider.GetLyricsAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(LyricsSourceKind.NativePlayer, result.SourceKind);
        Assert.Contains("0033bKWc0afdVZ", result.Source);
        Assert.Equal("第一句", result.GetLine(TimeSpan.FromSeconds(11)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("musicu.fcg", handler.Requests[0]);
        Assert.Contains("songmid=0033bKWc0afdVZ", handler.Requests[1]);
    }

    [Fact]
    public async Task QqMusicLookup_UsesDedicatedCacheWhenServiceIsUnavailable()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"musicbar-qq-cache-{Guid.NewGuid():N}");
        try
        {
            var track = new LyricsTrack(
                "陪你", "陶喆", "STUPID POP SONGS",
                TimeSpan.FromSeconds(304), "QQ音乐");
            using var onlineClient = new HttpClient(new QqMusicHandler());
            var first = new QqMusicLyricsProvider(
                onlineClient, new LyricsDiskCache(directory));
            await first.GetLyricsAsync(track, CancellationToken.None);

            var unavailable = new ThrowingHandler();
            using var offlineClient = new HttpClient(unavailable);
            var second = new QqMusicLyricsProvider(
                offlineClient, new LyricsDiskCache(directory));
            var cached = await second.GetLyricsAsync(track, CancellationToken.None);

            Assert.NotNull(cached);
            Assert.Equal(LyricsSourceKind.NativePlayer, cached.SourceKind);
            Assert.Equal("第一句", cached.GetLine(TimeSpan.FromSeconds(11)));
            Assert.Equal(0, unavailable.RequestCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ExactTimeoutThenSearchHandler(string searchResponse) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                return Task.FromException<HttpResponseMessage>(
                    new TaskCanceledException("simulated exact lookup timeout"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchResponse, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class QqMusicHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? string.Empty);
            var payload = Requests.Count == 1
                ? """
                  {
                    "req": {
                      "data": {
                        "body": {
                          "song": {
                            "list": [{
                              "mid": "0033bKWc0afdVZ",
                              "title": "陪你",
                              "interval": 304,
                              "album": { "name": "STUPID POP SONGS" },
                              "singer": [{ "name": "陶喆" }]
                            }]
                          }
                        }
                      }
                    }
                  }
                  """
                : JsonSerializer.Serialize(new
                {
                    retcode = 0,
                    code = 0,
                    lyric = "[00:10.00]第一句\n[00:20.00]第二句",
                    trans = ""
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("simulated offline state"));
        }
    }
}
