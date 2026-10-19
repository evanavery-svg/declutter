using System.Globalization;

namespace Declutter.Core.Util;

public static class ByteSize
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    /// <summary>Formats a byte count the way Explorer does (1024-based, one decimal above KB).</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes} bytes";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 ? "0" : "0.0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }
}
