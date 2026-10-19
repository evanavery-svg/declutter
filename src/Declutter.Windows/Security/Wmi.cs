using System.Management;
using System.Runtime.InteropServices;

namespace Declutter.Windows.Security;

/// <summary>Small, failure-tolerant WMI helper: any error (missing namespace, access denied) yields null.</summary>
internal static class Wmi
{
    public static List<Dictionary<string, object?>>? Query(string scope, string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
            searcher.Options.Timeout = TimeSpan.FromSeconds(20);
            var rows = new List<Dictionary<string, object?>>();
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in obj.Properties) row[p.Name] = p.Value;
                    rows.Add(row);
                }
            }
            return rows;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    public static string? Str(this Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) ? v?.ToString() : null;

    public static bool? Bool(this Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is bool b ? b : null;

    /// <summary>Numbers come back as many integer types; uint.MaxValue (and other huge values) mean "never".</summary>
    public static long? Long(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try
        {
            return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static int? Days(this Dictionary<string, object?> row, string key) =>
        row.Long(key) is long d && d is >= 0 and < 10000 ? (int)d : null;

    public static string[] Strings(this Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is string[] a ? a : [];

    public static DateTime? Date(this Dictionary<string, object?> row, string key)
    {
        if (row.Str(key) is not { Length: > 0 } s) return null;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
