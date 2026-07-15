using MusicBar.Services;

namespace MusicBar.Tests;

public sealed class MusicPlayerLauncherServiceTests
{
    [Theory]
    [InlineData("\"D:\\Music\\Player.exe\",0", "D:\\Music\\Player.exe")]
    [InlineData("D:\\Music\\Player.exe, 3", "D:\\Music\\Player.exe")]
    [InlineData("D:\\Music\\Player.exe", "D:\\Music\\Player.exe")]
    public void NormalizeDisplayIconPath_RemovesQuotesAndResourceIndex(
        string value,
        string expected)
    {
        Assert.Equal(expected, MusicPlayerLauncherService.NormalizeDisplayIconPath(value));
    }
}
