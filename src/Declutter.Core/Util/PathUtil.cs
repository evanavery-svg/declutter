namespace Declutter.Core.Util;

public static class PathUtil
{
    /// <summary>Full path with "." / ".." resolved and no trailing separator (except for a root).</summary>
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (root != null && full.Length > root.Length)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="folder"/> or lies anywhere beneath it.</summary>
    public static bool IsSameOrUnder(string path, string folder)
    {
        string p = Normalize(path);
        string f = Normalize(folder);
        if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        if (!f.EndsWith(Path.DirectorySeparatorChar)) f += Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="path"/> lies strictly beneath <paramref name="folder"/>.</summary>
    public static bool IsStrictlyUnder(string path, string folder) =>
        IsSameOrUnder(path, folder) && !PathsEqual(path, folder);

    public static bool PathsEqual(string a, string b) =>
        Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsRoot(string path)
    {
        string full = Normalize(path);
        string? root = Path.GetPathRoot(full);
        return root != null && full.Length <= root.Length;
    }
}
