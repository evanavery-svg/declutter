using System.Globalization;
using System.Text.RegularExpressions;
using Declutter.Core.Util;

namespace Declutter.Core.Apps;

/// <summary>
/// Turns one registry "Uninstall" key (as a name → value dictionary) into an app the user can remove,
/// filtering out the hidden components, patches and updates that Settings also hides.
/// </summary>
public static partial class UninstallEntryParser
{
    private static readonly string[] HiddenReleaseTypes = ["Update", "Hotfix", "Security Update", "Service Pack", "Update Rollup"];

    public static InstalledApp? Parse(string keyName, IReadOnlyDictionary<string, object?> values, bool machineWide)
    {
        string? name = Str(values, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Int(values, "SystemComponent") == 1) return null;
        if (!string.IsNullOrEmpty(Str(values, "ParentKeyName"))) return null;
        if (HiddenReleaseTypes.Contains(Str(values, "ReleaseType") ?? "", StringComparer.OrdinalIgnoreCase)) return null;
        if (KbUpdateName().IsMatch(name)) return null;

        bool isMsi = Int(values, "WindowsInstaller") == 1 && IsGuid(keyName);
        string? uninstall = Str(values, "UninstallString");
        if (string.IsNullOrWhiteSpace(uninstall) && !isMsi) return null;

        long? size = Int(values, "EstimatedSize") is int kb and > 0 ? kb * 1024L : null;

        return new InstalledApp
        {
            Id = (machineWide ? "HKLM:" : "HKCU:") + keyName,
            Name = name.Trim(),
            Kind = AppKind.Desktop,
            Publisher = Str(values, "Publisher")?.Trim(),
            Version = Str(values, "DisplayVersion")?.Trim(),
            InstallDate = ParseInstallDate(Str(values, "InstallDate")),
            SizeBytes = size,
            InstallLocation = Str(values, "InstallLocation")?.Trim().Trim('"'),
            UninstallCommand = BuildUninstallCommand(keyName, uninstall, isMsi),
            IsMsi = isMsi,
            RequiresAdmin = machineWide,
        };
    }

    /// <summary>
    /// MSI entries often say "MsiExec.exe /I{GUID}", which opens a Modify/Repair dialog. /X uninstalls.
    /// </summary>
    internal static string? BuildUninstallCommand(string keyName, string? uninstallString, bool isMsi)
    {
        if (isMsi && (string.IsNullOrWhiteSpace(uninstallString) || uninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase)))
            return $"MsiExec.exe /X{keyName}";

        if (uninstallString is null) return null;
        var match = MsiInstallSwitch().Match(uninstallString);
        if (match.Success && uninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            return MsiInstallSwitch().Replace(uninstallString, "/X$1", 1);

        return uninstallString.Trim();
    }

    internal static DateTime? ParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] formats = ["yyyyMMdd", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "d.M.yyyy", "dd.MM.yyyy"];
        if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
            date.Year is >= 1995 and <= 2100)
            return date;
        return null;
    }

    private static bool IsGuid(string s) => s.StartsWith('{') && Guid.TryParse(s, out _);

    private static string? Str(IReadOnlyDictionary<string, object?> v, string key) =>
        v.TryGetValue(key, out var o) ? o?.ToString() : null;

    private static int? Int(IReadOnlyDictionary<string, object?> v, string key)
    {
        if (!v.TryGetValue(key, out var o) || o is null) return null;
        return o switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) => p,
            _ => null,
        };
    }

    [GeneratedRegex(@"/I\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstallSwitch();

    [GeneratedRegex(@"\(KB\d{6,}\)|^Security Update for|^Update for Microsoft", RegexOptions.IgnoreCase)]
    private static partial Regex KbUpdateName();

    /// <summary>Removes duplicate entries (the same product registered in both 32- and 64-bit views).</summary>
    public static IReadOnlyList<InstalledApp> Deduplicate(IEnumerable<InstalledApp> apps) =>
        apps.GroupBy(a => (a.Name.ToUpperInvariant(), (a.Version ?? "").ToUpperInvariant()))
            .Select(g => g.OrderByDescending(a => a.SizeBytes ?? 0).First())
            .ToList();

    /// <summary>Program file to launch for an uninstall command.</summary>
    public static (string FileName, string Arguments) SplitCommand(string command) => CommandLine.Split(command);
}
