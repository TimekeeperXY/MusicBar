using System.Windows.Media;

namespace MusicBar.Models;

public enum MusicPlayerLaunchKind
{
    Executable,
    AppUserModelId
}

public sealed record InstalledMusicPlayer(
    string Id,
    string Name,
    MusicPlayerLaunchKind LaunchKind,
    string LaunchTarget,
    string? LaunchArguments,
    IReadOnlyList<string> ProcessNames,
    ImageSource? Icon);
