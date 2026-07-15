using MusicBar.Models;
using MusicBar.Services.Lyrics;

namespace MusicBar.Tests;

public sealed class SodaLyricsProviderTests
{
    [Fact]
    public void ParseKrc_UsesLineStartAndRemovesWordTimingTags()
    {
        const string content = "[1710,2530]<0,190,0>忽<190,210,0>然<400,320,0>想<720,380,0>起\n" +
                               "[5000,1000]<0,500,0>下一句";

        var lines = SodaLyricsProvider.ParseKrc(content);

        Assert.Collection(lines,
            line =>
            {
                Assert.Equal(TimeSpan.FromMilliseconds(1710), line.Time);
                Assert.Equal("忽然想起", line.Text);
            },
            line =>
            {
                Assert.Equal(TimeSpan.FromSeconds(5), line.Time);
                Assert.Equal("下一句", line.Text);
            });
    }

    [Fact]
    public void Confidence_AcceptsExactSodaMetadata()
    {
        var track = new LyricsTrack("Moonchild", "陶喆", "", TimeSpan.FromSeconds(256), "汽水音乐");
        var candidate = new SodaLyricsProvider.SodaCandidate(
            "7491635677626796033", "Moonchild", ["陶喆"],
            TimeSpan.FromMilliseconds(256093), "lyrics", 0);

        Assert.True(SodaLyricsProvider.CalculateConfidence(track, candidate) >= 0.99);
    }

    [Fact]
    public void MsgpackrReader_ReusesRecordDefinitions()
    {
        byte[] encoded =
        [
            0x92,
            0xd4, 0x72, 0x40, 0x91, 0xa1, (byte)'x', 0x01,
            0x40, 0x02
        ];

        var values = Assert.IsType<List<object?>>(new MsgpackrReader(encoded).Read());
        var first = Assert.IsType<Dictionary<string, object?>>(values[0]);
        var second = Assert.IsType<Dictionary<string, object?>>(values[1]);

        Assert.Equal(1L, first["x"]);
        Assert.Equal(2L, second["x"]);
    }
}
