using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DriveMonitor.App.Services;

/// <summary>
/// Raises <see cref="VolumesChanged"/> when a drive letter appears or disappears: a pendrive plugged in
/// or removed, a memory card inserted, a network drive mapped. Windows broadcasts these to every
/// top-level window as WM_DEVICECHANGE; one device often sends several, so they are debounced.
/// <see cref="VolumesArrived"/> reports the new letters themselves without that delay.
/// </summary>
public sealed class DeviceChangeWatcher : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypVolume = 2;
    private const int DbtfNet = 0x0002;

    // DEV_BROADCAST_VOLUME { DEV_BROADCAST_HDR (3 DWORDs); DWORD dbcv_unitmask; WORD dbcv_flags; }
    private const int UnitMaskOffset = 12;
    private const int FlagsOffset = 16;
    private const int VolumeBroadcastSize = 18;

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

    /// <summary>
    /// Raised on the UI thread for each arrival, before <see cref="VolumesChanged"/>, with the letters of the local
    /// volumes that arrived (not network drives).
    /// </summary>
    public event EventHandler<IReadOnlyList<char>>? VolumesArrived;

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

    /// <summary>The drive letters in a DEV_BROADCAST_VOLUME unit mask: bit 0 is A:, bit 25 is Z:.</summary>
    internal static IReadOnlyList<char> LettersOf(uint unitMask)
    {
        var letters = new List<char>(1);
        for (int i = 0; i < 26; i++)
        {
            if ((unitMask & (1u << i)) != 0)
                letters.Add((char)('A' + i));
        }

        return letters;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange && !_disposed && IsVolumeChange(wParam, lParam))
        {
            // At once, not after the delay: until the new letter is monitored, what happens on it is lost.
            if (wParam.ToInt64() == DbtDeviceArrival && ArrivedLetters(lParam) is { Count: > 0 } letters)
                VolumesArrived?.Invoke(this, letters);

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

    private static IReadOnlyList<char> ArrivedLetters(IntPtr lParam)
    {
        if (Marshal.ReadInt32(lParam, 0) < VolumeBroadcastSize)
            return [];

        // Network drives are never monitored by default, so they are left to the enumeration.
        if ((Marshal.ReadInt16(lParam, FlagsOffset) & DbtfNet) != 0)
            return [];
        return LettersOf(unchecked((uint)Marshal.ReadInt32(lParam, UnitMaskOffset)));
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        if (!_disposed)
            VolumesChanged?.Invoke(this, EventArgs.Empty);
    }
}
