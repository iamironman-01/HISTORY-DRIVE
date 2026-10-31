using DriveMonitor.Core.Filtering;

namespace DriveMonitor.Tests.Filtering;

/// <summary>The monitored drive-letter set and the per-event path match.</summary>
public sealed class DriveFilterTests
{
    [Fact]
    public void NewFilter_MonitorsNothing()
    {
        var filter = new DriveFilter();

        Assert.Empty(filter.MonitoredDrives);
        Assert.False(filter.IsMonitored('C'));
        Assert.False(filter.TryMatch(@"C:\x.txt", out _));
    }

    [Fact]
    public void SetMonitored_Single_AddsAndRemovesADrive()
    {
        var filter = new DriveFilter();

        filter.SetMonitored('E', true);

        Assert.True(filter.IsMonitored('E'));
        Assert.True(filter.IsMonitored('e'));
        Assert.Equal(new[] { 'E' }, filter.MonitoredDrives);

        filter.SetMonitored('E', false);

        Assert.False(filter.IsMonitored('E'));
        Assert.Empty(filter.MonitoredDrives);
    }

    [Fact]
    public void SetMonitored_LowerCaseLetter_IsTheSameDrive()
    {
        var filter = new DriveFilter();

        filter.SetMonitored('f', true);

        Assert.True(filter.IsMonitored('F'));
        Assert.Equal(new[] { 'F' }, filter.MonitoredDrives);
    }

    [Theory]
    [InlineData('1')]
    [InlineData('\0')]
    [InlineData(':')]
    [InlineData('@')]
    [InlineData('[')]
    [InlineData('\u00E9')]
    public void SetMonitored_NonLetter_ThrowsAndChangesNothing(char drive)
    {
        var filter = new DriveFilter();
        filter.SetMonitored('E', true);
        int changes = 0;
        filter.Changed += () => changes++;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => filter.SetMonitored(drive, true));

        Assert.Equal("drive", error.ParamName);
        Assert.Equal(new[] { 'E' }, filter.MonitoredDrives);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Changed_FiresOnlyWhenASingleDriveReallyChanges()
    {
        var filter = new DriveFilter();
        int changes = 0;
        filter.Changed += () => changes++;

        filter.SetMonitored('E', true);
        Assert.Equal(1, changes);

        filter.SetMonitored('E', true);
        filter.SetMonitored('e', true);
        filter.SetMonitored('F', false);
        Assert.Equal(1, changes);

        filter.SetMonitored('E', false);
        Assert.Equal(2, changes);

        filter.SetMonitored('E', false);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void SetMonitoredBulk_ReplacesTheWholeSelection()
    {
        var filter = new DriveFilter();
        filter.SetMonitored('C', true);

        filter.SetMonitored(new[] { 'E', 'f', 'E' });

        Assert.Equal(new[] { 'E', 'F' }, filter.MonitoredDrives);
        Assert.False(filter.IsMonitored('C'));
    }

    [Fact]
    public void SetMonitoredBulk_SilentlyIgnoresNonLetters()
    {
        var filter = new DriveFilter();

        filter.SetMonitored(new[] { 'E', '1', '?', ' ' });

        Assert.Equal(new[] { 'E' }, filter.MonitoredDrives);
    }

    [Fact]
    public void Changed_FiresOnlyWhenTheBulkSelectionDiffers()
    {
        var filter = new DriveFilter();
        int changes = 0;
        filter.Changed += () => changes++;

        filter.SetMonitored(new[] { 'E', 'F' });
        Assert.Equal(1, changes);

        filter.SetMonitored(new[] { 'F', 'E' });
        filter.SetMonitored(new[] { 'e', 'f', 'F' });
        Assert.Equal(1, changes);

        filter.SetMonitored(Array.Empty<char>());
        Assert.Equal(2, changes);

        filter.SetMonitored(Array.Empty<char>());
        Assert.Equal(2, changes);
    }

    [Fact]
    public void MonitoredDrives_AreInAlphabeticalOrder()
    {
        var filter = new DriveFilter();

        filter.SetMonitored("ZCAE");

        Assert.Equal(new[] { 'A', 'C', 'E', 'Z' }, filter.MonitoredDrives);
    }

    [Fact]
    public void MonitoredDrives_CanHoldAllTwentySixLetters()
    {
        var filter = new DriveFilter();
        const string all = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        filter.SetMonitored(all.ToLowerInvariant());

        Assert.Equal(all, new string(filter.MonitoredDrives.ToArray()));
        Assert.True(filter.IsMonitored('A'));
        Assert.True(filter.IsMonitored('Z'));
    }

    [Theory]
    [InlineData(@"E:\photos\a.jpg")]
    [InlineData(@"e:\photos\a.jpg")]
    [InlineData("E:")]
    [InlineData("e:")]
    public void TryMatch_PathOnMonitoredDrive_ReturnsTheUpperCaseLetter(string path)
    {
        var filter = new DriveFilter();
        filter.SetMonitored('E', true);

        Assert.True(filter.TryMatch(path, out char drive));
        Assert.Equal('E', drive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("E")]
    [InlineData(@"F:\x.txt")]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData(@"\Device\HarddiskVolume5\x.txt")]
    [InlineData(@"\??\E:\x.txt")]
    [InlineData("1:")]
    [InlineData(@"?:\x.txt")]
    [InlineData(@":\x.txt")]
    public void TryMatch_PathNotOnAMonitoredDrive_ReturnsFalse(string? path)
    {
        var filter = new DriveFilter();
        filter.SetMonitored('E', true);

        Assert.False(filter.TryMatch(path, out char drive));
        Assert.Equal('\0', drive);
    }

    [Fact]
    public void TryMatch_FollowsSelectionChanges()
    {
        var filter = new DriveFilter();

        Assert.False(filter.TryMatch(@"G:\x.txt", out _));

        filter.SetMonitored('G', true);
        Assert.True(filter.TryMatch(@"G:\x.txt", out _));

        filter.SetMonitored(new[] { 'H' });
        Assert.False(filter.TryMatch(@"G:\x.txt", out _));
    }

    [Theory]
    [InlineData('1')]
    [InlineData('\0')]
    [InlineData('[')]
    public void IsMonitored_NonLetter_IsFalse(char drive)
    {
        var filter = new DriveFilter();
        filter.SetMonitored("ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        Assert.False(filter.IsMonitored(drive));
    }
}
