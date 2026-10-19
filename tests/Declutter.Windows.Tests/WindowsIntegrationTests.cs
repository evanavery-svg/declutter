using Declutter.Core.Junk;
using Declutter.Core.Safety;
using Declutter.Core.Startup;
using Declutter.Core.Util;
using Microsoft.Win32;

namespace Declutter.Windows.Tests;

public class WindowsIntegrationTests
{
    private static readonly KnownFolders Folders = WindowsEnvironment.DetectKnownFolders();

    [Fact]
    public void KnownFolders_AreDetected()
    {
        Assert.True(Directory.Exists(Folders.WindowsDir));
        Assert.True(Directory.Exists(Folders.UserProfile));
        Assert.NotNull(Folders.SafeTempDir);
        Assert.NotNull(Folders.Downloads);
    }

    [Fact]
    public void Guard_ProtectsTheRealSystem()
    {
        var guard = new SafetyGuard(Folders);
        Assert.False(guard.Check(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), DeletionKind.Junk).Allowed);
        Assert.False(guard.Check(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), DeletionKind.UserFile).Allowed);
        Assert.False(guard.Check(@"C:\pagefile.sys", DeletionKind.UserFile).Allowed);
        Assert.False(guard.Check(Folders.Documents!, DeletionKind.UserFile).Allowed);
        Assert.True(guard.Check(Path.Combine(Folders.SafeTempDir!, "x.tmp"), DeletionKind.Junk).Allowed);
    }

    [Fact]
    public async Task EveryJunkCategory_ScansWithoutErrors()
    {
        var context = new JunkContext(new SafetyGuard(Folders), WindowsEnvironment.IsElevated, SystemClock.Instance, NullActivityLog.Instance);
        IEnumerable<IJunkSource> sources = [.. JunkCatalog.Create(Folders), new RecycleBinSource(() => IntPtr.Zero)];

        foreach (var source in sources)
        {
            var result = await source.ScanAsync(context, CancellationToken.None);
            Assert.True(result.Bytes >= 0, source.Id);
        }
    }

    [Fact]
    public async Task ShellRecycler_MovesAFileToTheRecycleBin()
    {
        string file = Path.Combine(Folders.SafeTempDir!, $"declutter-recycle-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "test");
        var recycler = new ShellRecycler(() => IntPtr.Zero);

        // Temp is app data, which the guard rightly refuses for personal-file deletes, so call the shell directly here.
        bool ok = await StaThread.Run(() => recycler.Recycle(file));

        Assert.True(ok);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task StartupManager_ListsItems()
    {
        var manager = new StartupManager(NullActivityLog.Instance, WindowsEnvironment.IsElevated);
        var items = await manager.GetItemsAsync();
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
    }

    [Fact]
    public async Task StartupManager_TogglesARunKeyItem_TheTaskManagerWay()
    {
        const string name = "DeclutterIntegrationTest";
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        run.SetValue(name, $"\"{Path.Combine(Environment.SystemDirectory, "notepad.exe")}\"");
        try
        {
            var manager = new StartupManager(NullActivityLog.Instance, WindowsEnvironment.IsElevated);
            var item = (await manager.GetItemsAsync()).Single(i => i.Source == StartupSource.RegistryUser && i.Key == name);
            Assert.True(item.Enabled);

            await manager.SetEnabledAsync(item, false);
            var reread = (await manager.GetItemsAsync()).Single(i => i.Id == item.Id);
            Assert.False(reread.Enabled);
            Assert.NotNull(run.GetValue(name)); // the entry itself is untouched

            await manager.SetEnabledAsync(reread, true);
            Assert.True((await manager.GetItemsAsync()).Single(i => i.Id == item.Id).Enabled);
        }
        finally
        {
            run.DeleteValue(name, throwOnMissingValue: false);
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApproved.UserKeyBase + @"\Run", writable: true);
            approved?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    [Fact]
    public async Task StartupManager_TogglesAScheduledLogonTask()
    {
        if (!WindowsEnvironment.IsElevated) return; // creating logon tasks needs admin
        const string task = "DeclutterIntegrationTestTask";
        string schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        string created = await RunAsync(schtasks, $"/create /f /tn {task} /sc onlogon /tr \"{Path.Combine(Environment.SystemDirectory, "notepad.exe")}\"");
        try
        {
            var manager = new StartupManager(NullActivityLog.Instance, true);
            var items = await manager.GetItemsAsync();
            var item = items.SingleOrDefault(i => i.Source == StartupSource.ScheduledTask && i.Key == "\\" + task);
            if (item == null)
            {
                string single = await RunAsync(schtasks, $"/query /tn {task} /xml");
                Assert.Fail($"Task not listed. schtasks /create said: {created}\nTasks found: " +
                            string.Join(", ", items.Where(i => i.Source == StartupSource.ScheduledTask).Select(i => i.Key)) +
                            $"\nIts XML: {single[..Math.Min(single.Length, 1500)]}");
            }
            Assert.True(item.Enabled);

            await manager.SetEnabledAsync(item, false);
            Assert.False((await manager.GetItemsAsync()).Single(i => i.Id == item.Id).Enabled);
        }
        finally
        {
            await RunAsync(schtasks, $"/delete /f /tn {task}");
        }
    }

    [Fact]
    public async Task InstalledApps_ListsDesktopProgramsAndStoreApps()
    {
        var apps = await new InstalledApps(NullActivityLog.Instance, WindowsEnvironment.IsElevated).GetAppsAsync();

        Assert.Contains(apps, a => a.Kind == Core.Apps.AppKind.Desktop);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.All(apps.Where(a => a.Kind == Core.Apps.AppKind.Desktop), a => Assert.False(string.IsNullOrWhiteSpace(a.UninstallCommand)));
    }

    [Fact]
    public void DriveSpace_IsReported()
    {
        var space = SystemTools.GetDriveSpace(Folders.SystemDrive);
        Assert.NotNull(space);
        Assert.True(space.Total > 0);
    }

    private static async Task<string> RunAsync(string exe, string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        string output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }
}
