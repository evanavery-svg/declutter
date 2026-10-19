using System.Runtime.InteropServices;
using System.Security.Principal;
using Declutter.Core.Safety;

namespace Declutter.Windows;

public static class WindowsEnvironment
{
    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static KnownFolders DetectKnownFolders()
    {
        string windows = Folder(Environment.SpecialFolder.Windows, @"C:\Windows");
        string drive = Path.GetPathRoot(windows) ?? @"C:\";
        string profile = Folder(Environment.SpecialFolder.UserProfile, Path.Combine(drive, "Users", Environment.UserName));

        var oneDrive = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p) && Directory.Exists(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new KnownFolders
        {
            SystemDrive = drive,
            WindowsDir = windows,
            ProgramFiles = Folder(Environment.SpecialFolder.ProgramFiles, Path.Combine(drive, "Program Files")),
            ProgramFilesX86 = Folder(Environment.SpecialFolder.ProgramFilesX86, Path.Combine(drive, "Program Files (x86)")),
            ProgramData = Folder(Environment.SpecialFolder.CommonApplicationData, Path.Combine(drive, "ProgramData")),
            UsersRoot = Path.GetDirectoryName(profile) ?? Path.Combine(drive, "Users"),
            UserProfile = profile,
            LocalAppData = Folder(Environment.SpecialFolder.LocalApplicationData, Path.Combine(profile, "AppData", "Local")),
            RoamingAppData = Folder(Environment.SpecialFolder.ApplicationData, Path.Combine(profile, "AppData", "Roaming")),
            TempDir = Path.GetTempPath(),
            Desktop = Optional(Environment.SpecialFolder.DesktopDirectory),
            Documents = Optional(Environment.SpecialFolder.MyDocuments),
            Downloads = DownloadsFolder() ?? Path.Combine(profile, "Downloads"),
            Pictures = Optional(Environment.SpecialFolder.MyPictures),
            Videos = Optional(Environment.SpecialFolder.MyVideos),
            Music = Optional(Environment.SpecialFolder.MyMusic),
            OneDriveFolders = oneDrive,
        };
    }

    private static string Folder(Environment.SpecialFolder folder, string fallback)
    {
        string path = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(path) ? fallback : path;
    }

    private static string? Optional(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? DownloadsFolder()
    {
        if (NativeMethods.SHGetKnownFolderPath(NativeMethods.FOLDERID_Downloads, 0, IntPtr.Zero, out IntPtr ptr) != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }
}
