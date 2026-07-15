using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicBar.Models;
using MusicBar.Services;
using MusicBar.Services.Lyrics;
using System.Windows;
using System.Windows.Media;

namespace MusicBar.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MediaSessionService _media = new();
    private readonly MusicPlayerLauncherService _playerLauncher = new();
    private readonly LrcLyricsService _lrcParser = new();
    private readonly ManualLyricsProvider _manualLyrics = new();
    private readonly CachedOnlineLyricsProvider _onlineLyrics;
    private readonly LyricsCoordinator _lyrics;
    private TimeSpan _latestPosition;

    [ObservableProperty] private string title = MediaSnapshot.Empty.Title;
    [ObservableProperty] private string artistAndSource = MediaSnapshot.Empty.Artist;
    [ObservableProperty] private string currentLyric = "右键可加载本地 LRC 歌词";
    [ObservableProperty] private ImageSource? artwork;
    [ObservableProperty] private string playPauseGlyph = "\uE768";
    [ObservableProperty] private bool canTogglePlayback;
    [ObservableProperty] private bool canPrevious;
    [ObservableProperty] private bool canNext;
    [ObservableProperty] private bool hasMediaSession;
    [ObservableProperty] private bool hasInstalledPlayers;
    [ObservableProperty] private string launcherHint = "正在检测播放器…";
    [ObservableProperty] private IReadOnlyList<InstalledMusicPlayer> installedPlayers = [];

    public IAsyncRelayCommand TogglePlayPauseCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand<InstalledMusicPlayer> LaunchPlayerCommand { get; }

    public MainViewModel()
    {
        _onlineLyrics = new CachedOnlineLyricsProvider(new LrclibLyricsProvider());
        _lyrics = new LyricsCoordinator([
            _manualLyrics,
            new NeteaseLyricsProvider(),
            new SodaLyricsProvider(),
            _onlineLyrics
        ]);
        TogglePlayPauseCommand = new AsyncRelayCommand(_media.TogglePlayPauseAsync, () => CanTogglePlayback);
        PreviousCommand = new AsyncRelayCommand(_media.PreviousAsync, () => CanPrevious);
        NextCommand = new AsyncRelayCommand(_media.NextAsync, () => CanNext);
        LaunchPlayerCommand = new AsyncRelayCommand<InstalledMusicPlayer>(LaunchPlayerAsync);
        _media.SnapshotChanged += OnSnapshotChanged;
        _lyrics.StateChanged += OnLyricsStateChanged;
    }

    public async Task InitializeAsync()
    {
        var discovery = Task.Run(_playerLauncher.DiscoverInstalledPlayers);
        await _media.InitializeAsync();
        try
        {
            InstalledPlayers = await discovery;
            HasInstalledPlayers = InstalledPlayers.Count > 0;
            LauncherHint = HasInstalledPlayers ? "打开播放器" : "未检测到音乐播放器";
        }
        catch
        {
            InstalledPlayers = [];
            HasInstalledPlayers = false;
            LauncherHint = "播放器检测失败";
        }
    }

    public async Task LoadLyricsAsync(string path)
    {
        var track = _lyrics.CurrentTrack ?? throw new InvalidOperationException("请先播放一首歌曲，再加载对应的 LRC。 ");
        var document = _lrcParser.Parse(path);
        _manualLyrics.Set(track, document);
        await _lyrics.UpdateTrackAsync(track, force: true);
        CurrentLyric = _lyrics.GetDisplayLine(_latestPosition);
    }

    public async Task RefreshOnlineLyricsAsync()
    {
        var track = _lyrics.CurrentTrack ??
            throw new InvalidOperationException("请先播放一首歌曲，再重新搜索在线歌词。");

        _onlineLyrics.Invalidate(track);
        await _lyrics.UpdateTrackAsync(
            track,
            force: true,
            minimumPriority: _onlineLyrics.Priority);
        CurrentLyric = _lyrics.GetDisplayLine(_latestPosition);
    }

    public TimeSpan CurrentPosition => _latestPosition;

    public void CalibratePosition(TimeSpan actualPosition)
    {
        _media.CalibratePosition(actualPosition);
        _latestPosition = actualPosition;
        CurrentLyric = _lyrics.GetDisplayLine(actualPosition);
    }

    public void AdjustLyricsOffset(TimeSpan delta) => _lyrics.AdjustOffset(delta);

    public void ResetLyricsOffset() => _lyrics.ResetOffset();

    private async Task LaunchPlayerAsync(InstalledMusicPlayer? player)
    {
        if (player is null)
        {
            return;
        }

        LauncherHint = $"正在启动 {player.Name}…";
        try
        {
            await _playerLauncher.LaunchAsync(player);
            LauncherHint = $"等待 {player.Name} 播放";
        }
        catch (Exception exception)
        {
            LauncherHint = $"{player.Name} 启动失败";
            MessageBox.Show($"无法启动 {player.Name}：{exception.Message}", "MusicBar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSnapshotChanged(object? sender, MediaSnapshot snapshot)
    {
        Application.Current.Dispatcher.Invoke(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(MediaSnapshot snapshot)
    {
        HasMediaSession = snapshot.HasSession;
        Title = snapshot.Title;
        ArtistAndSource = snapshot.HasSession
            ? $"{snapshot.Artist}  ·  {GetFriendlySource(snapshot.SourceAppId)}"
            : snapshot.Artist;
        Artwork = snapshot.Artwork;
        PlayPauseGlyph = snapshot.IsPlaying ? "\uE769" : "\uE768";
        CanTogglePlayback = snapshot.HasSession && (snapshot.CanPlay || snapshot.CanPause);
        CanPrevious = snapshot.CanPrevious;
        CanNext = snapshot.CanNext;
        _latestPosition = snapshot.Position;
        if (snapshot.HasSession)
        {
            LauncherHint = "打开播放器";
            _ = _lyrics.UpdateTrackAsync(new LyricsTrack(
                snapshot.Title, snapshot.Artist, snapshot.Album,
                snapshot.Duration, snapshot.SourceAppId));
            CurrentLyric = _lyrics.GetDisplayLine(snapshot.Position);
        }
        else
        {
            CurrentLyric = "右键可加载本地 LRC 歌词";
        }

        TogglePlayPauseCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    private void OnLyricsStateChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
            CurrentLyric = _lyrics.GetDisplayLine(_latestPosition));
    }

    private static string GetFriendlySource(string source)
    {
        var normalized = source.ToLowerInvariant();
        if (normalized.Contains("spotify")) return "Spotify";
        if (normalized.Contains("applemusic") || normalized.Contains("apple music")) return "Apple Music";
        if (normalized.Contains("cloudmusic") || normalized.Contains("netease")) return "网易云音乐";
        if (normalized.Contains("qqmusic")) return "QQ音乐";
        if (normalized.Contains("douyin") || normalized.Contains("qishui")) return "汽水音乐";
        if (normalized.Contains("chrome")) return "Chrome";
        if (normalized.Contains("msedge")) return "Edge";
        return string.IsNullOrWhiteSpace(source) ? "系统媒体" : source.Split('!')[0];
    }

    public void Dispose()
    {
        _media.SnapshotChanged -= OnSnapshotChanged;
        _lyrics.StateChanged -= OnLyricsStateChanged;
        _lyrics.Dispose();
        _media.Dispose();
    }
}
