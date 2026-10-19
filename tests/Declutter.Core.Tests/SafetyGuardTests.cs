using Declutter.Core.Safety;

namespace Declutter.Core.Tests;

public class SafetyGuardTests : IDisposable
{
    private readonly FakeMachine _m = new();
    private KnownFolders F => _m.Folders;

    public void Dispose() => _m.Dispose();

    [Fact]
    public void Junk_InTempFolder_IsAllowed() =>
        Assert.True(_m.Guard.Check(Path.Combine(F.TempDir, "setup123", "a.tmp"), DeletionKind.Junk).Allowed);

    [Fact]
    public void Junk_InBrowserCache_IsAllowed() =>
        Assert.True(_m.Guard.Check(Path.Combine(F.LocalAppData, "Microsoft", "Edge", "User Data", "Default", "Cache", "f_0001"), DeletionKind.Junk).Allowed);

    [Theory]
    [InlineData("Documents")]
    [InlineData("Desktop")]
    [InlineData("Downloads")]
    public void Junk_InPersonalFolders_IsRefused(string folder) =>
        Assert.False(_m.Guard.Check(Path.Combine(F.UserProfile, folder, "thesis.docx"), DeletionKind.Junk).Allowed);

    [Fact]
    public void Junk_InWindowsSystemFolders_IsRefused()
    {
        Assert.False(_m.Guard.Check(Path.Combine(F.WindowsDir, "System32", "kernel32.dll"), DeletionKind.Junk).Allowed);
        Assert.False(_m.Guard.Check(Path.Combine(F.WindowsDir, "Prefetch", "X.pf"), DeletionKind.Junk).Allowed);
        Assert.False(_m.Guard.Check(Path.Combine(F.WindowsDir, "SoftwareDistribution", "DataStore", "DataStore.edb"), DeletionKind.Junk).Allowed);
    }

    [Fact]
    public void Junk_InAllowListedWindowsFolders_IsAllowed()
    {
        Assert.True(_m.Guard.Check(Path.Combine(F.WindowsDir, "Temp", "x.log"), DeletionKind.Junk).Allowed);
        Assert.True(_m.Guard.Check(Path.Combine(F.WindowsDir, "SoftwareDistribution", "Download", "abc", "x.cab"), DeletionKind.Junk).Allowed);
        Assert.True(_m.Guard.Check(Path.Combine(F.WindowsDir, "MEMORY.DMP"), DeletionKind.Junk).Allowed);
        Assert.True(_m.Guard.Check(Path.Combine(F.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue", "r", "x.wer"), DeletionKind.Junk).Allowed);
    }

    [Fact]
    public void AllowListEntries_DoNotMatchSimilarlyNamedSiblings()
    {
        Assert.False(_m.Guard.Check(Path.Combine(F.WindowsDir, "MEMORY.DMP.keep"), DeletionKind.Junk).Allowed);
        Assert.False(_m.Guard.Check(Path.Combine(F.WindowsDir, "TempImportant", "x"), DeletionKind.Junk).Allowed);
    }

    [Fact]
    public void WindowsFolder_IsNeverAUserFile() =>
        Assert.False(_m.Guard.Check(Path.Combine(F.WindowsDir, "Temp", "x.log"), DeletionKind.UserFile).Allowed);

    [Theory]
    [InlineData(DeletionKind.Junk)]
    [InlineData(DeletionKind.UserFile)]
    public void ProgramFiles_IsAlwaysRefused(DeletionKind kind)
    {
        Assert.False(_m.Guard.Check(Path.Combine(F.ProgramFiles, "App", "app.exe"), kind).Allowed);
        Assert.False(_m.Guard.Check(Path.Combine(F.ProgramFilesX86, "App", "cache", "x.tmp"), kind).Allowed);
    }

    [Fact]
    public void UserFile_InDocuments_IsAllowed() =>
        Assert.True(_m.Guard.Check(Path.Combine(F.Documents!, "old", "video.mp4"), DeletionKind.UserFile).Allowed);

    [Fact]
    public void UserFile_InAppData_IsRefused() =>
        Assert.False(_m.Guard.Check(Path.Combine(F.RoamingAppData, "App", "settings.json"), DeletionKind.UserFile).Allowed);

    [Fact]
    public void EssentialFolders_ThemselvesAndTheirParents_AreRefused()
    {
        foreach (var folder in new[] { F.Documents!, F.UserProfile, F.UsersRoot, F.LocalAppData, F.AppDataRoot, F.WindowsDir, F.Root() })
        {
            Assert.False(_m.Guard.Check(folder, DeletionKind.UserFile).Allowed, folder);
            Assert.False(_m.Guard.Check(folder, DeletionKind.Junk).Allowed, folder);
        }
    }

    [Fact]
    public void PathTraversal_IsResolvedBeforeChecking()
    {
        string sneaky = F.TempDir + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar +
                        ".." + Path.DirectorySeparatorChar + "Documents" + Path.DirectorySeparatorChar + "taxes.pdf";
        Assert.False(_m.Guard.Check(sneaky, DeletionKind.Junk).Allowed);
    }

    [Theory]
    [InlineData("relative/path.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void NonAbsolutePaths_AreRefused(string path) =>
        Assert.False(_m.Guard.Check(path, DeletionKind.UserFile).Allowed);

    [Fact]
    public void Wildcards_AreRefused() =>
        Assert.False(_m.Guard.Check(Path.Combine(F.TempDir, "*"), DeletionKind.Junk).Allowed);

    [Fact]
    public void FileSystemRoot_IsRefused() =>
        Assert.False(_m.Guard.Check(Path.GetPathRoot(F.Root())!, DeletionKind.UserFile).Allowed);

    [Theory]
    [InlineData("System Volume Information")]
    [InlineData("$Recycle.Bin")]
    [InlineData("Recovery")]
    public void SystemFoldersAtDriveRoot_AreRefused(string name) =>
        Assert.False(_m.Guard.Check(Path.Combine(Path.GetPathRoot(F.Root())!, name, "x"), DeletionKind.UserFile).Allowed);

    [Fact]
    public void PagefileAtDriveRoot_IsRefused() =>
        Assert.False(_m.Guard.Check(Path.Combine(Path.GetPathRoot(F.Root())!, "pagefile.sys"), DeletionKind.UserFile).Allowed);

    [Fact]
    public void SharedProgramData_IsRefused() =>
        Assert.False(_m.Guard.Check(Path.Combine(F.ProgramData, "Vendor", "license.dat"), DeletionKind.Junk).Allowed);

    [Fact]
    public void JunkOutsideAnyKnownCacheLocation_IsRefused() =>
        Assert.False(_m.Guard.Check(Path.Combine(_m.Root, "Games", "save.dat"), DeletionKind.Junk).Allowed);

    [Fact]
    public void SafeTempDir_IsNull_WhenTempPointsAtProfileOrRoot()
    {
        var atProfile = WithTemp(F.UserProfile);
        Assert.Null(atProfile.SafeTempDir);
        Assert.Null(WithTemp(Path.GetPathRoot(F.Root())!).SafeTempDir);
        Assert.Null(WithTemp(F.LocalAppData).SafeTempDir);
        Assert.Null(WithTemp(Path.Combine(F.WindowsDir, "System32")).SafeTempDir);

        // And with such a TEMP, user documents stay off-limits for junk cleaning.
        var guard = new SafetyGuard(atProfile);
        Assert.False(guard.Check(Path.Combine(F.Documents!, "a.docx"), DeletionKind.Junk).Allowed);
    }

    [Fact]
    public void SafeTempDir_AcceptsDedicatedFolderOutsideProfile() =>
        Assert.NotNull(WithTemp(Path.Combine(_m.Root, "Temp")).SafeTempDir);

    private KnownFolders WithTemp(string temp) => new()
    {
        SystemDrive = F.SystemDrive, WindowsDir = F.WindowsDir, ProgramFiles = F.ProgramFiles, ProgramFilesX86 = F.ProgramFilesX86,
        ProgramData = F.ProgramData, UsersRoot = F.UsersRoot, UserProfile = F.UserProfile, LocalAppData = F.LocalAppData,
        RoamingAppData = F.RoamingAppData, TempDir = temp, Documents = F.Documents, Downloads = F.Downloads, Desktop = F.Desktop,
    };
}

internal static class KnownFoldersTestExtensions
{
    public static string Root(this KnownFolders f) => f.SystemDrive;
}
