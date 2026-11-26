using System.Diagnostics;
using Declutter.Core.Protection;
using Microsoft.Win32;

namespace Declutter.Windows.Protection;

/// <summary>Finds extensions installed in Chrome, Edge, Brave, Vivaldi, Opera and Firefox. Read-only.</summary>
public static class ExtensionScanner
{
    private sealed record ChromiumBrowser(string Name, string UserData, string Exe, string ExtensionsPage, string StorePage);

    private static IEnumerable<ChromiumBrowser> Chromium(string local, string roaming) =>
    [
        new("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"), "msedge.exe", "edge://extensions", "https://microsoftedge.microsoft.com/addons/detail/"),
        new("Chrome", Path.Combine(local, "Google", "Chrome", "User Data"), "chrome.exe", "chrome://extensions", "https://chromewebstore.google.com/detail/"),
        new("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), "brave.exe", "brave://extensions", "https://chromewebstore.google.com/detail/"),
        new("Vivaldi", Path.Combine(local, "Vivaldi", "User Data"), "vivaldi.exe", "vivaldi://extensions", "https://chromewebstore.google.com/detail/"),
        new("Opera", Path.Combine(roaming, "Opera Software", "Opera Stable"), "opera.exe", "opera://extensions", "https://addons.opera.com/extensions/details/app_id/"),
    ];

    public static Task<IReadOnlyList<BrowserExtension>> ScanAsync(string localAppData, string roamingAppData, CancellationToken ct) => Task.Run<IReadOnlyList<BrowserExtension>>(() =>
    {
        var found = new List<BrowserExtension>();
        foreach (var browser in Chromium(localAppData, roamingAppData))
        {
            if (!Directory.Exists(browser.UserData)) continue;
            // Opera keeps a single profile at the top level; the others use Default / Profile N.
            var profiles = File.Exists(Path.Combine(browser.UserData, "Preferences"))
                ? [browser.UserData]
                : SafeDirectories(browser.UserData).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal));
            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();
                var settings = Extensions.ParseChromiumPreferences(Read(Path.Combine(profile, "Preferences")), Read(Path.Combine(profile, "Secure Preferences")));
                foreach (var extDir in SafeDirectories(Path.Combine(profile, "Extensions")))
                {
                    string id = Path.GetFileName(extDir);
                    if (id == "Temp") continue;
                    string? versionDir = SafeDirectories(extDir).OrderByDescending(VersionKey).FirstOrDefault();
                    if (versionDir == null || Read(Path.Combine(versionDir, "manifest.json")) is not { } manifest) continue;
                    var ext = Extensions.ParseChromium(browser.Name, id, manifest,
                        locale => Read(Path.Combine(versionDir, "_locales", locale, "messages.json")) ?? Read(Path.Combine(versionDir, "_locales", locale.Replace('-', '_'), "messages.json")),
                        settings.GetValueOrDefault(id));
                    if (ext != null && found.All(f => !(f.Browser == ext.Browser && f.Id == ext.Id)))
                        found.Add(ext with { Profile = Path.GetFileName(profile) });
                }
            }
        }

        string firefoxProfiles = Path.Combine(roamingAppData, "Mozilla", "Firefox", "Profiles");
        foreach (var profile in SafeDirectories(firefoxProfiles))
        {
            if (Read(Path.Combine(profile, "extensions.json")) is { } json)
                found.AddRange(Extensions.ParseFirefox(json, Path.GetFileName(profile)).Where(e => found.All(f => !(f.Browser == "Firefox" && f.Id == e.Id))));
        }
        return found;
    }, ct);

    /// <summary>Opens the browser's own extensions page (where an extension can be removed).</summary>
    public static bool OpenExtensionsPage(string browser)
    {
        string? exe;
        string page;
        if (browser == "Firefox")
        {
            exe = AppPath("firefox.exe");
            page = "about:addons";
        }
        else
        {
            var b = Chromium("", "").FirstOrDefault(c => c.Name == browser);
            if (b == null) return false;
            exe = AppPath(b.Exe);
            page = b.ExtensionsPage;
        }
        if (exe == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, page) { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static string? StorePage(BrowserExtension e) => e.Browser == "Firefox"
        ? null
        : Chromium("", "").FirstOrDefault(c => c.Name == e.Browser)?.StorePage + e.Id;

    private static string? AppPath(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        return null;
    }

    private static Version VersionKey(string dir) =>
        Version.TryParse(Path.GetFileName(dir).Split('_')[0], out var v) ? v : new Version(0, 0);

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Read(string path)
    {
        try
        {
            // Browsers keep these files open; FileShare.ReadWrite lets us read them while the browser runs.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
