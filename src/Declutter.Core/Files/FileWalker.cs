using Declutter.Core.Safety;

namespace Declutter.Core.Files;

public sealed record FileEntry(string Path, long Size, DateTime LastWriteUtc, DateTime CreationUtc, FileAttributes Attributes)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>The most recent of creation / modification, which is when the file last "arrived" here.</summary>
    public DateTime LastTouchedUtc => LastWriteUtc > CreationUtc ? LastWriteUtc : CreationUtc;

    public static FileEntry From(FileInfo info) =>
        new(info.FullName, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes);
}

public sealed class WalkOptions
{
    public bool Recursive { get; init; } = true;
    public string SearchPattern { get; init; } = "*";

    /// <summary>Skip files and folders marked Hidden or System (used for personal-file scans).</summary>
    public bool SkipHiddenAndSystem { get; init; }

    /// <summary>Folder names never descended into, e.g. ".git" or "node_modules".</summary>
    public IReadOnlySet<string> ExcludedFolderNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full paths never descended into.</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];

    /// <summary>Called with each directory as it is entered (for progress display).</summary>
    public Action<string>? OnDirectory { get; init; }
}

/// <summary>
/// Depth-first file enumeration that tolerates access-denied folders and never follows symbolic links
/// or junctions (so it cannot escape the folder it was pointed at, or loop forever).
/// </summary>
public static class FileWalker
{
    public static IEnumerable<FileEntry> Enumerate(string root, WalkOptions options, CancellationToken ct = default)
    {
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || FileSystemSafety.IsLink(rootInfo)) yield break;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            options.OnDirectory?.Invoke(dir.FullName);

            foreach (var file in SafeFiles(dir, options.SearchPattern))
            {
                if (FileSystemSafety.IsLink(file)) continue;
                if (options.SkipHiddenAndSystem && (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                FileEntry entry;
                try
                {
                    entry = FileEntry.From(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // vanished or unreadable between listing and stat
                }
                yield return entry;
            }

            if (!options.Recursive) continue;

            foreach (var sub in SafeDirectories(dir))
            {
                if (FileSystemSafety.IsLink(sub)) continue;
                if (options.SkipHiddenAndSystem && (sub.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (options.ExcludedFolderNames.Contains(sub.Name)) continue;
                if (options.ExcludedPaths.Any(p => Util.PathUtil.IsSameOrUnder(sub.FullName, p))) continue;
                pending.Push(sub);
            }
        }
    }

    /// <summary>Directories under root (deepest first), not following links. Used to prune empty folders.</summary>
    public static IEnumerable<DirectoryInfo> EnumerateDirectoriesDeepestFirst(string root, CancellationToken ct = default)
    {
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || FileSystemSafety.IsLink(rootInfo)) return [];

        var ordered = new List<DirectoryInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sub in SafeDirectories(pending.Pop()))
            {
                if (FileSystemSafety.IsLink(sub)) continue;
                ordered.Add(sub);
                pending.Push(sub);
            }
        }
        ordered.Reverse(); // children were added after their parents
        return ordered;
    }

    private static IEnumerable<FileInfo> SafeFiles(DirectoryInfo dir, string pattern)
    {
        try
        {
            return dir.GetFiles(pattern, new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static IEnumerable<DirectoryInfo> SafeDirectories(DirectoryInfo dir)
    {
        try
        {
            return dir.GetDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }
}
