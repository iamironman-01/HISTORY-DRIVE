namespace DriveMonitor.Tests.TestSupport;

/// <summary>A new, empty folder under the user's temp directory, deleted again on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        FullPath = Path.Combine(Path.GetTempPath(), "DriveMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public string PathOf(string name) => Path.Combine(FullPath, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something still holds a file open; leave it for the OS temp cleanup.
        }
    }
}
