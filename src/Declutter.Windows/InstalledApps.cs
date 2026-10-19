using System.Diagnostics;
using Declutter.Core.Apps;
using Declutter.Core.Files;
using Declutter.Core.Util;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Declutter.Windows;

public sealed record UninstallOutcome(bool Removed, string Message);

/// <summary>Lists installed desktop programs and Store apps, and removes them the supported way.</summary>
public sealed class InstalledApps(IActivityLog log, bool isElevated)
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public bool IsElevated => isElevated;

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct = default) => Task.Run<IReadOnlyList<InstalledApp>>(() =>
    {
        var all = ReadDesktopApps().ToList();
        ct.ThrowIfCancellationRequested();
        all.AddRange(ReadStoreApps());

        foreach (var app in all)
        {
            var (advice, reason) = AppAdvisor.Advise(app);
            app.Advice = advice;
            app.AdviceReason = reason;
        }

        return all.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }, ct);

    /// <summary>Fills in sizes the registry didn't provide by measuring the install folder.</summary>
    public static Task MeasureMissingSizesAsync(IEnumerable<InstalledApp> apps, Action<InstalledApp> onMeasured, CancellationToken ct) => Task.Run(() =>
    {
        foreach (var app in apps.Where(a => a.SizeBytes is null && !string.IsNullOrWhiteSpace(a.InstallLocation)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (!IsMeasurableInstallFolder(app.InstallLocation!)) continue;
            long? size = FolderMeasure.SizeOf(app.InstallLocation!, ct);
            if (size is > 0)
            {
                app.SizeBytes = size;
                onMeasured(app);
            }
        }
    }, ct);

    /// <summary>Guards against bogus InstallLocation values like "C:\Program Files" or "C:\".</summary>
    private static bool IsMeasurableInstallFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || PathUtil.IsRoot(path)) return false;
        var tooBroad = new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.System, Environment.SpecialFolder.MyDocuments,
        };
        return !tooBroad.Select(Environment.GetFolderPath)
            .Where(f => !string.IsNullOrEmpty(f))
            .Any(f => PathUtil.IsSameOrUnder(f, path));
    }

    private IReadOnlyList<InstalledApp> ReadDesktopApps()
    {
        var desktop = new List<InstalledApp>();
        desktop.AddRange(ReadUninstallKeys(RegistryHive.LocalMachine, RegistryView.Registry64));
        desktop.AddRange(ReadUninstallKeys(RegistryHive.LocalMachine, RegistryView.Registry32));
        desktop.AddRange(ReadUninstallKeys(RegistryHive.CurrentUser, RegistryView.Registry64));
        return UninstallEntryParser.Deduplicate(desktop);
    }

    private IEnumerable<InstalledApp> ReadUninstallKeys(RegistryHive hiveName, RegistryView view)
    {
        var apps = new List<InstalledApp>();
        try
        {
            using var hive = RegistryKey.OpenBaseKey(hiveName, view);
            using var root = hive.OpenSubKey(UninstallPath);
            if (root == null) return apps;

            foreach (string keyName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(keyName);
                    if (key == null) continue;
                    var values = key.GetValueNames().ToDictionary(n => n, n => key.GetValue(n), StringComparer.OrdinalIgnoreCase);
                    var app = UninstallEntryParser.Parse(keyName, values, hiveName == RegistryHive.LocalMachine);
                    if (app != null) apps.Add(app);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // skip unreadable entries
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Write("Apps", $"Couldn't read installed programs from {hiveName}: {ex.Message}");
        }
        return apps;
    }

    private IEnumerable<InstalledApp> ReadStoreApps()
    {
        var apps = new List<InstalledApp>();
        try
        {
            var manager = new PackageManager();
            foreach (var p in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (p.IsFramework || p.IsResourcePackage || p.IsBundle || p.SignatureKind == PackageSignatureKind.System) continue;
                    string name = StorePackageInfo.SafeDisplayName(p) ?? p.Id.Name;
                    apps.Add(new InstalledApp
                    {
                        Id = "Store:" + p.Id.FullName,
                        Name = name,
                        Kind = AppKind.Store,
                        Publisher = StorePackageInfo.SafePublisher(p),
                        Version = $"{p.Id.Version.Major}.{p.Id.Version.Minor}.{p.Id.Version.Build}.{p.Id.Version.Revision}",
                        InstallDate = StorePackageInfo.SafeInstalledDate(p),
                        InstallLocation = StorePackageInfo.SafeInstalledPath(p),
                        PackageFullName = p.Id.FullName,
                        PackageName = p.Id.Name,
                    });
                }
                catch (Exception)
                {
                    // one broken package shouldn't hide the rest
                }
            }
        }
        catch (Exception ex)
        {
            log.Write("Apps", "Couldn't list Store apps: " + ex.Message);
        }
        return apps;
    }

    public async Task<UninstallOutcome> UninstallAsync(InstalledApp app, CancellationToken ct = default)
    {
        log.Write("Apps", $"Uninstalling \"{app.Name}\" {app.Version}");
        UninstallOutcome outcome = app.Kind == AppKind.Store
            ? await RemoveStoreAppAsync(app)
            : await RunUninstallerAsync(app, ct);
        log.Write("Apps", $"\"{app.Name}\": {outcome.Message}");
        return outcome;
    }

    private static async Task<UninstallOutcome> RemoveStoreAppAsync(InstalledApp app)
    {
        try
        {
            var result = await new PackageManager().RemovePackageAsync(app.PackageFullName);
            return result.IsRegistered
                ? new UninstallOutcome(false, "Windows reported the app is still installed. " + result.ErrorText)
                : new UninstallOutcome(true, "Removed.");
        }
        catch (Exception ex)
        {
            return new UninstallOutcome(false, "Windows couldn't remove this app: " + ex.Message);
        }
    }

    private async Task<UninstallOutcome> RunUninstallerAsync(InstalledApp app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.UninstallCommand))
            return new UninstallOutcome(false, "This program doesn't provide an uninstaller.");
        if (app.RequiresAdmin && !isElevated)
            return new UninstallOutcome(false, "Removing this program needs administrator rights.");

        var (file, args) = CommandLine.Split(Environment.ExpandEnvironmentVariables(app.UninstallCommand));
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            if (process != null) await process.WaitForExitAsync(ct);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new UninstallOutcome(false, "Couldn't start the uninstaller: " + ex.Message);
        }

        // Many uninstallers hand off to a second process and exit immediately; check the registry
        // for a while before deciding.
        for (int i = 0; i < 10; i++)
        {
            if (!await IsStillInstalledAsync(app, ct)) return new UninstallOutcome(true, "Uninstalled.");
            await Task.Delay(1000, ct);
        }
        return new UninstallOutcome(false, "The uninstaller finished but the program is still listed. It may still be running, or was cancelled. Press Refresh when it's done.");
    }

    private Task<bool> IsStillInstalledAsync(InstalledApp app, CancellationToken ct) => Task.Run(() =>
        ReadDesktopApps().Any(a => a.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(a.Version, app.Version, StringComparison.OrdinalIgnoreCase)), ct);
}
