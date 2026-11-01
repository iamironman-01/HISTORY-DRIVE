using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using DriveMonitor.App.Services;
using DriveMonitor.Core;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Models;

namespace DriveMonitor.App.ViewModels;

/// <summary>What the status bar shows: the engine's state, plus the transitions while Start/Stop run.</summary>
public enum MonitorState
{
    Stopped,
    Starting,
    Monitoring,
    Stopping,
    Error,
}

/// <summary>
/// State of the main window. Owns the <see cref="MonitorEngine"/>, pulls its events on a UI timer and
/// keeps the drive list, the three tables, the banners and the status bar up to date.
/// </summary>
/// <remarks>
/// Everything here runs on the UI thread. Engine events arrive on worker threads and are posted with
/// BeginInvoke; starting and stopping the kernel session and all file I/O run on the thread pool, and so
/// does the shutdown when the window closes (<see cref="ShutDownAsync"/>).
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    private const int DrainBatch = 50_000;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LifecycleWaitOnExit = TimeSpan.FromSeconds(15);

    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly MonitorEngine _engine;
    private readonly bool _isElevated;
    private readonly ActivityFilter _filter = new();
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _searchTimer;
    private readonly List<FileActivityEvent> _drained = [];
    private readonly Stopwatch _secondClock = new();
    private readonly RelayCommand _startStopCommand;
    private readonly RelayCommand _toggleLogCommand;
    private Task? _lifecycleTask;
    private Task? _shutDown;
    private MonitorState _state = MonitorState.Stopped;
    private bool _isBusy;

    /// <summary>Set when the shutdown starts: from then on nothing starts again and nothing updates the view.</summary>
    private bool _disposed;
    private bool _closeRequested;
    private string? _lastError;
    private string? _lastTickError;
    private long _lastTickErrorTick;
    private string _searchText = "";
    private long _lastPublished;
    private string _eventRateText = "0 events/s";
    private string _publishedText = "0 events";
    private string _lostText = "Lost 0";
    private bool _hasLostEvents;
    private string _droppedText = "Dropped 0";
    private string _unnamedText = "Unnamed 0";
    private string _trackedFilesText = "0 files tracked";
    private string _diagnosticsText = "";
    private string _pausedText = "";
    private string _activityCountText = "";
    private string _filesCountText = "";
    private string _processesCountText = "";

    public MainViewModel(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _isElevated = MonitorEngine.IsElevated;
        _engine = new MonitorEngine(new MonitorOptions());
        _engine.Options.ExcludeOwnProcess = settings.HideOwnActivity;
        _arrivals = new DriveArrivals(_engine.Drives);

        ApplyFilterSettings();
        Activity = new ActivityFeed(_filter.AcceptsActivity, settings.MaxRows) { GroupRepeated = settings.GroupRepeated };
        Files = new StatsTable<string, FileStatsSnapshot, FileRowViewModel>(
            s => s.Path, s => new FileRowViewModel(s), (row, s) => row.Update(s), _filter.AcceptsFile, StringComparer.OrdinalIgnoreCase);
        Processes = new StatsTable<(int, string), ProcessStatsSnapshot, ProcessRowViewModel>(
            s => (s.ProcessId, s.ProcessName), s => new ProcessRowViewModel(s), (row, s) => row.Update(s), _filter.AcceptsProcess);

        // The commands that start something are off once the window is closing (ShutDownAsync). An export, for one,
        // could still be writing its file when the window closes and the process ends.
        _startStopCommand = new RelayCommand(ToggleMonitoring, () => !_isBusy && _isElevated && !_disposed);
        _toggleLogCommand = new RelayCommand(ToggleLog, () => !_logBusy && !_disposed);
        StartStopCommand = _startStopCommand;
        ToggleLogCommand = _toggleLogCommand;
        ClearCommand = new RelayCommand(Clear);
        ExportActivityCommand = new RelayCommand(ExportActivity, () => !_disposed);
        ExportFilesCommand = new RelayCommand(ExportFiles, () => !_disposed);
        ExportProcessesCommand = new RelayCommand(ExportProcesses, () => !_disposed);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        ClearSearchCommand = new RelayCommand(() => SetSearchNow(""));
        CopyPathCommand = new RelayCommand(CopyPaths);
        OpenFolderCommand = new RelayCommand(p => OpenContainingFolder(FirstRow(p)));
        FilterToFileCommand = new RelayCommand(p => FilterToFile(FirstRow(p)));
        FilterToProcessCommand = new RelayCommand(p => FilterToProcess(FirstRow(p)));

        _engine.StateChanged += OnEngineStateChanged;
        _engine.Faulted += OnEngineFaulted;
        _engine.Restarted += OnEngineRestarted;
        StartEngine = () =>
        {
            _engine.Start();
            return _engine.StartWarning;
        };
        DisposeEngine = _engine.Dispose;

        _tickTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TickInterval };
        _tickTimer.Tick += OnTick;
        _searchTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = SearchDelay };
        _searchTimer.Tick += (_, _) => ApplySearch();
        _driveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = DriveInfoInterval };
        _driveTimer.Tick += (_, _) => _ = RefreshDrivesAsync(refreshVolumes: false, initial: false);

        UpdateState();
        UpdateCounts();
    }

    /// <summary>
    /// Asks the window to close: after "Restart as administrator" started the elevated copy, or once the shutdown that
    /// a close had to wait for is done (<see cref="TryClose"/>).
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>The settings object that is saved on exit; the window stores its bounds here.</summary>
    public AppSettings Settings => _settings;

    /// <summary>
    /// A start, on the thread pool: <see cref="MonitorEngine.Start"/>, then what it worked around
    /// (<see cref="MonitorEngine.StartWarning"/>). Tests replace it to start no kernel session.
    /// </summary>
    internal Func<string?> StartEngine { get; set; }

    /// <summary>
    /// The last step of the shutdown, on the thread pool: <see cref="MonitorEngine.Dispose"/>, which ends the activity log
    /// and on a slow drive waits for it. Tests wrap it to hold the shutdown there.
    /// </summary>
    internal Action DisposeEngine { get; set; }

    /// <summary>The engine; tests start their logs on it, outside the logs folder that Record log uses.</summary>
    internal MonitorEngine Engine => _engine;

    public ObservableCollection<BannerViewModel> Banners { get; } = [];

    public ActivityFeed Activity { get; }

    public StatsTable<string, FileStatsSnapshot, FileRowViewModel> Files { get; }

    public StatsTable<(int, string), ProcessStatsSnapshot, ProcessRowViewModel> Processes { get; }

    public ICommand StartStopCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand ExportActivityCommand { get; }

    public ICommand ExportFilesCommand { get; }

    public ICommand ExportProcessesCommand { get; }

    public ICommand ToggleLogCommand { get; }

    public ICommand OpenLogsFolderCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand FilterToFileCommand { get; }

    public ICommand FilterToProcessCommand { get; }

    public MonitorState State => _state;

    public bool IsMonitoring => _engine.IsRunning;

    public string StartStopText => _state switch
    {
        MonitorState.Starting => "Starting…",
        MonitorState.Stopping => "Stopping…",
        MonitorState.Monitoring => "Stop",
        _ => "Start",
    };

    public string StartStopGlyph => _state is MonitorState.Monitoring or MonitorState.Stopping ? "\uE71A" : "\uE768";

    public string StateText => _state switch
    {
        MonitorState.Starting => "Starting…",
        MonitorState.Stopping => "Stopping…",
        MonitorState.Monitoring => MonitoredDriveCount switch
        {
            0 => "Monitoring, no drive selected",
            1 => "Monitoring 1 drive",
            int n => $"Monitoring {n} drives",
        },
        MonitorState.Error => "Error",
        _ => "Stopped",
    };

    /// <summary>Kernel session and pipeline counters for the tooltip on the state text.</summary>
    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set => SetProperty(ref _diagnosticsText, value);
    }

    public bool IsPaused
    {
        get => Activity.IsPaused;
        set
        {
            if (value == Activity.IsPaused)
                return;
            if (value)
            {
                Activity.Pause();
            }
            else
            {
                long skipped = Activity.Resume();
                if (skipped > 0)
                {
                    ShowBanner(BannerKeys.PauseOverflow, BannerKind.Info,
                        $"{DisplayText.Number(skipped)} events arrived while the view was paused and are not in Live activity. " +
                        "The Files and Processes tabs include them.",
                        autoClose: TimeSpan.FromSeconds(15));
                }
            }

            OnPropertyChanged();
            UpdatePausedText();
            UpdateCounts();
        }
    }

    public bool AutoScroll
    {
        get => _settings.AutoScroll;
        set
        {
            if (_settings.AutoScroll == value)
                return;
            _settings.AutoScroll = value;
            OnPropertyChanged();
        }
    }

    public bool GroupRepeated
    {
        get => _settings.GroupRepeated;
        set
        {
            if (_settings.GroupRepeated == value)
                return;
            _settings.GroupRepeated = value;
            Activity.GroupRepeated = value;
            OnPropertyChanged();
        }
    }

    public bool ShowReads
    {
        get => _settings.ShowReads;
        set => SetShow(_settings.ShowReads, value, v => _settings.ShowReads = v);
    }

    public bool ShowWrites
    {
        get => _settings.ShowWrites;
        set => SetShow(_settings.ShowWrites, value, v => _settings.ShowWrites = v);
    }

    public bool ShowCreated
    {
        get => _settings.ShowCreated;
        set => SetShow(_settings.ShowCreated, value, v => _settings.ShowCreated = v);
    }

    public bool ShowDeleted
    {
        get => _settings.ShowDeleted;
        set => SetShow(_settings.ShowDeleted, value, v => _settings.ShowDeleted = v);
    }

    public bool ShowRenamed
    {
        get => _settings.ShowRenamed;
        set => SetShow(_settings.ShowRenamed, value, v => _settings.ShowRenamed = v);
    }

    public bool ShowOpens
    {
        get => _settings.ShowOpens;
        set => SetShow(_settings.ShowOpens, value, v => _settings.ShowOpens = v);
    }

    public bool ShowListings
    {
        get => _settings.ShowListings;
        set => SetShow(_settings.ShowListings, value, v => _settings.ShowListings = v);
    }

    public bool ShowOtherChanges
    {
        get => _settings.ShowOtherChanges;
        set => SetShow(_settings.ShowOtherChanges, value, v => _settings.ShowOtherChanges = v);
    }

    public bool ShowPaging
    {
        get => _settings.ShowPaging;
        set => SetShow(_settings.ShowPaging, value, v => _settings.ShowPaging = v);
    }

    public bool ShowFailed
    {
        get => _settings.ShowFailed;
        set => SetShow(_settings.ShowFailed, value, v => _settings.ShowFailed = v);
    }

    public bool HideOwnActivity
    {
        get => _settings.HideOwnActivity;
        set
        {
            if (_settings.HideOwnActivity == value)
                return;
            _settings.HideOwnActivity = value;
            _engine.Options.ExcludeOwnProcess = value;
            OnPropertyChanged();
        }
    }

    public bool AutoMonitorNewDrives
    {
        get => _settings.AutoMonitorNewDrives;
        set
        {
            if (_settings.AutoMonitorNewDrives == value)
                return;
            _settings.AutoMonitorNewDrives = value;
            OnPropertyChanged();
        }
    }

    public bool AutoStart
    {
        get => _settings.AutoStart;
        set
        {
            if (_settings.AutoStart == value)
                return;
            _settings.AutoStart = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Filters every table; applied 250 ms after the last keystroke.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? ""))
                return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    public string EventRateText
    {
        get => _eventRateText;
        private set => SetProperty(ref _eventRateText, value);
    }

    public string PublishedText
    {
        get => _publishedText;
        private set => SetProperty(ref _publishedText, value);
    }

    public string LostText
    {
        get => _lostText;
        private set => SetProperty(ref _lostText, value);
    }

    public bool HasLostEvents
    {
        get => _hasLostEvents;
        private set => SetProperty(ref _hasLostEvents, value);
    }

    public string DroppedText
    {
        get => _droppedText;
        private set => SetProperty(ref _droppedText, value);
    }

    public string UnnamedText
    {
        get => _unnamedText;
        private set => SetProperty(ref _unnamedText, value);
    }

    public string TrackedFilesText
    {
        get => _trackedFilesText;
        private set => SetProperty(ref _trackedFilesText, value);
    }

    /// <summary>"View paused · 1,234 new events waiting", or empty.</summary>
    public string PausedText
    {
        get => _pausedText;
        private set => SetProperty(ref _pausedText, value);
    }

    public string ActivityCountText
    {
        get => _activityCountText;
        private set => SetProperty(ref _activityCountText, value);
    }

    public string FilesCountText
    {
        get => _filesCountText;
        private set => SetProperty(ref _filesCountText, value);
    }

    public string ProcessesCountText
    {
        get => _processesCountText;
        private set => SetProperty(ref _processesCountText, value);
    }

    /// <summary>Reads the drive list and, when allowed, starts monitoring. Call once the window is shown.</summary>
    public async Task InitializeAsync()
    {
        if (!_isElevated)
        {
            ShowBanner(BannerKeys.Admin, BannerKind.Warning, "Administrator rights are required to watch file activity",
                "Restart as administrator", RestartAsAdministrator, closeOnAction: false);
        }

        _secondClock.Start();
        _tickTimer.Start();
        _driveListRequested = true;
        await RefreshDrivesAsync(refreshVolumes: false, initial: true);
        if (_disposed)
            return;

        // Only now: a refresh before the first one finished would announce every drive as newly connected.
        _driveTimer.Start();
        if (_isElevated && _settings.AutoStart)
            await StartAsync();
    }

    /// <summary>Shows an error that happened on a background thread (reported by the application).</summary>
    public void ReportBackgroundError(Exception error)
    {
        if (!_disposed)
            ShowBanner(BannerKeys.Unexpected, BannerKind.Error, "Something went wrong in the background: " + error.Message);
    }

    /// <summary>
    /// Called when the window is asked to close. True once monitoring has stopped and the activity log is saved, so
    /// the window can close. Before that it starts the shutdown (<see cref="ShutDownAsync"/>) and returns false: the
    /// window stays open, and <see cref="CloseRequested"/> asks it to close again when the shutdown is done. Further
    /// calls meanwhile start nothing new.
    /// </summary>
    public bool TryClose()
    {
        Task shutDown = ShutDownAsync();
        if (shutDown.IsCompleted)
            return true;

        if (!_closeRequested)
        {
            _closeRequested = true;
            _ = RequestCloseWhenShutDownAsync(shutDown);
        }

        return false;
    }

    /// <summary>
    /// Stops the timers, the kernel session and the activity log, once. Saving the log waits for its drive, up to
    /// 30 s for a slow one, so the stopping runs on the thread pool; meanwhile the commands that start something are
    /// off and a banner says that the log is being saved. Later calls return the same task, which never fails.
    /// </summary>
    public Task ShutDownAsync()
    {
        if (_shutDown is { } running)
            return running;

        _disposed = true;
        bool stopping = _isBusy || _engine.IsRunning;
        bool savingLog = _logBusy || _engine.Log is not null;
        Task? lifecycle = _lifecycleTask;
        Task? logStop = _logStopTask;

        // Started before the view is updated, so that nothing failing there can keep the kernel session running.
        _shutDown = Task.Run(() =>
        {
            try
            {
                StopEverything(lifecycle, logStop);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex, "shutdown");
            }
        });

        _tickTimer.Stop();
        _searchTimer.Stop();
        _driveTimer.Stop();
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.Faulted -= OnEngineFaulted;
        _engine.Restarted -= OnEngineRestarted;

        // Greys out the buttons of the commands that start something; they stay off from now on.
        foreach (ICommand command in new[] { StartStopCommand, ToggleLogCommand, ExportActivityCommand, ExportFilesCommand, ExportProcessesCommand })
            ((RelayCommand)command).RaiseCanExecuteChanged();

        if (stopping)
            SetState(MonitorState.Stopping);
        if (savingLog)
            ShowBanner(BannerKeys.Closing, BannerKind.Info, "Saving the activity log… DriveMonitor closes when it is done.");

        return _shutDown;
    }

    /// <summary>
    /// Stops the kernel session from any thread, e.g. while the process is going down after an
    /// unhandled exception. A kernel session outlives its process, so this matters.
    /// </summary>
    public void EmergencyStop()
    {
        try
        {
            _engine.Dispose();
        }
        catch (Exception)
        {
            // The process is ending anyway.
        }
    }

    private int MonitoredDriveCount => Drives.Count(d => d.IsMonitored && d.IsConnected);

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        try
        {
            _engine.DrainEvents(_drained, DrainBatch);
            Activity.Add(_drained); // buffered while the view is paused

            // Rows of these tables only change in place or get appended, so they keep updating.
            Files.Apply(_engine.Stats.TakeChangedFiles());
            Processes.Apply(_engine.Stats.TakeChangedProcesses());

            if (_secondClock.ElapsedMilliseconds >= 1000)
            {
                double seconds = _secondClock.Elapsed.TotalSeconds;
                _secondClock.Restart();
                UpdateEverySecond(seconds);
            }
        }
        catch (Exception ex)
        {
            ReportTickError(ex);
        }
        finally
        {
            _drained.Clear();
        }
    }

    /// <summary>Reports a failed refresh without repeating the same message four times a second.</summary>
    private void ReportTickError(Exception error)
    {
        long now = Environment.TickCount64;
        if (error.Message == _lastTickError && now - _lastTickErrorTick < 10_000)
            return;

        _lastTickError = error.Message;
        _lastTickErrorTick = now;
        ErrorLog.Write(error, "display update");
        ShowBanner(BannerKeys.Unexpected, BannerKind.Error, "Updating the display failed: " + error.Message);
    }

    private void UpdateEverySecond(double seconds)
    {
        List<DriveTotals> totals = _engine.Stats.GetDriveTotals();
        foreach (DriveItemViewModel drive in Drives)
        {
            DriveTotals? driveTotals = null;
            foreach (DriveTotals t in totals)
            {
                if (t.Drive == drive.Letter)
                {
                    driveTotals = t;
                    break;
                }
            }

            drive.UpdateActivity(driveTotals, seconds);
        }

        long published = _engine.PublishedEvents;
        EventRateText = $"{DisplayText.Number((long)Math.Round(Math.Max(0, published - _lastPublished) / seconds))} events/s";
        _lastPublished = published;
        PublishedText = $"{DisplayText.Number(published)} events";

        int lost = _engine.LostEvents;
        HasLostEvents = lost > 0;
        LostText = $"Lost {DisplayText.Number(lost)}";
        DroppedText = $"Dropped {DisplayText.Number(_engine.DroppedEvents)}";
        UnnamedText = $"Unnamed {DisplayText.Number(_engine.UnnamedEvents)}";
        TrackedFilesText = $"{DisplayText.Number(_engine.Stats.TrackedFiles)} files tracked";
        DiagnosticsText = BuildDiagnostics();

        // Sorted tables keep a row where it was inserted; rows that grew since are moved to their place now.
        Activity.ResortChangedRows();
        Files.ResortChangedRows();
        Processes.ResortChangedRows();

        CheckLogHealth();
        UpdateCounts();
        UpdatePausedText();
        ExpireBanners();
    }

    private string BuildDiagnostics()
    {
        var text = new StringBuilder();
        text.Append("Kernel file events received: ").Append(DisplayText.Number(_engine.KernelEvents));
        text.Append("\nWaiting for completion: ").Append(DisplayText.Number(_engine.PendingCompletions));
        text.Append("\nCallback errors: ").Append(DisplayText.Number(_engine.CallbackErrors));
        if (_engine.LastCallbackError is { } callbackError)
            text.Append(" (last: ").Append(callbackError).Append(')');
        text.Append("\nSession restarts: ").Append(DisplayText.Number(_engine.Restarts));
        text.Append("\nNames of already open files requested: ").Append(_engine.FileNameRundownRequested ? "yes" : "no");
        text.Append("\nWindows reports new names of renamed files: ").Append(_engine.PathOperationsSeen ? "yes" : "not seen yet");
        text.Append("\nFailed operations: ").Append(DisplayText.Number(_engine.Stats.FailedEvents));
        if (_engine.Stats.UntrackedEvents > 0)
        {
            text.Append("\nEvents for files beyond the tracking limit: ").Append(DisplayText.Number(_engine.Stats.UntrackedEvents));
        }

        if (_engine.StartedAt is { } started && _engine.IsRunning)
            text.Append("\nMonitoring since ").Append(DisplayText.Time(started));
        if (_lastError is not null)
            text.Append("\nLast error: ").Append(_lastError);

        CsvActivityLog? log = _engine.Log;
        if (log is null)
        {
            text.Append("\nLog: off");
        }
        else
        {
            text.Append("\nLog: ").Append(log.FilePath)
                .Append(" (").Append(DisplayText.Number(log.Written)).Append(" written, ")
                .Append(DisplayText.Number(log.Dropped)).Append(" dropped)");
            if (log.Error is { } logError)
                text.Append(" - stopped: ").Append(logError.Message);
        }

        return text.ToString();
    }

    private void UpdateCounts()
    {
        ActivityCountText = CountText(Activity.View.Count, Activity.TotalRows);
        FilesCountText = CountText(Files.View.Count, Files.Items.Count);
        ProcessesCountText = CountText(Processes.View.Count, Processes.Items.Count);
    }

    private static string CountText(int visible, int total) =>
        total == 0 ? "" : visible == total ? DisplayText.Number(total) : $"{DisplayText.Number(visible)} of {DisplayText.Number(total)}";

    private void UpdatePausedText()
    {
        if (!Activity.IsPaused)
        {
            PausedText = "";
            return;
        }

        string text = $"View paused · {DisplayText.Number(Activity.PausedEvents)} new events waiting";
        if (Activity.PausedOverflow > 0)
            text += $" · {DisplayText.Number(Activity.PausedOverflow)} not kept";
        PausedText = text;
    }

    private void SetShow(bool current, bool value, Action<bool> store, [CallerMemberName] string? propertyName = null)
    {
        if (current == value)
            return;
        store(value);
        ApplyFilterSettings();
        Activity.Refresh();
        OnPropertyChanged(propertyName);
        UpdateCounts();
    }

    private void ApplyFilterSettings()
    {
        _filter.SetVisible(FileOperation.Read, _settings.ShowReads);
        _filter.SetVisible(FileOperation.Write, _settings.ShowWrites);
        _filter.SetVisible(FileOperation.Create, _settings.ShowCreated);
        _filter.SetVisible(FileOperation.Delete, _settings.ShowDeleted);
        _filter.SetVisible(FileOperation.Rename, _settings.ShowRenamed);
        _filter.SetVisible(FileOperation.Open, _settings.ShowOpens);
        _filter.SetVisible(FileOperation.List, _settings.ShowListings);
        _filter.SetVisible(FileOperation.SetInfo, _settings.ShowOtherChanges);
        _filter.SetVisible(FileOperation.Flush, _settings.ShowOtherChanges);
        _filter.ShowPaging = _settings.ShowPaging;
        _filter.ShowFailed = _settings.ShowFailed;
    }

    private void ApplySearch()
    {
        _searchTimer.Stop();
        string search = _searchText.Trim();
        if (search == _filter.Search)
            return;

        _filter.Search = search;
        Activity.Refresh();
        Files.ApplySearch(search.Length > 0);
        Processes.ApplySearch(search.Length > 0);
        UpdateCounts();
    }

    /// <summary>Sets the search text and applies it without the typing delay.</summary>
    private void SetSearchNow(string text)
    {
        _searchText = text;
        OnPropertyChanged(nameof(SearchText));
        ApplySearch();
    }

    private void ToggleMonitoring()
    {
        if (_engine.IsRunning)
            _ = StopAsync();
        else
            _ = StartAsync();
    }

    /// <summary>Starts monitoring: Start, auto-start, and the banners' "Try again" and "Start again".</summary>
    internal async Task StartAsync()
    {
        if (_isBusy || _disposed || _engine.IsRunning)
            return;

        SetBusy(MonitorState.Starting);
        CloseBanner(BannerKeys.StartError);
        CloseBanner(BannerKeys.Faulted);
        try
        {
            Task<string?> start = Task.Run(StartEngine);
            _lifecycleTask = start;
            string? warning = await start;
            _lastError = null;
            if (!_disposed)
                ShowStartWarning(warning);
        }
        catch (MonitorStartException ex)
        {
            _lastError = ex.Message;
            ShowBanner(BannerKeys.StartError, BannerKind.Error, ex.Message, "Try again", () => _ = StartAsync());
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "start");
            _lastError = ex.Message;
            ShowBanner(BannerKeys.StartError, BannerKind.Error, "Monitoring could not start: " + ex.Message, "Try again", () => _ = StartAsync());
        }
        finally
        {
            ClearBusy();
        }
    }

    private async Task StopAsync()
    {
        if (_isBusy || _disposed || !_engine.IsRunning)
            return;

        // Stopping drains the kernel buffers, which can take a second or two.
        SetBusy(MonitorState.Stopping);
        try
        {
            Task stop = Task.Run(_engine.Stop);
            _lifecycleTask = stop;
            await stop;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "stop");
            ShowBanner(BannerKeys.StartError, BannerKind.Error, "Monitoring did not stop cleanly: " + ex.Message);
        }
        finally
        {
            ClearBusy();
        }
    }

    /// <summary>The thread-pool part of <see cref="ShutDownAsync"/>.</summary>
    private void StopEverything(Task? lifecycle, Task? logStop)
    {
        // A start still running would otherwise create the session after the stop below.
        WaitQuietly(lifecycle, LifecycleWaitOnExit);

        // The kernel session first, because it outlives the process: while Windows logs off, App waits only briefly
        // for this before the process may be ended.
        _engine.Stop();

        // A log that "Record log" is still saving is no longer the engine's, so Dispose would not wait for it. Saving
        // a log takes at most about half a minute; see CsvActivityLog.Dispose.
        WaitQuietly(logStop, Timeout.InfiniteTimeSpan);
        DisposeEngine();
    }

    private static void WaitQuietly(Task? task, TimeSpan timeout)
    {
        try
        {
            task?.Wait(timeout);
        }
        catch (AggregateException)
        {
            // A failed start or stop, reported where it ran; nothing more to undo.
        }
    }

    private async Task RequestCloseWhenShutDownAsync(Task shutDown)
    {
        await shutDown;

        // As a dispatcher operation of its own: a shutdown that has just finished continues here at once, inside the
        // window's Closing, where the window cannot be closed again.
        await _dispatcher.InvokeAsync(() => CloseRequested?.Invoke(this, EventArgs.Empty), DispatcherPriority.Normal);
    }

    private void SetBusy(MonitorState transition)
    {
        _isBusy = true;
        SetState(transition);
        _startStopCommand.RaiseCanExecuteChanged();
    }

    private void ClearBusy()
    {
        _isBusy = false;
        UpdateState();
        _startStopCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Shows the engine's state, unless a start or stop is still running or the window is closing.</summary>
    internal void UpdateState()
    {
        if (_isBusy || _disposed)
            return;
        SetState(_engine.IsRunning ? MonitorState.Monitoring : _lastError is not null ? MonitorState.Error : MonitorState.Stopped);
    }

    /// <summary>
    /// After a start: shows what <see cref="MonitorEngine.Start"/> worked around (<see cref="MonitorEngine.StartWarning"/>),
    /// or removes the warning of an earlier start. The banner does not close by itself; it goes when monitoring stops.
    /// </summary>
    internal void ShowStartWarning(string? warning)
    {
        if (warning is null)
            CloseBanner(BannerKeys.StartWarning);
        else
            ShowBanner(BannerKeys.StartWarning, BannerKind.Warning, warning);
    }

    private void SetState(MonitorState state)
    {
        _state = state;

        // The start warning is about the kernel session that is running.
        if (state != MonitorState.Monitoring)
            CloseBanner(BannerKeys.StartWarning);

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsMonitoring));
        OnPropertyChanged(nameof(StartStopText));
        OnPropertyChanged(nameof(StartStopGlyph));
        OnPropertyChanged(nameof(StateText));
    }

    private void OnEngineStateChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!_disposed)
                UpdateState();
        }));

    private void OnEngineFaulted(object? sender, Exception error) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (_disposed)
                return;
            _lastError = error.Message;
            ShowBanner(BannerKeys.Faulted, BannerKind.Error, "Monitoring stopped: " + error.Message, "Start again", () => _ = StartAsync());
            UpdateState();
        }));

    private void OnEngineRestarted(object? sender, Exception error) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!_disposed)
            {
                ShowBanner(BannerKeys.Restarted, BannerKind.Info,
                    "The kernel session stopped and was restarted automatically; some events may be missing.");
            }
        }));

    private void ShowBanner(
        string key,
        BannerKind kind,
        string message,
        string? actionText = null,
        Action? action = null,
        TimeSpan? autoClose = null,
        bool closeOnAction = true)
    {
        Action? run = action;
        if (action is not null && closeOnAction)
        {
            run = () =>
            {
                CloseBanner(key);
                action();
            };
        }

        long? expires = autoClose is { } delay ? Environment.TickCount64 + (long)delay.TotalMilliseconds : null;
        var banner = new BannerViewModel(key, kind, message, actionText, run, CloseBanner, expires);
        for (int i = 0; i < Banners.Count; i++)
        {
            if (Banners[i].Key == key)
            {
                Banners[i] = banner;
                return;
            }
        }

        Banners.Add(banner);
    }

    private void CloseBanner(string key)
    {
        for (int i = Banners.Count - 1; i >= 0; i--)
        {
            if (Banners[i].Key == key)
                Banners.RemoveAt(i);
        }
    }

    private void CloseBanner(BannerViewModel banner) => Banners.Remove(banner);

    private void ExpireBanners()
    {
        long now = Environment.TickCount64;
        for (int i = Banners.Count - 1; i >= 0; i--)
        {
            if (Banners[i].ExpiresAtTick <= now)
                Banners.RemoveAt(i);
        }
    }

    /// <summary>Banner identities: a new banner with the same key replaces the old one.</summary>
    private static class BannerKeys
    {
        public const string Admin = "admin";
        public const string StartError = "start-error";
        public const string StartWarning = "start-warning";
        public const string Closing = "closing";
        public const string Faulted = "faulted";
        public const string Restarted = "restarted";
        public const string Unexpected = "unexpected";
        public const string PauseOverflow = "pause-overflow";
        public const string Export = "export";
        public const string Log = "log";
        public const string Clipboard = "clipboard";
        public const string Explorer = "explorer";
        public const string Drives = "drives";
        public const string Elevation = "elevation";

        public static string DriveArrived(char letter) => "drive-" + letter;
    }
}
