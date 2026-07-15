using MusicBar.Services;
using System.Text;

namespace MusicBar.Tests;

public sealed class NeteasePlaybackProbeTests
{
    [Theory]
    [InlineData("亲爱的，那不是爱情 - 张韶涵", "亲爱的，那不是爱情", "张韶涵")]
    [InlineData("歌名 - 现场版 - 歌手", "歌名 - 现场版", "歌手")]
    [InlineData("歌名 — 歌手", "歌名", "歌手")]
    public void ParseWindowTitle_ExtractsTrackFromLastSeparator(
        string value, string expectedTitle, string expectedArtist)
    {
        var track = NeteasePlaybackProbe.ParseWindowTitle(value);

        Assert.NotNull(track);
        Assert.Equal(expectedTitle, track.Title);
        Assert.Equal(expectedArtist, track.Artist);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("网易云音乐")]
    [InlineData("没有歌手的标题")]
    public void ParseWindowTitle_RejectsNonTrackTitles(string? value) =>
        Assert.Null(NeteasePlaybackProbe.ParseWindowTitle(value));

    [Fact]
    public void ParseEvaluationResponse_ReadsPausedProgress()
    {
        var response = Encoding.UTF8.GetBytes("""
            {"id":1,"result":{"result":{"type":"object","value":{"progressRatio":0.226,"isPlaying":false,"durationSeconds":257}}}}
            """);

        var state = NeteasePlaybackProbe.ParseEvaluationResponse(response);

        Assert.NotNull(state);
        Assert.Equal(0.226, state.ProgressRatio, 3);
        Assert.False(state.IsPlaying);
        Assert.Equal(257, state.DurationSeconds);
    }

    [Fact]
    public void CalculatePosition_UsesNativeProgressInsteadOfStaleSystemPosition()
    {
        var position = NeteasePlaybackProbe.CalculatePosition(
            TimeSpan.FromSeconds(257), 0.226);

        Assert.Equal(58.082, position.TotalSeconds, 3);
    }

    [Fact]
    public void ParseEvaluationResponse_RejectsMissingProgress()
    {
        var response = Encoding.UTF8.GetBytes(
            "{\"id\":1,\"result\":{\"result\":{\"type\":\"object\",\"value\":null}}}");

        Assert.Null(NeteasePlaybackProbe.ParseEvaluationResponse(response));
    }
}
