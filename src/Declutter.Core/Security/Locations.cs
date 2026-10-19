using Declutter.Core.Safety;
using Declutter.Core.Util;

namespace Declutter.Core.Security;

/// <summary>Where a program lives says a lot about it.</summary>
public static class Locations
{
    /// <summary>Inside Windows or Program Files: installed by Windows or an installer that needed admin rights.</summary>
    public static bool IsProtectedInstallLocation(string path, KnownFolders f) =>
        (PathUtil.IsSameOrUnder(path, f.WindowsDir) && !IsUnder(path, f.WindowsDir, "Temp")) ||
        PathUtil.IsSameOrUnder(path, f.ProgramFiles) ||
        PathUtil.IsSameOrUnder(path, f.ProgramFilesX86);

    /// <summary>Folders any program can write to without admin rights — where malware usually lands.</summary>
    public static bool IsUserWritable(string path, KnownFolders f) =>
        PathUtil.IsSameOrUnder(path, f.UserProfile) ||
        PathUtil.IsSameOrUnder(path, Path.Combine(f.UsersRoot, "Public")) ||
        (f.SafeTempDir is { } t && PathUtil.IsSameOrUnder(path, t)) ||
        IsUnder(path, f.WindowsDir, "Temp") ||
        (PathUtil.IsSameOrUnder(path, f.ProgramData) && !IsUnder(path, f.ProgramData, "Microsoft"));

    /// <summary>Temp folders: real programs run from here only briefly (installers), never at every startup.</summary>
    public static bool IsTemp(string path, KnownFolders f) =>
        (f.SafeTempDir is { } t && PathUtil.IsSameOrUnder(path, t)) || IsUnder(path, f.WindowsDir, "Temp");

    private static bool IsUnder(string path, string root, string child) =>
        PathUtil.IsSameOrUnder(path, Path.Combine(root, child));
}
