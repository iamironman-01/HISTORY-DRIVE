namespace DriveMonitor.Core;

/// <summary>Settings for <see cref="MonitorEngine"/>. Only <see cref="ExcludeOwnProcess"/> may change while running.</summary>
public sealed class MonitorOptions
{
    private volatile bool _excludeOwnProcess = true;

    /// <summary>
    /// ETW session name. Starting replaces a leftover session with the same name (for example
    /// after a crash), so two programs must not share a name.
    /// </summary>
    public string SessionName { get; set; } = "DriveMonitor-FileIO";

    /// <summary>Kernel buffer memory for the session. Larger values lose fewer events during bursts.</summary>
    public int BufferSizeMB { get; set; } = 64;

    /// <summary>
    /// Match operations with their completion events to report real byte counts, success or
    /// failure, and whether a file was created or merely opened.
    /// </summary>
    public bool UseCompletionEvents { get; set; } = true;

    /// <summary>How long an operation may wait for its completion event before it is published as Unknown.</summary>
    public int CompletionWaitMs { get; set; } = 2000;

    /// <summary>Events kept for <see cref="MonitorEngine.DrainEvents"/>; when nobody drains, the oldest are dropped.</summary>
    public int MaxQueuedEvents { get; set; } = 250_000;

    /// <summary>Distinct files with statistics; later new files are counted but not tracked.</summary>
    public int MaxTrackedFiles { get; set; } = 200_000;

    /// <summary>Report folder listings (noisy while someone browses a drive).</summary>
    public bool CaptureDirectoryListings { get; set; } = true;

    /// <summary>
    /// How often (ms) Windows is asked to hand over buffered events. Without it events arrive about
    /// once a second; 0 keeps that default.
    /// </summary>
    public int FlushIntervalMs { get; set; } = 500;

    /// <summary>
    /// Ask Windows for the names of files that are already open when monitoring starts; otherwise
    /// I/O on them stays unnamed until they are reopened.
    /// </summary>
    public bool RequestFileNameRundown { get; set; } = true;

    /// <summary>Learn the new name of renamed files from an undocumented Windows 11 event, when Windows sends it.</summary>
    public bool ParsePathOperations { get; set; } = true;

    /// <summary>Restart the kernel session if it stops unexpectedly (at most three times a minute).</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>
    /// Size (MB) at which the activity log continues in a new file, named like the first with .002, .003
    /// and so on before the extension; 0 keeps one file of any size. Read when a log starts.
    /// </summary>
    public int MaxLogFileMB { get; set; } = 100;

    /// <summary>How many files of one activity log are kept; starting another deletes the oldest. 0 keeps all. Read when a log starts.</summary>
    public int MaxLogFiles { get; set; } = 10;

    /// <summary>
    /// The activity log stops with an error when less than this (MB) is free on its drive, so a long
    /// recording cannot fill the disk; 0 turns the check off. Read when a log starts.
    /// </summary>
    public int MinLogFreeSpaceMB { get; set; } = 512;

    /// <summary>Hide this program's own file activity, such as saving an export onto a monitored drive.</summary>
    public bool ExcludeOwnProcess
    {
        get => _excludeOwnProcess;
        set => _excludeOwnProcess = value;
    }
}
