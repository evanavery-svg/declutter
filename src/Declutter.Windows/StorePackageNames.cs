using Windows.Management.Deployment;

namespace Declutter.Windows;

/// <summary>Looks up friendly names for installed Store packages by family name.</summary>
internal sealed class StorePackageNames
{
    private readonly Dictionary<string, (string DisplayName, string? Publisher)?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private PackageManager? _manager;

    public (string DisplayName, string? Publisher)? Lookup(string familyName)
    {
        if (_cache.TryGetValue(familyName, out var cached)) return cached;

        (string, string?)? result = null;
        try
        {
            _manager ??= new PackageManager();
            var package = _manager.FindPackagesForUser(string.Empty, familyName).FirstOrDefault();
            if (package != null)
            {
                string name = StorePackageInfo.SafeDisplayName(package) ?? package.Id.Name;
                result = (name, StorePackageInfo.SafePublisher(package));
            }
        }
        catch (Exception)
        {
            result = null;
        }

        _cache[familyName] = result;
        return result;
    }
}
