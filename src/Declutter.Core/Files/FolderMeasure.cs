namespace Declutter.Core.Files;

public static class FolderMeasure
{
    /// <summary>Total size of all files under a folder (not following links); null if it can't be read.</summary>
    public static long? SizeOf(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        try
        {
            return FileWalker.Enumerate(folder, new WalkOptions(), ct).Sum(f => f.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
