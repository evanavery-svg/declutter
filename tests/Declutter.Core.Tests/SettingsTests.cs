using Declutter.Core.Settings;

namespace Declutter.Core.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "declutter-settings-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Missing_GivesDefaults()
    {
        var s = new SettingsStore(File).Load();
        Assert.Equal("System", s.Theme);
        Assert.True(s.CheckForUpdates);
        Assert.True(s.KeepHistory);
        Assert.Null(s.ProtectedApiKey);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new SettingsStore(File);
        store.Save(new AppSettings { Theme = "Dark", CheckForUpdates = false, ProtectedApiKey = "abc=" });
        var s = store.Load();
        Assert.Equal("Dark", s.Theme);
        Assert.False(s.CheckForUpdates);
        Assert.Equal("abc=", s.ProtectedApiKey);
        Assert.False(System.IO.File.Exists(File + ".tmp"));
    }

    [Fact]
    public void Damaged_GivesDefaults()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ not json");
        Assert.Equal("System", new SettingsStore(File).Load().Theme);
    }

    [Fact]
    public void UnknownFields_AreIgnored_AndMissingOnesDefault()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, """{ "Theme": "Light", "SomethingFromTheFuture": 42 }""");
        var s = new SettingsStore(File).Load();
        Assert.Equal("Light", s.Theme);
        Assert.True(s.CheckForUpdates);
    }
}
