using System.Text.Json;

namespace Declutter.Core.Protection;

public enum ExtensionRisk
{
    Low,

    /// <summary>Well-known, trusted extension (even if it has broad access).</summary>
    Trusted,

    /// <summary>Can read and change websites; worth knowing what it is.</summary>
    Review,

    /// <summary>Broad access and not from the browser's store: a common sign of adware.</summary>
    High,
}

public sealed record BrowserExtension
{
    public required string Browser { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>False when it was side-loaded (installed by a program or by hand) rather than from the store.</summary>
    public bool FromStore { get; init; } = true;

    public IReadOnlyList<string> Permissions { get; init; } = [];
    public IReadOnlyList<string> Sites { get; init; } = [];
    public string? Profile { get; init; }
}

/// <summary>Reads installed extensions from Chromium browsers (Chrome, Edge, Brave…) and Firefox, and rates them.</summary>
public static class Extensions
{
    private static readonly string[] AllSitesPatterns = ["<all_urls>", "*://*/*", "http://*/*", "https://*/*", "*://*/", "http://*/", "https://*/"];

    private static readonly Dictionary<string, string> SensitivePermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cookies"] = "read your logins (cookies)",
        ["history"] = "see your browsing history",
        ["webRequest"] = "watch every page you load",
        ["webRequestBlocking"] = "change pages as they load",
        ["proxy"] = "route your browsing through another server",
        ["nativeMessaging"] = "talk to programs on your PC",
        ["debugger"] = "take full control of tabs",
        ["management"] = "install or disable other extensions",
        ["clipboardRead"] = "read your clipboard",
        ["downloads"] = "download files",
        ["privacy"] = "change privacy settings",
        ["browsingData"] = "delete browsing data",
        ["tabs"] = "see every tab you have open",
    };

    /// <summary>Well-known extensions by store ID (Chrome Web Store / Edge Add-ons / Firefox).</summary>
    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        "cjpalhdlnbpafiamejdnhcphjbkeiagm", "odfafepnkmbhccpbejgmiehpchacaeak", "uBlock0@raymondhill.net",        // uBlock Origin
        "ddkjiahejlhfcafbddmgiahcphecmpfh",                                                                       // uBlock Origin Lite
        "cfhdojbkjhnklbpkdaibdccddilifddb", "gmgoamodcdcjnbaobigkjelfplakmdhh", "{d10d0bf8-f5b5-c8b4-a8b2-2b9879e08c5d}", // Adblock Plus
        "gighmmpiobklfepjocnamgkkbiglidom", "ndcileolkflehcjpmjnfbnaibdcgglog",                                   // AdBlock
        "nngceckbapebfimnlniiiahkandclblb", "jbkfoedolllekgbhcbcoahefnbanhhlh", "{446900e4-71c2-419f-a6a7-df9c091e268b}", // Bitwarden
        "aeblfdkhhhdcdjpifhhbdiojplfjncoa", "dppgmdbiimibapkepcbdbmkaabgiofem", "{d634138d-c276-4fc8-924b-40a0ea21d284}", // 1Password
        "hdokiejnpimakedhajhdlcegeplioahd", "bbcinlkgjjkejfdpemiealijmmooekmp",                                   // LastPass
        "kbfnbcaeplbcioakkpcpgfkobkghlhen", "cnlefmmeadmemmdciolhbnfeacpdfbkd", "87677a2c52b84ad3a151a4a72f5bd3c4@jetpack", // Grammarly
        "ghbmnnjooekpmoecnnnilnnbdlolhkhi",                                                                       // Google Docs Offline
        "eimadpbcbfnmbkopoojfekhnkhdbieeh", "ifoakfbpdcdoeenechcleahebpibofpc", "addon@darkreader.org",           // Dark Reader
        "gcbommkclmclpchllfjekcdonpmejbdp", "jgnejmbjhcmcfgfbfkfiaobbjfnjejba",                                   // HTTPS Everywhere / DuckDuckGo
        "bkdgflcldnnnapblkhphbgpggdiikppg", "caoacbimdbbljakfhgikoodekdnlcgpk",                                   // DuckDuckGo Privacy Essentials
        "pkehgijcmpdhfbdbbnkijodmdjhbjlgp", "jmkmfnlbbbhbnhfahjanggpgcclfecoe",                                   // Privacy Badger
        "fihnjjcciajhdojfnbdddfaoknhalnja",                                                                       // I don't care about cookies
        "mlomiejdfkolichcflejclcbmpeaniij", "lklmhefoneonjalpjcnhaidnodopinib",                                   // Ghostery
        "efaidnbmnnnibpcajpcglclefindmkaj",                                                                       // Adobe Acrobat
        "fmkadmapgofadopljbjfkapdkoienihi",                                                                       // React Developer Tools
        "nmmhkkegccagdldgiimedpiccmgmieda",                                                                       // Google Wallet / Payments
        "aapocclcgogkmnckokdopfmhonfmgoek", "felcaaldnbdncclmgdcncolpebgiejap", "pjkljhegncpnkpknbcohdijeoejaedia",
        "apdfllckaahabafndbhieahigkjlhalf", "blpcfgokakmgnkcojhhkbfbldkacnbeo", "aohghmighlieiainnegkcijnfilokake", // Google built-ins
    };

    public static ExtensionRisk Rate(BrowserExtension e, out string why)
    {
        if (Trusted.Contains(e.Id))
        {
            why = "A well-known, widely used extension.";
            return ExtensionRisk.Trusted;
        }

        bool allSites = e.Sites.Any(s => AllSitesPatterns.Contains(s, StringComparer.OrdinalIgnoreCase));
        var sensitive = e.Permissions.Where(SensitivePermissions.ContainsKey).Select(p => SensitivePermissions[p]).Distinct().ToList();

        if (allSites && !e.FromStore)
        {
            why = "It can read and change every website you visit, and it wasn't installed from the browser's store. " +
                  "That's how adware and search hijackers usually get in. Remove it unless you put it there yourself.";
            return ExtensionRisk.High;
        }
        if (allSites)
        {
            why = "It can read and change every website you visit" + (sensitive.Count > 0 ? ", and " + string.Join(", ", sensitive.Take(3)) : "") +
                  ". Fine for a tool you trust; remove it if you don't recognise it.";
            return ExtensionRisk.Review;
        }
        if (!e.FromStore)
        {
            why = "It wasn't installed from the browser's store. Check you recognise it.";
            return ExtensionRisk.Review;
        }
        why = sensitive.Count > 0 ? "It can " + string.Join(", ", sensitive.Take(3)) + ", but only on specific sites." : "Limited access.";
        return ExtensionRisk.Low;
    }

    // ---- Chromium --------------------------------------------------------------------------------

    /// <summary>
    /// Parses one installed extension version folder (…\Extensions\&lt;id&gt;\&lt;version&gt;). The name can be a
    /// "__MSG_key__" placeholder looked up in the extension's default locale.
    /// </summary>
    public static BrowserExtension? ParseChromium(string browser, string id, string manifestJson, Func<string, string?> readLocaleMessages, ChromiumSettings? settings)
    {
        JsonElement m;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            m = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (m.ValueKind != JsonValueKind.Object) return null;
        if (settings is { IsComponent: true }) return null; // built into the browser

        string? locale = Str(m, "default_locale");
        Dictionary<string, string>? messages = null;
        string Resolve(string? text)
        {
            if (text is null || !text.StartsWith("__MSG_", StringComparison.Ordinal) || !text.EndsWith("__", StringComparison.Ordinal)) return text ?? "";
            if (messages == null)
            {
                messages = new(StringComparer.OrdinalIgnoreCase);
                if (locale != null && readLocaleMessages(locale) is { } json)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                        foreach (var p in doc.RootElement.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.Object && Str(p.Value, "message") is { } msg) messages[p.Name] = msg;
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            string key = text[6..^2];
            return messages.TryGetValue(key, out var value) ? value : text;
        }

        var permissions = new List<string>();
        var sites = new List<string>();
        foreach (var p in Strings(m, "permissions"))
            (p.Contains("://") || p == "<all_urls>" ? sites : permissions).Add(p);
        sites.AddRange(Strings(m, "host_permissions"));
        if (m.TryGetProperty("content_scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Array)
            foreach (var cs in scripts.EnumerateArray()) sites.AddRange(Strings(cs, "matches"));

        string name = Resolve(Str(m, "name"));
        if (name.StartsWith("__MSG_", StringComparison.Ordinal)) name = id;
        return new BrowserExtension
        {
            Browser = browser,
            Id = id,
            Name = name,
            Version = Str(m, "version"),
            Description = Resolve(Str(m, "description")) is { Length: > 0 } d ? d : null,
            Enabled = settings?.Enabled ?? true,
            FromStore = IsStoreUpdateUrl(Str(m, "update_url")) || settings?.FromStore == true,
            Permissions = permissions.Distinct().ToList(),
            Sites = sites.Distinct().ToList(),
        };
    }

    /// <summary>Extensions from a store update themselves from the store's servers.</summary>
    internal static bool IsStoreUpdateUrl(string? url) => url != null && (
        url.Contains("clients2.google.com/service/update2", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("edge.microsoft.com/extensionwebstorebase", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("extension-updates.opera.com", StringComparison.OrdinalIgnoreCase));

    /// <param name="FromStore">The browser itself records a store install (or an admin policy installed it).</param>
    public sealed record ChromiumSettings(bool Enabled, bool FromStore, bool IsComponent);

    /// <summary>
    /// Reads "extensions.settings" from a profile's Preferences / Secure Preferences files: whether each
    /// extension is enabled, came from the store, or is part of the browser itself.
    /// </summary>
    public static Dictionary<string, ChromiumSettings> ParseChromiumPreferences(params string?[] preferenceFiles)
    {
        var result = new Dictionary<string, ChromiumSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in preferenceFiles.Where(j => j != null))
        {
            try
            {
                using var doc = JsonDocument.Parse(json!);
                if (!doc.RootElement.TryGetProperty("extensions", out var ext) || !ext.TryGetProperty("settings", out var settings) ||
                    settings.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var p in settings.EnumerateObject())
                {
                    var s = p.Value;
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    int location = s.TryGetProperty("location", out var l) && l.TryGetInt32(out int li) ? li : 1;
                    bool fromStore = s.TryGetProperty("from_webstore", out var fw) && fw.ValueKind == JsonValueKind.True;
                    bool policy = location is 3 or 6 or 9;   // installed by an admin policy or external installer
                    bool component = location is 5 or 10;    // part of the browser
                    bool disabled = s.TryGetProperty("disable_reasons", out var dr) &&
                                    (dr.ValueKind == JsonValueKind.Number && dr.GetInt32() != 0 || dr.ValueKind == JsonValueKind.Array && dr.GetArrayLength() > 0);
                    if (s.TryGetProperty("state", out var st) && st.TryGetInt32(out int state) && state == 0) disabled = true;
                    result[p.Name] = new ChromiumSettings(!disabled, fromStore || policy, component);
                }
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }

    // ---- Firefox ---------------------------------------------------------------------------------

    /// <summary>Parses a Firefox profile's extensions.json.</summary>
    public static IReadOnlyList<BrowserExtension> ParseFirefox(string extensionsJson, string profile)
    {
        var list = new List<BrowserExtension>();
        try
        {
            using var doc = JsonDocument.Parse(extensionsJson);
            if (!doc.RootElement.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array) return list;
            foreach (var a in addons.EnumerateArray())
            {
                if (Str(a, "type") != "extension") continue;
                string location = Str(a, "location") ?? "";
                if (location.StartsWith("app-", StringComparison.Ordinal) && location != "app-profile") continue; // built into Firefox
                if (a.TryGetProperty("isSystem", out var sys) && sys.ValueKind == JsonValueKind.True) continue;

                string id = Str(a, "id") ?? "";
                string name = a.TryGetProperty("defaultLocale", out var dl) && Str(dl, "name") is { } n ? n : id;
                var perms = a.TryGetProperty("userPermissions", out var up) ? up : default;
                list.Add(new BrowserExtension
                {
                    Browser = "Firefox",
                    Id = id,
                    Name = name,
                    Version = Str(a, "version"),
                    Description = a.TryGetProperty("defaultLocale", out var dl2) ? Str(dl2, "description") : null,
                    Enabled = a.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True,
                    FromStore = !(a.TryGetProperty("sourceURI", out var src) && src.ValueKind == JsonValueKind.String &&
                                  !src.GetString()!.Contains("addons.mozilla.org", StringComparison.OrdinalIgnoreCase)
                                  && !src.GetString()!.Contains("mozilla.net", StringComparison.OrdinalIgnoreCase)),
                    Permissions = perms.ValueKind == JsonValueKind.Object ? Strings(perms, "permissions") : [],
                    Sites = perms.ValueKind == JsonValueKind.Object ? Strings(perms, "origins") : [],
                    Profile = profile,
                });
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
