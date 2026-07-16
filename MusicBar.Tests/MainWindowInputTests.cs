using System.Windows.Controls;
using System.Windows.Documents;
using System.Runtime.ExceptionServices;

namespace MusicBar.Tests;

public sealed class MainWindowInputTests
{
    [Fact]
    public void IsInsideButtonAcceptsRunContentWithoutThrowing()
    {
        RunInSta(() =>
        {
            var text = new TextBlock();
            var run = new Run("歌曲名");
            text.Inlines.Add(run);

            Assert.False(MainWindow.IsInsideButton(run));
        });
    }

    [Fact]
    public void IsInsideButtonRecognizesButtonItself()
    {
        RunInSta(() => Assert.True(MainWindow.IsInsideButton(new Button())));
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
