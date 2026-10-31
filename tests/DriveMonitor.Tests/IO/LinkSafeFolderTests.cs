using DriveMonitor.Core.IO;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.IO;

/// <summary>
/// Opening and renaming files without following links, against the links a standard user can plant in a
/// folder the elevated app writes to. The victim files and folders must stay untouched.
/// </summary>
public sealed class LinkSafeFolderTests
{
    [Fact]
    public void OpenFile_CreatesTheFileInTheFolder_AndReopensItToAppend()
    {
        using var temp = new TempDirectory();
        using LinkSafeFolder folder = LinkSafeFolder.Open(temp.FullPath);

        using (FileStream log = folder.OpenFile("errors.log", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            log.Write("one "u8);
        using (FileStream log = folder.OpenFile("errors.log", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
        {
            log.Seek(0, SeekOrigin.End);
            log.Write("two"u8);
        }

        Assert.Equal("one two", File.ReadAllText(temp.PathOf("errors.log")));
    }

    [Fact]
    public void OpenFile_GivesAStreamThatTruncatesTheOpenFile()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.PathOf("errors.log"), "a long old log");
        using LinkSafeFolder folder = LinkSafeFolder.Open(temp.FullPath);

        using (FileStream log = folder.OpenFile("errors.log", FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            log.SetLength(0);
            log.Write("new"u8);
        }

        Assert.Equal("new", File.ReadAllText(temp.PathOf("errors.log")));
    }

    [Fact]
    public void OpenFile_InOpenMode_DoesNotCreateAMissingFile()
    {
        using var temp = new TempDirectory();
        using LinkSafeFolder folder = LinkSafeFolder.Open(temp.FullPath);

        Assert.Throws<IOException>(() => folder.OpenFile("missing.json", FileMode.Open, FileAccess.Read, FileShare.Read));
        Assert.False(File.Exists(temp.PathOf("missing.json")));
    }

    [Theory]
    [InlineData(FileMode.Create)]
    [InlineData(FileMode.Truncate)]
    [InlineData(FileMode.Append)]
    public void OpenFile_RejectsModesThatTruncateBeforeTheFileIsChecked(FileMode mode)
    {
        using var temp = new TempDirectory();
        using LinkSafeFolder folder = LinkSafeFolder.Open(temp.FullPath);

        Assert.Throws<ArgumentOutOfRangeException>(() => folder.OpenFile("settings.json", mode, FileAccess.Write, FileShare.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"sub\settings.json")]
    [InlineData("sub/settings.json")]
    [InlineData(@"..\settings.json")]
    [InlineData("settings.json:stream")]
    public void OpenFile_AcceptsOnlyAFileName(string name)
    {
        using var temp = new TempDirectory();
        using LinkSafeFolder folder = LinkSafeFolder.Open(temp.FullPath);

        Assert.Throws<ArgumentException>(() => folder.OpenFile(name, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None));
    }

    [Fact]
    public void Open_RefusesAFolderThatIsAJunction()
    {
        using var temp = new TempDirectory();
        string victim = Directory.CreateDirectory(temp.PathOf("victim")).FullName;
        string junction = temp.PathOf("DriveMonitor");
        using IDisposable planted = FileLinks.CreateJunction(junction, victim);

        Assert.True(Directory.Exists(junction)); // a path check that follows the junction sees a folder
        Assert.Throws<IOException>(() => LinkSafeFolder.Open(junction));
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
    }

    [Fact]
    public void Open_RefusesAFile()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.PathOf("DriveMonitor"), "");

        Assert.Throws<IOException>(() => LinkSafeFolder.Open(temp.PathOf("DriveMonitor")));
    }

    [Fact]
    public void Open_KeepsTheFolderFromBeingRenamedUntilDisposed()
    {
        using var temp = new TempDirectory();
        string path = Directory.CreateDirectory(temp.PathOf("DriveMonitor")).FullName;

        // Renaming it away would let a junction take its name.
        using (LinkSafeFolder.Open(path))
            Assert.Throws<IOException>(() => Directory.Move(path, temp.PathOf("moved")));

        Directory.Move(path, temp.PathOf("moved"));
    }

    [Fact]
    public void OpenFile_RefusesAJunctionWithTheFileName()
    {
        using var temp = new TempDirectory();
        string victim = Directory.CreateDirectory(temp.PathOf("victim")).FullName;
        string app = Directory.CreateDirectory(temp.PathOf("DriveMonitor")).FullName;
        using IDisposable planted = FileLinks.CreateJunction(Path.Combine(app, "errors.log"), victim);
        using LinkSafeFolder folder = LinkSafeFolder.Open(app);

        Assert.Throws<IOException>(() => folder.OpenFile("errors.log", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read));
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
    }

    [Fact]
    public void OpenFile_RefusesAHardLinkToAnotherFile()
    {
        using var temp = new TempDirectory();
        string victim = temp.PathOf("victim.txt");
        File.WriteAllText(victim, "keep me");
        string app = Directory.CreateDirectory(temp.PathOf("DriveMonitor")).FullName;
        FileLinks.CreateHardLink(Path.Combine(app, "settings.json.tmp"), victim);
        using LinkSafeFolder folder = LinkSafeFolder.Open(app);

        Assert.Throws<IOException>(
            () => folder.OpenFile("settings.json.tmp", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, allowRename: true));
        Assert.Equal("keep me", File.ReadAllText(victim));
    }

    [Fact]
    public void OpenFile_RefusesASymbolicLinkToAnotherFile()
    {
        using var temp = new TempDirectory();
        string victim = temp.PathOf("victim.txt");
        File.WriteAllText(victim, "keep me");
        string app = Directory.CreateDirectory(temp.PathOf("DriveMonitor")).FullName;
        if (!FileLinks.TryCreateFileSymbolicLink(Path.Combine(app, "errors.log"), victim))
            return; // Windows refuses symbolic links without administrator rights or Developer Mode; CI runs elevated.
        using LinkSafeFolder folder = LinkSafeFolder.Open(app);

        Assert.Throws<IOException>(() => folder.OpenFile("errors.log", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read));
        Assert.Equal("keep me", File.ReadAllText(victim));
    }

    [Fact]
    public void Rename_ReplacesTheFile_AndLeavesNoTemporaryFile()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.PathOf("settings.json"), "old");
        using LinkSafeFolder folder = LinkSafeFolder.Open(temp.FullPath);

        using (FileStream temporary = folder.OpenFile("settings.json.tmp", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, allowRename: true))
        {
            temporary.Write("new"u8);
            folder.Rename(temporary, "settings.json");
        }

        Assert.Equal("new", File.ReadAllText(temp.PathOf("settings.json")));
        Assert.False(File.Exists(temp.PathOf("settings.json.tmp")));
    }

    [Fact]
    public void Rename_CreatesTheFileWhenItIsMissing()
    {
        using var temp = new TempDirectory();
        using LinkSafeFolder folder = LinkSafeFolder.Open(temp.FullPath);

        using (FileStream temporary = folder.OpenFile("a.tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None, allowRename: true))
        {
            temporary.Write("x"u8);
            folder.Rename(temporary, "a");
        }

        Assert.Equal("x", File.ReadAllText(temp.PathOf("a")));
    }

    [Fact]
    public void Rename_RefusesToReplaceAJunction()
    {
        using var temp = new TempDirectory();
        string victim = Directory.CreateDirectory(temp.PathOf("victim")).FullName;
        string app = Directory.CreateDirectory(temp.PathOf("DriveMonitor")).FullName;
        using IDisposable planted = FileLinks.CreateJunction(Path.Combine(app, "settings.json"), victim);
        using LinkSafeFolder folder = LinkSafeFolder.Open(app);
        using FileStream temporary = folder.OpenFile("settings.json.tmp", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, allowRename: true);

        Assert.Throws<IOException>(() => folder.Rename(temporary, "settings.json"));
        Assert.True(File.Exists(Path.Combine(app, "settings.json.tmp")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
    }

    [Fact]
    public void Rename_RefusesToReplaceAHardLinkedFile()
    {
        using var temp = new TempDirectory();
        string victim = temp.PathOf("victim.txt");
        File.WriteAllText(victim, "keep me");
        string app = Directory.CreateDirectory(temp.PathOf("DriveMonitor")).FullName;
        FileLinks.CreateHardLink(Path.Combine(app, "settings.json"), victim);
        using LinkSafeFolder folder = LinkSafeFolder.Open(app);
        using FileStream temporary = folder.OpenFile("settings.json.tmp", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, allowRename: true);
        temporary.Write("new"u8);

        Assert.Throws<IOException>(() => folder.Rename(temporary, "settings.json"));
        Assert.Equal("keep me", File.ReadAllText(victim));
    }
}
