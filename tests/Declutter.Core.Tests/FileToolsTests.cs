using Declutter.Core.Files;
using Declutter.Core.Safety;

namespace Declutter.Core.Tests;

public class FileToolsTests : IDisposable
{
    private readonly FakeMachine _m = new();
    public void Dispose() => _m.Dispose();

    private static FileEntry Entry(string path, long size, DateTime? touched = null)
    {
        var t = touched ?? DateTime.UtcNow;
        return new FileEntry(path, size, t, t, FileAttributes.Normal);
    }

    [Fact]
    public void Walker_SkipsSymlinkedFoldersAndFiles()
    {
        string outside = _m.WriteFile(Path.Combine(_m.Root, "outside", "secret.bin"));
        string root = Path.Combine(_m.Folders.Documents!, "scan");
        _m.WriteFile(Path.Combine(root, "real.txt"));
        Directory.CreateSymbolicLink(Path.Combine(root, "linkdir"), Path.GetDirectoryName(outside)!);
        File.CreateSymbolicLink(Path.Combine(root, "linkfile.bin"), outside);

        var files = FileWalker.Enumerate(root, new WalkOptions()).Select(f => f.Name).ToList();

        Assert.Equal(["real.txt"], files);
    }

    [Fact]
    public void Walker_HonoursExcludedFolderNames()
    {
        string root = Path.Combine(_m.Folders.Documents!, "proj");
        _m.WriteFile(Path.Combine(root, "node_modules", "lib.js"));
        _m.WriteFile(Path.Combine(root, "main.js"));

        var files = FileWalker.Enumerate(root, new WalkOptions { ExcludedFolderNames = StorageScanner.DefaultExcludedFolderNames })
            .Select(f => f.Name).ToList();

        Assert.Equal(["main.js"], files);
    }

    [Fact]
    public void Walker_MissingRoot_YieldsNothing() =>
        Assert.Empty(FileWalker.Enumerate(Path.Combine(_m.Root, "nope"), new WalkOptions()));

    [Fact]
    public void PathPattern_ExpandsStarSegments()
    {
        Directory.CreateDirectory(Path.Combine(_m.Root, "ud", "Default", "Cache"));
        Directory.CreateDirectory(Path.Combine(_m.Root, "ud", "Profile 1", "Cache"));
        Directory.CreateDirectory(Path.Combine(_m.Root, "ud", "Crashpad"));

        var expanded = PathPattern.Expand(Path.Combine(_m.Root, "ud", "*", "Cache")).Select(Path.GetFileName).ToList();

        Assert.Equal(2, expanded.Count);
    }

    [Fact]
    public void Scanner_MergesNestedRoots_AndSumsTopLevelFolders()
    {
        string docs = _m.Folders.Documents!;
        _m.WriteFile(Path.Combine(docs, "Photos", "a.jpg"), 300);
        _m.WriteFile(Path.Combine(docs, "Photos", "2024", "b.jpg"), 200);
        _m.WriteFile(Path.Combine(docs, "notes.txt"), 50);

        var result = StorageScanner.Scan([docs, Path.Combine(docs, "Photos")], [], null, default);

        Assert.Equal(3, result.Files.Count);
        Assert.Equal(550, result.TotalBytes);
        Assert.Equal(500, result.Folders.First().Bytes);
        Assert.EndsWith("Photos", result.Folders.First().Path);
    }

    [Fact]
    public void Scanner_SkipsExcludedPaths()
    {
        string docs = _m.Folders.Documents!;
        _m.WriteFile(Path.Combine(docs, "keep", "a.bin"));
        _m.WriteFile(Path.Combine(docs, "skip", "b.bin"));

        var result = StorageScanner.Scan([docs], [Path.Combine(docs, "skip")], null, default);

        Assert.Single(result.Files);
    }

    [Fact]
    public void LargeFiles_AreFilteredAndSortedBiggestFirst()
    {
        var files = new[] { Entry("/a", 10), Entry("/b", 500), Entry("/c", 300), Entry("/d", 99) };

        var large = LargeFileFinder.Find(files, 100);

        Assert.Equal(["/b", "/c"], large.Select(f => f.Path));
    }

    [Fact]
    public void OldFiles_OnlyMatchesInsideFolderAndOlderThanCutoff()
    {
        var now = DateTime.UtcNow;
        string dl = _m.Folders.Downloads!;
        var files = new[]
        {
            Entry(Path.Combine(dl, "old.zip"), 10, now.AddDays(-200)),
            Entry(Path.Combine(dl, "new.zip"), 10, now.AddDays(-5)),
            Entry(Path.Combine(_m.Folders.Documents!, "old.doc"), 10, now.AddDays(-400)),
        };

        var old = OldFileFinder.Find(files, dl, TimeSpan.FromDays(90), now);

        Assert.Equal(["old.zip"], old.Select(f => f.Name));
    }

    [Fact]
    public void Duplicates_GroupsIdenticalContent_Only()
    {
        string docs = _m.Folders.Documents!;
        var a = _m.WriteFile(Path.Combine(docs, "a.bin"), 5000, 7);
        var b = _m.WriteFile(Path.Combine(docs, "sub", "b.bin"), 5000, 7);
        var c = _m.WriteFile(Path.Combine(docs, "c.bin"), 5000, 8); // same size, different content
        _m.WriteFile(Path.Combine(docs, "d.bin"), 4000, 7);          // different size

        var files = StorageScanner.Scan([docs], [], null, default).Files;
        var groups = DuplicateFinder.Find(files, 1, null, default);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(5000, group.WastedBytes);
        Assert.DoesNotContain(group.Files, f => f.Path == c);
        Assert.Contains(group.Files, f => f.Path == a);
        Assert.Contains(group.Files, f => f.Path == b);
    }

    [Fact]
    public void Duplicates_DetectsDifferencesBeyondThePartialHash()
    {
        string docs = _m.Folders.Documents!;
        int size = DuplicateFinder.PartialHashBytes * 2;
        var bytes = new byte[size];
        new Random(1).NextBytes(bytes);
        Directory.CreateDirectory(docs);
        File.WriteAllBytes(Path.Combine(docs, "x.bin"), bytes);
        File.WriteAllBytes(Path.Combine(docs, "y.bin"), bytes);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(docs, "z.bin"), bytes);

        var groups = DuplicateFinder.Find(StorageScanner.Scan([docs], [], null, default).Files, 1, null, default);

        var group = Assert.Single(groups);
        Assert.Equal(["x.bin", "y.bin"], group.Files.Select(f => f.Name).Order());
    }

    [Fact]
    public void Duplicates_RespectMinimumSize()
    {
        string docs = _m.Folders.Documents!;
        _m.WriteFile(Path.Combine(docs, "a.txt"), 10);
        _m.WriteFile(Path.Combine(docs, "b.txt"), 10);

        Assert.Empty(DuplicateFinder.Find(StorageScanner.Scan([docs], [], null, default).Files, 1024, null, default));
    }

    [Fact]
    public void Duplicates_SuggestKeepingTheOriginal_NotTheDownloadOrCopy()
    {
        var t = DateTime.UtcNow;
        var original = new FileEntry(Path.Combine(_m.Folders.Documents!, "Report.pdf"), 1, t.AddDays(-10), t.AddDays(-10), 0);
        var download = new FileEntry(Path.Combine(_m.Folders.Downloads!, "Report.pdf"), 1, t.AddDays(-20), t.AddDays(-20), 0);
        var copy = new FileEntry(Path.Combine(_m.Folders.Documents!, "Report - Copy.pdf"), 1, t.AddDays(-30), t.AddDays(-30), 0);

        var ordered = DuplicateFinder.OrderByKeepPreference([download, copy, original]).ToList();

        Assert.Equal(original, ordered[0]);
    }

    [Fact]
    public void Deleter_RecyclesAllowedFiles()
    {
        string file = _m.WriteFile(Path.Combine(_m.Folders.Documents!, "big.iso"), 1000);
        var recycler = new FakeRecycler();
        var log = new RecordingLog();

        var result = new UserFileDeleter(_m.Guard, recycler, log).Recycle([FileEntry.From(new FileInfo(file))]);

        Assert.Equal(1, result.Recycled);
        Assert.Equal(1000, result.BytesRecycled);
        Assert.Equal([file], recycler.Recycled);
        Assert.Single(log.Lines);
    }

    [Fact]
    public void Deleter_RefusesProtectedFiles_WithoutCallingTheRecycler()
    {
        string file = _m.WriteFile(Path.Combine(_m.Folders.RoamingAppData, "App", "db.sqlite"));
        var recycler = new FakeRecycler();

        var result = new UserFileDeleter(_m.Guard, recycler, new RecordingLog()).Recycle([FileEntry.From(new FileInfo(file))]);

        Assert.Equal(0, result.Recycled);
        Assert.Single(result.Failures);
        Assert.Empty(recycler.Recycled);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Deleter_ReportsRecyclerFailures()
    {
        string file = _m.WriteFile(Path.Combine(_m.Folders.Documents!, "open.docx"));

        var result = new UserFileDeleter(_m.Guard, new FakeRecycler(fail: true), new RecordingLog()).Recycle([FileEntry.From(new FileInfo(file))]);

        Assert.Equal(0, result.Recycled);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void DuplicateSelection_ThatRemovesEveryCopy_IsRejected()
    {
        var group = new DuplicateGroup("h", 10, [Entry("/a", 10), Entry("/b", 10)]);

        Assert.NotNull(UserFileDeleter.ValidateDuplicateSelection([group], new HashSet<string> { "/a", "/b" }));
        Assert.Null(UserFileDeleter.ValidateDuplicateSelection([group], new HashSet<string> { "/b" }));
    }

    [Fact]
    public void CloudOnlyAttributes_AreRecognised()
    {
        Assert.True(FileSystemSafety.IsCloudOnly((FileAttributes)0x00400000));
        Assert.True(FileSystemSafety.IsCloudOnly(FileAttributes.Offline));
        Assert.False(FileSystemSafety.IsCloudOnly(FileAttributes.Archive | FileAttributes.ReparsePoint));
    }

    private sealed class FakeRecycler(bool fail = false) : IRecycler
    {
        public List<string> Recycled { get; } = [];

        public bool Recycle(string path)
        {
            if (fail) return false;
            Recycled.Add(path);
            File.Delete(path);
            return true;
        }
    }
}
