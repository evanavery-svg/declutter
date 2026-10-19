using System.Text.RegularExpressions;
using Declutter.Core.Safety;
using Declutter.Core.Startup;
using Declutter.Core.Util;

namespace Declutter.Core.Security;

/// <summary>
/// Malware has to start itself again after every restart, so it hides among startup programs: usually
/// as a hidden or encoded script, or as an unsigned program in a temp or app-data folder.
/// </summary>
public static partial class PersistenceCheck
{
    public const string Id = "persistence";

    public static CheckOutcome Evaluate(IReadOnlyList<StartupItem> items, IReadOnlyDictionary<string, SignatureInfo> signatures, KnownFolders folders)
    {
        var findings = new List<SecurityFinding>();
        foreach (var item in items.Where(i => i.Enabled))
        {
            if (DescribeScriptTrick(item.Command) is { } trick)
            {
                findings.Add(new SecurityFinding
                {
                    CheckId = Id,
                    Severity = Severity.Critical,
                    Title = $"Startup item runs a hidden script: {item.Name}",
                    Explanation = $"Every time you sign in, this {trick}. Normal apps don't start like this; it's a common way for " +
                                  "malware to keep itself running. Turning it off is safe and can be undone.",
                    Evidence = item.Command,
                    FilePath = item.FilePath,
                    StartupItemId = item.Id,
                    Actions = [FindingAction.DisableStartup, FindingAction.SearchOnline],
                });
                continue;
            }

            if (item.FilePath is not { } path || item.FileMissing) continue;
            var sig = signatures.GetValueOrDefault(path);

            if (Locations.IsTemp(path, folders))
            {
                findings.Add(new SecurityFinding
                {
                    CheckId = Id,
                    Severity = sig is { IsValid: true } ? Severity.Warning : Severity.Critical,
                    Title = $"Startup item runs from a temp folder: {item.Name}",
                    Explanation = "Real programs don't start from the Temp folder every time you sign in. Installers run from " +
                                  "there briefly, but anything that starts from there at every sign-in is suspicious.",
                    Evidence = item.Command,
                    FilePath = path,
                    StartupItemId = item.Id,
                    Actions = [FindingAction.ScanWithDefender, FindingAction.DisableStartup, FindingAction.ShowFile, FindingAction.SearchOnline],
                });
            }
            else if (Locations.IsUserWritable(path, folders) && sig is { IsSigned: false })
            {
                findings.Add(new SecurityFinding
                {
                    CheckId = Id,
                    Severity = Severity.Warning,
                    Title = $"Unsigned program starts with Windows: {item.Name}",
                    Explanation = "It lives in a folder any program can write to and isn't digitally signed by its maker, so there's " +
                                  "no way to tell who made it. Many small legitimate tools are like this too. If you don't recognise it, " +
                                  "scan it and turn it off.",
                    Evidence = path,
                    FilePath = path,
                    StartupItemId = item.Id,
                    Actions = [FindingAction.ScanWithDefender, FindingAction.DisableStartup, FindingAction.ShowFile, FindingAction.SearchOnline],
                });
            }
            else if (sig is { IsSigned: true, IsValid: false })
            {
                findings.Add(new SecurityFinding
                {
                    CheckId = Id,
                    Severity = Severity.Warning,
                    Title = $"Startup program has a broken signature: {item.Name}",
                    Explanation = $"Its digital signature doesn't check out ({sig.Status}). The file may have been changed since its " +
                                  "maker signed it, which can mean tampering.",
                    Evidence = path,
                    FilePath = path,
                    StartupItemId = item.Id,
                    Actions = [FindingAction.ScanWithDefender, FindingAction.DisableStartup, FindingAction.ShowFile],
                });
            }
        }

        return new CheckOutcome(Id, "Nothing suspicious starts when you sign in", findings);
    }

    /// <summary>Describes the trick if a command line uses a script host in a way malware typically does.</summary>
    public static string? DescribeScriptTrick(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string cmd = command.ToLowerInvariant();
        string exe = Path.GetFileName(CommandLine.Split(command).FileName).ToLowerInvariant();

        bool powershell = exe is "powershell.exe" or "powershell" or "pwsh.exe" or "pwsh" || cmd.Contains("powershell");
        if (powershell)
        {
            if (EncodedCommand().IsMatch(cmd)) return "runs a PowerShell command that has been scrambled (encoded) so you can't read it";
            if (cmd.Contains("downloadstring") || cmd.Contains("downloadfile") || cmd.Contains("invoke-webrequest") ||
                cmd.Contains("iwr ") || cmd.Contains("net.webclient") || cmd.Contains("start-bitstransfer"))
                return "downloads and runs code from the internet with PowerShell";
            if (cmd.Contains("frombase64string") || InvokeExpression().IsMatch(cmd))
                return "runs PowerShell code that unpacks and executes more hidden code";
            if (HiddenWindow().IsMatch(cmd)) return "runs PowerShell in a hidden window";
        }

        if (exe is "mshta.exe" or "mshta" && (cmd.Contains("http") || cmd.Contains("javascript:") || cmd.Contains("vbscript:")))
            return "runs a script with mshta, a tool malware abuses to run code from websites";
        if (exe is "regsvr32.exe" or "regsvr32" && cmd.Contains("/i:http"))
            return "uses regsvr32 to run code from the internet";
        if (exe is "rundll32.exe" or "rundll32" && (cmd.Contains("javascript:") || cmd.Contains("\\appdata\\") || cmd.Contains("\\temp\\")))
            return "uses rundll32 to run code from a script or a user folder";
        if (exe is "wscript.exe" or "cscript.exe" or "wscript" or "cscript" &&
            (cmd.Contains("\\appdata\\") || cmd.Contains("\\temp\\") || cmd.Contains("\\public\\") || cmd.Contains("//b")))
            return "runs a hidden Windows script (.vbs/.js) from a user folder";
        if (exe is "cmd.exe" or "cmd" && cmd.Contains("powershell") && HiddenWindow().IsMatch(cmd))
            return "runs PowerShell in a hidden window";

        return null;
    }

    [GeneratedRegex(@"\s-(e|ec|enc|enco|encod|encode|encoded|encodedc|encodedco|encodedcom|encodedcomm|encodedcomma|encodedcomman|encodedcommand)\s+[a-z0-9+/=]{16,}")]
    private static partial Regex EncodedCommand();

    [GeneratedRegex(@"\biex\b|invoke-expression")]
    private static partial Regex InvokeExpression();

    [GeneratedRegex(@"-(w|wi|win|wind|windo|window|windows|windowst|windowsty|windowstyl|windowstyle)\s+(h|hi|hid|hidd|hidde|hidden|1)\b")]
    private static partial Regex HiddenWindow();
}
