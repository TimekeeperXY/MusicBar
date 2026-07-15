using System.Globalization;
using System.Windows;

namespace MusicBar;

public partial class TimeCalibrationWindow : Window
{
    public TimeSpan Position { get; private set; }

    public TimeCalibrationWindow(TimeSpan currentPosition)
    {
        InitializeComponent();
        PositionTextBox.Text = $"{(int)currentPosition.TotalMinutes}:{currentPosition.Seconds:00}";
        PositionTextBox.SelectAll();
        PositionTextBox.Focus();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var text = PositionTextBox.Text.Trim();
        var parts = text.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) &&
            double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) &&
            minutes >= 0 && seconds is >= 0 and < 60)
        {
            Position = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            DialogResult = true;
            return;
        }

        MessageBox.Show(this, "请输入有效时间，例如 1:23。", "MusicBar",
            MessageBoxButton.OK, MessageBoxImage.Information);
        PositionTextBox.SelectAll();
        PositionTextBox.Focus();
    }
}
