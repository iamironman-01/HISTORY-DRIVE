using System.Windows.Input;
using System.Windows.Threading;
using DriveMonitor.App.Services;
using DriveMonitor.App.ViewModels;
using DriveMonitor.Core;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Models;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.App;

/// <summary>
/// The main window's view model, on a thread that runs a WPF dispatcher as the app's UI thread does: the warning a
/// start can leave behind, and closing, which must never wait on that thread for the activity log. No test starts
/// monitoring, so no kernel session is created, elevated or not.
/// </summary>
public sealed class MainViewModelTests : IDisposable
{
    private const string Warning =
        @"Another program holds Global\DriveMonitor.Session.DriveMonitor-FileIO, which keeps two DriveMonitors from sharing " +
        "the session \"DriveMonitor-FileIO\", but no such session is running, so monitoring started anyway.";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a test gives something that must not happen yet the chance to happen anyway.</summary>
    private static readonly TimeSpan Briefly = TimeSpan.FromMilliseconds(500);

    private static readonly FileActivityEvent Event = new()
    {
        Sequence = 1,
        Timestamp = new DateTime(2026, 10, 31, 12, 0, 0),
        Operation = FileOperation.Write,
        Path = @"E:\reports\q3.xlsx",
        Drive = 'E',
        Bytes = 4096,
        ProcessName = "EXCEL.EXE",
        ProcessId = 4242,
    };

    private readonly UiThread _ui = new();
    private readonly MainViewModel _viewModel;
    private readonly ManualResetEventSlim _atLastStep = new();
    private readonly ManualResetEventSlim _release = new();
    private readonly ManualResetEventSlim _releaseStart = new();
    private readonly ManualResetEventSlim _releaseLog = new();
    private int _lastSteps;

    public MainViewModelTests()
    {
        _viewModel = _ui.Invoke(() => new MainViewModel(new AppSettings { AutoStart = false }, Dispatcher.CurrentDispatcher));
    }

    public void Dispose()
    {
        // A test that held the shutdown, a start or a log and failed before letting it go.
        _release.Set();
        _releaseStart.Set();
        _releaseLog.Set();
        _ui.Invoke(_viewModel.ShutDownAsync).Wait(Timeout);
        _ui.Dispose();
        _atLastStep.Dispose();
        _release.Dispose();
        _releaseStart.Dispose();
        _releaseLog.Dispose();
    }

    [Fact]
    public void StartWarning_IsAWarningBannerThatDoesNotCloseByItself()
    {
        _ui.Invoke(() => _viewModel.ShowStartWarning(Warning));

        BannerViewModel banner = Assert.Single(Banners());
        Assert.Equal(BannerKind.Warning, banner.Kind);
        Assert.Equal(Warning, banner.Message);
        Assert.Null(banner.ExpiresAtTick);
    }

    [Fact]
    public void StartWarning_IsReplacedByTheNextStartsWarning()
    {
        _ui.Invoke(() => _viewModel.ShowStartWarning(Warning));

        _ui.Invoke(() => _viewModel.ShowStartWarning("A later start's warning"));

        Assert.Equal("A later start's warning", Assert.Single(Banners()).Message);
    }

    [Fact]
    public void StartWarning_GoesAwayWhenAStartHasNone()
    {
        _ui.Invoke(() => _viewModel.ShowStartWarning(Warning));

        _ui.Invoke(() => _viewModel.ShowStartWarning(null));

        Assert.Empty(Banners());
    }

    [Fact]
    public void StartWarning_GoesAwayWhenMonitoringStops()
    {
        _ui.Invoke(() => _viewModel.ShowStartWarning(Warning));

        _ui.Invoke(_viewModel.UpdateState); // the engine is not running (in a test it never ran)

        Assert.Equal(MonitorState.Stopped, _ui.Invoke(() => _viewModel.State));
        Assert.Empty(Banners());
    }

    [Fact]
    public async Task StartAsync_ShowsTheWarningOfTheStart()
    {
        _viewModel.StartEngine = () => Warning; // a start that went ahead without the guard, with no kernel session
        List<BannerViewModel> shown = ShownBanners();

        await _ui.Invoke(_viewModel.StartAsync).WaitAsync(Timeout);

        // Shown once the start is over. (Here the engine itself never ran, so the state then went back to Stopped
        // and took the warning away again, as it does when monitoring stops.)
        BannerViewModel banner = Assert.Single(_ui.Invoke(() => shown.ToList()));
        Assert.Equal(BannerKind.Warning, banner.Kind);
        Assert.Equal(Warning, banner.Message);
        Assert.Null(banner.ExpiresAtTick);
    }

    [Fact]
    public async Task TryClose_WhileTheLogIsSaved_KeepsTheWindowOpenWithoutBlockingTheUiThread()
    {
        using var temp = new TempDirectory();
        CsvActivityLog log = StartLog(temp);
        HoldShutDown();

        // TryClose returns although the shutdown is held where it saves the log (Invoke fails when the UI thread is blocked).
        Assert.False(_ui.Invoke(_viewModel.TryClose));
        Assert.True(_atLastStep.Wait(Timeout), "the shutdown did not get to saving the log");

        // Meanwhile the UI thread keeps working, the window says what it waits for, and asking again does not close it.
        BannerViewModel saving = Assert.Single(Banners());
        Assert.Equal(BannerKind.Info, saving.Kind);
        Assert.StartsWith("Saving the activity log", saving.Message, StringComparison.Ordinal);
        Assert.False(_ui.Invoke(_viewModel.TryClose));

        _release.Set();
        await _ui.Invoke(_viewModel.ShutDownAsync).WaitAsync(Timeout);
        Assert.True(_ui.Invoke(_viewModel.TryClose));
        Assert.Null(_viewModel.Engine.Log);
        Assert.Equal(1L, log.Written);
    }

    [Fact]
    public async Task TryClose_AgainWhileClosing_StartsNoSecondStopAndAsksToCloseOnce()
    {
        HoldShutDown();
        var askedToClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int closeRequests = 0;
        _ui.Invoke(() => _viewModel.CloseRequested += (_, _) =>
        {
            closeRequests++;
            askedToClose.TrySetResult();
        });

        Assert.False(_ui.Invoke(_viewModel.TryClose));
        Assert.False(_ui.Invoke(_viewModel.TryClose));
        Assert.Same(_ui.Invoke(_viewModel.ShutDownAsync), _ui.Invoke(_viewModel.ShutDownAsync));
        Assert.Empty(Banners()); // no log to save
        _release.Set();

        await askedToClose.Task.WaitAsync(Timeout);
        Assert.True(_ui.Invoke(_viewModel.TryClose));
        Assert.Equal(1, _ui.Invoke(() => closeRequests));
        Assert.Equal(1, Volatile.Read(ref _lastSteps));
    }

    [Fact]
    public async Task ShutDownAsync_TurnsOffTheCommandsThatStartSomething()
    {
        ICommand[] commands =
        [
            _viewModel.StartStopCommand,
            _viewModel.ToggleLogCommand,
            _viewModel.ExportActivityCommand,
            _viewModel.ExportFilesCommand,
            _viewModel.ExportProcessesCommand,
        ];
        var changed = new HashSet<ICommand>();
        _ui.Invoke(() =>
        {
            foreach (ICommand command in commands)
                command.CanExecuteChanged += (sender, _) => changed.Add((ICommand)sender!);
        });
        Assert.True(CanExecute(_viewModel.ToggleLogCommand));
        Assert.True(CanExecute(_viewModel.ExportFilesCommand));

        await _ui.Invoke(_viewModel.ShutDownAsync).WaitAsync(Timeout);

        Assert.All(commands, command => Assert.False(CanExecute(command)));
        Assert.Equal(commands.Length, _ui.Invoke(() => changed.Count)); // so that their buttons grey out
    }

    [Fact]
    public async Task ShutDownAsync_WhileAStartRuns_WaitsForItAndStaysStopping()
    {
        using var starting = new ManualResetEventSlim();
        _viewModel.StartEngine = () =>
        {
            starting.Set();
            _releaseStart.Wait();
            return Warning;
        };
        Task start = _ui.Invoke(_viewModel.StartAsync);
        Assert.True(starting.Wait(Timeout), "the start did not run");
        HoldShutDown();

        Task shutDown = _ui.Invoke(_viewModel.ShutDownAsync);

        // The kernel session is stopped only after the start: one still running would create it after the stop.
        Assert.Equal(MonitorState.Stopping, _ui.Invoke(() => _viewModel.State));
        Assert.False(_atLastStep.Wait(Briefly), "the shutdown went on while a start was still running");

        _releaseStart.Set();
        await start.WaitAsync(Timeout);
        Assert.True(_atLastStep.Wait(Timeout), "the shutdown did not go on after the start");

        // The end of that start changes nothing on the closing window: no start warning, still stopping.
        Assert.Equal(MonitorState.Stopping, _ui.Invoke(() => _viewModel.State));
        Assert.Empty(Banners());
        _release.Set();
        await shutDown.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ShutDownAsync_SavesAndClosesTheActivityLog()
    {
        using var temp = new TempDirectory();
        CsvActivityLog log = StartLog(temp);

        await _ui.Invoke(_viewModel.ShutDownAsync).WaitAsync(Timeout);

        Assert.Null(_viewModel.Engine.Log);
        Assert.Equal(1L, log.Written);
        using (new FileStream(log.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Opened without sharing: the log has closed the file.
        }

        Assert.Contains(Event.Path, File.ReadAllText(log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutDownAsync_WhileRecordLogIsStillSavingTheLog_WaitsForIt()
    {
        using var temp = new TempDirectory();
        using var writing = new ManualResetEventSlim();
        MonitorEngine engine = _viewModel.Engine;
        engine.Options.MinLogFreeSpaceMB = 1; // so that the log checks the free space before it writes
        engine.StartLog(temp.PathOf("activity.csv"));
        CsvActivityLog log = engine.Log!;
        log.StallTimeout = Timeout;

        // A slow drive: the log's first write waits until the test lets it go.
        log.FreeSpaceProbe = _ =>
        {
            writing.Set();
            _releaseLog.Wait();
            return long.MaxValue;
        };
        log.Write(Event);
        Assert.True(writing.Wait(Timeout), "the log did not start writing");

        // Record log is turned off: from now on the log is no longer the engine's, and it is still being saved.
        _ui.Invoke(() => _viewModel.ToggleLogCommand.Execute(null));
        long writtenAtLastStep = -1;
        HoldShutDown(() => writtenAtLastStep = log.Written);

        Task shutDown = _ui.Invoke(_viewModel.ShutDownAsync);

        Assert.StartsWith("Saving the activity log", Assert.Single(Banners()).Message, StringComparison.Ordinal);
        Assert.False(_atLastStep.Wait(Briefly), "the shutdown went on while Record log was still saving its log");

        _releaseLog.Set();
        Assert.True(_atLastStep.Wait(Timeout), "the shutdown did not go on once the log was saved");
        Assert.Equal(1L, writtenAtLastStep);
        _release.Set();
        await shutDown.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ShutDownAsync_WhenStoppingTheSessionFails_StillEndsTheLogAndReportsTheError()
    {
        using var temp = new TempDirectory();
        CsvActivityLog log = StartLog(temp);
        var failure = new InvalidOperationException("The kernel session would not stop.");
        var reported = new List<Exception>();
        _viewModel.ReportError = (error, _) =>
        {
            lock (reported)
                reported.Add(error);
        };
        _viewModel.StopEngine = () => throw failure;

        await _ui.Invoke(_viewModel.ShutDownAsync).WaitAsync(Timeout);

        Assert.Null(_viewModel.Engine.Log);
        using (new FileStream(log.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Opened without sharing: the log has closed the file.
        }

        Assert.Contains(Event.Path, File.ReadAllText(log.FilePath), StringComparison.Ordinal);
        lock (reported)
            Assert.Same(failure, Assert.Single(reported));
    }

    private List<BannerViewModel> Banners() => _ui.Invoke(() => _viewModel.Banners.ToList());

    /// <summary>Collects, on the UI thread, every banner shown from now on, also one closed again since.</summary>
    private List<BannerViewModel> ShownBanners()
    {
        var shown = new List<BannerViewModel>();
        _ui.Invoke(() => _viewModel.Banners.CollectionChanged += (_, e) => shown.AddRange(e.NewItems?.OfType<BannerViewModel>() ?? []));
        return shown;
    }

    private bool CanExecute(ICommand command) => _ui.Invoke(() => command.CanExecute(null));

    /// <summary>Starts a log in <paramref name="temp"/>, as Record log does in the logs folder, with one event to write.</summary>
    private CsvActivityLog StartLog(TempDirectory temp)
    {
        MonitorEngine engine = _viewModel.Engine;
        engine.Options.MinLogFreeSpaceMB = 0; // whatever the temp drive has free
        engine.StartLog(temp.PathOf("activity.csv"));
        CsvActivityLog log = engine.Log!;
        log.Write(Event);
        return log;
    }

    /// <summary>
    /// Holds the shutdown at its last step, where the log is saved, until <see cref="_release"/> is set; first runs
    /// <paramref name="atLastStep"/> there.
    /// </summary>
    private void HoldShutDown(Action? atLastStep = null)
    {
        Action disposeEngine = _viewModel.DisposeEngine;
        _viewModel.DisposeEngine = () =>
        {
            atLastStep?.Invoke();
            Interlocked.Increment(ref _lastSteps);
            _atLastStep.Set();
            _release.Wait();
            disposeEngine();
        };
    }

    /// <summary>A thread that runs a WPF dispatcher, so that the view model's awaits come back to it as on the app's UI thread.</summary>
    private sealed class UiThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;

        public UiThread()
        {
            using var started = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                started.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            started.Wait();
            _dispatcher = dispatcher!;
        }

        /// <summary>Runs <paramref name="function"/> on the thread; fails if the thread does not get to it in time (it is blocked).</summary>
        public T Invoke<T>(Func<T> function)
        {
            DispatcherOperation<T> operation = _dispatcher.InvokeAsync(function);
            Assert.True(operation.Task.Wait(Timeout), "the UI thread is blocked");
            return operation.Result;
        }

        public void Invoke(Action action) => Invoke(() =>
        {
            action();
            return true;
        });

        public void Dispose()
        {
            _dispatcher.InvokeShutdown();
            _thread.Join();
        }
    }
}
