using DriveMonitor.App.Services;

namespace DriveMonitor.Tests.App;

/// <summary>
/// One DriveMonitor per Windows session, with a mutex name that any program can take. Each test uses a name of
/// its own, so a DriveMonitor running on this machine is not disturbed.
/// </summary>
public sealed class SingleInstanceTests
{
    private readonly string _name = @"Local\DriveMonitor.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void TryAcquire_FreeName_TakesTheMutex()
    {
        using SingleInstance? instance = SingleInstance.TryAcquire(TimeSpan.Zero, _name, () => throw new InvalidOperationException("not asked"));

        Assert.NotNull(instance);
        Assert.True(instance.OwnsMutex);
    }

    [Fact]
    public void TryAcquire_NameHeldByAnotherProgram_StartsWithoutTheMutex()
    {
        using var squatter = new NameHolder(_name);

        using SingleInstance? instance = SingleInstance.TryAcquire(TimeSpan.Zero, _name, () => false);

        Assert.NotNull(instance);
        Assert.False(instance.OwnsMutex);
    }

    [Fact]
    public void TryAcquire_NameHeldWhileDriveMonitorRuns_GivesWay()
    {
        using var running = new NameHolder(_name);

        Assert.Null(SingleInstance.TryAcquire(TimeSpan.Zero, _name, () => true));
    }

    [Fact]
    public void TryAcquire_WaitsForTheOwnerToLetGo()
    {
        // "Restart as administrator": the elevated copy waits while the first instance closes. The mutex belongs to
        // the thread that took it, so this test stays on one thread rather than awaiting.
        var holder = new NameHolder(_name);
        var closing = new Thread(() =>
        {
            Thread.Sleep(200);
            holder.Dispose();
        });
        closing.Start();

        using SingleInstance? instance = SingleInstance.TryAcquire(TimeSpan.FromSeconds(10), _name, () => true);

        Assert.NotNull(instance);
        Assert.True(instance.OwnsMutex);
        closing.Join();
    }

    /// <summary>Owns the named mutex on a thread of its own until disposed, as another process would.</summary>
    private sealed class NameHolder : IDisposable
    {
        private readonly ManualResetEventSlim _letGo = new();
        private readonly Thread _thread;

        public NameHolder(string name)
        {
            using var owned = new ManualResetEventSlim();
            bool createdNew = false;
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(initiallyOwned: true, name, out createdNew);
                owned.Set();
                _letGo.Wait();
                if (createdNew)
                    mutex.ReleaseMutex();
            })
            {
                IsBackground = true,
            };
            _thread.Start();
            owned.Wait();
            Assert.True(createdNew, "the test's mutex name was already taken");
        }

        public void Dispose()
        {
            _letGo.Set();
            _thread.Join();
        }
    }
}
