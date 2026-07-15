using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace MusicBar.Services;

public sealed class SystemThemeService : IDisposable
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private bool _disposed;

    public event EventHandler? SystemAppearanceChanged;

    public SystemThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Apply()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(Apply);
            return;
        }

        var isLight = ReadDword("SystemUsesLightTheme", 0) != 0;
        var transparencyEnabled = ReadDword("EnableTransparency", 1) != 0;
        var palette = CreatePalette(isLight, transparencyEnabled, ReadAccentColor());

        SetBrush("PanelBrush", palette.Panel);
        SetBrush("PanelBorderBrush", palette.Border);
        SetBrush("TextPrimary", palette.TextPrimary);
        SetBrush("TextSecondary", palette.TextSecondary);
        SetBrush("AccentBrush", palette.Accent);
        SetBrush("ControlHoverBrush", palette.ControlHover);
        SetBrush("ControlPressedBrush", palette.ControlPressed);
        SetBrush("PlaySurfaceBrush", palette.PlaySurface);
        SetBrush("PlayForegroundBrush", palette.PlayForeground);
        SetBrush("ArtworkSurfaceBrush", palette.ArtworkSurface);
        SetBrush("ArtworkIconBrush", palette.ArtworkIcon);
    }

    internal static ThemePalette CreatePalette(bool isLight, bool transparencyEnabled, Color accent)
    {
        accent = EnsureAccentContrast(accent, isLight);
        if (SystemParameters.HighContrast)
        {
            return new ThemePalette(
                SystemColors.WindowColor, SystemColors.WindowTextColor,
                SystemColors.WindowTextColor, SystemColors.GrayTextColor, SystemColors.HighlightColor,
                SystemColors.ControlColor, SystemColors.ControlDarkColor,
                SystemColors.HighlightColor, SystemColors.HighlightTextColor,
                SystemColors.ControlColor, SystemColors.GrayTextColor);
        }

        if (isLight)
        {
            return new ThemePalette(
                transparencyEnabled ? Color.FromArgb(0x28, 255, 255, 255) : Color.FromRgb(243, 243, 243),
                Color.FromArgb(0x24, 0, 0, 0),
                Color.FromArgb(0xE8, 0, 0, 0), Color.FromArgb(0x9E, 0, 0, 0), accent,
                Color.FromArgb(0x14, 0, 0, 0), Color.FromArgb(0x22, 0, 0, 0),
                Color.FromArgb(0x1E, 0, 0, 0), Color.FromArgb(0xE8, 0, 0, 0),
                Color.FromArgb(0x12, 0, 0, 0), Color.FromArgb(0x72, 0, 0, 0));
        }

        return new ThemePalette(
            transparencyEnabled ? Color.FromArgb(0x24, 0, 0, 0) : Color.FromRgb(32, 32, 32),
            Color.FromArgb(0x26, 255, 255, 255),
            Color.FromArgb(0xF2, 255, 255, 255), Color.FromArgb(0xA8, 255, 255, 255), accent,
            Color.FromArgb(0x18, 255, 255, 255), Color.FromArgb(0x2C, 255, 255, 255),
            Color.FromArgb(0x24, 255, 255, 255), Color.FromArgb(0xF2, 255, 255, 255),
            Color.FromArgb(0x16, 255, 255, 255), Color.FromArgb(0x78, 255, 255, 255));
    }

    private static Color ReadAccentColor()
    {
        if (DwmGetColorizationColor(out var colorization, out _) == 0)
        {
            return Color.FromArgb(
                255,
                (byte)((colorization >> 16) & 0xFF),
                (byte)((colorization >> 8) & 0xFF),
                (byte)(colorization & 0xFF));
        }

        return SystemParameters.WindowGlassColor;
    }

    private static Color EnsureAccentContrast(Color color, bool onLightBackground)
    {
        var luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255;
        if (onLightBackground && luminance > 0.62)
        {
            return Blend(color, Colors.Black, 0.34);
        }
        if (!onLightBackground && luminance < 0.48)
        {
            return Blend(color, Colors.White, 0.42);
        }
        return color;
    }

    private static Color Blend(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + ((second.R - first.R) * amount)),
        (byte)Math.Round(first.G + ((second.G - first.G) * amount)),
        (byte)Math.Round(first.B + ((second.B - first.B) * amount)));

    private static int ReadDword(string name, int fallback) =>
        Registry.GetValue(PersonalizeKey, name, fallback) is int value ? value : fallback;

    private static void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => NotifyChanged();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => NotifyChanged();

    private void NotifyChanged()
    {
        if (_disposed)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Apply();
            SystemAppearanceChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colorization, out bool opaqueBlend);

    internal sealed record ThemePalette(
        Color Panel, Color Border, Color TextPrimary, Color TextSecondary, Color Accent,
        Color ControlHover, Color ControlPressed, Color PlaySurface, Color PlayForeground,
        Color ArtworkSurface, Color ArtworkIcon);
}
