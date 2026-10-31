using System.ComponentModel;
using System.Text;
using DriveMonitor.Core;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Models;

namespace DriveMonitor.App.ViewModels;

/// <summary>One row of the Files table: totals for one file or folder since monitoring started or was cleared.</summary>
public sealed class FileRowViewModel : ObservableObject
{
    private static readonly PropertyChangedEventArgs AllProperties = new(string.Empty);

    private FileStatsSnapshot _stats;
    private string? _fileName;
    private string? _folder;
    private string _statusText = "";
    private FileOperation _statusOperation;

    internal FileRowViewModel(FileStatsSnapshot stats)
    {
        _stats = stats;
        Describe();
    }

    public string Path => _stats.Path;

    public string FileName => _fileName ??= DisplayText.FileName(Path);

    public string Folder => _folder ??= DisplayText.Folder(Path);

    public bool IsDirectory => _stats.IsDirectory;

    public long Reads => _stats.Reads;

    public long BytesRead => _stats.BytesRead;

    public long Writes => _stats.Writes;

    public long BytesWritten => _stats.BytesWritten;

    public long PagingBytesRead => _stats.PagingBytesRead;

    public long PagingBytesWritten => _stats.PagingBytesWritten;

    public int Opens => _stats.Opens;

    public FileLifecycle Lifecycle => _stats.Lifecycle;

    public DateTime FirstSeen => _stats.FirstSeen;

    public DateTime LastSeen => _stats.LastSeen;

    public string LastSeenText => DisplayText.Time(LastSeen);

    public FileOperation LastOperation => _stats.LastOperation;

    public string LastOperationText => DisplayText.Operation(LastOperation);

    public string LastProcess => _stats.LastProcess;

    /// <summary>Up to three process names, plus "+N more".</summary>
    public string Processes => _stats.Processes;

    /// <summary>Every process name, for the search; null when <see cref="Processes"/> already names them all.</summary>
    public IReadOnlyList<string>? ProcessNames => _stats.ProcessNames;

    /// <summary>New path of the last rename, when Windows reported it.</summary>
    public string? RenamedTo => _stats.RenamedTo;

    /// <summary>Created / Deleted / Renamed → new name / Resized / Modified / Read only / Opened.</summary>
    public string StatusText => _statusText;

    /// <summary>The operation whose colour the status is shown in.</summary>
    public FileOperation StatusOperation => _statusOperation;

    public string ToolTip
    {
        get
        {
            var text = new StringBuilder(Path);
            if (RenamedTo is not null)
                text.Append("\nRenamed to ").Append(RenamedTo);
            if (PagingBytesRead > 0 || PagingBytesWritten > 0)
            {
                text.Append("\nPaging I/O (cache and memory manager): read ").Append(Formatting.Bytes(PagingBytesRead))
                    .Append(", written ").Append(Formatting.Bytes(PagingBytesWritten));
            }

            text.Append("\nFirst seen ").Append(DisplayText.Time(FirstSeen)).Append(", last ").Append(DisplayText.Time(LastSeen));
            if (Processes.Length > 0)
                text.Append("\nProcesses: ").Append(Processes);
            return text.ToString();
        }
    }

    internal void Update(FileStatsSnapshot stats)
    {
        _stats = stats;
        Describe();
        OnPropertyChanged(AllProperties);
    }

    private void Describe()
    {
        FileLifecycle life = _stats.Lifecycle;
        bool created = (life & FileLifecycle.Created) != 0;

        if ((life & FileLifecycle.Deleted) != 0)
        {
            (_statusText, _statusOperation) = (created ? "Created and deleted" : "Deleted", FileOperation.Delete);
        }
        else if ((life & FileLifecycle.Renamed) != 0)
        {
            string? to = _stats.RenamedTo;
            if (to is not null && to.Contains(@"\$RECYCLE.BIN\", StringComparison.OrdinalIgnoreCase))
            {
                (_statusText, _statusOperation) = ("Moved to the Recycle Bin", FileOperation.Delete);
            }
            else
            {
                string text = (created ? "Created, renamed" : "Renamed") + (to is null ? "" : DescribeRenameTarget(to));
                (_statusText, _statusOperation) = (text, FileOperation.Rename);
            }
        }
        else if (created)
        {
            (_statusText, _statusOperation) = ("Created", FileOperation.Create);
        }
        else if ((life & FileLifecycle.Resized) != 0)
        {
            (_statusText, _statusOperation) = ("Resized", FileOperation.Write);
        }
        else if (_stats.Writes > 0 || _stats.PagingBytesWritten > 0)
        {
            (_statusText, _statusOperation) = ("Modified", FileOperation.Write);
        }
        else if (_stats.Reads > 0 || _stats.PagingBytesRead > 0)
        {
            (_statusText, _statusOperation) = ("Read only", FileOperation.Read);
        }
        else
        {
            (_statusText, _statusOperation) = (_stats.IsDirectory ? "Folder" : "Opened", FileOperation.Open);
        }
    }

    /// <summary>" → new.txt" for a rename in the same folder, " → E:\other\new.txt" for a move.</summary>
    private string DescribeRenameTarget(string target)
    {
        bool sameFolder = string.Equals(
            System.IO.Path.GetDirectoryName(target), System.IO.Path.GetDirectoryName(Path), StringComparison.OrdinalIgnoreCase);
        return " → " + (sameFolder ? DisplayText.FileName(target) : target);
    }
}
