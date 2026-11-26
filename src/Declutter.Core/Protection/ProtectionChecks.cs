using System.Globalization;
using Declutter.Core.Health;
using Declutter.Core.Util;

namespace Declutter.Core.Protection;

/// <summary>A verdict for one protection area, with the reasons and what to do.</summary>
public sealed record ProtectionResult(string Area, HealthGrade Grade, string Summary, IReadOnlyList<HealthNote> Notes, string? ActionUri = null, string? ActionLabel = null);

// ---- Backup ---------------------------------------------------------------------------------------

public sealed record BackupEvidence
{
    /// <summary>Personal folders (Desktop, Documents, Pictures) and whether each is inside a OneDrive folder.</summary>
    public IReadOnlyDictionary<string, bool> FoldersInOneDrive { get; init; } = new Dictionary<string, bool>();
    public bool OneDriveRunning { get; init; }

    /// <summary>When File History last finished a backup, if it's set up.</summary>
    public DateTime? FileHistoryLastBackupUtc { get; init; }
    public bool FileHistoryConfigured { get; init; }

    /// <summary>Names of installed backup apps (Backblaze, Acronis…), which Declutter can't look inside.</summary>
    public IReadOnlyList<string> BackupApps { get; init; } = [];
}

public static class BackupCheck
{
    /// <summary>Installed-app names that are backup software.</summary>
    public static readonly string[] KnownBackupApps =
    [
        "backblaze", "carbonite", "acronis", "macrium", "idrive", "crashplan", "veeam agent", "easeus todo backup", "aomei backupper",
        "paragon backup", "spideroak", "sync.com", "pcloud", "mega", "google drive", "dropbox", "icloud", "box drive", "duplicati", "arq",
    ];

    public static ProtectionResult Evaluate(BackupEvidence e, DateTime nowUtc)
    {
        var notes = new List<HealthNote>();
        var inCloud = e.FoldersInOneDrive.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        var notInCloud = e.FoldersInOneDrive.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
        bool fileHistoryRecent = e.FileHistoryLastBackupUtc is { } last && nowUtc - last < TimeSpan.FromDays(14);

        if (inCloud.Count > 0)
            notes.Add(new(e.OneDriveRunning ? NoteKind.Good : NoteKind.Warning, e.OneDriveRunning
                ? $"{Join(inCloud)} {(inCloud.Count == 1 ? "is" : "are")} backed up to OneDrive."
                : $"{Join(inCloud)} {(inCloud.Count == 1 ? "is" : "are")} set up for OneDrive, but OneDrive isn't running, so changes aren't being backed up right now."));
        if (notInCloud.Count > 0 && inCloud.Count > 0)
            notes.Add(new(NoteKind.Info, $"{Join(notInCloud)} {(notInCloud.Count == 1 ? "isn't" : "aren't")} in OneDrive."));

        if (e.FileHistoryLastBackupUtc is { } fh)
            notes.Add(new(fileHistoryRecent ? NoteKind.Good : NoteKind.Warning, fileHistoryRecent
                ? $"File History last backed up {Ago(nowUtc - fh)}."
                : $"File History is set up, but its last backup was {Ago(nowUtc - fh)}. Is the backup drive plugged in?"));
        else if (e.FileHistoryConfigured)
            notes.Add(new(NoteKind.Warning, "File History is set up but hasn't finished a backup yet."));

        foreach (var app in e.BackupApps)
            notes.Add(new(NoteKind.Info, $"{app} is installed. Declutter can't see inside it; open it to check its last backup."));

        bool cloudCoversDocuments = e.OneDriveRunning && e.FoldersInOneDrive.TryGetValue("Documents", out bool docs) && docs;
        HealthGrade grade;
        string summary;
        if ((cloudCoversDocuments && notInCloud.Count == 0) || fileHistoryRecent)
        {
            grade = HealthGrade.Good;
            summary = "Your files are backed up";
        }
        else if (cloudCoversDocuments || inCloud.Count > 0 || e.BackupApps.Count > 0 || e.FileHistoryConfigured)
        {
            grade = HealthGrade.Fair;
            summary = "Some of your files are backed up";
        }
        else
        {
            grade = HealthGrade.Poor;
            summary = "No backup found";
            notes.Insert(0, new(NoteKind.Critical,
                "If this PC broke or was stolen today, everything in Documents, Desktop and Pictures would be gone. " +
                "The easiest fix is Windows Backup (it uses OneDrive): open it from the Start menu and switch on folder backup."));
        }
        return new ProtectionResult("Backup", grade, summary, notes, "ms-settings:backup", "Open backup settings");
    }

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    internal static string Ago(TimeSpan t) => t.TotalDays >= 2 ? $"{(int)t.TotalDays} days ago" : t.TotalHours >= 2 ? $"{(int)t.TotalHours} hours ago" : "recently";
}

// ---- Device security ------------------------------------------------------------------------------

public sealed record DeviceSecurityEvidence
{
    /// <summary>BitLocker / Device Encryption on the Windows drive. Null = couldn't read (needs admin).</summary>
    public bool? DriveEncrypted { get; init; }
    public bool? SecureBoot { get; init; }
    public bool? TpmPresent { get; init; }
    public bool IsLaptop { get; init; }
}

public static class DeviceSecurityCheck
{
    public static ProtectionResult Evaluate(DeviceSecurityEvidence e)
    {
        var notes = new List<HealthNote>();
        var grade = HealthGrade.Good;

        switch (e.DriveEncrypted)
        {
            case true:
                notes.Add(new(NoteKind.Good, "The Windows drive is encrypted. If the PC is lost or stolen, your files can't be read without your sign-in."));
                break;
            case false:
                grade = e.IsLaptop ? HealthGrade.Fair : grade;
                notes.Add(new(e.IsLaptop ? NoteKind.Warning : NoteKind.Info,
                    "The Windows drive isn't encrypted. If the " + (e.IsLaptop ? "laptop" : "PC") + " is lost or stolen, anyone can take the drive out and read your files. " +
                    "Turn on Device Encryption or BitLocker in Settings → Privacy & security → Device encryption (write down the recovery key it gives you)."));
                break;
            default:
                notes.Add(new(NoteKind.Info, "Drive encryption can only be checked with administrator rights."));
                break;
        }

        if (e.SecureBoot == false)
        {
            grade = HealthGrade.Fair;
            notes.Add(new(NoteKind.Warning, "Secure Boot is off. It stops malware from loading before Windows starts. It's switched on in the PC's firmware (BIOS/UEFI) settings."));
        }
        else if (e.SecureBoot == true)
        {
            notes.Add(new(NoteKind.Good, "Secure Boot is on."));
        }

        if (e.TpmPresent == true) notes.Add(new(NoteKind.Good, "The security chip (TPM) is present and working."));
        else if (e.TpmPresent == false) notes.Add(new(NoteKind.Info, "No security chip (TPM) was found. Windows 11 normally requires one."));

        string summary = grade == HealthGrade.Good ? "Protected if it's lost or stolen" : "Could be better protected";
        if (e.DriveEncrypted is null && e.SecureBoot is null) { grade = HealthGrade.Unknown; summary = "Couldn't check everything"; }
        return new ProtectionResult("Device security", grade, summary, notes, "ms-settings:deviceencryption", "Open encryption settings");
    }
}

// ---- Windows Update -------------------------------------------------------------------------------

public sealed record UpdateEvidence
{
    public DateTime? LastSuccessfulUpdateUtc { get; init; }
    public bool RestartPending { get; init; }
    public TimeSpan? Uptime { get; init; }

    /// <summary>e.g. "Windows 11 Home" and "24H2".</summary>
    public string? WindowsName { get; init; }
    public string? WindowsVersion { get; init; }
}

public static class WindowsUpdateCheck
{
    /// <summary>
    /// When each version stops getting security updates (Home and Pro; Enterprise/Education get about a year
    /// longer). Microsoft publishes these dates in advance.
    /// </summary>
    private static readonly Dictionary<string, DateTime> Windows11HomePro = new()
    {
        ["21H2"] = new(2023, 10, 10),
        ["22H2"] = new(2024, 10, 8),
        ["23H2"] = new(2025, 11, 11),
        ["24H2"] = new(2026, 10, 13),
        ["25H2"] = new(2027, 10, 12),
    };

    private static readonly DateTime Windows10End = new(2025, 10, 14);

    public static DateTime? EndOfSupport(string? windowsName, string? version)
    {
        string name = windowsName ?? "";
        if (name.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)) return Windows10End;
        if (!name.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) || version == null) return null;
        if (!Windows11HomePro.TryGetValue(version.ToUpperInvariant(), out var end)) return null;
        bool longer = name.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || name.Contains("Education", StringComparison.OrdinalIgnoreCase);
        return longer ? end.AddYears(1) : end;
    }

    public static ProtectionResult Evaluate(UpdateEvidence e, DateTime nowUtc)
    {
        var notes = new List<HealthNote>();
        var grade = HealthGrade.Good;

        if (EndOfSupport(e.WindowsName, e.WindowsVersion) is { } end)
        {
            if (nowUtc.Date > end)
            {
                grade = HealthGrade.Poor;
                notes.Add(new(NoteKind.Critical, $"{e.WindowsName} {e.WindowsVersion} stopped getting security updates on {end.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}. " +
                    "Install the latest version from Windows Update (it's free); look for \"Feature update\"."));
            }
            else if (end - nowUtc.Date < TimeSpan.FromDays(90))
            {
                grade = HealthGrade.Fair;
                notes.Add(new(NoteKind.Warning, $"{e.WindowsVersion} stops getting security updates on {end.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}. Update to the latest version before then."));
            }
            else
            {
                notes.Add(new(NoteKind.Good, $"{e.WindowsVersion} gets security updates until {end.ToString("MMMM yyyy", CultureInfo.CurrentCulture)}."));
            }
        }

        if (e.LastSuccessfulUpdateUtc is { } last)
        {
            var age = nowUtc - last;
            if (age > TimeSpan.FromDays(45))
            {
                grade = Worst(grade, HealthGrade.Fair);
                notes.Add(new(NoteKind.Warning, $"The last update was installed {BackupCheck.Ago(age)}. Windows normally updates every month; check Windows Update for problems."));
            }
            else
            {
                notes.Add(new(NoteKind.Good, $"Last update installed {BackupCheck.Ago(age)}."));
            }
        }

        if (e.RestartPending)
        {
            grade = Worst(grade, HealthGrade.Fair);
            notes.Add(new(NoteKind.Warning, "Updates are waiting for a restart to finish installing. Until then you're not fully protected. Restart when convenient."));
        }
        else if (e.Uptime is { TotalDays: > 7 } up)
        {
            notes.Add(new(NoteKind.Info, $"Windows hasn't restarted in {SystemInfoText.Uptime(up)}. A restart now and then finishes updates and clears out slowdowns."));
        }

        string summary = grade switch
        {
            HealthGrade.Good => "Windows is up to date",
            HealthGrade.Fair => "Windows needs attention",
            _ => "Windows is missing security updates",
        };
        return new ProtectionResult("Windows Update", grade, summary, notes, "ms-settings:windowsupdate", "Open Windows Update");
    }

    private static HealthGrade Worst(HealthGrade a, HealthGrade b) => a > b ? a : b;
}
