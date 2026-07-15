using System.Threading;
using System.Windows;

namespace MusicBar;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\MusicBar.SingleInstance.47BE5BD0-2A95-49BC-BFB5-93379FA879E6";
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
