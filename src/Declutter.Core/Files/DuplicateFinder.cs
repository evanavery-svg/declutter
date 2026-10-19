using System.Security.Cryptography;

namespace Declutter.Core.Files;

public sealed class DuplicateGroup
{
    public DuplicateGroup(string hash, long size, IReadOnlyList<FileEntry> files)
    {
        Hash = hash;
        Size = size;
        Files = files;
    }

    public string Hash { get; }
    public long Size { get; }

    /// <summary>All identical copies; the suggested one to keep is first.</summary>
    public IReadOnlyList<FileEntry> Files { get; }

    public FileEntry SuggestedKeeper => Files[0];

    /// <summary>Space recovered by keeping exactly one copy.</summary>
    public long WastedBytes => Size * (Files.Count - 1);
}

public sealed record DuplicateProgress(int FilesHashed, int FilesToHash);

/// <summary>
/// Finds files with byte-for-byte identical content: group by size, then by a hash of the first
/// 64 KB, then by a full SHA-256 — so most files are never read in full.
/// </summary>
public static class DuplicateFinder
{
    internal const int PartialHashBytes = 64 * 1024;

    public static IReadOnlyList<DuplicateGroup> Find(IEnumerable<FileEntry> files, long minimumBytes, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var bySize = files
            .Where(f => f.Size >= Math.Max(1, minimumBytes))
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .ToList();

        int toHash = bySize.Sum(g => g.Count());
        int hashed = 0;
        var groups = new List<DuplicateGroup>();

        foreach (var sizeGroup in bySize)
        {
            ct.ThrowIfCancellationRequested();
            long size = sizeGroup.Key;

            var byPartial = HashAll(sizeGroup, f => HashFile(f.Path, PartialHashBytes, ct), ref hashed, toHash, progress);
            foreach (var partialGroup in byPartial.Where(g => g.Count() > 1))
            {
                IEnumerable<IGrouping<string, FileEntry>> finalGroups = size <= PartialHashBytes
                    ? [partialGroup] // the partial hash already covered the whole file
                    : HashAll(partialGroup, f => HashFile(f.Path, long.MaxValue, ct), ref hashed, 0, null);

                foreach (var g in finalGroups.Where(g => g.Count() > 1))
                {
                    groups.Add(new DuplicateGroup(g.Key, size, OrderByKeepPreference(g).ToList()));
                }
            }
        }

        return groups.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private static List<IGrouping<string, FileEntry>> HashAll(IEnumerable<FileEntry> files, Func<FileEntry, string?> hash,
        ref int hashed, int total, IProgress<DuplicateProgress>? progress)
    {
        var results = new List<(FileEntry File, string Hash)>();
        foreach (var f in files)
        {
            string? h = hash(f);
            if (h != null) results.Add((f, h));
            if (progress != null)
            {
                hashed++;
                if (hashed % 25 == 0 || hashed == total) progress.Report(new DuplicateProgress(hashed, total));
            }
        }
        return results.GroupBy(r => r.Hash, r => r.File).ToList();
    }

    internal static string? HashFile(string path, long maxBytes, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1 << 16];
            long remaining = maxBytes;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) break;
                sha.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // unreadable files are simply not reported as duplicates
        }
    }

    /// <summary>
    /// Which copy to keep: prefer one that isn't in Downloads or a "copy", then the oldest (likely the
    /// original), then the shortest path.
    /// </summary>
    internal static IEnumerable<FileEntry> OrderByKeepPreference(IEnumerable<FileEntry> files) =>
        files.OrderBy(f => LooksLikeACopy(f) ? 1 : 0)
            .ThenBy(f => f.CreationUtc < f.LastWriteUtc ? f.CreationUtc : f.LastWriteUtc)
            .ThenBy(f => f.Path.Length)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase);

    private static bool LooksLikeACopy(FileEntry f)
    {
        string name = Path.GetFileNameWithoutExtension(f.Path);
        bool copyName = name.Contains(" - Copy", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("(1)") || name.Contains("(2)") || name.Contains("(3)")
                        || name.EndsWith(" copy", StringComparison.OrdinalIgnoreCase);
        bool inDownloads = f.Path.Contains(Path.DirectorySeparatorChar + "Downloads" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return copyName || inDownloads;
    }
}
