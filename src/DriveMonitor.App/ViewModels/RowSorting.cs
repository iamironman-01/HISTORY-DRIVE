using System.Collections;
using System.ComponentModel;

namespace DriveMonitor.App.ViewModels;

/// <summary>
/// Typed comparers for column sorting. WPF's SortDescriptions read every value through reflection,
/// which is slow on a table of 200,000 files; a ListCollectionView.CustomSort comparer is not.
/// </summary>
public static class RowSorting
{
    /// <returns>Null when the column cannot be sorted.</returns>
    public static IComparer? ForActivity(string sortMemberPath, ListSortDirection direction)
    {
        Comparison<ActivityRowViewModel>? comparison = sortMemberPath switch
        {
            nameof(ActivityRowViewModel.FirstTime) => (a, b) => a.FirstTime.CompareTo(b.FirstTime),
            nameof(ActivityRowViewModel.Operation) => (a, b) => string.CompareOrdinal(a.OperationText, b.OperationText),
            nameof(ActivityRowViewModel.Count) => (a, b) => a.Count.CompareTo(b.Count),
            nameof(ActivityRowViewModel.Bytes) => (a, b) => a.Bytes.CompareTo(b.Bytes),
            nameof(ActivityRowViewModel.FileName) => (a, b) => Text(a.FileName, b.FileName) is var c and not 0 ? c : Text(a.Path, b.Path),
            nameof(ActivityRowViewModel.ProcessName) => (a, b) => Text(a.ProcessName, b.ProcessName),
            nameof(ActivityRowViewModel.ProcessId) => (a, b) => a.ProcessId.CompareTo(b.ProcessId),
            _ => null,
        };
        return Create(comparison, (a, b) => a.Sequence.CompareTo(b.Sequence), direction);
    }

    /// <returns>Null when the column cannot be sorted.</returns>
    public static IComparer? ForFiles(string sortMemberPath, ListSortDirection direction)
    {
        Comparison<FileRowViewModel>? comparison = sortMemberPath switch
        {
            nameof(FileRowViewModel.Path) => (a, b) => Text(a.Path, b.Path),
            nameof(FileRowViewModel.Reads) => (a, b) => a.Reads.CompareTo(b.Reads),
            nameof(FileRowViewModel.BytesRead) => (a, b) => a.BytesRead.CompareTo(b.BytesRead),
            nameof(FileRowViewModel.Writes) => (a, b) => a.Writes.CompareTo(b.Writes),
            nameof(FileRowViewModel.BytesWritten) => (a, b) => a.BytesWritten.CompareTo(b.BytesWritten),
            nameof(FileRowViewModel.Opens) => (a, b) => a.Opens.CompareTo(b.Opens),
            nameof(FileRowViewModel.StatusText) => (a, b) => Text(a.StatusText, b.StatusText),
            nameof(FileRowViewModel.LastSeen) => (a, b) => a.LastSeen.CompareTo(b.LastSeen),
            nameof(FileRowViewModel.LastOperationText) => (a, b) => string.CompareOrdinal(a.LastOperationText, b.LastOperationText),
            nameof(FileRowViewModel.Processes) => (a, b) => Text(a.Processes, b.Processes),
            _ => null,
        };
        return Create(comparison, (a, b) => Text(a.Path, b.Path), direction);
    }

    /// <returns>Null when the column cannot be sorted.</returns>
    public static IComparer? ForProcesses(string sortMemberPath, ListSortDirection direction)
    {
        Comparison<ProcessRowViewModel>? comparison = sortMemberPath switch
        {
            nameof(ProcessRowViewModel.ProcessName) => (a, b) => Text(a.ProcessName, b.ProcessName),
            nameof(ProcessRowViewModel.ProcessId) => (a, b) => a.ProcessId.CompareTo(b.ProcessId),
            nameof(ProcessRowViewModel.Reads) => (a, b) => a.Reads.CompareTo(b.Reads),
            nameof(ProcessRowViewModel.BytesRead) => (a, b) => a.BytesRead.CompareTo(b.BytesRead),
            nameof(ProcessRowViewModel.Writes) => (a, b) => a.Writes.CompareTo(b.Writes),
            nameof(ProcessRowViewModel.BytesWritten) => (a, b) => a.BytesWritten.CompareTo(b.BytesWritten),
            nameof(ProcessRowViewModel.Creates) => (a, b) => a.Creates.CompareTo(b.Creates),
            nameof(ProcessRowViewModel.Deletes) => (a, b) => a.Deletes.CompareTo(b.Deletes),
            nameof(ProcessRowViewModel.Renames) => (a, b) => a.Renames.CompareTo(b.Renames),
            nameof(ProcessRowViewModel.Files) => (a, b) => a.Files.CompareTo(b.Files),
            nameof(ProcessRowViewModel.LastSeen) => (a, b) => a.LastSeen.CompareTo(b.LastSeen),
            _ => null,
        };
        return Create(comparison, (a, b) => a.ProcessId.CompareTo(b.ProcessId), direction);
    }

    private static int Text(string? a, string? b) => StringComparer.OrdinalIgnoreCase.Compare(a, b);

    private static RowComparer<T>? Create<T>(Comparison<T>? primary, Comparison<T> tieBreak, ListSortDirection direction)
        where T : class =>
        primary is null ? null : new RowComparer<T>(primary, tieBreak, direction == ListSortDirection.Descending);

    private sealed class RowComparer<T>(Comparison<T> primary, Comparison<T> tieBreak, bool descending) : IComparer
        where T : class
    {
        public int Compare(object? x, object? y)
        {
            if (x is not T a || y is not T b)
                return 0;
            int result = primary(a, b);
            if (result == 0)
                result = tieBreak(a, b);
            return descending ? -result : result;
        }
    }
}
