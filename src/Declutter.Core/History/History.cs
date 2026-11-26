using System.Text.Json;
using System.Text.Json.Serialization;

namespace Declutter.Core.History;

/// <summary>
/// One day's measurements. Fields are null when that thing wasn't measured that day; later readings on the
/// same day fill in or replace earlier ones.
/// </summary>
public sealed record HistorySnapshot
{
    public DateOnly Date { get; init; }
    public long? DriveFreeBytes { get; init; }
    public long? DriveTotalBytes { get; init; }
    public double? BootSeconds { get; init; }
    public int? StartupAppsOn { get; init; }
    public int? Score { get; init; }
    public double? BatteryHealthPercent { get; init; }

    [JsonIgnore]
    public long? DriveUsedBytes => DriveFreeBytes is long free && DriveTotalBytes is long total ? total - free : null;

    /// <summary>This snapshot with every value <paramref name="newer"/> has replacing ours.</summary>
    public HistorySnapshot Merge(HistorySnapshot newer) => this with
    {
        DriveFreeBytes = newer.DriveFreeBytes ?? DriveFreeBytes,
        DriveTotalBytes = newer.DriveTotalBytes ?? DriveTotalBytes,
        BootSeconds = newer.BootSeconds ?? BootSeconds,
        StartupAppsOn = newer.StartupAppsOn ?? StartupAppsOn,
        Score = newer.Score ?? Score,
        BatteryHealthPercent = newer.BatteryHealthPercent ?? BatteryHealthPercent,
    };
}

/// <summary>
/// A small local history file (one entry per day, at most two years). Stays on this PC; nothing is uploaded.
/// A damaged file is treated as empty rather than stopping the app.
/// </summary>
public sealed class HistoryStore(string path)
{
    public const int MaxDays = 730;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string Path { get; } = path;

    public IReadOnlyList<HistorySnapshot> Load()
    {
        try
        {
            if (!File.Exists(Path)) return [];
            var list = JsonSerializer.Deserialize<List<HistorySnapshot>>(File.ReadAllText(Path), Json) ?? [];
            return list.OrderBy(s => s.Date).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Records today's readings, merging with anything already recorded today.</summary>
    public IReadOnlyList<HistorySnapshot> Record(HistorySnapshot reading)
    {
        var list = Load().ToList();
        int i = list.FindIndex(s => s.Date == reading.Date);
        if (i >= 0) list[i] = list[i].Merge(reading);
        else list.Add(reading);
        list = list.OrderBy(s => s.Date).ToList();
        if (list.Count > MaxDays) list = list.Skip(list.Count - MaxDays).ToList();

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, Path, overwrite: true);
        return list;
    }

    public void Clear()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

/// <summary>"At this rate your drive is full in about N weeks", from a straight-line fit of recent usage.</summary>
public static class DiskForecast
{
    /// <summary>
    /// Weeks until the drive is full, or null when there isn't enough history (at least 5 readings over 14+
    /// days in the last 90), usage isn't growing, or it's more than two years away (no point worrying).
    /// </summary>
    public static double? WeeksUntilFull(IReadOnlyList<HistorySnapshot> history, DateOnly today)
    {
        var points = history
            .Where(s => s.DriveUsedBytes != null && s.DriveTotalBytes != null && s.Date > today.AddDays(-90) && s.Date <= today)
            .OrderBy(s => s.Date)
            .ToList();
        if (points.Count < 5 || points[^1].Date.DayNumber - points[0].Date.DayNumber < 14) return null;

        // A different drive (new SSD, reinstall) makes the old readings meaningless.
        long total = points[^1].DriveTotalBytes!.Value;
        points = points.Where(p => Math.Abs(p.DriveTotalBytes!.Value - total) < total / 100).ToList();
        if (points.Count < 5) return null;

        double[] x = points.Select(p => (double)p.Date.DayNumber).ToArray();
        double[] y = points.Select(p => (double)p.DriveUsedBytes!.Value).ToArray();
        double mx = x.Average(), my = y.Average();
        double sxx = x.Sum(v => (v - mx) * (v - mx));
        if (sxx == 0) return null;
        double slopePerDay = x.Zip(y, (a, b) => (a - mx) * (b - my)).Sum() / sxx;

        // Growth under 50 MB a day is normal noise (updates, caches coming and going).
        if (slopePerDay < 50L * 1024 * 1024) return null;

        double free = points[^1].DriveFreeBytes!.Value;
        double weeks = free / slopePerDay / 7;
        return weeks > 104 ? null : Math.Max(0, weeks);
    }

    public static string Describe(double weeks) => weeks switch
    {
        < 1 => "At the rate it's been filling up, your drive will be full within a week.",
        < 8 => $"At the rate it's been filling up, your drive will be full in about {Math.Round(weeks):0} weeks.",
        _ => $"At the rate it's been filling up, your drive will be full in about {Math.Round(weeks / 4.345):0} months.",
    };
}
