using Declutter.Core.Safety;
using Declutter.Core.Util;

namespace Declutter.Core.Security;

/// <summary>
/// Malware often names itself after a Windows process (svchost.exe, csrss.exe…) to blend in, or uses
/// a near-miss spelling. The real ones only ever run from the Windows folder.
/// </summary>
public static class ImpostorCheck
{
    public const string Id = "impostors";

    /// <summary>Windows process names and the folder (under C:\Windows) the genuine one lives in.</summary>
    internal static readonly Dictionary<string, string[]> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["svchost.exe"] = ["System32", "SysWOW64"],
        ["csrss.exe"] = ["System32"],
        ["lsass.exe"] = ["System32"],
        ["smss.exe"] = ["System32"],
        ["wininit.exe"] = ["System32"],
        ["winlogon.exe"] = ["System32"],
        ["services.exe"] = ["System32"],
        ["spoolsv.exe"] = ["System32"],
        ["taskhostw.exe"] = ["System32"],
        ["dwm.exe"] = ["System32"],
        ["ctfmon.exe"] = ["System32", "SysWOW64"],
        ["conhost.exe"] = ["System32"],
        ["dllhost.exe"] = ["System32", "SysWOW64"],
        ["rundll32.exe"] = ["System32", "SysWOW64"],
        ["sihost.exe"] = ["System32"],
        ["fontdrvhost.exe"] = ["System32"],
        ["explorer.exe"] = [""],
        ["RuntimeBroker.exe"] = ["System32", "SysWOW64"],
        ["SearchIndexer.exe"] = ["System32"],
        ["WmiPrvSE.exe"] = ["System32\\wbem", "SysWOW64\\wbem"],
    };

    /// <summary>Misspellings malware uses to look like Windows at a glance.</summary>
    internal static readonly HashSet<string> LookAlikes = new(StringComparer.OrdinalIgnoreCase)
    {
        "scvhost.exe", "svhost.exe", "svchosts.exe", "svch0st.exe", "svchos.exe", "svcchost.exe", "svchost32.exe",
        "csrs.exe", "csrrs.exe", "cssrs.exe", "lsas.exe", "lsasss.exe", "isass.exe", "1sass.exe", "smms.exe",
        "winlogin.exe", "win1ogon.exe", "explore.exe", "explorer32.exe", "expl0rer.exe", "explorar.exe", "servises.exe",
        "spoolsvc.exe", "taskhosts.exe", "taskhostex.exe", "dlhost.exe", "conhosts.exe", "rundll.exe", "runtimebrokers.exe",
        "wmiprvse32.exe", "windowsupdate.exe", "winupdate.exe", "microsoftupdate.exe", "windefender.exe", "msdefender.exe",
    };

    public static CheckOutcome Evaluate(IReadOnlyList<ProcessInfo> processes, KnownFolders folders)
    {
        var findings = new List<SecurityFinding>();

        foreach (var p in processes)
        {
            string name = Path.GetFileName(p.Name);
            if (SystemProcesses.TryGetValue(name, out var homes))
            {
                if (p.Path == null) continue; // protected process we can't inspect: these are the genuine ones
                string folder = Path.GetDirectoryName(p.Path) ?? "";
                bool genuine = homes.Any(h => PathUtil.PathsEqual(folder, Path.Combine([folders.WindowsDir, .. h.Split('\\', StringSplitOptions.RemoveEmptyEntries)])));
                if (genuine) continue;

                findings.Add(new SecurityFinding
                {
                    CheckId = Id,
                    Severity = Severity.Critical,
                    Title = $"Program pretending to be Windows: {name}",
                    Explanation = $"The real {name} only ever runs from the Windows folder, but this one runs from somewhere else. " +
                                  "Disguising itself as a Windows process is a classic malware trick.",
                    Evidence = p.Path,
                    FilePath = p.Path,
                    ProcessId = p.Pid,
                    Actions = [FindingAction.ScanWithDefender, FindingAction.ShowFile, FindingAction.EndProcess],
                });
            }
            else if (LookAlikes.Contains(name))
            {
                findings.Add(new SecurityFinding
                {
                    CheckId = Id,
                    Severity = Severity.Critical,
                    Title = $"Program with a name that imitates Windows: {name}",
                    Explanation = "This name is a near-miss spelling of a Windows process. Windows has no program by this name. " +
                                  "Malware uses names like this to look harmless in Task Manager.",
                    Evidence = p.Path ?? name,
                    FilePath = p.Path,
                    ProcessId = p.Pid,
                    Actions = p.Path == null
                        ? [FindingAction.SearchOnline, FindingAction.EndProcess]
                        : [FindingAction.ScanWithDefender, FindingAction.ShowFile, FindingAction.EndProcess],
                });
            }
        }

        return new CheckOutcome(Id, "No programs pretending to be part of Windows", findings);
    }
}
