namespace Declutter.Core.Files;

/// <summary>
/// Expands folder patterns where a whole path segment is "*", e.g.
/// "...\Edge\User Data\*\Cache" matches every browser profile. Never follows links.
/// </summary>
public static class PathPattern
{
    public static IEnumerable<string> Expand(string pattern)
    {
        string? root = Path.GetPathRoot(pattern);
        if (string.IsNullOrEmpty(root)) yield break;

        string[] segments = pattern[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = [root];

        foreach (var segment in segments)
        {
            if (segment == "*")
            {
                current = current.SelectMany(SafeSubdirectories).ToList();
            }
            else
            {
                current = current.Select(c => Path.Combine(c, segment)).ToList();
            }
        }

        foreach (var path in current)
        {
            if (Directory.Exists(path) || File.Exists(path)) yield return path;
        }
    }

    private static IEnumerable<string> SafeSubdirectories(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists) return [];
            return info.GetDirectories()
                .Where(d => !Safety.FileSystemSafety.IsLink(d))
                .Select(d => d.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
