using Declutter.Core.Safety;
using Declutter.Core.Util;

namespace Declutter.Core.Files;

public sealed record FolderSize(string Path, long Bytes, int FileCount);

public sealed class StorageScanResult
{
    public required IReadOnlyList<FileEntry> Files { get; init; }

    /// <summary>Size of each immediate subfolder of each scanned root, biggest first.</summary>
    public required IReadOnlyList<FolderSize> Folders { get; init; }

    public long TotalBytes => Files.Sum(f => f.Size);
    public int CloudOnlySkipped { get; init; }
}

public sealed record ScanProgress(int FilesSeen, long BytesSeen, string CurrentFolder);

/// <summary>Walks the user's chosen folders once and collects everything the file tools need.</summary>
public static class StorageScanner
{
    /// <summary>Folders that belong to tools / projects: deleting single files inside them breaks things.</summary>
    public static readonly IReadOnlySet<string> DefaultExcludedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", ".venv", "venv", "__pycache__", ".vs", ".idea", ".gradle",
        "AppData", "$RECYCLE.BIN", "System Volume Information",
    };

    public static StorageScanResult Scan(IEnumerable<string> roots, IReadOnlyList<string> excludedPaths, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var files = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderTotals = new Dictionary<string, (long Bytes, int Count)>(StringComparer.OrdinalIgnoreCase);
        int cloudOnly = 0;
        long bytesSeen = 0;
        string currentFolder = "";
        var lastReport = DateTime.UtcNow;

        // Drop roots nested inside other roots (e.g. Documents inside a chosen OneDrive folder).
        var normalizedRoots = roots.Where(Directory.Exists).Select(PathUtil.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        normalizedRoots = normalizedRoots
            .Where(r => !normalizedRoots.Any(o => !o.Equals(r, StringComparison.OrdinalIgnoreCase) && PathUtil.IsStrictlyUnder(r, o)))
            .ToList();

        foreach (var root in normalizedRoots)
        {
            var options = new WalkOptions
            {
                SkipHiddenAndSystem = true,
                ExcludedFolderNames = DefaultExcludedFolderNames,
                ExcludedPaths = excludedPaths,
                OnDirectory = d => currentFolder = d,
            };

            foreach (var file in FileWalker.Enumerate(root, options, ct))
            {
                if (FileSystemSafety.IsCloudOnly(file.Attributes))
                {
                    cloudOnly++;
                    continue;
                }
                if (!seen.Add(file.Path)) continue;

                files.Add(file);
                bytesSeen += file.Size;

                string bucket = TopLevelFolder(root, file.Path);
                folderTotals.TryGetValue(bucket, out var t);
                folderTotals[bucket] = (t.Bytes + file.Size, t.Count + 1);

                if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report(new ScanProgress(files.Count, bytesSeen, currentFolder));
                }
            }
        }

        progress?.Report(new ScanProgress(files.Count, bytesSeen, ""));
        return new StorageScanResult
        {
            Files = files,
            Folders = folderTotals.Select(kv => new FolderSize(kv.Key, kv.Value.Bytes, kv.Value.Count))
                .OrderByDescending(f => f.Bytes).ToList(),
            CloudOnlySkipped = cloudOnly,
        };
    }

    /// <summary>"C:\Users\me\Documents\a\b.txt" under root "C:\Users\me\Documents" → "C:\Users\me\Documents\a".</summary>
    internal static string TopLevelFolder(string root, string filePath)
    {
        string relative = Path.GetRelativePath(root, filePath);
        int sep = relative.IndexOf(Path.DirectorySeparatorChar);
        return sep < 0 ? root : Path.Combine(root, relative[..sep]);
    }
}
