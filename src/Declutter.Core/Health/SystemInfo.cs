using System.Globalization;
using Declutter.Core.Util;

namespace Declutter.Core.Health;

public sealed record GpuInfo(string Name, long? MemoryBytes);

public sealed record DriveSummary(string Model, long SizeBytes);

/// <summary>What's inside this PC, in plain terms.</summary>
public sealed record SystemInfo
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? BiosVersion { get; init; }
    public DateTime? BiosDate { get; init; }

    public string? WindowsName { get; init; }
    public string? WindowsVersion { get; init; }
    public string? WindowsBuild { get; init; }
    public DateTime? WindowsInstalled { get; init; }

    public string? Processor { get; init; }
    public int? Cores { get; init; }
    public int? Threads { get; init; }

    public long? MemoryBytes { get; init; }
    public long? MemoryFreeBytes { get; init; }
    public int? MemoryModules { get; init; }
    public int? MemorySlots { get; init; }
    public int? MemorySpeedMHz { get; init; }

    public IReadOnlyList<GpuInfo> Graphics { get; init; } = [];
    public IReadOnlyList<DriveSummary> Drives { get; init; } = [];
    public TimeSpan? Uptime { get; init; }

    /// <summary>Manufacturers fill unused serial fields with placeholders; those aren't real serials.</summary>
    public string? RealSerial => SerialNumber is { } s && !IsPlaceholder(s) ? s.Trim() : null;

    public double? MemoryUsedFraction => MemoryBytes is long t && t > 0 && MemoryFreeBytes is long f ? 1 - (double)f / t : null;

    internal static bool IsPlaceholder(string value)
    {
        string v = value.Trim().ToLowerInvariant();
        return v.Length < 3 || v.All(c => c == '0') ||
               v is "to be filled by o.e.m." or "default string" or "system serial number" or "none" or "not specified" or "n/a" or "0123456789" ||
               v.StartsWith("vmware", StringComparison.Ordinal);
    }
}

/// <summary>Where to check the warranty for a given manufacturer.</summary>
public static class Warranty
{
    public static string? PageFor(string? manufacturer, string? serial)
    {
        string m = (manufacturer ?? "").ToLowerInvariant();
        if (m.Contains("dell"))
            return serial != null ? $"https://www.dell.com/support/home/product-support/servicetag/{Uri.EscapeDataString(serial)}/overview" : "https://www.dell.com/support/home";
        if (m.Contains("lenovo")) return "https://pcsupport.lenovo.com/warrantylookup";
        if (m.Contains("hp") || m.Contains("hewlett")) return "https://support.hp.com/checkwarranty";
        if (m.Contains("microsoft")) return "https://mybusinessservice.surface.com/";
        if (m.Contains("asus")) return "https://www.asus.com/support/warranty-status-inquiry/";
        if (m.Contains("acer")) return "https://www.acer.com/support/warranty";
        if (m.Contains("msi") || m.Contains("micro-star")) return "https://www.msi.com/support/warranty-check";
        if (m.Contains("samsung")) return "https://www.samsung.com/support/";
        if (m.Contains("apple")) return "https://checkcoverage.apple.com/";
        return null;
    }
}

public static class SystemInfoText
{
    public static string Uptime(TimeSpan t) => t.TotalDays >= 1
        ? $"{(int)t.TotalDays} day{((int)t.TotalDays == 1 ? "" : "s")}, {t.Hours} hour{(t.Hours == 1 ? "" : "s")}"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} hour{((int)t.TotalHours == 1 ? "" : "s")}" : $"{Math.Max(1, (int)t.TotalMinutes)} minutes";

    public static string Memory(SystemInfo s)
    {
        if (s.MemoryBytes is not long total) return "Unknown";
        string text = ByteSize.Format(RoundToMarketedSize(total));
        if (s.MemorySpeedMHz is int mhz && mhz > 0) text += $" ({mhz.ToString(CultureInfo.CurrentCulture)} MHz)";
        if (s.MemoryModules is int used && s.MemorySlots is int slots && slots > 0) text += $" · {used} of {slots} slots used";
        return text;
    }

    /// <summary>Windows reports a little less than the installed amount (some is reserved); show what's on the box.</summary>
    internal static long RoundToMarketedSize(long bytes)
    {
        const long gb = 1L << 30;
        if (bytes < 2 * gb) return bytes;
        long rounded = (long)Math.Round((double)bytes / gb) * gb;
        return Math.Abs(rounded - bytes) <= gb / 2 ? rounded : bytes;
    }
}
