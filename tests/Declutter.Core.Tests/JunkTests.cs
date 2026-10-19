using Declutter.Core.Junk;

namespace Declutter.Core.Tests;

public class JunkTests : IDisposable
{
    private readonly FakeMachine _m = new();
    public void Dispose() => _m.Dispose();

    private static readonly DateTime InThreeDays = DateTime.UtcNow.AddDays(3);

    private FileJunkSource TempSource(bool removeEmpty = true) =>
        new("t", "Temp", "", [new JunkRule(_m.Folders.TempDir) { MinAge = TimeSpan.FromDays(2), RemoveEmptyFolders = removeEmpty }]);

    [Fact]
    public void Scan_CountsOldFilesRecursively()
    {
        _m.WriteFile(Path.Combine(_m.Folders.TempDir, "a.tmp"), 1000);
        _m.WriteFile(Path.Combine(_m.Folders.TempDir, "sub", "b.tmp"), 500);

        var result = TempSource().Scan(_m.Context(InThreeDays), default);

        Assert.Equal(2, result.Count);
        Assert.Equal(1500, result.Bytes);
    }

    [Fact]
    public void Scan_SkipsRecentlyUsedFiles()
    {
        _m.WriteFile(Path.Combine(_m.Folders.TempDir, "installer-in-progress.tmp"), 1000);

        var result = TempSource().Scan(_m.Context(DateTime.UtcNow), default);

        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void Clean_DeletesFiles_KeepsRoot_PrunesEmptyFolders()
    {
        _m.WriteFile(Path.Combine(_m.Folders.TempDir, "a.tmp"));
        _m.WriteFile(Path.Combine(_m.Folders.TempDir, "deep", "er", "b.tmp"));
        var source = TempSource();
        var ctx = _m.Context(InThreeDays);

        var cleaned = source.Clean(source.Scan(ctx, default), ctx, null, default);

        Assert.Equal(2, cleaned.Deleted);
        Assert.Equal(200, cleaned.BytesFreed);
        Assert.True(Directory.Exists(_m.Folders.TempDir));
        Assert.False(Directory.Exists(Path.Combine(_m.Folders.TempDir, "deep")));
    }

    [Fact]
    public void Clean_DoesNotRemoveFolders_WhenRuleSaysNot()
    {
        _m.WriteFile(Path.Combine(_m.Folders.TempDir, "deep", "b.tmp"));
        var source = TempSource(removeEmpty: false);
        var ctx = _m.Context(InThreeDays);

        source.Clean(source.Scan(ctx, default), ctx, null, default);

        Assert.True(Directory.Exists(Path.Combine(_m.Folders.TempDir, "deep")));
    }

    [Fact]
    public void Scan_And_Clean_NeverFollowSymlinkedFolders()
    {
        string precious = _m.WriteFile(Path.Combine(_m.Folders.Documents!, "precious.docx"));
        Directory.CreateSymbolicLink(Path.Combine(_m.Folders.TempDir, "link-to-docs"), _m.Folders.Documents!);
        _m.WriteFile(Path.Combine(_m.Folders.TempDir, "junk.tmp"));
        var source = TempSource();
        var ctx = _m.Context(InThreeDays);

        var scan = source.Scan(ctx, default);
        Assert.DoesNotContain(scan.Items, i => i.Path.Contains("precious"));

        source.Clean(scan, ctx, null, default);
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void Clean_RefusesItemsOutsideTheAllowList_EvenIfAScanReturnedThem()
    {
        string precious = _m.WriteFile(Path.Combine(_m.Folders.Documents!, "precious.docx"));
        var forged = new JunkScanResult([new JunkItem(precious, 100, _m.Folders.Documents!, null)], 100, 1, null);
        var source = TempSource();

        var cleaned = source.Clean(forged, _m.Context(InThreeDays), null, default);

        Assert.Equal(1, cleaned.Refused);
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void Clean_RefusesItemsNotUnderTheirRoot()
    {
        string other = _m.WriteFile(Path.Combine(_m.Folders.LocalAppData, "SomeApp", "settings.json"));
        var forged = new JunkScanResult([new JunkItem(other, 100, _m.Folders.TempDir, null)], 100, 1, null);

        var cleaned = TempSource().Clean(forged, _m.Context(InThreeDays), null, default);

        Assert.Equal(1, cleaned.Refused);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void AdminRules_AreSkippedWithANote_WhenNotElevated()
    {
        string winTemp = Path.Combine(_m.Folders.WindowsDir, "Temp");
        _m.WriteFile(Path.Combine(winTemp, "x.log"));
        var source = new FileJunkSource("w", "Windows temp", "", [new JunkRule(winTemp) { RequiresAdmin = true }]);

        var asUser = source.Scan(_m.Context(InThreeDays, elevated: false), default);
        var asAdmin = source.Scan(_m.Context(InThreeDays, elevated: true), default);

        Assert.Equal(0, asUser.Count);
        Assert.NotNull(asUser.Note);
        Assert.Equal(1, asAdmin.Count);
        Assert.True(source.RequiresAdmin);
    }

    [Fact]
    public void SearchPattern_LimitsWhatIsMatched()
    {
        string explorer = Path.Combine(_m.Folders.LocalAppData, "Microsoft", "Windows", "Explorer");
        _m.WriteFile(Path.Combine(explorer, "thumbcache_256.db"));
        string keep = _m.WriteFile(Path.Combine(explorer, "ExplorerStartupLog.etl"));
        var source = new FileJunkSource("th", "Thumbs", "",
            [new JunkRule(explorer) { SearchPattern = "thumbcache_*.db", Recursive = false, RemoveEmptyFolders = false }]);
        var ctx = _m.Context();

        var scan = source.Scan(ctx, default);
        source.Clean(scan, ctx, null, default);

        Assert.Equal(1, scan.Count);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void ProfileWildcards_ExpandToEachBrowserProfile()
    {
        string userData = Path.Combine(_m.Folders.LocalAppData, "Google", "Chrome", "User Data");
        _m.WriteFile(Path.Combine(userData, "Default", "Cache", "Cache_Data", "f_1"), 10);
        _m.WriteFile(Path.Combine(userData, "Profile 2", "Cache", "Cache_Data", "f_2"), 20);
        string bookmarks = _m.WriteFile(Path.Combine(userData, "Default", "Bookmarks"), 30);
        var source = new FileJunkSource("c", "Chrome", "", [new JunkRule(Path.Combine(userData, "*", "Cache"))]);
        var ctx = _m.Context();

        var scan = source.Scan(ctx, default);
        source.Clean(scan, ctx, null, default);

        Assert.Equal(30, scan.Bytes);
        Assert.True(File.Exists(bookmarks));
    }

    [Fact]
    public void Catalog_EveryRule_PointsInsideTheGuardsAllowList()
    {
        foreach (var source in JunkCatalog.Create(_m.Folders))
        {
            foreach (var rule in source.Rules)
            {
                string concrete = rule.Root.Replace("*", "Default");
                string probe = Path.Combine(concrete, rule.SearchPattern == "*" ? "probe.tmp" : rule.SearchPattern.Replace("*", "x"));
                Assert.True(_m.Guard.Check(probe, Safety.DeletionKind.Junk).Allowed, $"{source.Id}: {rule.Root}");
            }
        }
    }

    [Fact]
    public void Catalog_HasUniqueIds_AndDescriptions()
    {
        var sources = JunkCatalog.Create(_m.Folders);
        Assert.Equal(sources.Count, sources.Select(s => s.Id).Distinct().Count());
        Assert.All(sources, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
    }

    [Fact]
    public async Task CleanAsync_ReportsProgress()
    {
        _m.WriteFile(Path.Combine(_m.Folders.TempDir, "a.tmp"), 2048);
        var source = TempSource();
        var ctx = _m.Context(InThreeDays);
        var reported = new List<long>();

        var scan = await source.ScanAsync(ctx, default);
        await source.CleanAsync(scan, ctx, new SyncProgress<long>(reported.Add), default);

        Assert.Equal(2048, reported.Last());
    }
}

/// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
