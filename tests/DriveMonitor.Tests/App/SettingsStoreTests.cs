using System.Globalization;
using DriveMonitor.App.Services;
using DriveMonitor.Core.IO;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.App;

/// <summary>
/// The app's settings file: loading values a damaged or hand-edited file may contain, and saving it or setting it
/// aside without following links.
/// </summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public void Load_KeepsTheSavedWindowBounds()
    {
        using var temp = new TempDirectory();
        AppSettings settings = LoadWindow(temp, left: -1920, top: 40.5, width: 1280, height: 800);

        WindowBounds window = Assert.IsType<WindowBounds>(settings.Window);
        Assert.Equal(-1920, window.Left);
        Assert.Equal(40.5, window.Top);
        Assert.Equal(1280, window.Width);
        Assert.Equal(800, window.Height);
    }

    [Theory]
    [InlineData(0, 0, 1e300, 1e300)] // WPF throws for a window size past Int32.MaxValue
    [InlineData(0, 0, 3e9, 800)]
    [InlineData(0, 0, 1280, 3e9)]
    [InlineData(0, 0, 100_001, 800)]
    [InlineData(1e300, 0, 1280, 800)] // and for a position past it
    [InlineData(0, -3e9, 1280, 800)]
    [InlineData(1_000_001, 0, 1280, 800)]
    [InlineData(0, 0, 0, 800)]
    [InlineData(0, 0, 1280, -1)]
    public void Load_DropsWindowBoundsNoScreenCanHave(double left, double top, double width, double height)
    {
        using var temp = new TempDirectory();

        Assert.Null(LoadWindow(temp, left, top, width, height).Window);
    }

    [Fact]
    public void Load_KeepsTheLargestPlausibleBounds()
    {
        using var temp = new TempDirectory();

        Assert.NotNull(LoadWindow(temp, left: -1_000_000, top: 1_000_000, width: 100_000, height: 100_000).Window);
    }

    [Fact]
    public void TrySetAside_RenamesTheFileSoTheNextLoadUsesDefaults()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.PathOf("settings.json"));
        File.WriteAllText(store.FilePath, """{ "MaxRows": 5000 }""");
        File.WriteAllText(temp.PathOf("settings.bad.json"), "an older bad file");

        string? moved = store.TrySetAside();

        Assert.Equal(temp.PathOf("settings.bad.json"), moved);
        Assert.False(File.Exists(store.FilePath));
        Assert.Equal("""{ "MaxRows": 5000 }""", File.ReadAllText(moved!));
        Assert.Equal(AppSettings.DefaultMaxRows, store.Load().MaxRows);
    }

    [Fact]
    public void TheAppsFolders_AreBelowTheTrustedRoot()
    {
        // Record log, Open logs folder, the settings and the error log open their folders below this root.
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SettingsStore.TrustedRoot);
        Assert.True(LinkSafeFolder.IsBelow(SettingsStore.TrustedRoot, SettingsStore.AppDataDirectory));
        Assert.True(LinkSafeFolder.IsBelow(SettingsStore.TrustedRoot, Path.Combine(SettingsStore.LogsDirectory, "activity-20261101-120000.csv")));
    }

    [Fact]
    public void TrySetAside_WithoutAFile_DoesNothing()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.PathOf("settings.json"));

        Assert.Null(store.TrySetAside());
        Assert.Empty(Directory.GetFiles(temp.FullPath));
    }

    [Fact]
    public void TrySave_BelowATrustedRoot_CreatesTheFolderAndWritesTheFile()
    {
        using var temp = new TempDirectory();
        string app = temp.PathOf("DriveMonitor");
        var store = new SettingsStore(Path.Combine(app, "settings.json"), trustedRoot: temp.FullPath);

        Assert.True(store.TrySave(new AppSettings { MaxRows = 5000 }));

        Assert.Equal(new[] { store.FilePath }, Directory.EnumerateFileSystemEntries(app));
        Assert.Equal(5000, store.Load().MaxRows);
    }

    [Fact]
    public void TrySave_BelowATrustedRoot_RefusesAJunctionInPlaceOfTheFolder()
    {
        using var temp = new TempDirectory();
        string root = Directory.CreateDirectory(temp.PathOf("Local")).FullName;
        string victim = Directory.CreateDirectory(temp.PathOf("victim")).FullName;
        string app = Path.Combine(root, "DriveMonitor");
        using IDisposable planted = FileLinks.CreateJunction(app, victim);
        var store = new SettingsStore(Path.Combine(app, "settings.json"), trustedRoot: root);

        Assert.False(store.TrySave(new AppSettings()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
    }

    [Fact]
    public void TrySetAside_BelowATrustedRoot_RenamesTheFile()
    {
        using var temp = new TempDirectory();
        string app = Directory.CreateDirectory(temp.PathOf("DriveMonitor")).FullName;
        var store = new SettingsStore(Path.Combine(app, "settings.json"), trustedRoot: temp.FullPath);
        File.WriteAllText(store.FilePath, """{ "MaxRows": 5000 }""");

        Assert.Equal(Path.Combine(app, "settings.bad.json"), store.TrySetAside());
        Assert.Equal(new[] { Path.Combine(app, "settings.bad.json") }, Directory.EnumerateFileSystemEntries(app));
    }

    [Fact]
    public void TrySetAside_BelowATrustedRoot_RefusesAJunctionInPlaceOfTheFolder()
    {
        // File.Move followed it: the elevated app moved and replaced whatever files the junction's target had of
        // those names.
        using var temp = new TempDirectory();
        string root = Directory.CreateDirectory(temp.PathOf("Local")).FullName;
        string victim = Directory.CreateDirectory(temp.PathOf("victim")).FullName;
        File.WriteAllText(Path.Combine(victim, "settings.json"), "keep me");
        File.WriteAllText(Path.Combine(victim, "settings.bad.json"), "keep me too");
        string app = Path.Combine(root, "DriveMonitor");
        using IDisposable planted = FileLinks.CreateJunction(app, victim);
        var store = new SettingsStore(Path.Combine(app, "settings.json"), trustedRoot: root);

        Assert.Null(store.TrySetAside());
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(victim, "settings.json")));
        Assert.Equal("keep me too", File.ReadAllText(Path.Combine(victim, "settings.bad.json")));
    }

    private static AppSettings LoadWindow(TempDirectory temp, double left, double top, double width, double height)
    {
        var store = new SettingsStore(temp.PathOf("settings.json"));
        string json = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{ "Window": { "Left": {{left:R}}, "Top": {{top:R}}, "Width": {{width:R}}, "Height": {{height:R}} } }""");
        File.WriteAllText(store.FilePath, json);
        return store.Load();
    }
}
