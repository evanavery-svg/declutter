using Declutter.Core.Safety;
using Declutter.Core.Util;

namespace Declutter.Core.Security;

public sealed record DefenderStatus
{
    public bool AntivirusEnabled { get; init; }
    public bool RealTimeProtectionEnabled { get; init; }
    public int? SignatureAgeDays { get; init; }
    public int? QuickScanAgeDays { get; init; }
    public int? FullScanAgeDays { get; init; }

    /// <summary>"Normal", "Passive Mode", "EDR Block Mode"… (Passive = another antivirus is in charge).</summary>
    public string? RunningMode { get; init; }

    public bool IsPassive => RunningMode?.Contains("Passive", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>An antivirus registered with Windows Security Center.</summary>
public sealed record AntivirusProduct(string Name, bool Enabled, bool UpToDate)
{
    // Not just "Defender": Bitdefender would match.
    public bool IsDefender => Name.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase) ||
                              Name.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes Security Center's productState: the middle byte is 0x10/0x11 when real-time scanning is on,
    /// the low byte 0x00 when definitions are up to date (0x10 when out of date).
    /// </summary>
    public static AntivirusProduct FromProductState(string name, uint state)
    {
        uint scanner = (state >> 8) & 0xFF;
        uint definitions = state & 0xFF;
        return new AntivirusProduct(name, scanner is 0x10 or 0x11, definitions == 0x00);
    }
}

public sealed record DefenderThreat(string Name, int StatusId, IReadOnlyList<string> Resources, DateTime? DetectedUtc)
{
    /// <summary>Detected, quarantine/remove failed, abandoned or block failed: still on the PC.</summary>
    public bool IsActive => StatusId is 1 or 102 or 103 or 105 or 107;

    /// <summary>The user chose to allow it.</summary>
    public bool IsAllowed => StatusId == 5;

    /// <summary>Resource strings look like "file:_C:\path\to\file.exe"; returns the first file path.</summary>
    public string? FirstFile => Resources
        .Select(r => r.StartsWith("file:_", StringComparison.OrdinalIgnoreCase) ? r[6..] : r.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? r[5..] : null)
        .FirstOrDefault(r => r != null);
}

public sealed record DefenderExclusions(IReadOnlyList<string> Paths, IReadOnlyList<string> Processes, IReadOnlyList<string> Extensions);

/// <summary>Turns Microsoft Defender's own status into plain-English findings.</summary>
public static class DefenderCheck
{
    public const string ProtectionId = "antivirus";
    public const string ThreatsId = "threats";
    public const string ExclusionsId = "exclusions";

    private static readonly string[] RiskyExtensions = [".exe", ".dll", ".ps1", ".bat", ".cmd", ".vbs", ".js", ".jse", ".vbe", ".scr", ".com", ".hta", ".msi", ".lnk", ".zip"];

    public static CheckOutcome EvaluateProtection(DefenderStatus? defender, IReadOnlyList<AntivirusProduct> products)
    {
        var findings = new List<SecurityFinding>();
        var otherAv = products.Where(p => !p.IsDefender && p.Enabled).ToList();

        if (otherAv.Count > 0)
        {
            foreach (var av in otherAv.Where(a => !a.UpToDate))
            {
                findings.Add(new SecurityFinding
                {
                    CheckId = ProtectionId,
                    Severity = Severity.Warning,
                    Title = $"{av.Name} is out of date",
                    Explanation = "Your antivirus hasn't updated its virus definitions, so it can miss new threats. Open it and run an update.",
                });
            }
            return new CheckOutcome(ProtectionId, $"Protected by {string.Join(" and ", otherAv.Select(a => a.Name))}", findings);
        }

        if (defender == null)
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ProtectionId,
                Severity = Severity.Warning,
                Title = "Couldn't read Microsoft Defender's status",
                Explanation = "Open Windows Security to check that virus protection is on.",
                Actions = [FindingAction.OpenWindowsSecurity],
            });
            return new CheckOutcome(ProtectionId, "", findings);
        }

        if (!defender.AntivirusEnabled || defender.IsPassive)
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ProtectionId,
                Severity = Severity.Critical,
                Title = "No antivirus is protecting this PC",
                Explanation = "Microsoft Defender is turned off and no other antivirus is registered. Some malware switches Defender off " +
                              "to hide. Turn it back on in Windows Security → Virus & threat protection.",
                Actions = [FindingAction.OpenWindowsSecurity],
            });
        }
        else if (!defender.RealTimeProtectionEnabled)
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ProtectionId,
                Severity = Severity.Critical,
                Title = "Real-time protection is off",
                Explanation = "Defender only checks files when you scan manually, so new malware can run unnoticed. Turn real-time " +
                              "protection back on in Windows Security → Virus & threat protection → Manage settings.",
                Actions = [FindingAction.OpenWindowsSecurity],
            });
        }

        if (defender.AntivirusEnabled && defender.SignatureAgeDays is > 7 and < 10000)
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ProtectionId,
                Severity = Severity.Warning,
                Title = $"Virus definitions are {defender.SignatureAgeDays} days old",
                Explanation = "Defender normally updates every day. Old definitions can't recognise new threats.",
                Actions = [FindingAction.UpdateDefinitions],
            });
        }

        int? lastScan = Min(defender.QuickScanAgeDays, defender.FullScanAgeDays);
        if (defender.AntivirusEnabled && (lastScan is null or > 30))
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ProtectionId,
                Severity = Severity.Info,
                Title = lastScan is null ? "Defender hasn't scanned this PC yet" : $"Defender's last scan was {lastScan} days ago",
                Explanation = "A quick scan checks the places malware usually hides and takes a few minutes.",
                Actions = [FindingAction.RunQuickScan],
            });
        }

        return new CheckOutcome(ProtectionId, "Microsoft Defender is on and up to date", findings);
    }

    public static CheckOutcome EvaluateThreats(IReadOnlyList<DefenderThreat> threats, DateTime nowUtc)
    {
        var findings = new List<SecurityFinding>();
        foreach (var t in threats.Where(t => t.IsActive).DistinctBy(t => t.Name))
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ThreatsId,
                Severity = Severity.Critical,
                Title = $"Defender found a threat it hasn't removed: {t.Name}",
                Explanation = "Microsoft Defender detected this but it's still on your PC. Open Windows Security → Protection history " +
                              "and choose Remove or Quarantine, then run a quick scan.",
                Evidence = t.FirstFile,
                FilePath = t.FirstFile,
                Actions = [FindingAction.OpenWindowsSecurity, FindingAction.RunQuickScan],
            });
        }

        foreach (var t in threats.Where(t => t.IsAllowed).DistinctBy(t => t.Name))
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ThreatsId,
                Severity = Severity.Warning,
                Title = $"A detected threat was allowed to stay: {t.Name}",
                Explanation = "Someone chose \"Allow\" when Defender flagged this. If that wasn't a deliberate choice for a program you " +
                              "trust, remove the allowance in Windows Security → Protection history.",
                Evidence = t.FirstFile,
                Actions = [FindingAction.OpenWindowsSecurity],
            });
        }

        var recent = threats.Where(t => !t.IsActive && !t.IsAllowed && t.DetectedUtc is { } d && nowUtc - d < TimeSpan.FromDays(30)).ToList();
        if (recent.Count > 0)
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ThreatsId,
                Severity = Severity.Info,
                Title = $"Defender blocked or removed {recent.Count} threat{(recent.Count == 1 ? "" : "s")} in the last 30 days",
                Explanation = "They were dealt with. Nothing to do, but it's worth thinking about where they came from (a download, an email attachment, a USB stick).",
                Evidence = string.Join("\n", recent.Select(t => t.Name).Distinct().Take(6)),
                Actions = [FindingAction.OpenWindowsSecurity],
            });
        }

        return new CheckOutcome(ThreatsId, "Defender has no active threats on record", findings);
    }

    public static CheckOutcome EvaluateExclusions(DefenderExclusions? exclusions, KnownFolders f)
    {
        if (exclusions == null)
            return new CheckOutcome(ExclusionsId, "", [new SecurityFinding
            {
                CheckId = ExclusionsId,
                Severity = Severity.Info,
                Title = "Defender exclusions weren't checked",
                Explanation = "Windows only shows the list of files Defender ignores to administrators. Restart Declutter as administrator to include this check.",
            }]);

        var findings = new List<SecurityFinding>();
        string[] broad = [f.UsersRoot, f.UserProfile, f.AppDataRoot, f.LocalAppData, f.RoamingAppData, f.TempDir, f.WindowsDir,
                          f.ProgramData, f.ProgramFiles, f.ProgramFilesX86, .. f.PersonalFolders, Path.Combine(f.UsersRoot, "Public")];

        foreach (var path in exclusions.Paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            string expanded = Environment.ExpandEnvironmentVariables(path).TrimEnd('\\', '*');
            bool isBroad = expanded.Length <= 3 || !Path.IsPathFullyQualified(expanded) ||
                           broad.Any(b => !string.IsNullOrEmpty(b) && SafePathsEqual(expanded, b));
            findings.Add(new SecurityFinding
            {
                CheckId = ExclusionsId,
                Severity = isBroad ? Severity.Critical : Severity.Info,
                Title = isBroad ? $"Defender is told to ignore a whole folder: {path}" : $"Defender ignores: {path}",
                Explanation = isBroad
                    ? "Nothing in this location is ever scanned. Malware often adds exclusions like this to hide from antivirus. " +
                      "Unless you added it on purpose, remove it in Windows Security → Virus & threat protection → Manage settings → Exclusions."
                    : "Developers and gamers add exclusions like this to speed things up. Remove it if you don't know why it's there.",
                Evidence = path,
                Actions = [FindingAction.OpenWindowsSecurity],
            });
        }

        foreach (var ext in exclusions.Extensions.Where(e => RiskyExtensions.Contains(NormalizeExtension(e), StringComparer.OrdinalIgnoreCase)))
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ExclusionsId,
                Severity = Severity.Critical,
                Title = $"Defender is told to ignore every {NormalizeExtension(ext)} file",
                Explanation = "That's a type of file that can run code, so excluding it switches off protection for most malware. Remove it " +
                              "in Windows Security → Virus & threat protection → Manage settings → Exclusions.",
                Evidence = ext,
                Actions = [FindingAction.OpenWindowsSecurity],
            });
        }

        foreach (var process in exclusions.Processes.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ExclusionsId,
                Severity = Locations.IsUserWritable(process, f) || !Path.IsPathFullyQualified(process) ? Severity.Warning : Severity.Info,
                Title = $"Defender ignores files opened by: {Path.GetFileName(process)}",
                Explanation = "Anything this program reads or writes isn't scanned. Fine for a tool you trust; remove it if you don't recognise it.",
                Evidence = process,
                Actions = [FindingAction.OpenWindowsSecurity],
            });
        }

        return new CheckOutcome(ExclusionsId, "No risky Defender exclusions", findings);
    }

    private static string NormalizeExtension(string ext) => "." + ext.Trim().TrimStart('*').TrimStart('.');

    private static bool SafePathsEqual(string a, string b)
    {
        try
        {
            return PathUtil.PathsEqual(a, b);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int? Min(int? a, int? b) =>
        a is null || a >= 10000 ? (b is null || b >= 10000 ? null : b) : b is null || b >= 10000 ? a : Math.Min(a.Value, b.Value);
}
