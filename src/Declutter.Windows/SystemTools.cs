using System.ComponentModel;
using System.Diagnostics;
using Declutter.Core.Util;

namespace Declutter.Windows;

public sealed record RestorePointOutcome(bool Created, string Message);

public static class SystemTools
{
    /// <summary>Creates a System Restore point so changes can be rolled back from Windows Recovery.</summary>
    public static async Task<RestorePointOutcome> CreateRestorePointAsync(IActivityLog log, CancellationToken ct = default)
    {
        if (!WindowsEnvironment.IsElevated)
            return new RestorePointOutcome(false, "Creating a restore point needs administrator rights.");

        const string script =
            "$ErrorActionPreference='Stop'; " +
            "try { Checkpoint-Computer -Description 'Before Declutter cleanup' -RestorePointType 'MODIFY_SETTINGS' -WarningVariable w -WarningAction SilentlyContinue; " +
            "if ($w) { Write-Output ('WARN:' + $w[0]) } else { Write-Output 'OK' } } " +
            "catch { Write-Output ('ERR:' + $_.Exception.Message); exit 1 }";

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"", ct);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new RestorePointOutcome(false, "Couldn't start PowerShell: " + ex.Message);
        }

        string output = result.Output.Trim();
        RestorePointOutcome outcome;
        if (output.EndsWith("OK", StringComparison.Ordinal))
        {
            outcome = new RestorePointOutcome(true, "Restore point created. You can undo system changes from Settings → System → Recovery.");
        }
        else if (output.Contains("WARN:", StringComparison.Ordinal))
        {
            // Windows allows one restore point per 24 hours by default; an earlier one still protects you.
            outcome = new RestorePointOutcome(true, "Windows already made a restore point in the last 24 hours, so that one will be used.");
        }
        else
        {
            string reason = output.Contains("ERR:", StringComparison.Ordinal) ? output[(output.IndexOf("ERR:", StringComparison.Ordinal) + 4)..].Trim() : result.Error.Trim();
            outcome = new RestorePointOutcome(false,
                "Couldn't create a restore point. System Protection may be turned off for your C: drive " +
                "(open \"Create a restore point\" in the Start menu to turn it on). Details: " + reason);
        }

        log.Write("System", outcome.Message);
        return outcome;
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // Nothing useful to do if Explorer won't start.
        }
    }

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
    }

    /// <summary>Restarts this app with a UAC prompt. Returns false if the user said no.</summary>
    public static bool RestartElevated()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false; // UAC prompt cancelled
        }
    }

    /// <summary>Which of the given process names are currently running.</summary>
    public static IReadOnlySet<string> RunningProcesses(IEnumerable<string> names)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0) running.Add(name);
            foreach (var p in processes) p.Dispose();
        }
        return running;
    }

    public sealed record DriveSpace(string Name, long Total, long Free)
    {
        public long Used => Total - Free;
        public double UsedFraction => Total == 0 ? 0 : (double)Used / Total;
    }

    public static DriveSpace? GetDriveSpace(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? new DriveSpace(drive.Name, drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
