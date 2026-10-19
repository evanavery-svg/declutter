using System.Diagnostics;
using Declutter.Core.Startup;
using Declutter.Core.Util;
using Microsoft.Win32;

namespace Declutter.Windows;

/// <summary>
/// Lists and toggles everything that starts when you sign in. Nothing is ever deleted: Run-key and
/// Startup-folder items use the same on/off switch as Task Manager, scheduled tasks are disabled with
/// schtasks, and Store apps use their StartupTask state.
/// </summary>
public sealed class StartupManager(IActivityLog log, bool isElevated)
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string StoreTasksPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private sealed record RunKey(StartupSource Source, RegistryHive Hive, string Path);

    private static readonly RunKey[] RunKeys =
    [
        new(StartupSource.RegistryUser, RegistryHive.CurrentUser, RunPath),
        new(StartupSource.RegistryMachine, RegistryHive.LocalMachine, RunPath),
        new(StartupSource.RegistryMachine32, RegistryHive.LocalMachine, Run32Path),
    ];

    public bool IsElevated => isElevated;

    public async Task<IReadOnlyList<StartupItem>> GetItemsAsync(CancellationToken ct = default)
    {
        var items = new List<StartupItem>();
        await Task.Run(() =>
        {
            foreach (var key in RunKeys) Collect(items, () => ReadRunKey(key));
            Collect(items, () => ReadStartupFolder(StartupSource.StartupFolderUser, Environment.GetFolderPath(Environment.SpecialFolder.Startup)));
            Collect(items, () => ReadStartupFolder(StartupSource.StartupFolderAllUsers, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)));
            Collect(items, ReadStoreApps);
        }, ct);

        try
        {
            items.AddRange(await ReadScheduledTasksAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Write("Startup", "Couldn't read scheduled tasks: " + ex.Message);
        }

        return items
            .OrderBy(i => i.Enabled ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void Collect(List<StartupItem> items, Func<IEnumerable<StartupItem>> read)
    {
        try
        {
            items.AddRange(read());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log.Write("Startup", "Couldn't read a startup location: " + ex.Message);
        }
    }

    public async Task SetEnabledAsync(StartupItem item, bool enabled, CancellationToken ct = default)
    {
        if (item.LockedReason != null)
            throw new UnauthorizedAccessException(item.LockedReason);
        if (item.RequiresAdmin && !isElevated)
            throw new UnauthorizedAccessException("Changing this item needs administrator rights.");

        switch (item.Source)
        {
            case StartupSource.RegistryUser:
            case StartupSource.RegistryMachine:
            case StartupSource.RegistryMachine32:
            case StartupSource.StartupFolderUser:
            case StartupSource.StartupFolderAllUsers:
                WriteStartupApproved(item, enabled);
                break;
            case StartupSource.ScheduledTask:
                await SetTaskEnabledAsync(item.Key, enabled, ct);
                break;
            case StartupSource.StoreApp:
                SetStoreTaskEnabled(item.Key, enabled);
                break;
        }

        item.Enabled = enabled;
        log.Write("Startup", $"{(enabled ? "Enabled" : "Disabled")} \"{item.Name}\" ({item.SourceDescription})");
    }

    // --- Run keys & Startup folders --------------------------------------------------------------

    private static RegistryHive ApprovedHive(StartupSource source) =>
        source is StartupSource.RegistryUser or StartupSource.StartupFolderUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    private static bool IsMachineWide(StartupSource source) => ApprovedHive(source) == RegistryHive.LocalMachine;

    private IEnumerable<StartupItem> ReadRunKey(RunKey key)
    {
        using var hive = RegistryKey.OpenBaseKey(key.Hive, RegistryView.Registry64);
        using var run = hive.OpenSubKey(key.Path);
        if (run == null) yield break;
        using var approved = hive.OpenSubKey(StartupApproved.UserKeyBase + "\\" + StartupApproved.SubKeyFor(key.Source));

        foreach (string name in run.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || run.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command)) continue;
            yield return Build(key.Source, name, name, command, StartupApproved.IsEnabled(approved?.GetValue(name) as byte[]));
        }
    }

    private IEnumerable<StartupItem> ReadStartupFolder(StartupSource source, string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) yield break;
        using var hive = RegistryKey.OpenBaseKey(ApprovedHive(source), RegistryView.Registry64);
        using var approved = hive.OpenSubKey(StartupApproved.UserKeyBase + "\\" + StartupApproved.SubKeyFor(source));

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string command = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) ?? file : file;
            yield return Build(source, Path.GetFileNameWithoutExtension(file), fileName, Quote(command),
                StartupApproved.IsEnabled(approved?.GetValue(fileName) as byte[]));
        }
    }

    private static void WriteStartupApproved(StartupItem item, bool enabled)
    {
        using var hive = RegistryKey.OpenBaseKey(ApprovedHive(item.Source), RegistryView.Registry64);
        using var approved = hive.CreateSubKey(StartupApproved.UserKeyBase + "\\" + StartupApproved.SubKeyFor(item.Source), writable: true);
        approved.SetValue(item.Key, StartupApproved.Encode(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
    }

    private StartupItem Build(StartupSource source, string name, string key, string command, bool enabled, string? publisherOverride = null)
    {
        string exe = CommandLine.ExtractExecutable(command);
        bool rooted = Path.IsPathFullyQualified(exe);
        bool missing = rooted && !File.Exists(exe);
        string? publisher = publisherOverride;
        string? description = null;
        if (rooted && !missing)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(exe);
                publisher ??= string.IsNullOrWhiteSpace(version.CompanyName) ? null : version.CompanyName.Trim();
                description = string.IsNullOrWhiteSpace(version.FileDescription) ? null : version.FileDescription.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // version info is cosmetic
            }
        }

        var (advice, reason) = StartupAdvisor.Advise(name + " " + description, command, publisher, missing);
        return new StartupItem
        {
            Id = $"{source}|{key}",
            Name = name,
            Key = key,
            Command = command,
            Source = source,
            Enabled = enabled,
            FilePath = rooted ? exe : null,
            FileMissing = missing,
            Publisher = publisher,
            Description = description,
            RequiresAdmin = IsMachineWide(source) || source == StartupSource.ScheduledTask,
            Advice = advice,
            AdviceReason = reason,
        };
    }

    private static string Quote(string path) => path.Contains(' ') && !path.StartsWith('"') ? $"\"{path}\"" : path;

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnk);
                string target = shortcut.TargetPath;
                string args = shortcut.Arguments;
                if (string.IsNullOrWhiteSpace(target)) return null;
                return string.IsNullOrWhiteSpace(args) ? Quote(target) : $"{Quote(target)} {args}";
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception)
        {
            return null; // unreadable shortcut: fall back to showing the .lnk itself
        }
    }

    // --- Scheduled tasks -------------------------------------------------------------------------

    private async Task<IReadOnlyList<StartupItem>> ReadScheduledTasksAsync(CancellationToken ct)
    {
        var result = await ProcessRunner.RunDetectingEncodingAsync(ProcessRunner.SystemTool("schtasks.exe"), "/query /xml", ct);
        if (result.ExitCode != 0) return [];

        return ScheduledTaskXml.ParseStartupTasks(result.Output)
            .Select(t => Build(StartupSource.ScheduledTask, t.Name, t.Path, t.Command ?? "(runs a built-in handler)", t.Enabled, t.Author))
            .ToList();
    }

    private static async Task SetTaskEnabledAsync(string taskPath, bool enabled, CancellationToken ct)
    {
        string args = $"/change /tn \"{taskPath.Replace("\"", "")}\" {(enabled ? "/enable" : "/disable")}";
        var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("schtasks.exe"), args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "Task Scheduler refused the change." : result.Error.Trim());
    }

    // --- Store apps ------------------------------------------------------------------------------

    private IEnumerable<StartupItem> ReadStoreApps()
    {
        using var root = Registry.CurrentUser.OpenSubKey(StoreTasksPath);
        if (root == null) yield break;
        var packages = new StorePackageNames();

        foreach (string family in root.GetSubKeyNames())
        {
            using var familyKey = root.OpenSubKey(family);
            if (familyKey == null) continue;

            foreach (string taskId in familyKey.GetSubKeyNames())
            {
                using var taskKey = familyKey.OpenSubKey(taskId);
                if (taskKey?.GetValue("State") is not int state) continue;

                var package = packages.Lookup(family);
                if (package == null) continue; // leftover from an uninstalled app

                var (advice, reason) = StartupAdvisor.Advise(package.Value.DisplayName + " " + family, null, package.Value.Publisher, false);
                yield return new StartupItem
                {
                    Id = $"{StartupSource.StoreApp}|{family}\\{taskId}",
                    Name = package.Value.DisplayName,
                    Key = $"{family}\\{taskId}",
                    Command = $"Store app: {family}",
                    Source = StartupSource.StoreApp,
                    Enabled = StoreStartupState.IsEnabled(state),
                    Publisher = package.Value.Publisher,
                    LockedReason = StoreStartupState.CanToggle(state) ? null : "Controlled by your organisation's policy.",
                    Advice = advice,
                    AdviceReason = reason,
                };
            }
        }
    }

    private static void SetStoreTaskEnabled(string key, bool enabled)
    {
        using var taskKey = Registry.CurrentUser.OpenSubKey(StoreTasksPath + "\\" + key, writable: true)
                            ?? throw new InvalidOperationException("This startup task no longer exists.");
        if (taskKey.GetValue("State") is int state && !StoreStartupState.CanToggle(state))
            throw new UnauthorizedAccessException("This startup task is controlled by your organisation's policy.");
        taskKey.SetValue("State", StoreStartupState.For(enabled), RegistryValueKind.DWord);
    }
}
