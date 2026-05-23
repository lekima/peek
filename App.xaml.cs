using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace Peek;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF application lifetime cleanup is performed in OnExit.")]
internal sealed partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private bool _fatalDialogShown;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        _singleInstanceMutex = new Mutex(true, @"Local\PeekTranslationOverlay", out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        ShowFatalErrorAndShutdown();
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled application exception.", e.ExceptionObject as Exception);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    private void ShowFatalErrorAndShutdown()
    {
        if (!_fatalDialogShown)
        {
            _fatalDialogShown = true;
            MessageBox.Show(
                "Peek ran into an unexpected error and will close.",
                "Peek",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }

        Shutdown(1);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;

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

