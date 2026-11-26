namespace Declutter.Core.Health;

/// <summary>
/// Everything the PC health score looks at. Leave a value null if it hasn't been checked; it's then left out
/// rather than guessed.
/// </summary>
public sealed record ScoreInputs
{
    public double? DriveUsedFraction { get; init; }
    public int? SecurityCritical { get; init; }
    public int? SecurityWarnings { get; init; }
    public IReadOnlyList<(string Area, HealthGrade Grade)> Protection { get; init; } = [];
    public HealthGrade? WorstDrive { get; init; }
    public double? BatteryHealthPercent { get; init; }
    public int? StartupAppsOn { get; init; }
    public double? BootSeconds { get; init; }
    public int? OutdatedApps { get; init; }
    public TimeSpan? Uptime { get; init; }
}

/// <summary>One reason the score isn't 100, and where to fix it.</summary>
public sealed record ScoreDeduction(string Category, int Points, string Reason, string Page);

public sealed record HealthScoreResult(int Score, string Label, IReadOnlyList<ScoreDeduction> Deductions, IReadOnlyList<string> Unchecked);

/// <summary>
/// A 0–100 score that starts at 100 and loses points for specific, fixable problems, so every point lost
/// has a reason and a place to fix it. Weighted towards what matters most: security and data loss first,
/// then space, then speed.
/// </summary>
public static class HealthScore
{
    public static HealthScoreResult Compute(ScoreInputs i)
    {
        var d = new List<ScoreDeduction>();
        var unchecked_ = new List<string>();
        void Lose(string cat, int pts, string why, string page)
        {
            if (pts > 0) d.Add(new(cat, pts, why, page));
        }

        // Security (up to 40)
        if (i.SecurityCritical is int crit && i.SecurityWarnings is int warn)
        {
            Lose("Security", Math.Min(35, crit * 25), $"{crit} security problem{S(crit)} need{(crit == 1 ? "s" : "")} attention", "Security");
            Lose("Security", Math.Min(10, warn * 4), $"{warn} security finding{S(warn)} worth a look", "Security");
        }
        else unchecked_.Add("Security");

        // Protection: backups, encryption, Windows Update (up to 30)
        if (i.Protection.Count > 0)
        {
            foreach (var (area, grade) in i.Protection)
            {
                int pts = grade switch { HealthGrade.Poor => 10, HealthGrade.Fair => 4, _ => 0 };
                Lose("Protection", pts, $"{area}: {(grade == HealthGrade.Poor ? "needs attention" : "could be better")}", "Protection");
            }
        }
        else unchecked_.Add("Protection");

        // Disk space (up to 20)
        if (i.DriveUsedFraction is double used)
        {
            int pts = used switch { > 0.95 => 20, > 0.90 => 12, > 0.80 => 5, _ => 0 };
            Lose("Space", pts, $"Drive is {used:P0} full", "Junk");
        }
        else unchecked_.Add("Space");

        // Drive and battery health (up to 26)
        if (i.WorstDrive is HealthGrade drive)
            Lose("Hardware", drive switch { HealthGrade.Poor => 20, HealthGrade.Fair => 8, _ => 0 },
                drive == HealthGrade.Poor ? "A drive is showing signs of failing" : "A drive is showing wear", "Health");
        else unchecked_.Add("Drive health");
        if (i.BatteryHealthPercent is double battery)
            Lose("Hardware", battery switch { < 60 => 6, < 80 => 3, _ => 0 }, $"Battery holds {battery:0}% of its original charge", "Health");

        // Speed (up to 22)
        if (i.StartupAppsOn is int startup)
            Lose("Speed", startup switch { > 15 => 8, > 10 => 5, > 6 => 2, _ => 0 }, $"{startup} apps start with Windows", "Startup");
        else unchecked_.Add("Startup apps");
        if (i.BootSeconds is double boot)
            Lose("Speed", boot switch { > 90 => 8, > 60 => 4, _ => 0 }, $"Start-up takes about {boot:0} seconds", "Speed");
        if (i.OutdatedApps is int outdated)
            Lose("Protection", outdated switch { > 10 => 6, > 3 => 3, _ => 0 }, $"{outdated} apps have updates waiting", "AppUpdates");
        if (i.Uptime is TimeSpan up && up.TotalDays > 14)
            Lose("Speed", 3, $"Not restarted for {up.TotalDays:0} days", "ThisPc");

        int score = Math.Clamp(100 - d.Sum(x => x.Points), 0, 100);
        return new HealthScoreResult(score, LabelFor(score), d.OrderByDescending(x => x.Points).ToList(), unchecked_);
    }

    public static string LabelFor(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 55 => "Fair",
        _ => "Needs attention",
    };

    private static string S(int n) => n == 1 ? "" : "s";
}
