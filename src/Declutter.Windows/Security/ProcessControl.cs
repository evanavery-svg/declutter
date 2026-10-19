using System.Diagnostics;
using Declutter.Core.Safety;
using Declutter.Core.Util;

namespace Declutter.Windows.Security;

public static class ProcessControl
{
    /// <summary>
    /// Ends a process flagged by the security check. Refuses genuine Windows processes, and re-checks the
    /// program path first so a reused process ID can never end the wrong program.
    /// </summary>
    public static (bool Ok, string Message) TryEnd(int pid, string? expectedPath, KnownFolders folders, IActivityLog log)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            string? path = null;
            try { path = process.MainModule?.FileName; } catch (Exception) { }

            if (expectedPath != null && path != null && !PathUtil.PathsEqual(path, expectedPath))
                return (false, "That program has already closed.");
            if (path != null && Core.Security.Locations.IsProtectedInstallLocation(path, folders) &&
                PathUtil.IsSameOrUnder(path, folders.WindowsDir))
                return (false, "Declutter won't end a program that lives in the Windows folder.");

            process.Kill(entireProcessTree: false);
            process.WaitForExit(5000);
            log.Write("Security", $"Ended process {pid} ({path ?? process.ProcessName})");
            return (true, "Ended. It may start again at the next sign-in. Turn it off at startup or let Defender remove it.");
        }
        catch (ArgumentException)
        {
            return (false, "That program has already closed.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return (false, "Windows wouldn't let Declutter end it: " + ex.Message);
        }
    }
}
