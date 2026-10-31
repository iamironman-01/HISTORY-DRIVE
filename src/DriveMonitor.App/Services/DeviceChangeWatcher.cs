using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DriveMonitor.App.Services;

/// <summary>
/// Raises <see cref="VolumesChanged"/> when a drive letter appears or disappears: a pendrive plugged in
/// or removed, a memory card inserted, a network drive mapped. Windows broadcasts these to every
/// top-level window as WM_DEVICECHANGE; one device often sends several, so they are debounced.
/// </summary>
public sealed class DeviceChangeWatcher : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypVolume = 2;

    private readonly HwndSource _source;
    private readonly DispatcherTimer _debounce;
    private bool _disposed;

    public DeviceChangeWatcher(Window window, TimeSpan? debounce = null)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("The window has no Win32 handle.");
        _debounce = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher)
        {
            Interval = debounce ?? TimeSpan.FromMilliseconds(750),
        };
        _debounce.Tick += OnDebounceTick;
        _source.AddHook(WndProc);
    }

    /// <summary>Raised on the UI thread once the notifications for a change have settled.</summary>
    public event EventHandler? VolumesChanged;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _debounce.Stop();

        // The window may already have destroyed its source (and the hook with it).
        if (!_source.IsDisposed)
            _source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange && !_disposed && IsVolumeChange(wParam, lParam))
        {
            // Restart the delay: report once the burst of notifications is over.
            _debounce.Stop();
            _debounce.Start();
        }

        return IntPtr.Zero;
    }

    private static bool IsVolumeChange(IntPtr wParam, IntPtr lParam)
    {
        long code = wParam.ToInt64();
        if (code is not (DbtDeviceArrival or DbtDeviceRemoveComplete) || lParam == IntPtr.Zero)
            return false;

        // DEV_BROADCAST_HDR { DWORD dbch_size; DWORD dbch_devicetype; DWORD dbch_reserved; }
        return Marshal.ReadInt32(lParam, 4) == DbtDevTypVolume;
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        if (!_disposed)
            VolumesChanged?.Invoke(this, EventArgs.Empty);
    }
}
