using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DriveMonitor.App.Services;

/// <summary>
/// Allows one DriveMonitor window per Windows session, using a named mutex. Any program in the session can
/// create or hold that name, so a taken name alone does not stop the start: only a DriveMonitor that is
/// really running does.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\DriveMonitor.App.SingleInstance";

    /// <summary>
    /// Passed to the elevated copy that "Restart as administrator" starts, so it waits for this
    /// instance to exit instead of reporting that DriveMonitor is already running.
    /// </summary>
    public const string RestartedArgument = "--restarted";

    private readonly Mutex? _mutex;
    private bool _disposed;

    private SingleInstance(Mutex? mutex)
    {
        _mutex = mutex;
    }

    /// <summary>False when this instance runs without the mutex because another program holds the name.</summary>
    internal bool OwnsMutex => _mutex is not null;

    /// <summary>Takes ownership of the instance mutex, waiting up to <paramref name="wait"/>.</summary>
    /// <returns>
    /// Null when another DriveMonitor runs. When the name is held, or locked against this process, by anything
    /// else, an instance that runs without the mutex.
    /// </returns>
    public static SingleInstance? TryAcquire(TimeSpan wait) => TryAcquire(wait, MutexName, IsAnotherInstanceRunning);

    /// <param name="anotherInstanceRunning">Asked only when the mutex cannot be taken.</param>
    internal static SingleInstance? TryAcquire(TimeSpan wait, string mutexName, Func<bool> anotherInstanceRunning)
    {
        Mutex? mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Created by an elevated instance, which a non-elevated process may not open, or by a program that
            // locked others out of it.
            mutex = null;
        }

        if (mutex is not null)
        {
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
        }

        // Any program in this session may have taken the name just to keep DriveMonitor from starting.
        return anotherInstanceRunning() ? null : new SingleInstance(null);
    }

    /// <summary>Releases the mutex; must be called on the thread that acquired it.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_mutex is null)
            return;

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

    /// <summary>
    /// Whether another process of this program runs elevated in this Windows session. Anyone can start a program
    /// named DriveMonitor.exe, but not an elevated one, and DriveMonitor.exe always runs elevated: its manifest asks
    /// for administrator rights. A process this one cannot look at counts only when this one is not elevated
    /// itself, because a non-elevated process cannot look at an elevated one.
    /// </summary>
    private static bool IsAnotherInstanceRunning()
    {
        using Process current = Process.GetCurrentProcess();
        bool selfElevated = IsElevated(current.Id) == true;
        Process[] others = Process.GetProcessesByName(current.ProcessName);
        try
        {
            foreach (Process other in others)
            {
                if (other.Id == current.Id || other.SessionId != current.SessionId)
                    continue;
                bool? elevated = IsElevated(other.Id);
                if (elevated == true || (elevated is null && !selfElevated))
                    return true;
            }

            return false;
        }
        finally
        {
            foreach (Process other in others)
                other.Dispose();
        }
    }

    /// <returns>Null when the process cannot be looked at (it ended, or its token is closed to this process).</returns>
    private static bool? IsElevated(int processId)
    {
        using SafeProcessHandle process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
            return null;

        bool opened = Native.OpenProcessToken(process, Native.TokenQuery, out SafeAccessTokenHandle token);
        using (token)
        {
            if (!opened || !Native.GetTokenInformation(token, Native.TokenElevation, out int elevated, sizeof(int), out _))
                return null;
            return elevated != 0;
        }
    }

    private static class Native
    {
        public const uint ProcessQueryLimitedInformation = 0x1000;
        public const uint TokenQuery = 0x0008;
        public const int TokenElevation = 20;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(
            SafeAccessTokenHandle token, int informationClass, out int information, int informationLength, out int returnLength);
    }
}
