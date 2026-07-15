using System.Windows.Media;

namespace MusicBar.Models;

public sealed record MediaSnapshot(
    bool HasSession,
    string Title,
    string Artist,
    string Album,
    string SourceAppId,
    ImageSource? Artwork,
    bool IsPlaying,
    bool CanPlay,
    bool CanPause,
    bool CanPrevious,
    bool CanNext,
    TimeSpan Position,
    TimeSpan Duration)
{
    public static MediaSnapshot Empty { get; } = new(
        false, "等待音乐播放", "打开任意支持系统媒体控制的播放器", string.Empty, string.Empty,
        null, false, false, false, false, false, TimeSpan.Zero, TimeSpan.Zero);
}
