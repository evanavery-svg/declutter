using System.Globalization;
using Declutter.Core.Util;

namespace Declutter.Core.Health;

/// <summary>Turns raw drive and battery numbers into plain-English verdicts.</summary>
public static class HealthAnalyzer
{
    public static HealthAssessment AssessDisk(DiskInfo d)
    {
        var notes = new List<HealthNote>();
        var grade = HealthGrade.Good;

        void Worse(HealthGrade g)
        {
            if (g > grade) grade = g;
        }

        switch (d.HealthStatus.ToLowerInvariant())
        {
            case "unhealthy":
                Worse(HealthGrade.Poor);
                notes.Add(new(NoteKind.Critical, "Windows reports this drive as failing. Back up anything important now, then replace the drive."));
                break;
            case "warning":
                Worse(HealthGrade.Fair);
                notes.Add(new(NoteKind.Warning, "Windows has a warning for this drive, which often means its self-test predicts trouble. Make sure your files are backed up."));
                break;
            case "healthy":
                notes.Add(new(NoteKind.Good, "Windows' health check: healthy."));
                break;
            default:
                notes.Add(new(NoteKind.Info, "Windows didn't report a health status for this drive."));
                break;
        }

        if (d.WearPercent is int wear && d.IsSsd)
        {
            if (wear >= 100)
            {
                Worse(HealthGrade.Poor);
                notes.Add(new(NoteKind.Critical, $"It has used {wear}% of its rated lifetime of writes. SSDs often keep working past this, but plan to replace it and keep backups."));
            }
            else if (wear >= 80)
            {
                Worse(HealthGrade.Fair);
                notes.Add(new(NoteKind.Warning, $"It has used {wear}% of its rated lifetime of writes. Fine for now; start thinking about a replacement."));
            }
            else
            {
                notes.Add(new(NoteKind.Good, $"It has used {wear}% of its rated lifetime of writes, so plenty of life left."));
            }
        }

        if (d.TemperatureC is int t && t > 0)
        {
            // Some drives report TemperatureMax as a rated limit, others as the hottest they've ever been.
            // Only a realistic value is trusted as a limit, so a drive at its own (cool) record isn't called hot.
            int fallback = d.IsSsd ? 70 : 55;
            int limit = d.TemperatureMaxC is int max && max >= 60 ? max : fallback;
            if (t >= limit)
            {
                Worse(HealthGrade.Fair);
                notes.Add(new(NoteKind.Warning, $"It's running hot ({t}°C, limit {limit}°C). Check the vents aren't blocked; heat slows drives down and shortens their life."));
            }
            else
            {
                notes.Add(new(NoteKind.Good, $"Temperature {t}°C, which is normal."));
            }
        }

        long errors = (d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0);
        if (errors > 0)
        {
            Worse(HealthGrade.Fair);
            notes.Add(new(NoteKind.Warning, $"It has had {errors:N0} read/write error{(errors == 1 ? "" : "s")} it couldn't correct. A few can be harmless, but keep backups up to date."));
        }

        if (d.PowerOnHours is long hours && hours > 0)
            notes.Add(new(NoteKind.Info, $"Powered on for {hours:N0} hours (about {FormatYears(hours)})."));

        if (d.IsSystemDisk && d.IsHdd)
            notes.Add(new(NoteKind.Info, "Windows runs from a spinning hard drive. Swapping it for an SSD is the single biggest speed-up most older PCs can get."));

        bool knownStatus = d.HealthStatus.ToLowerInvariant() is "healthy" or "warning" or "unhealthy"; // Windows also says "Unknown"
        bool hasData = knownStatus || d.WearPercent != null || d.TemperatureC is > 0;
        if (!hasData) grade = HealthGrade.Unknown;

        string kind = d.IsSsd ? (d.BusType.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe SSD" : "SSD") : d.IsHdd ? "Hard drive" : "Drive";
        string summary = grade switch
        {
            HealthGrade.Good => $"{kind}, {ByteSize.Format(d.SizeBytes)}, in good health",
            HealthGrade.Fair => $"{kind}, {ByteSize.Format(d.SizeBytes)}, worth keeping an eye on",
            HealthGrade.Poor => $"{kind}, {ByteSize.Format(d.SizeBytes)}, needs attention",
            _ => $"{kind}, {ByteSize.Format(d.SizeBytes)}",
        };
        return new HealthAssessment(grade, summary, notes);
    }

    public static HealthAssessment AssessBattery(BatteryInfo b)
    {
        var notes = new List<HealthNote>();
        if (b.HealthPercent is not double health)
            return new HealthAssessment(HealthGrade.Unknown, "Windows didn't report this battery's capacity", notes);

        string wh(long mwh) => (mwh / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) + " Wh";
        notes.Add(new(NoteKind.Info, $"A full charge holds {wh(b.FullChargeCapacityMWh)}; when new it held {wh(b.DesignCapacityMWh)}."));
        if (b.CycleCount is int cycles && cycles > 0)
            notes.Add(new(NoteKind.Info, $"It has been through {cycles:N0} charge cycles. Most laptop batteries are rated for 300–1,000."));

        HealthGrade grade;
        if (health >= 80)
        {
            grade = HealthGrade.Good;
            notes.Add(new(NoteKind.Good, "That's healthy. Batteries lose a little capacity every year; this one is doing well."));
        }
        else if (health >= 60)
        {
            grade = HealthGrade.Fair;
            notes.Add(new(NoteKind.Warning, "It has worn noticeably, so expect shorter battery life than when the laptop was new. Normal for an older laptop."));
        }
        else
        {
            grade = HealthGrade.Poor;
            notes.Add(new(NoteKind.Critical, "It has lost a lot of capacity. A replacement battery would bring back most of the original battery life."));
        }

        string summary = $"Holds {health:0}% of its original charge";
        return new HealthAssessment(grade, summary, notes);
    }

    private static string FormatYears(long hours)
    {
        double years = hours / (24.0 * 365);
        if (years >= 1) return years.ToString("0.#", CultureInfo.CurrentCulture) + (years < 1.05 ? " year" : " years") + " of running time";
        double days = hours / 24.0;
        return days.ToString("0", CultureInfo.CurrentCulture) + " days of running time";
    }
}
