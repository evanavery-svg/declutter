using Declutter.Core.Util;

namespace Declutter.Core.Safety;

/// <summary>
/// The well-known Windows locations the app reasons about. Built from the real environment on Windows
/// (see Declutter.Windows.WindowsEnvironment) and from a fake root in tests.
/// </summary>
public sealed class KnownFolders
{
    public required string SystemDrive { get; init; }
    public required string WindowsDir { get; init; }
    public required string ProgramFiles { get; init; }
    public required string ProgramFilesX86 { get; init; }
    public required string ProgramData { get; init; }
    public required string UsersRoot { get; init; }
    public required string UserProfile { get; init; }
    public required string LocalAppData { get; init; }
    public required string RoamingAppData { get; init; }
    public required string TempDir { get; init; }
    public string? Desktop { get; init; }
    public string? Documents { get; init; }
    public string? Downloads { get; init; }
    public string? Pictures { get; init; }
    public string? Videos { get; init; }
    public string? Music { get; init; }
    public IReadOnlyList<string> OneDriveFolders { get; init; } = [];

    public string AppDataRoot => Path.Combine(UserProfile, "AppData");

    /// <summary>The user's personal folders that exist, without duplicates (Documents may be inside OneDrive, etc.).</summary>
    public IReadOnlyList<string> PersonalFolders =>
        new[] { Desktop, Documents, Downloads, Pictures, Videos, Music }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => PathUtil.Normalize(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Folders that must never be deleted themselves, and whose ancestors must never be deleted either.
    /// </summary>
    public IEnumerable<string> Anchors
    {
        get
        {
            yield return WindowsDir;
            yield return ProgramFiles;
            yield return ProgramFilesX86;
            yield return ProgramData;
            yield return UsersRoot;
            yield return UserProfile;
            yield return LocalAppData;
            yield return RoamingAppData;
            yield return AppDataRoot;
            foreach (var p in PersonalFolders) yield return p;
            foreach (var p in OneDriveFolders) yield return p;
        }
    }

    /// <summary>
    /// The user temp folder, but only if it is a sane, dedicated folder. If TEMP has been pointed at
    /// something like C:\ or the profile folder, temp cleaning is disabled rather than risk user files.
    /// </summary>
    public string? SafeTempDir
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TempDir) || !Path.IsPathFullyQualified(TempDir)) return null;
            if (PathUtil.IsRoot(TempDir)) return null;
            foreach (var anchor in Anchors)
            {
                if (PathUtil.IsSameOrUnder(anchor, TempDir)) return null; // TEMP is an anchor or contains one
            }
            if (PathUtil.IsSameOrUnder(TempDir, WindowsDir) ||
                PathUtil.IsSameOrUnder(TempDir, ProgramFiles) ||
                PathUtil.IsSameOrUnder(TempDir, ProgramFilesX86))
                return null;
            return PathUtil.Normalize(TempDir);
        }
    }

    /// <summary>Builds a complete, Windows-shaped layout under an arbitrary root (used by tests).</summary>
    public static KnownFolders CreateUnder(string root, string userName = "user")
    {
        string profile = Path.Combine(root, "Users", userName);
        string local = Path.Combine(profile, "AppData", "Local");
        return new KnownFolders
        {
            SystemDrive = root,
            WindowsDir = Path.Combine(root, "Windows"),
            ProgramFiles = Path.Combine(root, "Program Files"),
            ProgramFilesX86 = Path.Combine(root, "Program Files (x86)"),
            ProgramData = Path.Combine(root, "ProgramData"),
            UsersRoot = Path.Combine(root, "Users"),
            UserProfile = profile,
            LocalAppData = local,
            RoamingAppData = Path.Combine(profile, "AppData", "Roaming"),
            TempDir = Path.Combine(local, "Temp"),
            Desktop = Path.Combine(profile, "Desktop"),
            Documents = Path.Combine(profile, "Documents"),
            Downloads = Path.Combine(profile, "Downloads"),
            Pictures = Path.Combine(profile, "Pictures"),
            Videos = Path.Combine(profile, "Videos"),
            Music = Path.Combine(profile, "Music"),
        };
    }
}
