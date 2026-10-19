namespace Declutter.Core.Safety;

public static class FileSystemSafety
{
    /// <summary>
    /// True for symbolic links and junctions. These are never followed or deleted through: a junction
    /// inside a cache folder could otherwise point at C:\Windows or a user's documents.
    /// Other reparse points (OneDrive placeholders, dedup) are ordinary files for our purposes.
    /// </summary>
    public static bool IsLink(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try
        {
            return info.LinkTarget != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // Can't tell what it is: treat it as a link and leave it alone.
        }
    }

    /// <summary>
    /// True if any directory between <paramref name="root"/> (exclusive) and <paramref name="path"/>
    /// (inclusive) is a link, i.e. the path doesn't really live inside root.
    /// </summary>
    public static bool HasLinkBetween(string root, string path)
    {
        string normalizedRoot = Util.PathUtil.Normalize(root);
        string? current = Util.PathUtil.Normalize(path);
        while (current != null && !current.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && IsLink(info)) return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    /// <summary>OneDrive "online-only" files: reading them would download them, and they use no local space.</summary>
    public static bool IsCloudOnly(FileAttributes attributes)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        return (attributes & (FileAttributes.Offline | recallOnOpen | recallOnDataAccess)) != 0;
    }
}
