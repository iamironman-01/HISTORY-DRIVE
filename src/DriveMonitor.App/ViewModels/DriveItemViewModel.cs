using System.ComponentModel;
using DriveMonitor.Core;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Filtering;
using DriveMonitor.Core.Models;

namespace DriveMonitor.App.ViewModels;

/// <summary>A drive in the left panel: what it is, whether it is monitored, and its live transfer rates.</summary>
public sealed class DriveItemViewModel : ObservableObject
{
    private static readonly PropertyChangedEventArgs AllProperties = new(string.Empty);

    private readonly DriveFilter _filter;
    private readonly Action<DriveItemViewModel> _userChangedMonitoring;
    private DriveDescriptor _drive;
    private bool _isMonitored;
    private bool _isConnected = true;
    private bool _isActive;
    private string _rateText = "";
    private string _totalsText = "";
    private double _readRate;
    private double _writeRate;
    private long _lastBytesRead;
    private long _lastBytesWritten;
    private bool _hasBaseline;

    /// <param name="userChangedMonitoring">Called when the user ticks or unticks the drive (not for programmatic changes).</param>
    internal DriveItemViewModel(DriveDescriptor drive, DriveFilter filter, Action<DriveItemViewModel> userChangedMonitoring)
    {
        _drive = drive;
        _filter = filter;
        _userChangedMonitoring = userChangedMonitoring;
        UpdateRateText();
    }

    public char Letter => _drive.Letter;

    public DriveDescriptor Descriptor => _drive;

    /// <summary>Bound to the check box; pushes the choice into the engine's drive filter at once.</summary>
    public bool IsMonitored
    {
        get => _isMonitored;
        set
        {
            if (SetMonitoredCore(value))
                _userChangedMonitoring(this);
        }
    }

    /// <summary>False once the drive was removed; it stays listed so its totals remain visible.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>Something was read or written in the last second.</summary>
    public bool IsActive => _isActive;

    /// <summary>Segoe Fluent Icons glyph for the kind of drive.</summary>
    public string Glyph => _drive.Kind switch
    {
        DriveKind.Network => "\uE8CE",
        DriveKind.Optical => "\uE958",
        _ when _drive.IsExternal => "\uE88E",
        _ => "\uEDA2",
    };

    /// <summary>"E:  Label", with "(Windows)" for the system drive.</summary>
    public string Title
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(_drive.Label) ? DefaultName(_drive) : _drive.Label;
            return _drive.IsSystemDrive ? $"{Letter}:  {name} (Windows)" : $"{Letter}:  {name}";
        }
    }

    /// <summary>"USB · SanDisk Ultra · 29.8 GB (12.1 GB free) · FAT32".</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(4);
            if (BusText(_drive) is { } bus)
                parts.Add(bus);
            if (!string.IsNullOrWhiteSpace(_drive.Model))
                parts.Add(_drive.Model);
            if (!_drive.IsReady)
                parts.Add(_drive.Kind is DriveKind.Removable or DriveKind.Optical ? "No media" : "Not ready");
            else if (_drive.TotalBytes > 0)
                parts.Add($"{Formatting.Bytes(_drive.TotalBytes)} ({Formatting.Bytes(_drive.FreeBytes)} free)");
            if (_drive.IsReady && !string.IsNullOrEmpty(_drive.FileSystem))
                parts.Add(_drive.FileSystem);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>"Read 12.3 MB/s · Write 0 B/s", or why there is no rate.</summary>
    public string RateText => _rateText;

    /// <summary>"Read 1.2 GB · Written 340 MB" since monitoring started or was cleared; empty before any transfer.</summary>
    public string TotalsText => _totalsText;

    public string? ToolTip
    {
        get
        {
            string? device = string.IsNullOrEmpty(_drive.DevicePath) ? null : _drive.DevicePath;
            if (!_drive.IsSystemDrive)
                return device;

            const string warning =
                "This is the Windows drive. Windows and your apps read and write here all the time, " +
                "so monitoring it shows a lot of system activity.";
            return device is null ? warning : warning + "\n" + device;
        }
    }

    /// <summary>A short description for the "connected" notice, e.g. "USB drive F: (SanDisk Ultra, 29.8 GB)".</summary>
    internal string ArrivalDescription
    {
        get
        {
            string kind = _drive.Bus switch
            {
                StorageBus.Usb => "USB drive",
                StorageBus.Sd or StorageBus.Mmc => "Memory card",
                StorageBus.Ieee1394 => "FireWire drive",
                _ => "Removable drive",
            };
            var details = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(_drive.Model))
                details.Add(_drive.Model);
            else if (!string.IsNullOrWhiteSpace(_drive.Label))
                details.Add(_drive.Label);
            if (_drive.IsReady && _drive.TotalBytes > 0)
                details.Add(Formatting.Bytes(_drive.TotalBytes));
            return details.Count == 0 ? $"{kind} {Letter}:" : $"{kind} {Letter}: ({string.Join(", ", details)})";
        }
    }

    /// <summary>Changes monitoring without counting it as the user's choice (defaults, new drives).</summary>
    internal void SetMonitored(bool monitored) => SetMonitoredCore(monitored);

    /// <summary>Takes fresh volume information (size, free space, label) and marks the drive connected.</summary>
    internal void Update(DriveDescriptor drive)
    {
        _drive = drive;
        _isConnected = true;
        UpdateRateText();
        OnPropertyChanged(AllProperties);
    }

    internal void MarkDisconnected()
    {
        _isConnected = false;
        _readRate = 0;
        _writeRate = 0;
        UpdateRateText();
        OnPropertyChanged(AllProperties);
    }

    /// <summary>Updates the rates from the change in totals since the previous call.</summary>
    internal void UpdateActivity(DriveTotals? totals, double seconds)
    {
        long read = totals?.BytesRead ?? 0;
        long written = totals?.BytesWritten ?? 0;

        // Totals drop to zero when statistics are cleared; that is a new baseline, not a negative rate.
        bool comparable = _hasBaseline && read >= _lastBytesRead && written >= _lastBytesWritten && seconds > 0;
        _readRate = comparable ? (read - _lastBytesRead) / seconds : 0;
        _writeRate = comparable ? (written - _lastBytesWritten) / seconds : 0;
        _lastBytesRead = read;
        _lastBytesWritten = written;
        _hasBaseline = true;

        SetProperty(ref _totalsText, read > 0 || written > 0 ? $"Read {Formatting.Bytes(read)} · Written {Formatting.Bytes(written)}" : "", nameof(TotalsText));
        UpdateRateText();
    }

    /// <summary>Forgets totals after the statistics were cleared.</summary>
    internal void ResetActivity()
    {
        _hasBaseline = false;
        _lastBytesRead = 0;
        _lastBytesWritten = 0;
        _readRate = 0;
        _writeRate = 0;
        SetProperty(ref _totalsText, "", nameof(TotalsText));
        UpdateRateText();
    }

    private bool SetMonitoredCore(bool monitored)
    {
        _filter.SetMonitored(Letter, monitored);
        if (!SetProperty(ref _isMonitored, monitored, nameof(IsMonitored)))
            return false;
        UpdateRateText();
        return true;
    }

    private void UpdateRateText()
    {
        string text = !_isConnected ? "Disconnected"
            : !_isMonitored ? "Not monitored"
            : $"Read {Formatting.Rate(_readRate)} · Write {Formatting.Rate(_writeRate)}";
        SetProperty(ref _rateText, text, nameof(RateText));
        SetProperty(ref _isActive, _isConnected && _isMonitored && (_readRate > 0 || _writeRate > 0), nameof(IsActive));
    }

    private static string DefaultName(DriveDescriptor drive) => drive.Kind switch
    {
        DriveKind.Network => "Network drive",
        DriveKind.Optical => "DVD drive",
        DriveKind.Ram => "RAM disk",
        _ when drive.Bus == StorageBus.Usb => "USB drive",
        _ when drive.Bus is StorageBus.Sd or StorageBus.Mmc => "Memory card",
        DriveKind.Removable => "Removable disk",
        _ => "Local disk",
    };

    private static string? BusText(DriveDescriptor drive) => drive.Bus switch
    {
        StorageBus.Usb => "USB",
        StorageBus.Sd => "SD card",
        StorageBus.Mmc => "MMC",
        StorageBus.Ieee1394 => "FireWire",
        StorageBus.Nvme => "NVMe",
        StorageBus.Sata => "SATA",
        StorageBus.Sas => "SAS",
        StorageBus.Scsi => "SCSI",
        StorageBus.Ata => "ATA",
        StorageBus.Atapi => "ATAPI",
        StorageBus.Raid => "RAID",
        StorageBus.IScsi => "iSCSI",
        StorageBus.Fibre => "Fibre Channel",
        StorageBus.Virtual or StorageBus.FileBackedVirtual => "Virtual disk",
        StorageBus.Spaces => "Storage Spaces",
        StorageBus.Ufs => "UFS",
        _ => drive.Kind switch
        {
            DriveKind.Removable => "Removable",
            DriveKind.Fixed => "Local disk",
            DriveKind.Network => "Network",
            DriveKind.Optical => "Optical",
            DriveKind.Ram => "RAM disk",
            _ => null,
        },
    };
}
