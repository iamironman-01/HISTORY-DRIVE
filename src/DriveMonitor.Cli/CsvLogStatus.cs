using DriveMonitor.Core.Export;

namespace DriveMonitor.Cli;

/// <summary>
/// What drivemon says about its --csv log. An error, such as the log's drive running short of free space or being
/// pulled out, stops the log for good while monitoring goes on: drivemon says so on stderr as soon as it notices,
/// gives the final counts in the summary, and exits with 1, because rows the user asked for are missing from the log.
/// </summary>
internal sealed class CsvLogStatus(CsvActivityLog log)
{
    private bool _failureReported;

    /// <summary>How long stopping the log may take before drivemon says that it is still finishing it; tests change it.</summary>
    internal TimeSpan FinishingNoticeDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether the log stopped on an error. Once true, it stays true.</summary>
    public bool Failed => log.Error is not null;

    /// <summary>
    /// Says on <paramref name="error"/>, once, that the log stopped on an error. Called from the monitoring loop:
    /// the log refuses the later events and counts them as dropped, so monitoring goes on without it.
    /// </summary>
    public void ReportFailure(TextWriter error) => ReportFailure(error, monitoring: true);

    /// <summary>
    /// Stops the log, which first writes the rows still queued. On a slow drive that takes up to 30 s, so once it
    /// has taken <see cref="FinishingNoticeDelay"/> this says so rather than leave the console looking hung. Then
    /// says that the log stopped on an error, if it did and that has not been said yet.
    /// </summary>
    /// <param name="stopLog">Stops and disposes the log, as MonitorEngine.StopLog does.</param>
    public void Finish(Action stopLog, TextWriter output, TextWriter error)
    {
        Task stopping = Task.Run(stopLog);
        if (Task.WaitAny([stopping], FinishingNoticeDelay) < 0)
            output.WriteLine("Finishing the CSV log...");
        stopping.GetAwaiter().GetResult();
        ReportFailure(error, monitoring: false);
    }

    /// <summary>
    /// The summary's line for the log. Its counts are final once <see cref="Finish"/> has returned, unless stopping
    /// gave up on a drive that stopped responding: the rows that drive still holds are not counted yet.
    /// </summary>
    public string SummaryLine()
    {
        Exception? failure = log.Error; // first: it is set once the counts are final
        return SummaryLine(DescribeFiles(log.FilePath, log.CurrentFilePath), log.Written, log.Dropped, failure);
    }

    /// <summary>
    /// Why the log stopped and how many rows it wrote to which files. While monitoring goes on, the line adds that
    /// later events are not logged; at the end, how many rows were dropped, a number that only then is final.
    /// </summary>
    internal static string FailureLine(string files, long written, long dropped, Exception failure, bool monitoring)
    {
        string rows = written == 1 ? "1 row was" : $"{written:N0} rows were";
        string end = monitoring
            ? "; later events are not logged."
            : dropped == 1 ? " and 1 was dropped." : $" and {dropped:N0} were dropped.";
        return $"The CSV log stopped: {Sentence(failure.Message)} {rows} written to {files}{end}";
    }

    /// <summary>"CSV log: files  rows written: n  rows dropped: n", and the error that stopped the log, if any.</summary>
    internal static string SummaryLine(string files, long written, long dropped, Exception? failure)
    {
        string line = $"CSV log: {files}  rows written: {written:N0}  rows dropped: {dropped:N0}";
        return failure is null ? line : $"{line}  error: {OneLine(failure.Message)}";
    }

    /// <summary>
    /// The log's file or, once the log has continued in more files (name.002.csv and so on), its first and current
    /// file. Cleaned, because the folders on the path may have been named by whoever made the drive.
    /// </summary>
    internal static string DescribeFiles(string firstFile, string currentFile) =>
        ConsoleText.Clean(string.Equals(currentFile, firstFile, StringComparison.OrdinalIgnoreCase)
            ? firstFile
            : $"{firstFile} through {Path.GetFileName(currentFile)}");

    /// <summary>An error message on one line, ending like a sentence so that another can follow it.</summary>
    internal static string Sentence(string message)
    {
        string text = OneLine(message);
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
    }

    /// <summary>A message on one line. Windows' messages can contain the log's path (see <see cref="DescribeFiles"/>), and a few span lines.</summary>
    private static string OneLine(string message) => ConsoleText.Clean(message.TrimEnd());

    private void ReportFailure(TextWriter error, bool monitoring)
    {
        // Error is set last, once the counts that go with it are final.
        if (_failureReported || log.Error is not { } failure)
            return;

        _failureReported = true;
        error.WriteLine(FailureLine(DescribeFiles(log.FilePath, log.CurrentFilePath), log.Written, log.Dropped, failure, monitoring));
    }
}
