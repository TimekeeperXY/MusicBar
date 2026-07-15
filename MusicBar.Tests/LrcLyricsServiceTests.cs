using MusicBar.Services;

namespace MusicBar.Tests;

public sealed class LrcLyricsServiceTests
{
    [Fact]
    public void Load_ParsesMultipleTimestampFormatsAndSelectsCurrentLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"musicbar-{Guid.NewGuid():N}.lrc");
        try
        {
            File.WriteAllText(path, "[00:01.50]第一句\n[00:03:250]第二句\n[00:05.00][00:08.00]重复句");
            var service = new LrcLyricsService();

            service.Load(path);

            Assert.Equal("♪", service.GetLine(TimeSpan.FromSeconds(1)));
            Assert.Equal("第一句", service.GetLine(TimeSpan.FromSeconds(2)));
            Assert.Equal("第二句", service.GetLine(TimeSpan.FromSeconds(4)));
            Assert.Equal("重复句", service.GetLine(TimeSpan.FromSeconds(8.5)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetLine_WithoutLyrics_ReturnsLoadHint()
    {
        var service = new LrcLyricsService();

        Assert.Contains("LRC", service.GetLine(TimeSpan.Zero));
    }
}
