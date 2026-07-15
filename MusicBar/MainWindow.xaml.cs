using Microsoft.Win32;
using MusicBar.Services;
using MusicBar.ViewModels;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MusicBar;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);
    private readonly MainViewModel _viewModel = new();
    private readonly SystemThemeService _themeService = new();
    private readonly DispatcherTimer _zOrderTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private HwndSource? _windowSource;
    private int _taskbarCreatedMessage;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _themeService.SystemAppearanceChanged += OnSystemAppearanceChanged;
        _zOrderTimer.Tick += OnZOrderTimerTick;
        Deactivated += OnWindowDeactivated;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _themeService.Apply();
        DockToTaskbar();
        _zOrderTimer.Start();
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(
                "Windows 拒绝了全局媒体控制权限。请确认系统版本为 Windows 10 1809 或更高版本。",
                "MusicBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"媒体服务启动失败：{exception.Message}", "MusicBar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExToolWindow));
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private void DockToTaskbar()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out var bounds))
        {
            var dpi = GetDpiForWindow(taskbar);
            var scale = dpi > 0 ? 96d / dpi : 1d;
            var taskbarWidth = (bounds.Right - bounds.Left) * scale;
            var taskbarHeight = (bounds.Bottom - bounds.Top) * scale;

            if (taskbarWidth >= taskbarHeight)
            {
                Height = Math.Clamp(taskbarHeight, 40, 64);
                Width = Math.Clamp(Math.Min(560, taskbarWidth - 16), MinWidth, MaxWidth);
                Left = (bounds.Left * scale) + 8;
                Top = bounds.Top * scale;
                Dispatcher.BeginInvoke(KeepAboveTaskbar, DispatcherPriority.Loaded);
                return;
            }
        }

        var workArea = SystemParameters.WorkArea;
        Height = 48;
        Left = workArea.Left + 8;
        Top = workArea.Bottom - Height;
        Dispatcher.BeginInvoke(KeepAboveTaskbar, DispatcherPriority.Loaded);
    }

    private void OnSystemAppearanceChanged(object? sender, EventArgs e) => DockToTaskbar();

    private void OnWindowDeactivated(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(KeepAboveTaskbar, DispatcherPriority.ApplicationIdle);

    private void OnZOrderTimerTick(object? sender, EventArgs e) => KeepAboveTaskbar();

    private void KeepAboveTaskbar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == _taskbarCreatedMessage)
        {
            Dispatcher.BeginInvoke(() =>
            {
                DockToTaskbar();
                KeepAboveTaskbar();
            }, DispatcherPriority.Loaded);
        }
        return IntPtr.Zero;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase)
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void OnLoadLyricsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择与当前歌曲匹配的 LRC 歌词",
            Filter = "LRC 歌词 (*.lrc)|*.lrc|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                await _viewModel.LoadLyricsAsync(dialog.FileName);
            }
            catch (Exception exception)
            {
                MessageBox.Show($"歌词加载失败：{exception.Message}", "MusicBar",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void OnRefreshOnlineLyricsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RefreshOnlineLyricsAsync();
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(exception.Message, "MusicBar",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"在线歌词重新搜索失败：{exception.Message}", "MusicBar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDockClick(object sender, RoutedEventArgs e)
    {
        DockToTaskbar();
        KeepAboveTaskbar();
    }

    private void OnCalibratePositionClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TimeCalibrationWindow(_viewModel.CurrentPosition) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.CalibratePosition(dialog.Position);
        }
    }

    private void OnAdjustLyricsOffsetClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string seconds } &&
            double.TryParse(seconds, out var value))
        {
            _viewModel.AdjustLyricsOffset(TimeSpan.FromSeconds(value));
        }
    }

    private void OnResetLyricsOffsetClick(object sender, RoutedEventArgs e) =>
        _viewModel.ResetLyricsOffset();

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _zOrderTimer.Stop();
        _zOrderTimer.Tick -= OnZOrderTimerTick;
        Deactivated -= OnWindowDeactivated;
        _windowSource?.RemoveHook(WindowMessageHook);
        _themeService.SystemAppearanceChanged -= OnSystemAppearanceChanged;
        _themeService.Dispose();
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
