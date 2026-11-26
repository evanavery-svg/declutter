using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Declutter.Core.Speed;

/// <summary>One start-up of Windows, from the Diagnostics-Performance log (event 100).</summary>
public sealed record BootRecord(DateTime TimeUtc, int BootMs, int MainPathMs, int PostBootMs)
{
    public double Seconds => BootMs / 1000.0;
}

/// <summary>Something Windows says made a start-up slower (events 101–110).</summary>
public sealed record BootSlowdown(DateTime TimeUtc, string Name, string? File, int DegradationMs, string Kind);

public sealed record BootCulprit(string Name, string Kind, int Times, double AverageSeconds);

public sealed record BootSummary(BootRecord? Latest, double? TypicalSeconds, IReadOnlyList<BootRecord> Recent, IReadOnlyList<BootCulprit> Culprits)
{
    public string Verdict => TypicalSeconds switch
    {
        null => "No start-up timings recorded yet",
        < 30 => "Starts quickly",
        < 60 => "Starts at an OK speed",
        < 120 => "Starts slowly",
        _ => "Starts very slowly",
    };
}

/// <summary>
/// Reads the events Windows writes to "Microsoft-Windows-Diagnostics-Performance/Operational" each time it
/// starts: event 100 has the timings; 101–110 name the apps, drivers and services that slowed it down.
/// </summary>
public static class BootEvents
{
    public static (IReadOnlyList<BootRecord> Boots, IReadOnlyList<BootSlowdown> Slowdowns) Parse(string eventsXml)
    {
        var boots = new List<BootRecord>();
        var slowdowns = new List<BootSlowdown>();
        XElement root;
        try
        {
            // wevtutil prints bare <Event> elements one after another; give them a root.
            int start = eventsXml.IndexOf("<Event", StringComparison.Ordinal);
            if (start < 0) return (boots, slowdowns);
            root = XElement.Parse("<Events>" + eventsXml[start..] + "</Events>");
        }
        catch (XmlException)
        {
            return (boots, slowdowns);
        }

        foreach (var ev in root.Elements().Where(e => e.Name.LocalName == "Event"))
        {
            var system = ev.Elements().FirstOrDefault(e => e.Name.LocalName == "System");
            int id = int.TryParse(system?.Elements().FirstOrDefault(e => e.Name.LocalName == "EventID")?.Value, out int n) ? n : 0;
            string? created = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (!DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)) continue;

            var data = ev.Descendants().Where(e => e.Name.LocalName == "Data" && e.Attribute("Name") != null)
                .GroupBy(e => e.Attribute("Name")!.Value)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

            if (id == 100 && Int(data, "BootTime") is int boot and > 0)
            {
                boots.Add(new BootRecord(time, boot, Int(data, "MainPathBootTime") ?? 0, Int(data, "BootPostBootTime") ?? 0));
            }
            else if (id is >= 101 and <= 110 && Int(data, "DegradationTime") is int slow and > 0)
            {
                string name = Str(data, "FriendlyName") ?? Str(data, "Name") ?? "Unknown";
                string kind = id switch { 101 => "App", 102 => "Driver", 103 => "Service", _ => "Windows" };
                slowdowns.Add(new BootSlowdown(time, name, Str(data, "Name"), slow, kind));
            }
        }
        return (boots, slowdowns);
    }

    public static BootSummary Summarize(IReadOnlyList<BootRecord> boots, IReadOnlyList<BootSlowdown> slowdowns, DateTime nowUtc)
    {
        var recent = boots.OrderByDescending(b => b.TimeUtc).Take(20).OrderBy(b => b.TimeUtc).ToList();
        double? typical = recent.Count == 0 ? null : Median(recent.TakeLast(10).Select(b => b.Seconds).ToList());

        var culprits = slowdowns.Where(s => nowUtc - s.TimeUtc < TimeSpan.FromDays(90))
            .GroupBy(s => (s.Name, s.Kind))
            .Select(g => new BootCulprit(g.Key.Name, g.Key.Kind, g.Count(), g.Average(s => s.DegradationMs) / 1000.0))
            .Where(c => c.AverageSeconds >= 1)
            .OrderByDescending(c => c.AverageSeconds * Math.Min(c.Times, 5))
            .Take(8)
            .ToList();

        return new BootSummary(recent.LastOrDefault(), typical, recent, culprits);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    private static int? Int(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n is >= 0 and < int.MaxValue ? (int)n : null;

    private static string? Str(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}
