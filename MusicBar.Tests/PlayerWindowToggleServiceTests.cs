using MusicBar.Services;

namespace MusicBar.Tests;

public sealed class PlayerWindowToggleServiceTests
{
    [Theory]
    [InlineData("cloudmusic.exe", "netease")]
    [InlineData("网易云音乐", "netease")]
    [InlineData("QQMusic.exe", "qqmusic")]
    [InlineData("QQ音乐", "qqmusic")]
    [InlineData("SodaMusic.exe", "soda")]
    [InlineData("com.luna.music", "soda")]
    [InlineData("汽水音乐", "soda")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "spotify")]
    [InlineData("AppleInc.AppleMusic", "applemusic")]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("msedge.exe", "msedge")]
    public void GetPlayerIdMapsMediaSource(string sourceAppId, string expected)
    {
        Assert.Equal(expected, PlayerWindowToggleService.GetPlayerId(sourceAppId));
    }

    [Fact]
    public void GetProcessNamesIncludesSodaMainProcessAndLauncher()
    {
        var names = PlayerWindowToggleService.GetProcessNames("SodaMusic.exe");

        Assert.Contains("SodaMusic", names);
        Assert.Contains("SodaMusicLauncher", names);
    }

    [Fact]
    public void UnknownMediaSourceDoesNotTargetAnUnrelatedWindow()
    {
        Assert.Null(PlayerWindowToggleService.GetPlayerId("unknown.player"));
        Assert.Empty(PlayerWindowToggleService.GetProcessNames("unknown.player"));
    }
}
