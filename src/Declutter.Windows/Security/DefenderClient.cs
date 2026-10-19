using Declutter.Core.Util;

namespace Declutter.Windows.Security;

public sealed record DefenderOutcome(bool Ok, bool ThreatsFound, string Message);

/// <summary>Drives Microsoft Defender through its command-line tool, MpCmdRun.exe.</summary>
public sealed class DefenderClient(IActivityLog log)
{
    public static string? MpCmdRunPath
    {
        get
        {
            string classic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
            if (File.Exists(classic)) return classic;
            string platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
            try
            {
                return Directory.Exists(platform)
                    ? Directory.GetDirectories(platform).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                        .Select(d => Path.Combine(d, "MpCmdRun.exe")).FirstOrDefault(File.Exists)
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public bool IsAvailable => MpCmdRunPath != null;

    /// <summary>Quick scan of the places malware usually hides. Usually takes a few minutes.</summary>
    public Task<DefenderOutcome> QuickScanAsync(CancellationToken ct) => RunScanAsync("-Scan -ScanType 1", "Quick scan", ct);

    /// <summary>Scans one file or folder; Defender quarantines anything it finds.</summary>
    public Task<DefenderOutcome> ScanPathAsync(string path, CancellationToken ct) =>
        RunScanAsync($"-Scan -ScanType 3 -File \"{path.Replace("\"", "")}\"", $"Scan of {path}", ct);

    public async Task<DefenderOutcome> UpdateDefinitionsAsync(CancellationToken ct)
    {
        if (MpCmdRunPath is not { } exe) return Unavailable;
        var result = await ProcessRunner.RunAsync(exe, "-SignatureUpdate", ct);
        var outcome = result.ExitCode == 0
            ? new DefenderOutcome(true, false, "Virus definitions are up to date.")
            : new DefenderOutcome(false, false, "Defender couldn't update its definitions. Check your internet connection, or update from Windows Security.");
        log.Write("Security", $"Definitions update: exit {result.ExitCode}");
        return outcome;
    }

    private async Task<DefenderOutcome> RunScanAsync(string args, string what, CancellationToken ct)
    {
        if (MpCmdRunPath is not { } exe) return Unavailable;
        log.Write("Security", $"{what} started");
        try
        {
            var result = await ProcessRunner.RunAsync(exe, args, ct);
            var outcome = result.ExitCode switch
            {
                0 => new DefenderOutcome(true, false, "Microsoft Defender found no threats."),
                2 => new DefenderOutcome(true, true, "Microsoft Defender found threats and is dealing with them. Open Windows Security → Protection history to see what was found."),
                _ => new DefenderOutcome(false, false, "The scan didn't finish. " + LastLine(result.Output + "\n" + result.Error)),
            };
            log.Write("Security", $"{what} finished: {outcome.Message}");
            return outcome;
        }
        catch (OperationCanceledException)
        {
            try { await ProcessRunner.RunAsync(exe, "-Scan -Cancel", CancellationToken.None); } catch (Exception) { }
            log.Write("Security", $"{what} cancelled");
            throw;
        }
    }

    private static readonly DefenderOutcome Unavailable =
        new(false, false, "Microsoft Defender isn't available on this PC. If you use another antivirus, run a scan from that app.");

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
