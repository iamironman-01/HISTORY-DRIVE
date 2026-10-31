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
    private int _lastSteps;

    public MainViewModelTests()
    {
        _viewModel = _ui.Invoke(() => new MainViewModel(new AppSettings { AutoStart = false }, Dispatcher.CurrentDispatcher));
    }

    public void Dispose()
    {
        _release.Set(); // a test that held the shutdown and failed before letting it go
        _ui.Invoke(_viewModel.ShutDownAsync).Wait(Timeout);
        _ui.Dispose();
        _atLastStep.Dispose();
        _release.Dispose();
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

    private List<BannerViewModel> Banners() => _ui.Invoke(() => _viewModel.Banners.ToList());

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

    /// <summary>Holds the shutdown at its last step, where the log is saved, until <see cref="_release"/> is set.</summary>
    private void HoldShutDown()
    {
        Action disposeEngine = _viewModel.DisposeEngine;
        _viewModel.DisposeEngine = () =>
        {
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
