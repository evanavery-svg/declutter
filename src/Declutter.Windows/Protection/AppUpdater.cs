using Declutter.Core.Protection;
using Declutter.Core.Util;

namespace Declutter.Windows.Protection;

/// <summary>Finds and installs app updates with winget, Windows' built-in package manager.</summary>
public sealed class AppUpdater(IActivityLog log)
{
    /// <summary>winget is an "app execution alias" installed with App Installer.</summary>
    public static string? WingetPath
    {
        get
        {
            string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(alias) ? alias : null;
        }
    }

    public bool IsAvailable => WingetPath != null;

    public async Task<IReadOnlyList<AppUpdate>> ListAsync(CancellationToken ct)
    {
        if (WingetPath is not { } winget) return [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var result = await ProcessRunner.RunDetectingEncodingAsync(winget, "upgrade --include-unknown --accept-source-agreements --disable-interactivity", timeout.Token);
        return Winget.ParseUpgradeTable(result.Output)
            .Where(u => !u.Id.Equals("Microsoft.AppInstaller", StringComparison.OrdinalIgnoreCase)) // winget itself
            .ToList();
    }

    /// <summary>Updates one app silently. Returns null on success, or what went wrong.</summary>
    public async Task<string?> UpgradeAsync(AppUpdate app, CancellationToken ct)
    {
        if (WingetPath is not { } winget) return "winget isn't available.";
        log.Write("Updates", $"Updating {app.Name} ({app.Id}) {app.Version} → {app.Available}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        var result = await ProcessRunner.RunDetectingEncodingAsync(winget,
            $"upgrade --id \"{app.Id.Replace("\"", "")}\" --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
            timeout.Token);
        string? problem = result.ExitCode == 0 ? null : LastMeaningfulLine(result.Output) ?? $"winget exited with code {result.ExitCode}.";
        log.Write("Updates", $"{app.Name}: {problem ?? "updated"}");
        return problem;
    }

    private static string? LastMeaningfulLine(string output) =>
        output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l[(l.LastIndexOf('\r') + 1)..].Trim())
            .LastOrDefault(l => l.Length > 3 && l.Any(char.IsLetter));
}
