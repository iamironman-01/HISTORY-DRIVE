using DriveMonitor.Cli;
using DriveMonitor.Core.Filtering;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Tests.Cli;

/// <summary>drivemon without --drives: drives plugged in after start join the default selection.</summary>
public sealed class DriveWatcherTests
{
    private static readonly DriveDescriptor WindowsC = new() { Letter = 'C', Kind = DriveKind.Fixed, IsSystemDrive = true };
    private static readonly DriveDescriptor DataD = new() { Letter = 'D', Kind = DriveKind.Fixed };
    private static readonly DriveDescriptor PendriveF = new() { Letter = 'F', Kind = DriveKind.Removable, Label = "STICK" };
    private static readonly DriveDescriptor NetworkZ = new() { Letter = 'Z', Kind = DriveKind.Network };

    private readonly DriveFilter _filter = new();
    private readonly List<DriveDescriptor> _present = [];
    private int _enumerations;

    [Fact]
    public void FirstCheck_SelectsEveryDriveButTheWindowsDriveAndNetworkShares()
    {
        _present.AddRange([WindowsC, DataD, NetworkZ]);

        IReadOnlyList<DriveDescriptor> added = CreateWatcher().Check();

        Assert.Equal(new[] { DataD }, added);
        Assert.Equal(new[] { 'D' }, _filter.MonitoredDrives);
    }

    [Fact]
    public void FirstCheck_WithSystem_SelectsTheWindowsDriveToo()
    {
        _present.AddRange([WindowsC, DataD]);

        IReadOnlyList<DriveDescriptor> added = CreateWatcher(includeSystemDrive: true).Check();

        Assert.Equal(new[] { WindowsC, DataD }, added);
        Assert.Equal(new[] { 'C', 'D' }, _filter.MonitoredDrives);
    }

    [Fact]
    public void PendrivePluggedInLater_IsAddedAndReportedOnce()
    {
        _present.AddRange([WindowsC, DataD]);
        DriveWatcher watcher = CreateWatcher();
        watcher.Check();

        _present.Add(PendriveF);

        Assert.Equal(new[] { PendriveF }, watcher.Check());
        Assert.Equal(new[] { 'D', 'F' }, _filter.MonitoredDrives);
        Assert.Empty(watcher.Check());
        Assert.Empty(watcher.Check(full: true));
    }

    [Fact]
    public void NoDriveAtStart_ThePendriveIsStillAdded()
    {
        _present.Add(WindowsC);
        DriveWatcher watcher = CreateWatcher();
        Assert.Empty(watcher.Check());

        _present.Add(PendriveF);

        Assert.Equal(new[] { PendriveF }, watcher.Check());
    }

    [Fact]
    public void NetworkShareMappedLater_IsNotAdded()
    {
        _present.AddRange([WindowsC, DataD]);
        DriveWatcher watcher = CreateWatcher();
        watcher.Check();

        _present.Add(NetworkZ);

        Assert.Empty(watcher.Check());
        Assert.False(_filter.IsMonitored('Z'));
    }

    [Fact]
    public void NoLetterCameOrWent_TheDrivesAreNotEnumeratedAgain()
    {
        _present.AddRange([WindowsC, DataD]);
        DriveWatcher watcher = CreateWatcher();

        watcher.Check();
        watcher.Check();
        watcher.Check();

        Assert.Equal(1, _enumerations);
    }

    [Fact]
    public void FullCheck_NoticesALetterThatChangedHands()
    {
        // Z: was a network share at start; a pendrive took the letter between two checks.
        _present.AddRange([WindowsC, NetworkZ]);
        DriveWatcher watcher = CreateWatcher();
        watcher.Check();
        var pendriveZ = new DriveDescriptor { Letter = 'Z', Kind = DriveKind.Removable };
        _present[1] = pendriveZ;

        Assert.Empty(watcher.Check());
        Assert.Equal(new[] { pendriveZ }, watcher.Check(full: true));
        Assert.True(_filter.IsMonitored('Z'));
    }

    [Fact]
    public void PendrivePulledOut_StaysMonitored_SoItsReturnIsNotReportedAgain()
    {
        _present.AddRange([WindowsC, PendriveF]);
        DriveWatcher watcher = CreateWatcher();
        watcher.Check();

        _present.Remove(PendriveF);
        Assert.Empty(watcher.Check());
        _present.Add(PendriveF);

        Assert.Empty(watcher.Check());
        Assert.True(_filter.IsMonitored('F'));
    }

    [Fact]
    public void EnumerationFails_TheNextCheckTriesAgain()
    {
        bool fail = false;
        var watcher = new DriveWatcher(_filter, includeSystemDrive: false, PresentLetters,
            () => fail ? throw new IOException("The device is not ready.") : _present.ToList());
        _present.Add(WindowsC);
        watcher.Check();

        _present.Add(PendriveF);
        fail = true;
        Assert.Throws<IOException>(() => watcher.Check());
        fail = false;

        Assert.Equal(new[] { PendriveF }, watcher.Check());
    }

    [Fact]
    public void PresentLetters_IncludesTheWindowsDrive()
    {
        int letters = DriveWatcher.PresentLetters();

        Assert.NotEqual(0, letters & (1 << (DriveCatalog.SystemDriveLetter - 'A')));
    }

    [Fact]
    public void DescribeDrive_CleansTheLabelAndModelThatTheDriveSupplies()
    {
        var drive = new DriveDescriptor { Letter = 'F', Kind = DriveKind.Removable, Label = "STICK\u001b[2J", Model = "Evil\rCorp" };

        Assert.Equal("F: STICK?[2J (Evil?Corp)", LiveMonitor.DescribeDrive(drive));
        Assert.Equal("F:", LiveMonitor.DescribeDrive(new DriveDescriptor { Letter = 'F' }));
    }

    private DriveWatcher CreateWatcher(bool includeSystemDrive = false) =>
        new(_filter, includeSystemDrive, PresentLetters, () =>
        {
            _enumerations++;
            return _present.ToList();
        });

    private int PresentLetters() => _present.Aggregate(0, (letters, d) => letters | (1 << (d.Letter - 'A')));
}
