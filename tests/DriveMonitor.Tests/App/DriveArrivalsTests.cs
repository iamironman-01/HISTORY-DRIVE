using DriveMonitor.App.Services;
using DriveMonitor.App.ViewModels;
using DriveMonitor.Core.Filtering;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Tests.App;

/// <summary>Monitoring drive letters as soon as Windows reports them, ahead of the drive enumeration.</summary>
public sealed class DriveArrivalsTests
{
    private readonly DriveFilter _filter = new();
    private readonly Dictionary<char, DriveItemViewModel> _known = new();
    private readonly DriveArrivals _arrivals;

    public DriveArrivalsTests()
    {
        _arrivals = new DriveArrivals(_filter);
    }

    [Fact]
    public void Arrived_MonitorsALetterNoDriveWasListedFor()
    {
        _arrivals.Arrived(['F', 'G'], _known);

        Assert.Equal(new[] { 'F', 'G' }, _filter.MonitoredDrives);
    }

    [Fact]
    public void Arrived_MonitorsTheLetterOfAReturningExternalDrive()
    {
        Known('E', DriveKind.Removable, monitored: false, connected: false);

        _arrivals.Arrived(['E'], _known);

        Assert.True(_filter.IsMonitored('E'));
    }

    [Fact]
    public void Arrived_LeavesConnectedAndReturningInternalDrivesToTheirChoice()
    {
        Known('D', DriveKind.Fixed, monitored: false, connected: false);
        Known('R', DriveKind.Removable, monitored: false, connected: true); // a card reader whose card was inserted

        _arrivals.Arrived(['D', 'R'], _known);

        Assert.Empty(_filter.MonitoredDrives);
    }

    [Fact]
    public void Enumerated_UnmonitorsALetterWhoseVolumeWentAwayAgain()
    {
        Known('E', DriveKind.Removable, monitored: false, connected: false);
        _arrivals.Arrived(['E', 'F'], _known);

        _arrivals.Enumerated(_arrivals.Count, present: new HashSet<char>(), _known);

        Assert.Empty(_filter.MonitoredDrives);
    }

    [Fact]
    public void Enumerated_RestoresTheChoiceOfAMonitoredDriveThatStaysAway()
    {
        Known('E', DriveKind.Removable, monitored: true, connected: false);
        _arrivals.Arrived(['E'], _known);

        _arrivals.Enumerated(_arrivals.Count, present: new HashSet<char>(), _known);

        Assert.True(_filter.IsMonitored('E'));
    }

    [Fact]
    public void Enumerated_LeavesFoundDrivesToTheMerge()
    {
        _arrivals.Arrived(['F'], _known);

        _arrivals.Enumerated(_arrivals.Count, present: new HashSet<char> { 'F' }, _known);

        Assert.True(_filter.IsMonitored('F'));
    }

    [Fact]
    public void Enumerated_KeepsALetterThatArrivedAfterTheEnumerationStarted()
    {
        long atStart = _arrivals.Count;
        _arrivals.Arrived(['F'], _known);

        _arrivals.Enumerated(atStart, present: new HashSet<char>(), _known);
        Assert.True(_filter.IsMonitored('F')); // that enumeration could not have seen it

        _arrivals.Enumerated(_arrivals.Count, present: new HashSet<char>(), _known);
        Assert.False(_filter.IsMonitored('F'));
    }

    [Theory]
    [InlineData(0u, "")]
    [InlineData(1u << 4, "E")]
    [InlineData((1u << 0) | (1u << 25), "AZ")]
    [InlineData(0xFC000000u | (1u << 6), "G")] // bits past Z: are not drive letters
    public void LettersOf_ReadsTheBroadcastUnitMask(uint unitMask, string expected)
    {
        Assert.Equal(expected, new string(DeviceChangeWatcher.LettersOf(unitMask).ToArray()));
    }

    private void Known(char letter, DriveKind kind, bool monitored, bool connected)
    {
        var drive = new DriveItemViewModel(new DriveDescriptor { Letter = letter, Kind = kind }, _filter, _ => { });
        drive.SetMonitored(monitored);
        if (!connected)
            drive.MarkDisconnected();
        _known.Add(letter, drive);
    }
}
