using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MusicBar.Services;

internal sealed class PlayerWindowToggleService
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const uint GwOwner = 4;
    private const int SwHide = 0;
    private const int SwRestore = 9;
    private readonly Dictionary<string, IntPtr> _lastWindows = new(StringComparer.OrdinalIgnoreCase);

    public bool TryToggle(string? sourceAppId, IntPtr previousForegroundWindow)
    {
        var processNames = GetProcessNames(sourceAppId);
        if (processNames.Count == 0)
        {
            return false;
        }

        var processIds = GetProcessIds(processNames);
        if (processIds.Count == 0)
        {
            return false;
        }

        var cacheKey = GetPlayerId(sourceAppId) ?? sourceAppId ?? string.Empty;
        var target = FindBestWindow(processIds, cacheKey);
        if (target == IntPtr.Zero)
        {
            return false;
        }

        _lastWindows[cacheKey] = target;
        var foreground = GetForegroundWindow();
        var playerWasForeground = BelongsToProcess(foreground, processIds) ||
            BelongsToProcess(previousForegroundWindow, processIds);

        if (playerWasForeground)
        {
            if (foreground != target && BelongsToProcess(foreground, processIds))
            {
                ShowWindow(foreground, SwHide);
            }
            ShowWindow(target, SwHide);
            return true;
        }

        ShowWindow(target, SwRestore);
        BringWindowToTop(target);
        SetForegroundWindow(target);
        return true;
    }

    internal static string? GetPlayerId(string? sourceAppId)
    {
        var source = sourceAppId?.ToLowerInvariant() ?? string.Empty;
        if (source.Contains("cloudmusic") || source.Contains("netease") ||
            source.Contains("网易云音乐")) return "netease";
        if (source.Contains("qqmusic") || source.Contains("qq音乐")) return "qqmusic";
        if (source.Contains("sodamusic") || source.Contains("qishui") ||
            source.Contains("douyin") || source.Contains("luna") ||
            source.Contains("汽水音乐")) return "soda";
        if (source.Contains("spotify")) return "spotify";
        if (source.Contains("applemusic") || source.Contains("apple music")) return "applemusic";
        if (source.Contains("chrome")) return "chrome";
        if (source.Contains("msedge")) return "msedge";
        return null;
    }

    internal static IReadOnlyList<string> GetProcessNames(string? sourceAppId) =>
        GetPlayerId(sourceAppId) switch
        {
            "netease" => ["cloudmusic"],
            "qqmusic" => ["QQMusic"],
            "soda" => ["SodaMusic", "SodaMusicLauncher"],
            "spotify" => ["Spotify"],
            "applemusic" => ["AppleMusic"],
            "chrome" => ["chrome"],
            "msedge" => ["msedge"],
            _ => []
        };

    private IntPtr FindBestWindow(HashSet<uint> processIds, string cacheKey)
    {
        if (_lastWindows.TryGetValue(cacheKey, out var cached) &&
            IsWindow(cached) && BelongsToProcess(cached, processIds))
        {
            return cached;
        }

        var candidates = new List<(IntPtr Window, int Score)>();
        EnumWindows((window, _) =>
        {
            if (!BelongsToProcess(window, processIds) || !GetWindowRect(window, out var rect))
            {
                return true;
            }

            var width = Math.Max(0, rect.Right - rect.Left);
            var height = Math.Max(0, rect.Bottom - rect.Top);
            if (width < 160 || height < 100)
            {
                return true;
            }

            var score = IsWindowVisible(window) ? 1000 : 0;
            score += width * height >= 200_000 ? 200 : 0;
            score += GetWindow(window, GwOwner) == IntPtr.Zero ? 100 : 0;
            score += GetWindowTextLength(window) > 0 ? 80 : 0;
            score -= (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExToolWindow) != 0 ? 500 : 0;
            candidates.Add((window, score));
            return true;
        }, IntPtr.Zero);

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Window)
            .FirstOrDefault();
    }

    private static HashSet<uint> GetProcessIds(IReadOnlyList<string> processNames)
    {
        var ids = new HashSet<uint>();
        foreach (var processName in processNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        ids.Add((uint)process.Id);
                    }
                }
            }
            catch
            {
                // A process can exit while its windows are being discovered.
            }
        }
        return ids;
    }

    private static bool BelongsToProcess(IntPtr window, HashSet<uint> processIds)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }
        GetWindowThreadProcessId(window, out var processId);
        return processIds.Contains(processId);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
