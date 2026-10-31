using System.Windows.Threading;
using DriveMonitor.App.Services;
using DriveMonitor.App.ViewModels;

namespace DriveMonitor.Tests.App;

/// <summary>
/// The main window's view model, on a thread that runs a WPF dispatcher as the app's UI thread does: the warning a
/// start can leave behind. No test starts monitoring, so no kernel session is created, elevated or not.
/// </summary>
public sealed class MainViewModelTests : IDisposable
{
    private const string Warning =
        @"Another program holds Global\DriveMonitor.Session.DriveMonitor-FileIO, which keeps two DriveMonitors from sharing " +
        "the session \"DriveMonitor-FileIO\", but no such session is running, so monitoring started anyway.";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly UiThread _ui = new();
    private readonly MainViewModel _viewModel;

    public MainViewModelTests()
    {
        _viewModel = _ui.Invoke(() => new MainViewModel(new AppSettings { AutoStart = false }, Dispatcher.CurrentDispatcher));
    }

    public void Dispose()
    {
        _ui.Invoke(_viewModel.Dispose);
        _ui.Dispose();
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

    private List<BannerViewModel> Banners() => _ui.Invoke(() => _viewModel.Banners.ToList());

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
