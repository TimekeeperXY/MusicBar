using MusicBar.Models;
using MusicBar.Services.Lyrics;
using System.Net;
using System.Text;

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
}
