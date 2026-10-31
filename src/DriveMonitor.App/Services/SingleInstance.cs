using System.IO;

namespace DriveMonitor.App.Services;

/// <summary>Allows one DriveMonitor window per Windows session, using a named mutex.</summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\DriveMonitor.App.SingleInstance";

    /// <summary>
    /// Passed to the elevated copy that "Restart as administrator" starts, so it waits for this
    /// instance to exit instead of reporting that DriveMonitor is already running.
    /// </summary>
    public const string RestartedArgument = "--restarted";

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>Takes ownership of the instance mutex, waiting up to <paramref name="wait"/>.</summary>
    /// <returns>Null when another instance owns it.</returns>
    public static SingleInstance? TryAcquire(TimeSpan wait)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Created by an elevated instance, which a non-elevated process may not open: it exists, so it is running.
            return null;
        }

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner exited without releasing it; ownership passed to this thread.
            acquired = true;
        }

        if (acquired)
            return new SingleInstance(mutex);

        mutex.Dispose();
        return null;
    }

    /// <summary>Releases the mutex; must be called on the thread that acquired it.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; closing the handle below abandons it, which is harmless.
        }

        _mutex.Dispose();
    }
}
