using Declutter.Core.Files;
using Declutter.Core.Safety;
using Declutter.Core.Util;

namespace Declutter.Core.Junk;

/// <summary>A junk category made of folders on disk.</summary>
public sealed class FileJunkSource : IJunkSource
{
    public FileJunkSource(string id, string name, string description, IReadOnlyList<JunkRule> rules)
    {
        Id = id;
        Name = name;
        Description = description;
        Rules = rules;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<JunkRule> Rules { get; }
    public string? Warning { get; init; }
    public bool SelectedByDefault { get; init; } = true;
    public IReadOnlyList<BlockingProcess> BlockingProcesses { get; init; } = [];
    public bool RequiresAdmin => Rules.Count > 0 && Rules.All(r => r.RequiresAdmin);

    public Task<JunkScanResult> ScanAsync(JunkContext context, CancellationToken ct) =>
        Task.Run(() => Scan(context, ct), ct);

    public Task<JunkCleanResult> CleanAsync(JunkScanResult scan, JunkContext context, IProgress<long>? bytesFreed, CancellationToken ct) =>
        Task.Run(() => Clean(scan, context, bytesFreed, ct), ct);

    internal JunkScanResult Scan(JunkContext context, CancellationToken ct)
    {
        var items = new List<JunkItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        bool skippedForAdmin = false;
        DateTime now = context.Clock.UtcNow;

        foreach (var rule in Rules)
        {
            if (rule.RequiresAdmin && !context.IsElevated)
            {
                skippedForAdmin |= PathPattern.Expand(rule.Root).Any();
                continue;
            }

            foreach (var root in PathPattern.Expand(rule.Root))
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;

                var options = new WalkOptions { Recursive = rule.Recursive, SearchPattern = rule.SearchPattern };
                foreach (var file in FileWalker.Enumerate(root, options, ct))
                {
                    if (!IsOldEnough(file, rule.MinAge, now)) continue;
                    if (!context.Guard.Check(file.Path, DeletionKind.Junk).Allowed) continue;
                    if (!seen.Add(file.Path)) continue;

                    items.Add(new JunkItem(file.Path, file.Size, root, rule.MinAge));
                    bytes += file.Size;
                }
            }
        }

        string? note = skippedForAdmin ? "Some of these files need administrator rights to clean." : null;
        return new JunkScanResult(items, bytes, items.Count, note);
    }

    internal JunkCleanResult Clean(JunkScanResult scan, JunkContext context, IProgress<long>? progress, CancellationToken ct)
    {
        var result = new JunkCleanResult();
        DateTime now = context.Clock.UtcNow;

        // Remember which folders were already old before we start deleting: deleting a file updates
        // its folder's timestamp, and we only want to prune folders nothing else has touched recently.
        var prunable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in scan.Items.GroupBy(i => i.Root, StringComparer.OrdinalIgnoreCase))
        {
            var rule = Rules.FirstOrDefault(r => PathPattern.Expand(r.Root).Any(e => PathUtil.PathsEqual(e, group.Key)));
            if (rule is not { RemoveEmptyFolders: true, Recursive: true }) continue;
            prunable[group.Key] = FileWalker.EnumerateDirectoriesDeepestFirst(group.Key, ct)
                .Where(d => rule.MinAge is null || now - d.LastWriteTimeUtc >= rule.MinAge)
                .Select(d => d.FullName)
                .ToList();
        }

        long lastReported = 0;
        foreach (var item in scan.Items)
        {
            ct.ThrowIfCancellationRequested();
            switch (TryDelete(item, context, now))
            {
                case DeleteOutcome.Deleted:
                    result.Deleted++;
                    result.BytesFreed += item.Size;
                    break;
                case DeleteOutcome.Skipped:
                    result.Skipped++;
                    break;
                case DeleteOutcome.Refused:
                    result.Refused++;
                    break;
            }

            if (result.BytesFreed - lastReported > 4 * 1024 * 1024)
            {
                lastReported = result.BytesFreed;
                progress?.Report(result.BytesFreed);
            }
        }

        foreach (var (root, folders) in prunable)
        {
            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                TryRemoveEmptyFolder(root, folder, context);
            }
        }

        progress?.Report(result.BytesFreed);
        context.Log.Write("Junk", $"{Name}: deleted {result.Deleted} files ({ByteSize.Format(result.BytesFreed)}), " +
                                  $"skipped {result.Skipped} in use" + (result.Refused > 0 ? $", refused {result.Refused} by safety check" : ""));
        return result;
    }

    private enum DeleteOutcome { Deleted, Skipped, Refused, Gone }

    private DeleteOutcome TryDelete(JunkItem item, JunkContext context, DateTime now)
    {
        // Re-validate everything at delete time: the disk may have changed since the scan.
        if (!PathUtil.IsStrictlyUnder(item.Path, item.Root)) return DeleteOutcome.Refused;
        if (!context.Guard.Check(item.Path, DeletionKind.Junk).Allowed) return DeleteOutcome.Refused;

        try
        {
            var info = new FileInfo(item.Path);
            if (!info.Exists) return DeleteOutcome.Gone;
            if (FileSystemSafety.IsLink(info)) return DeleteOutcome.Refused;
            if (FileSystemSafety.HasLinkBetween(item.Root, info.DirectoryName!)) return DeleteOutcome.Refused;
            if (!IsOldEnough(FileEntry.From(info), item.MinAge, now)) return DeleteOutcome.Skipped;

            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                info.Attributes &= ~FileAttributes.ReadOnly;
            info.Delete();
            return DeleteOutcome.Deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DeleteOutcome.Skipped; // in use or locked: leave it
        }
    }

    private static void TryRemoveEmptyFolder(string root, string folder, JunkContext context)
    {
        try
        {
            if (!PathUtil.IsStrictlyUnder(folder, root)) return;
            if (!context.Guard.Check(folder, DeletionKind.Junk).Allowed) return;
            var info = new DirectoryInfo(folder);
            if (!info.Exists || FileSystemSafety.IsLink(info)) return;
            if (info.EnumerateFileSystemInfos().Any()) return;
            info.Delete(recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Folder in use or not empty any more; leave it.
        }
    }

    private static bool IsOldEnough(FileEntry file, TimeSpan? minAge, DateTime nowUtc) =>
        minAge is null || nowUtc - file.LastTouchedUtc >= minAge.Value;
}
