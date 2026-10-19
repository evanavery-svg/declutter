using Declutter.Core.Util;

namespace Declutter.Core.Safety;

public enum DeletionKind
{
    /// <summary>Cache / temp / log data that is permanently deleted.</summary>
    Junk,

    /// <summary>A user's own file, which is only ever sent to the Recycle Bin.</summary>
    UserFile,
}

public readonly record struct SafetyVerdict(bool Allowed, string? Reason)
{
    public static readonly SafetyVerdict Ok = new(true, null);
    public static SafetyVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// The last line of defence: every single delete in the app is checked here first, independently of
/// whatever scanner produced the path. Junk deletes use an allow-list (only known cache locations);
/// user-file deletes use a deny-list (never system or program locations).
/// </summary>
public sealed class SafetyGuard
{
    private static readonly string[] DriveRootFolders =
        ["System Volume Information", "$Recycle.Bin", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS",
         "Recovery", "Boot", "EFI", "Config.Msi"];

    private static readonly string[] DriveRootFiles =
        ["pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "BOOTSECT.BAK",
         "DumpStack.log", "DumpStack.log.tmp"];

    private readonly KnownFolders _folders;
    private readonly string[] _junkAllowed;

    public SafetyGuard(KnownFolders folders)
    {
        _folders = folders;
        _junkAllowed = BuildJunkAllowList(folders).Select(PathUtil.Normalize).ToArray();
    }

    public KnownFolders Folders => _folders;

    /// <summary>The only places junk cleaning may ever touch.</summary>
    public IReadOnlyList<string> JunkAllowList => _junkAllowed;

    private static IEnumerable<string> BuildJunkAllowList(KnownFolders f)
    {
        // Per-user application data (caches live here; personal documents do not).
        yield return f.LocalAppData;
        yield return f.RoamingAppData;
        yield return Path.Combine(f.AppDataRoot, "LocalLow");
        if (f.SafeTempDir is { } temp) yield return temp;

        // Specific, well-understood Windows cache folders.
        yield return Path.Combine(f.WindowsDir, "Temp");
        yield return Path.Combine(f.WindowsDir, "SoftwareDistribution", "Download");
        yield return Path.Combine(f.WindowsDir, "SoftwareDistribution", "DeliveryOptimization");
        yield return Path.Combine(f.WindowsDir, "ServiceProfiles", "NetworkService", "AppData", "Local",
            "Microsoft", "Windows", "DeliveryOptimization", "Cache");
        yield return Path.Combine(f.WindowsDir, "Minidump");
        yield return Path.Combine(f.WindowsDir, "LiveKernelReports");
        yield return Path.Combine(f.WindowsDir, "MEMORY.DMP");
        yield return Path.Combine(f.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive");
        yield return Path.Combine(f.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue");
    }

    public SafetyVerdict Check(string path, DeletionKind kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return SafetyVerdict.Deny("Empty path.");
        if (path.IndexOfAny(['*', '?', '\0', '"', '<', '>', '|']) >= 0) return SafetyVerdict.Deny("Path contains wildcard or invalid characters.");
        if (!Path.IsPathFullyQualified(path)) return SafetyVerdict.Deny("Path is not absolute.");

        string full;
        try
        {
            full = PathUtil.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SafetyVerdict.Deny("Path is not valid.");
        }

        if (PathUtil.IsRoot(full)) return SafetyVerdict.Deny("Drive roots are never deleted.");

        foreach (var anchor in _folders.Anchors)
        {
            if (PathUtil.IsSameOrUnder(anchor, full))
                return SafetyVerdict.Deny("This is (or contains) an essential Windows or user folder.");
        }

        string? root = Path.GetPathRoot(full);
        if (root != null)
        {
            string firstSegment = full[root.Length..].Split(Path.DirectorySeparatorChar, 2)[0];
            if (DriveRootFolders.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
                return SafetyVerdict.Deny("System folder at the root of a drive.");
            if (!full[root.Length..].Contains(Path.DirectorySeparatorChar) &&
                DriveRootFiles.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
                return SafetyVerdict.Deny("System file at the root of a drive.");
        }

        if (PathUtil.IsSameOrUnder(full, _folders.ProgramFiles) || PathUtil.IsSameOrUnder(full, _folders.ProgramFilesX86))
            return SafetyVerdict.Deny("Installed programs are only removed through their uninstaller.");

        bool inJunkAllowList = _junkAllowed.Any(a => PathUtil.IsSameOrUnder(full, a));

        if (PathUtil.IsSameOrUnder(full, _folders.WindowsDir) && !(kind == DeletionKind.Junk && inJunkAllowList))
            return SafetyVerdict.Deny("Windows system files are never deleted.");

        if (PathUtil.IsSameOrUnder(full, _folders.ProgramData) && !(kind == DeletionKind.Junk && inJunkAllowList))
            return SafetyVerdict.Deny("Shared program data is never deleted.");

        switch (kind)
        {
            case DeletionKind.Junk when !inJunkAllowList:
                return SafetyVerdict.Deny("Not inside a known cache or temp location.");
            case DeletionKind.UserFile when PathUtil.IsSameOrUnder(full, _folders.AppDataRoot):
                return SafetyVerdict.Deny("App data is cleaned by the Junk Cleaner, not as a personal file.");
        }

        return SafetyVerdict.Ok;
    }
}
