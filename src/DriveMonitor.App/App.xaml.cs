using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using DriveMonitor.App.Services;
using DriveMonitor.App.ViewModels;

namespace DriveMonitor.App;

/// <summary>
/// Entry point: one instance per Windows session, settings, the main window, and last-resort error
/// handling that always stops the kernel trace session before the process ends (a kernel session
/// outlives the process that started it).
/// </summary>
public partial class App : Application
{
    private const int ErrorStormLimit = 5;
    private const long ErrorStormWindowMs = 30_000;

    /// <summary>
    /// How long logging off or shutting down waits for monitoring to stop and the activity log to be saved. Windows
    /// lists a program that has not answered within about 5 s as one that prevents signing out or shutting down.
    /// </summary>
    private static readonly TimeSpan SessionEndingWait = TimeSpan.FromSeconds(3);

    private readonly Queue<long> _recentErrors = new();
    private SingleInstance? _instance;
    private SettingsStore? _settingsStore;
    private AppSettings? _settings;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _windowLoaded;
    private bool _windowClosed;
    private bool _showingError;
    private int _shutDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        // The elevated copy started by "Restart as administrator" waits for this one to exit.
        bool restarted = e.Args.Contains(SingleInstance.RestartedArgument, StringComparer.OrdinalIgnoreCase);
        _instance = SingleInstance.TryAcquire(restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        if (_instance is null)
        {
            MessageBox.Show("DriveMonitor is already running.", "DriveMonitor", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            _settingsStore = new SettingsStore();
            _settings = _settingsStore.Load();
            _viewModel = new MainViewModel(_settings, Dispatcher);
            _window = new MainWindow(_viewModel);
            _window.Loaded += (_, _) => _windowLoaded = true;
            _window.Closed += (_, _) => _windowClosed = true;
            MainWindow = _window;
            _window.Show();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "startup");
            ShutDownViewModel(save: false, Timeout.InfiniteTimeSpan);

            // Nothing is saved after a failed start, so a saved value that broke it would break every later start too.
            string? setAside = _settingsStore?.TrySetAside();
            string settingsNote = setAside is null ? "" : $"\n\nYour settings were moved to {setAside}; the next start uses the defaults.";
            MessageBox.Show(
                $"DriveMonitor could not start:\n\n{ex.Message}{settingsNote}\n\nDetails were saved to {ErrorLog.FilePath}.",
                "DriveMonitor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel)
            return;

        // Logging off or shutting down: save, and stop the kernel session, while there is still time. Windows ends
        // the process soon after the session ends, and lists it as one that prevents the logoff if it takes more than
        // about 5 s to answer, so this waits only briefly. The settings are saved first, and the kernel session (which
        // would outlive the process) is stopped before the log; events the log has not written by then may be lost.
        _window?.CaptureBounds();
        ShutDownViewModel(save: true, SessionEndingWait);

        // Close even if another program cancels the logoff. Posted so that the window does not close in the middle
        // of WM_QUERYENDSESSION.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Shutdown()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The window closes only once the view model has stopped everything, so this does not wait then. During a
        // logoff OnSessionEnding has already waited as long as it could, and this does nothing.
        ShutDownViewModel(save: true, Timeout.InfiniteTimeSpan);
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Saves the settings and stops monitoring (timers, kernel session, log), once. The view model stops on the
    /// thread pool; this waits for it at most <paramref name="wait"/>.
    /// </summary>
    private void ShutDownViewModel(bool save, TimeSpan wait)
    {
        if (Interlocked.Exchange(ref _shutDown, 1) != 0)
            return;

        // Before the wait: during a logoff Windows may end the process while the log is still being saved.
        if (save && _settings is not null)
            _settingsStore?.TrySave(_settings);

        try
        {
            _viewModel?.ShutDownAsync().Wait(wait);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "shutdown");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Exception error = e.Exception;
        ErrorLog.Write(error, "UI thread");
        if (_windowClosed || Volatile.Read(ref _shutDown) != 0)
            return; // closing anyway; the log has it

        // Before the window has loaded there is nothing to keep running.
        if (IsFatal(error) || !_windowLoaded || _window is null || IsErrorStorm())
        {
            ExitAfterFatalError(error);
            return;
        }

        // One dialog at a time: the timers keep running in the dialog's message loop.
        if (_showingError)
            return;
        _showingError = true;
        try
        {
            MessageBox.Show(_window,
                $"Something went wrong:\n\n{error.Message}\n\nDriveMonitor keeps running. Details were saved to {ErrorLog.FilePath}.",
                "DriveMonitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _showingError = false;
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Any thread; the process ends when this returns.
        var error = e.ExceptionObject as Exception;
        if (error is not null)
            ErrorLog.Write(error, "unhandled");
        _viewModel?.EmergencyStop();
        try
        {
            MessageBox.Show(
                $"DriveMonitor has to close because of an unexpected error:\n\n{error?.Message ?? e.ExceptionObject?.ToString()}\n\n" +
                $"Monitoring was stopped. Details were saved to {ErrorLog.FilePath}.",
                "DriveMonitor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // No way left to tell the user.
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Finalizer thread: a fire-and-forget task failed. Report it and keep running.
        e.SetObserved();
        ErrorLog.Write(e.Exception, "background task");
        MainViewModel? viewModel = _viewModel;
        Exception error = e.Exception.GetBaseException();
        if (viewModel is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => viewModel.ReportBackgroundError(error)));
    }

    private void ExitAfterFatalError(Exception error)
    {
        // First stop the kernel session: if anything below fails, it must not keep running.
        _viewModel?.EmergencyStop();
        if (!_showingError)
        {
            _showingError = true;
            try
            {
                MessageBox.Show(
                    $"DriveMonitor has to close because of an unexpected error:\n\n{error.Message}\n\n" +
                    $"Monitoring was stopped. Details were saved to {ErrorLog.FilePath}.",
                    "DriveMonitor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception)
            {
                // Out of memory or similar; closing is all that is left.
            }
        }

        try
        {
            Shutdown(1);
        }
        catch (Exception)
        {
            Environment.Exit(1);
        }
    }

    /// <summary>The same failure over and over (e.g. in every layout pass) would never let the user out.</summary>
    private bool IsErrorStorm()
    {
        long now = Environment.TickCount64;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > ErrorStormWindowMs)
            _recentErrors.Dequeue();
        return _recentErrors.Count >= ErrorStormLimit;
    }

    /// <summary>Failures after which the process state cannot be trusted, or that will repeat on every attempt.</summary>
    private static bool IsFatal(Exception error) =>
        error is OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException or SEHException
            or InvalidProgramException or BadImageFormatException or TypeInitializationException or TypeLoadException
            or MissingMemberException or XamlParseException;
}
