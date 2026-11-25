using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Declutter.Core.Health;

/// <summary>Reads the XML version of Windows' battery report ("powercfg /batteryreport /xml").</summary>
public static class BatteryReportParser
{
    public static IReadOnlyList<BatteryInfo> Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return [];
        }

        // Match on local names so the report's namespace (which has changed between Windows versions) doesn't matter.
        return doc.Descendants().Where(e => e.Name.LocalName == "Battery" && e.Parent?.Name.LocalName == "Batteries")
            .Select(b => new BatteryInfo
            {
                Name = Text(b, "Id") ?? "Battery",
                Manufacturer = Text(b, "Manufacturer"),
                Chemistry = Text(b, "Chemistry"),
                DesignCapacityMWh = Long(b, "DesignCapacity") ?? 0,
                FullChargeCapacityMWh = Long(b, "FullChargeCapacity") ?? 0,
                CycleCount = Long(b, "CycleCount") is long c and > 0 and < int.MaxValue ? (int)c : null,
            })
            .ToList();
    }

    private static string? Text(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    private static long? Long(XElement parent, string name) =>
        long.TryParse(Text(parent, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
}
