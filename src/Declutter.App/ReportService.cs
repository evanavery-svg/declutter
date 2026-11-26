using System.Globalization;
using Declutter.Core.Apps;
using Declutter.Core.Health;
using Declutter.Core.Reports;
using Declutter.Core.Security;
using Declutter.Core.Startup;
using Declutter.Core.Util;
using Declutter.Windows;
using Declutter.Windows.Health;

namespace Declutter.App;

/// <summary>Gathers a full picture of the PC (all read-only checks) and writes a shareable HTML report.</summary>
public sealed class ReportService(AppServices services)
{
    /// <summary>Extra sections other features contribute (speed, protection, health score…).</summary>
    public List<Func<CancellationToken, Task<IReadOnlyList<ReportSection>>>> Contributors { get; } = [];

    public async Task<string> CreateAsync(IProgress<string> progress, CancellationToken ct)
    {
        var sections = new List<ReportSection>();

        progress.Report("Reading system details…");
        var info = await SystemInfoCollector.CollectAsync(ct);
        sections.Add(SystemSection(info));

        progress.Report("Checking drive and battery health…");
        var health = await new HealthCollector(services.IsElevated).CollectAsync(ct);
        sections.Add(StorageSection(health));

        progress.Report("Running the security check…");
        var evidence = await services.Security.CollectAsync(null, ct);
        sections.Add(SecuritySection(SecurityAnalyzer.Analyze(evidence, services.Folders, DateTime.UtcNow)));
        sections.Add(StartupSection(evidence.StartupItems));

        progress.Report("Listing apps…");
        sections.Add(AppsSection(await services.Apps.GetAppsAsync(ct)));

        foreach (var contribute in Contributors)
        {
            try
            {
                sections.AddRange(await contribute(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                services.Log.Write("Report", "A report section failed: " + ex.Message);
            }
        }

        string title = $"PC report: {info.Manufacturer} {info.Model}".Trim();
        string html = new ReportBuilder(services.Folders.UserProfile, Environment.UserName).Build(title, DateTime.Now, sections);

        string folder = Path.Combine(services.Folders.Documents ?? services.Folders.UserProfile, "Declutter reports");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"Declutter report {DateTime.Now:yyyy-MM-dd HHmm}.html");
        await File.WriteAllTextAsync(file, html, ct);
        services.Log.Write("Report", "Created " + file);
        return file;
    }

    private static ReportSection SystemSection(SystemInfo i)
    {
        var rows = new List<ReportRow>
        {
            new("Computer", $"{i.Manufacturer} {i.Model}".Trim()),
            new("Windows", $"{i.WindowsName} {i.WindowsVersion} (build {i.WindowsBuild})"),
            new("Processor", i.Processor is null ? "Unknown" : $"{i.Processor} ({i.Cores} cores, {i.Threads} threads)"),
            new("Memory", SystemInfoText.Memory(i)),
        };
        rows.AddRange(i.Graphics.Select(g => new ReportRow("Graphics", g.Name)));
        rows.AddRange(i.Drives.Select(d => new ReportRow("Drive", $"{d.Model}, {ByteSize.Format(d.SizeBytes)}")));
        if (i.Uptime is { } up) rows.Add(new("Running since last restart", SystemInfoText.Uptime(up), up.TotalDays > 7 ? "caution" : null));
        if (i.BiosVersion != null) rows.Add(new("BIOS", i.BiosVersion + (i.BiosDate is { } d ? $" ({d:MMM yyyy})" : "")));
        return new ReportSection("This PC", rows);
    }

    private ReportSection StorageSection(HealthSnapshot health)
    {
        var rows = new List<ReportRow>();
        if (SystemTools.GetDriveSpace(services.Folders.SystemDrive) is { } space)
            rows.Add(new($"Drive {space.Name.TrimEnd('\\')}", $"{ByteSize.Format(space.Free)} free of {ByteSize.Format(space.Total)}",
                space.UsedFraction > 0.9 ? "danger" : space.UsedFraction > 0.8 ? "caution" : "good"));
        foreach (var d in health.Disks)
        {
            var a = HealthAnalyzer.AssessDisk(d);
            rows.Add(new(d.Name, a.Summary, Tone(a.Grade)));
        }
        foreach (var b in health.Batteries)
        {
            var a = HealthAnalyzer.AssessBattery(b);
            rows.Add(new("Battery", a.Summary + (b.CycleCount is int c ? $", {c} charge cycles" : ""), Tone(a.Grade)));
        }
        return new ReportSection("Storage, drives and battery", rows);
    }

    private static ReportSection SecuritySection(SecurityReport report)
    {
        var rows = report.Findings.Where(f => f.Severity != Severity.Info)
            .Select(f => new ReportRow(f.Severity == Severity.Critical ? "Needs attention" : "Worth a look", f.Title,
                f.Severity == Severity.Critical ? "danger" : "caution"))
            .ToList();
        rows.AddRange(report.Passed.Select(p => new ReportRow("Passed", p, "good")));
        return new ReportSection("Security", rows, report.Headline);
    }

    private static ReportSection StartupSection(IReadOnlyList<StartupItem> items)
    {
        var on = items.Where(i => i.Enabled).ToList();
        var rows = on.Select(i => new ReportRow(i.Name, i.Advice switch
        {
            StartupAdvice.Keep => "Keep on",
            StartupAdvice.Optional => "Your choice",
            StartupAdvice.SafeToDisable => "Safe to turn off",
            _ => "Not sure",
        }, i.Advice == StartupAdvice.SafeToDisable ? "caution" : null)).ToList();
        return new ReportSection("Apps that start with Windows", rows,
            $"{on.Count} of {items.Count} startup apps are turned on; {on.Count(i => i.Advice == StartupAdvice.SafeToDisable)} are safe to turn off.");
    }

    private static ReportSection AppsSection(IReadOnlyList<InstalledApp> apps)
    {
        var bloat = apps.Where(a => a.Advice == AppAdvice.Bloatware).ToList();
        var rows = bloat.Select(a => new ReportRow(a.Name, a.AdviceReason ?? "Suggested to remove", "caution")).ToList();
        long total = apps.Sum(a => a.SizeBytes ?? 0);
        return new ReportSection("Installed apps", rows,
            $"{apps.Count} apps installed ({ByteSize.Format(total)} where sizes are known). " +
            (bloat.Count == 0 ? "No preinstalled extras found." : $"{bloat.Count} preinstalled extras could be removed."));
    }

    internal static string? Tone(HealthGrade g) => g switch
    {
        HealthGrade.Good => "good",
        HealthGrade.Fair => "caution",
        HealthGrade.Poor => "danger",
        _ => null,
    };
}
