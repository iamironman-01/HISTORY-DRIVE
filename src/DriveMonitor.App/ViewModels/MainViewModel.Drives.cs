using System.Collections.ObjectModel;
using System.Windows.Threading;
using DriveMonitor.App.Services;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.App.ViewModels;

// The drive list: enumeration off the UI thread, merging after device changes, monitoring defaults.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan DriveInfoInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ArrivalBannerTime = TimeSpan.FromSeconds(6);

    private readonly Dictionary<char, DriveItemViewModel> _drivesByLetter = new();
    private readonly DispatcherTimer _driveTimer;
    private bool _driveListRequested;
    private bool _driveRefreshRunning;
    private bool _driveRefreshPending;
    private bool _driveRefreshPendingVolumes;

    /// <summary>Every drive letter seen since start, sorted; removed drives stay with IsConnected = false.</summary>
    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];

    /// <summary>Called by the window when Windows reports that a volume arrived or was removed.</summary>
    public void OnVolumesChanged()
    {
        // Before the first enumeration there is nothing to compare with; that enumeration sees the change.
        if (!_disposed && _driveListRequested)
            _ = RefreshDrivesAsync(refreshVolumes: true, initial: false);
    }

    /// <summary>
    /// Re-reads the drive list on the thread pool (it does device I/O) and merges it. Calls that arrive
    /// while a refresh runs are folded into one more pass afterwards.
    /// </summary>
    private async Task RefreshDrivesAsync(bool refreshVolumes, bool initial)
    {
        if (_driveRefreshRunning)
        {
            _driveRefreshPending = true;
            _driveRefreshPendingVolumes |= refreshVolumes;
            return;
        }

        _driveRefreshRunning = true;
        try
        {
            bool again;
            do
            {
                VolumeMapper volumes = _engine.Volumes;
                bool refresh = refreshVolumes;
                IReadOnlyList<DriveDescriptor> found = await Task.Run(() =>
                {
                    // First, so that file names on a pendrive that just arrived map to its letter.
                    if (refresh)
                        volumes.Refresh();
                    return DriveCatalog.Enumerate(volumes);
                });
                if (_disposed)
                    return;

                MergeDrives(found, initial);
                CloseBanner(BannerKeys.Drives);

                again = _driveRefreshPending;
                refreshVolumes = _driveRefreshPendingVolumes;
                _driveRefreshPending = false;
                _driveRefreshPendingVolumes = false;
                initial = false;
            }
            while (again);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "drive list");
            ShowBanner(BannerKeys.Drives, BannerKind.Error, "Could not read the list of drives: " + ex.Message);
        }
        finally
        {
            _driveRefreshRunning = false;
        }
    }

    private void MergeDrives(IReadOnlyList<DriveDescriptor> found, bool initial)
    {
        var present = new HashSet<char>();
        foreach (DriveDescriptor drive in found)
        {
            present.Add(drive.Letter);
            if (_drivesByLetter.TryGetValue(drive.Letter, out DriveItemViewModel? item))
            {
                bool reconnected = !item.IsConnected;
                item.Update(drive);
                if (reconnected)
                    OnDriveArrived(item, isNew: false);
                continue;
            }

            item = new DriveItemViewModel(drive, _engine.Drives, OnUserChangedMonitoring);
            _drivesByLetter.Add(drive.Letter, item);
            InsertSorted(item);
            if (initial)
                item.SetMonitored(DefaultMonitoring(drive));
            else
                OnDriveArrived(item, isNew: true);
        }

        foreach (DriveItemViewModel item in Drives)
        {
            if (item.IsConnected && !present.Contains(item.Letter))
                item.MarkDisconnected();
        }

        OnPropertyChanged(nameof(StateText));
    }

    /// <summary>
    /// A drive letter that appeared after start, or came back: pendrives and other external drives
    /// follow "Monitor new USB drives automatically" (the letter may now belong to another device);
    /// a new internal or network drive gets its usual default, a returning one keeps its choice.
    /// </summary>
    private void OnDriveArrived(DriveItemViewModel item, bool isNew)
    {
        DriveDescriptor drive = item.Descriptor;
        if (!drive.IsExternal)
        {
            if (isNew)
                item.SetMonitored(DefaultMonitoring(drive));
            return;
        }

        bool monitor = _settings.AutoMonitorNewDrives;
        item.SetMonitored(monitor);

        string message;
        if (!monitor)
            message = $"{item.ArrivalDescription} connected";
        else if (_engine.IsRunning)
            message = $"{item.ArrivalDescription} connected — now monitoring";
        else
            message = $"{item.ArrivalDescription} connected — it will be monitored when monitoring starts";

        ShowBanner(BannerKeys.DriveArrived(drive.Letter), BannerKind.Info, message,
            monitor ? null : "Monitor it", monitor ? null : () => item.IsMonitored = true,
            autoClose: monitor ? ArrivalBannerTime : TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// The choice at start: <see cref="DriveCatalog.IsMonitoredByDefault"/>, except that the saved
    /// settings decide for fixed drives (including whether to watch the Windows drive).
    /// </summary>
    private bool DefaultMonitoring(DriveDescriptor drive)
    {
        if (drive.IsSystemDrive)
            return _settings.MonitorSystemDrive;
        if (drive.Kind == DriveKind.Fixed)
            return DriveCatalog.IsMonitoredByDefault(drive) && !_settings.IsFixedDriveUnmonitored(drive.Letter);
        return DriveCatalog.IsMonitoredByDefault(drive);
    }

    private void OnUserChangedMonitoring(DriveItemViewModel item)
    {
        DriveDescriptor drive = item.Descriptor;
        if (drive.IsSystemDrive)
            _settings.MonitorSystemDrive = item.IsMonitored;
        else if (drive.Kind == DriveKind.Fixed)
            _settings.SetFixedDriveMonitored(drive.Letter, item.IsMonitored);

        // The banner offering to monitor this drive is answered.
        if (item.IsMonitored)
            CloseBanner(BannerKeys.DriveArrived(drive.Letter));
        OnPropertyChanged(nameof(StateText));
    }

    private void InsertSorted(DriveItemViewModel item)
    {
        int index = 0;
        while (index < Drives.Count && Drives[index].Letter < item.Letter)
            index++;
        Drives.Insert(index, item);
    }
}
