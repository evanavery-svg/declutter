using Windows.ApplicationModel;

namespace Declutter.Windows;

/// <summary>Package properties that throw for some packages (broken manifests, missing resources).</summary>
internal static class StorePackageInfo
{
    public static string? SafeDisplayName(Package p)
    {
        try
        {
            string name = p.DisplayName;
            return string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? SafePublisher(Package p)
    {
        try
        {
            string name = p.PublisherDisplayName;
            return string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static DateTime? SafeInstalledDate(Package p)
    {
        try
        {
            var date = p.InstalledDate;
            return date.Year < 2000 ? null : date.LocalDateTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? SafeInstalledPath(Package p)
    {
        try
        {
            return p.InstalledLocation?.Path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
