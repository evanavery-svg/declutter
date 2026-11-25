using System.Runtime.InteropServices;
using System.Text.Json;

namespace Declutter.Core.Updates;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

public sealed record ReleaseInfo(Version Version, string Tag, string? Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Where updates come from and how to read GitHub's "latest release" answer.</summary>
public static class Releases
{
    public const string Owner = "evanavery-svg";
    public const string Repository = "declutter";
    public const string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repository}/releases/latest";

    /// <summary>The only hosts a download may come from (GitHub redirects release files to its CDN).</summary>
    public static readonly IReadOnlySet<string> TrustedDownloadHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;

            string? tag = Str(root, "tag_name");
            if (tag == null || ParseVersion(tag) is not { } version) return null;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    string? name = Str(a, "name"), url = Str(a, "browser_download_url");
                    if (name == null || url == null) continue;
                    long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                    assets.Add(new ReleaseAsset(name, url, size, ParseSha256(Str(a, "digest"))));
                }
            }

            return new ReleaseInfo(version, tag, Str(root, "body"), Str(root, "html_url") ?? ReleasesPage, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"v1.0.14" or "1.0.14" → 1.0.14. Anything else → null.</summary>
    public static Version? ParseVersion(string tag)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }

    /// <summary>Compares major.minor.build only (the revision number isn't used by releases).</summary>
    public static bool IsNewer(Version latest, Version current) =>
        new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build)) >
        new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>The executable built for this PC's processor.</summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, Architecture architecture)
    {
        string wanted = architecture == Architecture.Arm64 ? "Declutter-ARM64.exe" : "Declutter.exe";
        return release.Assets.FirstOrDefault(a => a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>GitHub reports digests as "sha256:&lt;hex&gt;".</summary>
    internal static string? ParseSha256(string? digest)
    {
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        string hex = digest[7..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
