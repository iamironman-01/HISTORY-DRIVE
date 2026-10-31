using DriveMonitor.Core.Models;

namespace DriveMonitor.App.ViewModels;

/// <summary>
/// Decides which rows the tables show: operation categories, paging and failed operations for the
/// Live activity list, and the search text for every table. Called for each row on every refresh,
/// so it does no allocation.
/// </summary>
public sealed class ActivityFilter
{
    private int _visibleOperations;

    public bool ShowPaging { get; set; }

    public bool ShowFailed { get; set; }

    /// <summary>Case-insensitive text that must occur in a path or process name; empty matches everything.</summary>
    public string Search { get; set; } = "";

    public void SetVisible(FileOperation operation, bool visible)
    {
        int bit = 1 << (int)operation;
        _visibleOperations = visible ? _visibleOperations | bit : _visibleOperations & ~bit;
    }

    /// <summary>The ICollectionView filter of the Live activity list.</summary>
    public bool AcceptsActivity(object item) =>
        item is ActivityRowViewModel row
        && (_visibleOperations & (1 << (int)row.Operation)) != 0
        && (ShowPaging || !row.IsPaging)
        && (ShowFailed || !row.IsFailed)
        && (Search.Length == 0 || Matches(row.Path) || Matches(row.TargetPath) || Matches(row.ProcessName));

    /// <summary>
    /// The ICollectionView filter of the Files table. The Processes column names three processes at most, so the
    /// others a file has are matched from <see cref="FileRowViewModel.ProcessNames"/>.
    /// </summary>
    public bool AcceptsFile(object item) =>
        item is FileRowViewModel row
        && (Search.Length == 0 || Matches(row.Path) || Matches(row.RenamedTo) || Matches(row.Processes)
            || Matches(row.LastProcess) || MatchesAny(row.ProcessNames));

    /// <summary>The ICollectionView filter of the Processes table.</summary>
    public bool AcceptsProcess(object item) =>
        item is ProcessRowViewModel row && (Search.Length == 0 || Matches(row.ProcessName));

    private bool Matches(string? text) => text is not null && text.Contains(Search, StringComparison.OrdinalIgnoreCase);

    private bool MatchesAny(IReadOnlyList<string>? texts)
    {
        if (texts is null)
            return false;
        for (int i = 0; i < texts.Count; i++)
        {
            if (Matches(texts[i]))
                return true;
        }

        return false;
    }
}
