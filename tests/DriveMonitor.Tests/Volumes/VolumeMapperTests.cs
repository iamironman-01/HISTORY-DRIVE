using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Tests.Volumes;

/// <summary>Kernel path → DOS path translation, driven by a fake QueryDosDevice so no real drive is involved.</summary>
public sealed class VolumeMapperTests
{
    /// <summary>Every build of the mapping asks once for each letter A-Z.</summary>
    private const int QueriesPerRefresh = 26;

    [Fact]
    public void Constructor_QueriesEveryLetterOnceInOrder()
    {
        FakeDosDevices devices = TypicalDevices();

        _ = new VolumeMapper(devices.Query);

        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZ", new string(devices.Queried.ToArray()));
    }

    [Fact]
    public void ToDosPath_FileOnMappedVolume_UsesTheDriveLetter()
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(@"E:\a\b.txt", mapper.ToDosPath(@"\Device\HarddiskVolume5\a\b.txt"));
        Assert.Equal(@"C:\Windows\notepad.exe", mapper.ToDosPath(@"\Device\HarddiskVolume3\Windows\notepad.exe"));
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolume5")]
    [InlineData(@"\Device\HarddiskVolume5\")]
    public void ToDosPath_VolumeItself_IsTheDriveRoot(string kernelPath)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(@"E:\", mapper.ToDosPath(kernelPath));
    }

    [Fact]
    public void ToDosPath_VolumeNumbersSharingAPrefix_AreNotConfused()
    {
        var devices = new FakeDosDevices
        {
            ['C'] = @"\Device\HarddiskVolume1",
            ['F'] = @"\Device\HarddiskVolume10",
        };
        var mapper = new VolumeMapper(devices.Query);

        Assert.Equal(@"F:\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume10\x.txt"));
        Assert.Equal(@"C:\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume1\x.txt"));
        Assert.Equal(@"F:\", mapper.ToDosPath(@"\Device\HarddiskVolume10"));
        Assert.Equal(@"C:\", mapper.ToDosPath(@"\Device\HarddiskVolume1"));
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolume10\x.txt")]
    [InlineData(@"\Device\HarddiskVolume10")]
    [InlineData(@"\Device\HarddiskVolume1x\x.txt")]
    public void ToDosPath_DeviceNameExtendingAMappedOne_IsNotMapped(string kernelPath)
    {
        var devices = new FakeDosDevices { ['C'] = @"\Device\HarddiskVolume1" };
        var mapper = new VolumeMapper(devices.Query);

        Assert.Equal(kernelPath, mapper.ToDosPath(kernelPath));
    }

    [Theory]
    [InlineData(@"\DEVICE\HARDDISKVOLUME5\Docs\A.TXT")]
    [InlineData(@"\device\harddiskvolume5\Docs\A.TXT")]
    [InlineData(@"\Device\HarddiskVolume5\Docs\A.TXT")]
    public void ToDosPath_DeviceNameCase_DoesNotMatter(string kernelPath)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(@"E:\Docs\A.TXT", mapper.ToDosPath(kernelPath));
    }

    [Fact]
    public void ToDosPath_QueryReportingLowerCaseDevice_StillMaps()
    {
        var devices = new FakeDosDevices { ['E'] = @"\device\harddiskvolume5" };
        var mapper = new VolumeMapper(devices.Query);

        Assert.Equal(@"E:\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume5\x.txt"));
        Assert.Equal(@"\device\harddiskvolume5", mapper.GetDevicePath('E'));
    }

    [Theory]
    [InlineData(@"E:\photos\a.jpg")]
    [InlineData(@"e:\photos\a.jpg")]
    [InlineData("E:")]
    public void ToDosPath_DosPath_IsReturnedUnchanged(string path)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(path, mapper.ToDosPath(path));
    }

    [Theory]
    [InlineData(@"\??\E:\x\y.txt", @"E:\x\y.txt")]
    [InlineData(@"\\?\E:\x\y.txt", @"E:\x\y.txt")]
    [InlineData(@"\??\Q:\not-mapped.txt", @"Q:\not-mapped.txt")]
    [InlineData(@"\??\UNC\srv\share\x.txt", @"\\srv\share\x.txt")]
    [InlineData(@"\\?\UNC\srv\share\x.txt", @"\\srv\share\x.txt")]
    [InlineData(@"\??\unc\srv\share", @"\\srv\share")]
    public void ToDosPath_Win32NamespacePrefix_IsStripped(string kernelPath, string expected)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(expected, mapper.ToDosPath(kernelPath));
    }

    [Theory]
    [InlineData(@"\??\Volume{6c1a0b2e-1d3f-4e5a-9b7c-8d9e0f1a2b3c}\x.txt")]
    [InlineData(@"\??\UNC")]
    [InlineData(@"\??\")]
    [InlineData(@"\\?\")]
    public void ToDosPath_Win32NamespaceWithoutDriveOrUnc_IsReturnedUnchanged(string kernelPath)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(kernelPath, mapper.ToDosPath(kernelPath));
    }

    [Theory]
    [InlineData(@"\Device\Mup\srv\share\x.txt", @"\\srv\share\x.txt")]
    [InlineData(@"\device\mup\srv\share", @"\\srv\share")]
    public void ToDosPath_MupPath_BecomesUncPath(string kernelPath, string expected)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(expected, mapper.ToDosPath(kernelPath));
    }

    [Theory]
    [InlineData(@"\Device\Mup\;LanmanRedirector\;Z:0000000000012345\srv\share\x.txt")]
    [InlineData(@"\Device\Mup")]
    [InlineData(@"\Device\Mup\")]
    public void ToDosPath_MupRedirectorPrefixOrNoShare_IsReturnedUnchanged(string kernelPath)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(kernelPath, mapper.ToDosPath(kernelPath));
    }

    [Theory]
    [InlineData(@"\Device\NamedPipe\pipe-1")]
    [InlineData(@"\Device\HarddiskVolume99\x.txt")]
    [InlineData(@"\Device\HarddiskVolumeShadowCopy3\x.txt")]
    [InlineData(@"\Device\")]
    [InlineData(@"\Device")]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData(@"relative\path.txt")]
    [InlineData("")]
    public void ToDosPath_UnmappablePath_IsReturnedUnchanged(string kernelPath)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(kernelPath, mapper.ToDosPath(kernelPath));
    }

    [Fact]
    public void ToDosPath_Null_BecomesEmpty()
    {
        // TraceEvent stores the mapper's result as the file name, so it must never be null.
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal("", mapper.ToDosPath(null));
    }

    [Theory]
    [InlineData(@"\SystemRoot\System32\ntdll.dll", @"\System32\ntdll.dll")]
    [InlineData(@"\systemroot\explorer.exe", @"\explorer.exe")]
    [InlineData(@"\SystemRoot\", @"\")]
    public void ToDosPath_SystemRootPath_BecomesTheWindowsFolder(string kernelPath, string rest)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + rest, mapper.ToDosPath(kernelPath));
    }

    [Theory]
    [InlineData(@"\Device\LanmanRedirector\srv\share\x.txt", @"\\srv\share\x.txt")]
    [InlineData(@"\Device\LanmanRedirector\;Z:0000000000012345\srv\share", @"\Device\LanmanRedirector\;Z:0000000000012345\srv\share")]
    public void ToDosPath_LanmanRedirectorPath_BecomesUncPathUnlessItIsAMappedDrive(string kernelPath, string expected)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(expected, mapper.ToDosPath(kernelPath));
    }

    [Fact]
    public void SubstDrive_IsNotTakenAsAVolume()
    {
        var devices = new FakeDosDevices
        {
            ['C'] = @"\Device\HarddiskVolume3",
            ['S'] = @"\??\C:\projects",
        };
        var mapper = new VolumeMapper(devices.Query);

        Assert.Null(mapper.GetDevicePath('S'));
        Assert.Equal(@"C:\projects\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume3\projects\x.txt"));
    }

    [Fact]
    public void VolumeWithSeveralLetters_MapsToTheFirstLetter()
    {
        var devices = new FakeDosDevices
        {
            ['K'] = @"\Device\HarddiskVolume5",
            ['E'] = @"\Device\HarddiskVolume5",
        };
        var mapper = new VolumeMapper(devices.Query);

        Assert.Equal(@"E:\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume5\x.txt"));
        Assert.Equal(@"\Device\HarddiskVolume5", mapper.GetDevicePath('K'));
        Assert.Equal(@"\Device\HarddiskVolume5", mapper.GetDevicePath('E'));
    }

    [Theory]
    [InlineData('E')]
    [InlineData('e')]
    public void GetDevicePath_MappedLetter_ReturnsTheDevice(char letter)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Equal(@"\Device\HarddiskVolume5", mapper.GetDevicePath(letter));
    }

    [Theory]
    [InlineData('Q')]
    [InlineData('1')]
    [InlineData('\0')]
    [InlineData('@')]
    [InlineData('[')]
    [InlineData('\u00E9')]
    public void GetDevicePath_UnmappedOrInvalidLetter_ReturnsNull(char letter)
    {
        var mapper = new VolumeMapper(TypicalDevices().Query);

        Assert.Null(mapper.GetDevicePath(letter));
    }

    [Fact]
    public void QueryThrowingForOneLetter_LeavesOnlyThatLetterUnmapped()
    {
        FakeDosDevices devices = TypicalDevices();
        devices['D'] = @"\Device\HarddiskVolume4";
        devices.ThrowFor = 'D';

        var mapper = new VolumeMapper(devices.Query);

        Assert.Null(mapper.GetDevicePath('D'));
        Assert.Equal(@"\Device\HarddiskVolume3", mapper.GetDevicePath('C'));
        Assert.Equal(@"E:\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume5\x.txt"));
        Assert.Equal(@"\Device\HarddiskVolume4\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume4\x.txt"));

        mapper.Refresh();

        Assert.Equal(@"E:\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume5\x.txt"));
    }

    [Fact]
    public void Refresh_PicksUpANewlyMountedVolume()
    {
        FakeDosDevices devices = TypicalDevices();
        var mapper = new VolumeMapper(devices.Query);
        devices['G'] = @"\Device\HarddiskVolume12";

        Assert.Null(mapper.GetDevicePath('G')); // the mapping is a snapshot until refreshed

        mapper.Refresh();

        Assert.Equal(@"\Device\HarddiskVolume12", mapper.GetDevicePath('G'));
        Assert.Equal(@"G:\new.txt", mapper.ToDosPath(@"\Device\HarddiskVolume12\new.txt"));
    }

    [Fact]
    public void Refresh_ForgetsARemovedVolume()
    {
        FakeDosDevices devices = TypicalDevices();
        var mapper = new VolumeMapper(devices.Query);
        devices.Remove('E');

        mapper.Refresh();

        Assert.Null(mapper.GetDevicePath('E'));
        Assert.Equal(@"\Device\HarddiskVolume5\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume5\x.txt"));
    }

    /// <summary>
    /// The first miss after construction refreshes: the last-refresh tick starts at 0 and
    /// Environment.TickCount64 (milliseconds since boot) is far beyond the 2 s interval.
    /// </summary>
    [Theory]
    [InlineData("HarddiskVolume12")]
    [InlineData("harddiskvolume12")]
    [InlineData("CdRom1")]
    [InlineData("Floppy0")]
    public void ToDosPath_MissOnVolumeDevice_RefreshesAndFindsTheNewVolume(string deviceName)
    {
        FakeDosDevices devices = TypicalDevices();
        var mapper = new VolumeMapper(devices.Query);
        Assert.Equal(QueriesPerRefresh, devices.Calls);

        devices['G'] = @"\Device\" + deviceName; // plugged in after the mapper was built

        Assert.Equal(@"G:\x.txt", mapper.ToDosPath($@"\Device\{deviceName}\x.txt"));
        Assert.Equal(2 * QueriesPerRefresh, devices.Calls);
    }

    /// <summary>Relies on the two lookups running within 2 s of each other.</summary>
    [Fact]
    public void ToDosPath_AutomaticRefresh_IsRateLimited()
    {
        FakeDosDevices devices = TypicalDevices();
        var mapper = new VolumeMapper(devices.Query);

        Assert.Equal(@"\Device\HarddiskVolume12\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume12\x.txt"));
        Assert.Equal(2 * QueriesPerRefresh, devices.Calls); // the first miss refreshed

        devices['H'] = @"\Device\HarddiskVolume13";
        Assert.Equal(@"\Device\HarddiskVolume13\y.txt", mapper.ToDosPath(@"\Device\HarddiskVolume13\y.txt"));
        Assert.Equal(2 * QueriesPerRefresh, devices.Calls); // too soon for another automatic refresh

        mapper.Refresh(); // an explicit refresh is never rate-limited

        Assert.Equal(3 * QueriesPerRefresh, devices.Calls);
        Assert.Equal(@"H:\y.txt", mapper.ToDosPath(@"\Device\HarddiskVolume13\y.txt"));
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolumeShadowCopy3\x.txt")]
    [InlineData(@"\Device\harddiskvolumeshadowcopy3\x.txt")]
    [InlineData(@"\Device\NamedPipe\pipe-1")]
    [InlineData(@"\Device\Mup\srv\share\x.txt")]
    [InlineData(@"\Device\Afd\Endpoint")]
    [InlineData(@"\Device\LanmanRedirector\srv\share\x.txt")]
    public void ToDosPath_MissOnNonVolumeDevice_DoesNotRefresh(string kernelPath)
    {
        FakeDosDevices devices = TypicalDevices();
        var mapper = new VolumeMapper(devices.Query);

        _ = mapper.ToDosPath(kernelPath);

        Assert.Equal(QueriesPerRefresh, devices.Calls);

        // It did not use up the rate limit either: the next volume miss still refreshes.
        devices['G'] = @"\Device\HarddiskVolume12";
        Assert.Equal(@"G:\x.txt", mapper.ToDosPath(@"\Device\HarddiskVolume12\x.txt"));
        Assert.Equal(2 * QueriesPerRefresh, devices.Calls);
    }

    private static FakeDosDevices TypicalDevices() => new()
    {
        ['C'] = @"\Device\HarddiskVolume3",
        ['E'] = @"\Device\HarddiskVolume5",
    };

    /// <summary>Stands in for QueryDosDevice: a letter → device table that counts the lookups.</summary>
    private sealed class FakeDosDevices
    {
        private readonly Dictionary<char, string> _devices = new();

        public int Calls { get; private set; }

        public List<char> Queried { get; } = new();

        /// <summary>A letter whose lookup throws, like a QueryDosDevice failure.</summary>
        public char? ThrowFor { get; set; }

        public string this[char letter]
        {
            get => _devices[letter];
            set => _devices[letter] = value;
        }

        public void Remove(char letter) => _devices.Remove(letter);

        public string? Query(char letter)
        {
            Calls++;
            Queried.Add(letter);
            if (letter == ThrowFor)
                throw new InvalidOperationException($"QueryDosDevice failed for {letter}:");
            return _devices.TryGetValue(letter, out string? device) ? device : null;
        }
    }
}
