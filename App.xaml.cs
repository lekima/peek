using System.Diagnostics.CodeAnalysis;
using Application = System.Windows.Application;

namespace Peek;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF application lifetime cleanup is performed in OnExit.")]
internal sealed partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\PeekTranslationOverlay", out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;

        base.OnExit(e);
    }
}

