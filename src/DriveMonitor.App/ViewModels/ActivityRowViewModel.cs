using System.ComponentModel;
using System.Globalization;
using System.Text;
using DriveMonitor.App.Services;
using DriveMonitor.Core;
using DriveMonitor.Core.Models;

namespace DriveMonitor.App.ViewModels;

/// <summary>
/// One row of the Live activity list: a single operation, or identical ones grouped together (same
/// operation, file, process, paging flag and outcome, each within a second of the previous one).
/// </summary>
/// <remarks>
/// Only the grouped totals change after creation, and they are announced with one PropertyChanged
/// per row per refresh (<see cref="NotifyChanged"/>), not one per grouped event.
/// </remarks>
public sealed class ActivityRowViewModel : ObservableObject
{
    private static readonly PropertyChangedEventArgs AllProperties = new(string.Empty);

    private string? _fileName;
    private string? _folder;

    internal ActivityRowViewModel(FileActivityEvent ev)
    {
        Sequence = ev.Sequence;
        FirstTime = ev.Timestamp;
        LastTime = ev.Timestamp;
        Operation = ev.Operation;
        Path = ev.Path;
        TargetPath = ev.TargetPath;
        Drive = ev.Drive;
        Count = 1;
        Bytes = ev.Bytes;
        MinOffset = ev.Offset >= 0 ? ev.Offset : -1;
        MaxEnd = ev.Offset >= 0 ? ev.Offset + ev.Bytes : -1;
        ProcessName = ev.ProcessName;
        ProcessId = ev.ProcessId;
        Flags = ev.Flags;
        Status = ev.Status;
        NtStatus = ev.NtStatus;
        Detail = ev.Detail;
        IsPaging = ev.IsPaging;
    }

    /// <summary>Sequence number of the first grouped event; keeps arrival order when sorting finds a tie.</summary>
    public long Sequence { get; }

    public DateTime FirstTime { get; private set; }

    public DateTime LastTime { get; private set; }

    public FileOperation Operation { get; }

    public string OperationText => DisplayText.Operation(Operation);

    public string Path { get; }

    public string FileName => _fileName ??= DisplayText.FileName(Path);

    public string Folder => _folder ??= DisplayText.Folder(Path);

    /// <summary>New path of a rename or hard link, when Windows reported it.</summary>
    public string? TargetPath { get; }

    public char Drive { get; }

    /// <summary>Number of grouped operations.</summary>
    public int Count { get; private set; }

    /// <summary>Reads and writes: total bytes transferred. Size changes: the latest new size.</summary>
    public long Bytes { get; private set; }

    /// <summary>Lowest file offset read or written, or -1.</summary>
    public long MinOffset { get; private set; }

    /// <summary>Highest offset + length read or written, or -1.</summary>
    public long MaxEnd { get; private set; }

    public string ProcessName { get; }

    public int ProcessId { get; }

    /// <summary>Union of the grouped events' flags.</summary>
    public FileEventFlags Flags { get; private set; }

    public IoStatus Status { get; }

    public int NtStatus { get; }

    /// <summary>Core's detail of the latest grouped event that had one.</summary>
    public string? Detail { get; private set; }

    public bool IsPaging { get; }

    public bool IsFailed => Status == IoStatus.Failed;

    public bool IsDirectory => (Flags & FileEventFlags.Directory) != 0;

    public string TimeText => DisplayText.PreciseTime(FirstTime);

    public string? SizeText =>
        Operation is FileOperation.Read or FileOperation.Write || (Flags & FileEventFlags.SizeChange) != 0
            ? Formatting.Bytes(Bytes)
            : null;

    public string? CountText => Count > 1 ? "×" + DisplayText.Number(Count) : null;

    /// <summary>Core's detail plus paging / unbuffered / folder notes and the failure reason; null when empty.</summary>
    public string? DetailsText
    {
        get
        {
            var parts = new List<string>(3);
            if (IsFailed)
                parts.Add(string.IsNullOrEmpty(Detail) ? "failed" : "failed: " + Detail);
            else if (!string.IsNullOrEmpty(Detail))
                parts.Add(Detail);

            if (IsDirectory && Operation != FileOperation.List)
                parts.Add("folder");
            if (IsPaging)
                parts.Add("paging");
            else if ((Flags & FileEventFlags.NonCached) != 0)
                parts.Add("unbuffered");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    public string ToolTip
    {
        get
        {
            var text = new StringBuilder(Path);
            CultureInfo culture = CultureInfo.CurrentCulture;
            if (TargetPath is not null)
                text.Append("\n→ ").Append(TargetPath);
            if (Count > 1)
            {
                text.Append('\n').Append(culture, $"{Count:N0} operations, ")
                    .Append(DisplayText.PreciseTime(FirstTime)).Append(" – ").Append(DisplayText.PreciseTime(LastTime));
            }

            if (MinOffset >= 0 && MaxEnd > MinOffset)
                text.Append('\n').Append(culture, $"File bytes {MinOffset:N0} – {MaxEnd:N0}");
            if (NtStatus != 0)
                text.Append('\n').Append(CultureInfo.InvariantCulture, $"NTSTATUS 0x{unchecked((uint)NtStatus):X8}");
            return text.ToString();
        }
    }

    /// <summary><see cref="Environment.TickCount64"/> when the row was created or last grouped into.</summary>
    internal long LastTouched { get; set; }

    /// <summary>Removed from the list (too old); must not be grouped into any more.</summary>
    internal bool IsEvicted { get; set; }

    /// <summary>Created in the batch being processed and not in the list yet, so nothing displays it.</summary>
    internal bool IsPendingAdd { get; set; }

    /// <summary>Grouped into during the batch being processed; needs one change notification.</summary>
    internal bool IsDirty { get; set; }

    internal void Merge(FileActivityEvent ev)
    {
        Count++;
        if (ev.Timestamp < FirstTime)
            FirstTime = ev.Timestamp;
        if (ev.Timestamp > LastTime)
            LastTime = ev.Timestamp;
        Flags |= ev.Flags;

        if (Operation is FileOperation.Read or FileOperation.Write)
        {
            Bytes += ev.Bytes;
            if (ev.Offset >= 0)
            {
                MinOffset = MinOffset < 0 ? ev.Offset : Math.Min(MinOffset, ev.Offset);
                MaxEnd = Math.Max(MaxEnd, ev.Offset + ev.Bytes);
            }
        }
        else if ((ev.Flags & FileEventFlags.SizeChange) != 0)
        {
            // A new size, not a transfer: the latest one is what matters.
            Bytes = ev.Bytes;
        }

        if (ev.Detail is not null)
            Detail = ev.Detail;
    }

    /// <summary>Tells bound cells that the grouped totals changed.</summary>
    internal void NotifyChanged() => OnPropertyChanged(AllProperties);

    internal ActivityRecord ToRecord() => new(
        FirstTime, LastTime, Operation, Count, Bytes, Path, TargetPath, Drive, ProcessName, ProcessId, Flags, Status, NtStatus,
        Detail, MinOffset, MaxEnd);
}
