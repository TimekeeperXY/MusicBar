using Microsoft.Win32;
using MusicBar.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace MusicBar.Services;

public sealed class MusicPlayerLauncherService
{
    private static readonly PlayerDefinition[] Definitions =
    [
        new("netease", "网易云音乐", ["网易云音乐", "NetEase Cloud Music", "CloudMusic"],
            ["cloudmusic.exe"], ["cloudmusic"], [Path.Combine("NetEase", "CloudMusic")],
            "--remote-debugging-port=16453"),
        new("qqmusic", "QQ音乐", ["QQ音乐", "QQMusic"],
            ["QQMusic.exe"], ["QQMusic"], [Path.Combine("Tencent", "QQMusic")]),
        new("soda", "汽水音乐", ["汽水音乐", "Soda Music", "SodaMusic"],
            ["SodaMusicLauncher.exe", "SodaMusic.exe"], ["SodaMusic", "SodaMusicLauncher"],
            ["Soda Music", "SodaMusic"]),
        new("spotify", "Spotify", ["Spotify"],
            ["Spotify.exe"], ["Spotify"], ["Spotify"]),
        new("applemusic", "Apple Music", ["Apple Music", "AppleMusic", "AppleInc.AppleMusic"],
            ["AppleMusic.exe"], ["AppleMusic"], [Path.Combine("Apple", "Apple Music")])
    ];

    public IReadOnlyList<InstalledMusicPlayer> DiscoverInstalledPlayers()
    {
        var uninstallEntries = ReadUninstallEntries();
        var packages = ReadStorePackages();
        var players = new List<InstalledMusicPlayer>();

        foreach (var definition in Definitions)
        {
            var executable = FindDesktopExecutable(definition, uninstallEntries);
            if (executable is not null)
            {
                players.Add(new InstalledMusicPlayer(
                    definition.Id, definition.Name, MusicPlayerLaunchKind.Executable,
                    executable, definition.LaunchArguments, definition.ProcessNames,
                    TryReadFileIcon(executable)));
                continue;
            }

            var package = packages.FirstOrDefault(candidate =>
                definition.Aliases.Any(alias =>
                    candidate.PackageIdentity.Contains(alias, StringComparison.OrdinalIgnoreCase)));
            if (package is not null)
            {
                players.Add(new InstalledMusicPlayer(
                    definition.Id, definition.Name, MusicPlayerLaunchKind.AppUserModelId,
                    package.AppUserModelId, null, definition.ProcessNames, package.Icon));
            }
        }

        return players;
    }

    public Task LaunchAsync(InstalledMusicPlayer player)
    {
        if (TryActivateRunningPlayer(player.ProcessNames))
        {
            return Task.CompletedTask;
        }

        ProcessStartInfo startInfo;
        if (player.LaunchKind == MusicPlayerLaunchKind.AppUserModelId)
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{player.LaunchTarget}",
                UseShellExecute = true
            };
        }
        else
        {
            if (!File.Exists(player.LaunchTarget))
            {
                throw new FileNotFoundException($"找不到 {player.Name} 的启动程序。", player.LaunchTarget);
            }

            startInfo = new ProcessStartInfo
            {
                FileName = player.LaunchTarget,
                Arguments = player.LaunchArguments ?? string.Empty,
                WorkingDirectory = Path.GetDirectoryName(player.LaunchTarget),
                UseShellExecute = true
            };
        }

        Process.Start(startInfo);
        return Task.CompletedTask;
    }

    internal static string? NormalizeDisplayIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : value.Trim('"');
        }

        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _))
        {
            value = value[..comma];
        }
        return value.Trim().Trim('"');
    }

    private static string? FindDesktopExecutable(
        PlayerDefinition definition,
        IReadOnlyList<UninstallEntry> entries)
    {
        foreach (var entry in entries.Where(entry => definition.Aliases.Any(alias =>
                     entry.DisplayName.Contains(alias, StringComparison.OrdinalIgnoreCase))))
        {
            var iconPath = NormalizeDisplayIconPath(entry.DisplayIcon);
            if (IsLaunchExecutable(iconPath, definition.ExecutableNames))
            {
                return Path.GetFullPath(iconPath!);
            }

            foreach (var executableName in definition.ExecutableNames)
            {
                var path = string.IsNullOrWhiteSpace(entry.InstallLocation)
                    ? null
                    : Path.Combine(Environment.ExpandEnvironmentVariables(entry.InstallLocation), executableName);
                if (path is not null && File.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
        }

        foreach (var root in GetStandardInstallRoots())
        {
            foreach (var relativeDirectory in definition.RelativeDirectories)
            {
                foreach (var executableName in definition.ExecutableNames)
                {
                    var path = Path.Combine(root, relativeDirectory, executableName);
                    if (File.Exists(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }
            }
        }
        return null;
    }

    private static bool IsLaunchExecutable(string? path, IReadOnlyList<string> executableNames) =>
        path is not null && File.Exists(path) && executableNames.Any(name =>
            string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> GetStandardInstallRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
    ];

    private static List<UninstallEntry> ReadUninstallEntries()
    {
        var results = new List<UninstallEntry>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var subkeyName in uninstall.GetSubKeyNames())
                    {
                        using var subkey = uninstall.OpenSubKey(subkeyName);
                        var displayName = subkey?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            results.Add(new UninstallEntry(
                                displayName,
                                subkey?.GetValue("DisplayIcon") as string,
                                subkey?.GetValue("InstallLocation") as string));
                        }
                    }
                }
                catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    // Another registry view can still provide the same application data.
                }
            }
        }
        return results;
    }

    private static List<StorePackage> ReadStorePackages()
    {
        var results = new List<StorePackage>();
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                var identity = $"{package.Id.Name} {package.Id.FamilyName}";
                if (!Definitions.Any(definition => definition.Aliases.Any(alias =>
                        identity.Contains(alias, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                var installPath = package.InstalledLocation?.Path;
                if (string.IsNullOrWhiteSpace(installPath))
                {
                    continue;
                }

                var manifestPath = Path.Combine(installPath, "AppxManifest.xml");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var manifest = XDocument.Load(manifestPath);
                var application = manifest.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "Application");
                var applicationId = application?.Attribute("Id")?.Value;
                if (string.IsNullOrWhiteSpace(applicationId))
                {
                    continue;
                }

                var logo = application!.DescendantsAndSelf()
                    .Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "Square44x44Logo")?.Value;
                results.Add(new StorePackage(
                    identity,
                    $"{package.Id.FamilyName}!{applicationId}",
                    TryReadPackageIcon(installPath, logo)));
            }
        }
        catch
        {
            // Package enumeration is unavailable in some managed or restricted Windows profiles.
        }
        return results;
    }

    private static ImageSource? TryReadPackageIcon(string installPath, string? relativeLogo)
    {
        if (string.IsNullOrWhiteSpace(relativeLogo))
        {
            return null;
        }

        try
        {
            var requestedPath = Path.Combine(installPath, relativeLogo.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(requestedPath) ?? installPath;
            var path = File.Exists(requestedPath)
                ? requestedPath
                : Directory.GetFiles(
                        directory,
                        $"{Path.GetFileNameWithoutExtension(requestedPath)}*.png",
                        SearchOption.TopDirectoryOnly)
                    .OrderBy(candidate => candidate.Contains("targetsize-48", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .FirstOrDefault();
            if (path is null)
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryReadFileIcon(string path)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private static bool TryActivateRunningPlayer(IReadOnlyList<string> processNames)
    {
        foreach (var processName in processNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero)
                    {
                        continue;
                    }
                    ShowWindow(window, SwRestore);
                    SetForegroundWindow(window);
                    return true;
                }
            }
        }
        return false;
    }

    private const uint ShgfiIcon = 0x000000100;
    private const int SwRestore = 9;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path, uint fileAttributes, ref ShFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    private sealed record PlayerDefinition(
        string Id,
        string Name,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> ExecutableNames,
        IReadOnlyList<string> ProcessNames,
        IReadOnlyList<string> RelativeDirectories,
        string? LaunchArguments = null);

    private sealed record UninstallEntry(string DisplayName, string? DisplayIcon, string? InstallLocation);
    private sealed record StorePackage(string PackageIdentity, string AppUserModelId, ImageSource? Icon);
}
