namespace Declutter.Core.Files;

public static class LargeFileFinder
{
    public static IReadOnlyList<FileEntry> Find(IEnumerable<FileEntry> files, long minimumBytes, int maxResults = 500) =>
        files.Where(f => f.Size >= minimumBytes)
            .OrderByDescending(f => f.Size)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
}

public static class OldFileFinder
{
    /// <summary>Files in <paramref name="folder"/> that haven't been added or changed for <paramref name="olderThan"/>.</summary>
    public static IReadOnlyList<FileEntry> Find(IEnumerable<FileEntry> files, string folder, TimeSpan olderThan, DateTime nowUtc) =>
        files.Where(f => Util.PathUtil.IsStrictlyUnder(f.Path, folder))
            .Where(f => nowUtc - f.LastTouchedUtc >= olderThan)
            .OrderByDescending(f => f.Size)
            .ToList();
}
