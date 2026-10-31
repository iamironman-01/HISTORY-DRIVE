using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using DriveMonitor.Cli;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.Cli;

/// <summary>
/// The self-test's folder, which an elevated process uses in a drive root other users can write to: it must
/// never adopt, follow or delete anything another user put there. Assumes the temp folder is not itself
/// reached through a junction.
/// </summary>
public sealed class TestFolderTests
{
    [Fact]
    public void Create_MakesANewFolderWithARandomName()
    {
        using var temp = new TempDirectory();

        using TestFolder first = TestFolder.Create(temp.FullPath, securityDescriptor: null);
        using TestFolder second = TestFolder.Create(temp.FullPath, securityDescriptor: null);

        Assert.True(Directory.Exists(first.FullPath));
        Assert.NotEqual(first.FullPath, second.FullPath);
        string name = Path.GetFileName(first.FullPath);
        Assert.StartsWith(TestFolder.NamePrefix, name);
        Assert.Equal(TestFolder.NamePrefix.Length + 16, name.Length);
    }

    [Fact]
    public void Create_ExistingFolder_IsNotAdoptedAndKeepsItsContent()
    {
        using var temp = new TempDirectory();
        string existing = Directory.CreateDirectory(temp.PathOf("taken")).FullName;
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");

        IOException error = Assert.Throws<IOException>(() => TestFolder.Create(temp.FullPath, "taken", securityDescriptor: null));

        Assert.Contains("already exists", error.Message);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(existing, "keep.txt")));
    }

    [Fact]
    public void Create_ExistingJunction_IsNotAdopted()
    {
        using var temp = new TempDirectory();
        string target = Directory.CreateDirectory(temp.PathOf("target")).FullName;
        using Junction junction = Junction.Create(temp.PathOf("taken"), target);

        Assert.Throws<IOException>(() => TestFolder.Create(temp.FullPath, "taken", securityDescriptor: null));

        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.True(Directory.Exists(temp.PathOf("taken")));
    }

    [Fact]
    public void Create_InAFolderReachedThroughAJunction_IsRefusedAndLeavesNothing()
    {
        using var temp = new TempDirectory();
        string real = Directory.CreateDirectory(temp.PathOf("real")).FullName;
        using Junction link = Junction.Create(temp.PathOf("link"), real);

        IOException error = Assert.Throws<IOException>(() => TestFolder.Create(link.Path, securityDescriptor: null));

        Assert.Contains("junction", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }

    [Fact]
    public void PrivateSecurity_LetsOnlyAdministratorsAndSystemChangeTheFolderAndItsFiles()
    {
        // Checked on the descriptor itself: a test process without elevation could not delete a folder that
        // has it (it grants that process not even SYNCHRONIZE).
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(TestFolder.PrivateSecurity);
        List<FileSystemAccessRule> rules = RulesOf(security);

        Assert.True(security.AreAccessRulesProtected); // nothing inherited from the drive root
        Assert.Equal(3, rules.Count);
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        });
        Assert.Equal(FileSystemRights.FullControl, RightsOf(rules, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.Equal(FileSystemRights.FullControl, RightsOf(rules, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.Equal(FileSystemRights.ReadPermissions, RightsOf(rules, new SecurityIdentifier("S-1-3-4"))); // OWNER RIGHTS
    }

    [Fact]
    public void Create_AppliesTheAccessListAsItCreatesTheFolder()
    {
        using var temp = new TempDirectory();
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;

        using TestFolder folder = TestFolder.Create(temp.FullPath, $"D:P(A;OICI;FA;;;{me.Value})");

        DirectorySecurity security = new DirectoryInfo(folder.FullPath).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        FileSystemAccessRule rule = Assert.Single(RulesOf(security));
        Assert.Equal(me, rule.IdentityReference);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }

    [Fact]
    public void WhileInUse_TheFolderCannotBeRenamedOrDeleted()
    {
        using var temp = new TempDirectory();
        using TestFolder folder = TestFolder.Create(temp.FullPath, securityDescriptor: null);

        Assert.Throws<IOException>(() => Directory.Move(folder.FullPath, temp.PathOf("swapped")));
        Assert.Throws<IOException>(() => Directory.Delete(folder.FullPath));

        Assert.True(Directory.Exists(folder.FullPath));
    }

    [Fact]
    public void Remove_EverythingCreated_RemovesTheFolder()
    {
        using var temp = new TempDirectory();
        TestFolder folder = TestFolder.Create(temp.FullPath, securityDescriptor: null);
        string alpha = folder.PathOf("alpha.bin");
        string beta = folder.PathOf("beta.bin");
        string scratch = folder.PathOf("scratch.tmp");
        string gone = folder.PathOf("gone.bin");
        using (FileStream stream = folder.CreateNew(alpha, FileAccess.Write, FileShare.None))
            stream.WriteByte(1);
        folder.Move(alpha, beta);
        using (folder.CreateNew(scratch, FileAccess.Write, FileShare.None, FileOptions.DeleteOnClose))
        {
        }

        folder.Forget(scratch);
        using (folder.CreateNew(gone, FileAccess.Write, FileShare.None))
        {
        }

        folder.Delete(gone);

        Assert.Null(folder.Remove());
        Assert.False(Directory.Exists(folder.FullPath));
        Assert.Null(folder.Remove());
    }

    [Fact]
    public void Remove_LeavesWhatTheTestDidNotCreate()
    {
        using var temp = new TempDirectory();
        TestFolder folder = TestFolder.Create(temp.FullPath, securityDescriptor: null);
        string mine = folder.PathOf("alpha.bin");
        using (folder.CreateNew(mine, FileAccess.Write, FileShare.None))
        {
        }

        string foreign = folder.PathOf("someone-else.txt");
        File.WriteAllText(foreign, "not the self-test's");

        string? note = folder.Remove();

        Assert.NotNull(note);
        Assert.Contains("1 item(s)", note);
        Assert.False(File.Exists(mine));
        Assert.Equal("not the self-test's", File.ReadAllText(foreign));
    }

    [Fact]
    public void Remove_NeverFollowsAJunctionInside()
    {
        using var temp = new TempDirectory();
        string precious = Directory.CreateDirectory(temp.PathOf("precious")).FullName;
        File.WriteAllText(Path.Combine(precious, "keep.txt"), "keep");
        TestFolder folder = TestFolder.Create(temp.FullPath, securityDescriptor: null);
        using Junction junction = Junction.Create(folder.PathOf("sub"), precious);

        string? note = folder.Remove();

        Assert.NotNull(note);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(precious, "keep.txt")));
    }

    [Fact]
    public void CreateNew_NameTakenFirst_FailsAndTheFileIsLeftAlone()
    {
        // FAT and exFAT have no access lists, so another user can claim a name inside the folder first.
        using var temp = new TempDirectory();
        TestFolder folder = TestFolder.Create(temp.FullPath, securityDescriptor: null);
        string alpha = folder.PathOf("alpha.bin");
        File.WriteAllText(alpha, "planted");

        Assert.Throws<IOException>(() => folder.CreateNew(alpha, FileAccess.Write, FileShare.None));
        Assert.Throws<InvalidOperationException>(() => folder.Delete(alpha));
        Assert.Throws<InvalidOperationException>(() => folder.Move(alpha, folder.PathOf("beta.bin")));

        Assert.NotNull(folder.Remove());
        Assert.Equal("planted", File.ReadAllText(alpha));
    }

    [Fact]
    public void CreateNew_OutsideTheFolder_IsRefused()
    {
        using var temp = new TempDirectory();
        using TestFolder folder = TestFolder.Create(temp.FullPath, securityDescriptor: null);

        Assert.Throws<ArgumentException>(() => folder.CreateNew(temp.PathOf("outside.bin"), FileAccess.Write, FileShare.None));
        Assert.False(File.Exists(temp.PathOf("outside.bin")));
    }

    private static List<FileSystemAccessRule> RulesOf(DirectorySecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

    private static FileSystemRights RightsOf(List<FileSystemAccessRule> rules, SecurityIdentifier sid) =>
        Assert.Single(rules, rule => rule.IdentityReference.Equals(sid)).FileSystemRights;

    /// <summary>
    /// A junction, which needs no privilege (unlike a symbolic link), so any user can plant one. Disposing
    /// removes the link itself: .NET's recursive delete reports an error for junctions and would leave the
    /// temporary folder behind.
    /// </summary>
    private sealed class Junction : IDisposable
    {
        private Junction(string path) => Path = path;

        public string Path { get; }

        public static Junction Create(string link, string target)
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
                start.ArgumentList.Add(argument);

            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"mklink /J failed: {output}");
            return new Junction(link);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path);
        }
    }
}
