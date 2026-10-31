using System.ComponentModel;
using DriveMonitor.Core.Aggregation;

namespace DriveMonitor.App.ViewModels;

/// <summary>One row of the Processes table: what one process did on the monitored drives (paging I/O excluded).</summary>
public sealed class ProcessRowViewModel : ObservableObject
{
    private static readonly PropertyChangedEventArgs AllProperties = new(string.Empty);

    private ProcessStatsSnapshot _stats;

    internal ProcessRowViewModel(ProcessStatsSnapshot stats)
    {
        _stats = stats;
    }

    public int ProcessId => _stats.ProcessId;

    public string ProcessName => _stats.ProcessName;

    public long Reads => _stats.Reads;

    public long BytesRead => _stats.BytesRead;

    public long Writes => _stats.Writes;

    public long BytesWritten => _stats.BytesWritten;

    public long Creates => _stats.Creates;

    public long Deletes => _stats.Deletes;

    public long Renames => _stats.Renames;

    /// <summary>Distinct tracked files the process touched.</summary>
    public int Files => _stats.Files;

    public DateTime FirstSeen => _stats.FirstSeen;

    public DateTime LastSeen => _stats.LastSeen;

    public string LastSeenText => DisplayText.Time(LastSeen);

    internal void Update(ProcessStatsSnapshot stats)
    {
        _stats = stats;
        OnPropertyChanged(AllProperties);
    }
}
