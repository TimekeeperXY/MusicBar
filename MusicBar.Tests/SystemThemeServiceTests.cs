using MusicBar.Services;
using System.Windows.Media;

namespace MusicBar.Tests;

public sealed class SystemThemeServiceTests
{
    [Fact]
    public void CreatePalette_UsesReadableForegroundForBothSystemThemes()
    {
        var accent = Color.FromRgb(0, 120, 215);

        var light = SystemThemeService.CreatePalette(true, true, accent);
        var dark = SystemThemeService.CreatePalette(false, true, accent);

        Assert.True(light.TextPrimary.R < dark.TextPrimary.R);
        Assert.True(light.Panel.A < 255);
        Assert.True(dark.Panel.A < 255);
    }

    [Fact]
    public void CreatePalette_DisablesTransparencyWhenSystemSettingIsOff()
    {
        var palette = SystemThemeService.CreatePalette(false, false, Colors.CornflowerBlue);

        Assert.Equal(255, palette.Panel.A);
        Assert.Equal(Color.FromRgb(32, 32, 32), palette.Panel);
    }
}
