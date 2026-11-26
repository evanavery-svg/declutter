using Declutter.Core.Health;
using Declutter.Core.Protection;

namespace Declutter.Core.Tests;

public class ProtectionTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    // ---- Backup ----

    private static Dictionary<string, bool> Folders(bool desktop, bool docs, bool pics) =>
        new() { ["Desktop"] = desktop, ["Documents"] = docs, ["Pictures"] = pics };

    [Fact]
    public void Backup_OneDriveCoveringEverything_IsGood()
    {
        var r = BackupCheck.Evaluate(new BackupEvidence { FoldersInOneDrive = Folders(true, true, true), OneDriveRunning = true }, Now);
        Assert.Equal(HealthGrade.Good, r.Grade);
        Assert.Contains(r.Notes, n => n.Text.Contains("Desktop, Documents and Pictures are backed up"));
    }

    [Fact]
    public void Backup_OneDriveNotRunning_IsOnlyPartial()
    {
        var r = BackupCheck.Evaluate(new BackupEvidence { FoldersInOneDrive = Folders(true, true, true), OneDriveRunning = false }, Now);
        Assert.Equal(HealthGrade.Fair, r.Grade);
        Assert.Contains(r.Notes, n => n.Kind == NoteKind.Warning && n.Text.Contains("isn't running"));
    }

    [Fact]
    public void Backup_Nothing_IsPoor_WithAPlainWarning()
    {
        var r = BackupCheck.Evaluate(new BackupEvidence { FoldersInOneDrive = Folders(false, false, false) }, Now);
        Assert.Equal(HealthGrade.Poor, r.Grade);
        Assert.Equal(NoteKind.Critical, r.Notes[0].Kind);
        Assert.Contains("would be gone", r.Notes[0].Text);
    }

    [Fact]
    public void Backup_RecentFileHistory_IsGood_StaleIsAWarning()
    {
        Assert.Equal(HealthGrade.Good, BackupCheck.Evaluate(new BackupEvidence { FileHistoryConfigured = true, FileHistoryLastBackupUtc = Now.AddDays(-1) }, Now).Grade);
        var stale = BackupCheck.Evaluate(new BackupEvidence { FileHistoryConfigured = true, FileHistoryLastBackupUtc = Now.AddDays(-40) }, Now);
        Assert.Equal(HealthGrade.Fair, stale.Grade);
        Assert.Contains(stale.Notes, n => n.Text.Contains("40 days ago"));
    }

    [Fact]
    public void Backup_ThirdPartyApp_IsAcknowledged() =>
        Assert.Equal(HealthGrade.Fair, BackupCheck.Evaluate(new BackupEvidence { BackupApps = ["Backblaze"] }, Now).Grade);

    // ---- Device security ----

    [Fact]
    public void Device_UnencryptedLaptop_IsAWarning_DesktopIsInfo()
    {
        Assert.Equal(HealthGrade.Fair, DeviceSecurityCheck.Evaluate(new DeviceSecurityEvidence { DriveEncrypted = false, SecureBoot = true, IsLaptop = true }).Grade);
        Assert.Equal(HealthGrade.Good, DeviceSecurityCheck.Evaluate(new DeviceSecurityEvidence { DriveEncrypted = false, SecureBoot = true, IsLaptop = false }).Grade);
    }

    [Fact]
    public void Device_SecureBootOff_IsAWarning() =>
        Assert.Contains(DeviceSecurityCheck.Evaluate(new DeviceSecurityEvidence { DriveEncrypted = true, SecureBoot = false }).Notes, n => n.Text.Contains("Secure Boot is off"));

    [Fact]
    public void Device_NothingReadable_IsUnknown() =>
        Assert.Equal(HealthGrade.Unknown, DeviceSecurityCheck.Evaluate(new DeviceSecurityEvidence()).Grade);

    // ---- Windows Update ----

    [Theory]
    [InlineData("Windows 11 Home", "24H2", "2026-10-13")]
    [InlineData("Windows 11 Pro", "25H2", "2027-10-12")]
    [InlineData("Windows 11 Enterprise", "24H2", "2027-10-13")]
    [InlineData("Windows 10 Pro", "22H2", "2025-10-14")]
    public void EndOfSupport_Dates(string name, string version, string expected) =>
        Assert.Equal(DateTime.Parse(expected), WindowsUpdateCheck.EndOfSupport(name, version));

    [Fact]
    public void Update_OutOfSupportVersion_IsPoor()
    {
        var r = WindowsUpdateCheck.Evaluate(new UpdateEvidence { WindowsName = "Windows 11 Home", WindowsVersion = "24H2", LastSuccessfulUpdateUtc = Now.AddDays(-3) }, Now);
        Assert.Equal(HealthGrade.Poor, r.Grade);
        Assert.Contains(r.Notes, n => n.Kind == NoteKind.Critical && n.Text.Contains("stopped getting security updates"));
    }

    [Fact]
    public void Update_CurrentAndRecent_IsGood()
    {
        var r = WindowsUpdateCheck.Evaluate(new UpdateEvidence { WindowsName = "Windows 11 Home", WindowsVersion = "25H2", LastSuccessfulUpdateUtc = Now.AddDays(-5) }, Now);
        Assert.Equal(HealthGrade.Good, r.Grade);
    }

    [Fact]
    public void Update_PendingRestartAndStaleUpdates_AreWarnings()
    {
        var r = WindowsUpdateCheck.Evaluate(new UpdateEvidence { WindowsName = "Windows 11 Pro", WindowsVersion = "25H2", LastSuccessfulUpdateUtc = Now.AddDays(-60), RestartPending = true }, Now);
        Assert.Equal(HealthGrade.Fair, r.Grade);
        Assert.Contains(r.Notes, n => n.Text.Contains("60 days ago"));
        Assert.Contains(r.Notes, n => n.Text.Contains("waiting for a restart"));
    }

    // ---- Extensions ----

    private const string Manifest = """
        { "manifest_version": 3, "name": "__MSG_appName__", "description": "__MSG_appDesc__", "version": "2.1", "default_locale": "en",
          "permissions": ["cookies", "tabs", "storage"], "host_permissions": ["<all_urls>"],
          "update_url": "https://clients2.google.com/service/update2/crx" }
        """;

    private static string? Locale(string l) => l == "en" ? """{ "appName": { "message": "Coupon Helper" }, "appDesc": { "message": "Saves money" } }""" : null;

    [Fact]
    public void Chromium_ManifestIsParsed_WithLocalisedName()
    {
        var e = Extensions.ParseChromium("Chrome", "abcdefghijklmnopabcdefghijklmnop", Manifest, Locale, null)!;
        Assert.Equal("Coupon Helper", e.Name);
        Assert.Equal("Saves money", e.Description);
        Assert.Contains("<all_urls>", e.Sites);
        Assert.Contains("cookies", e.Permissions);
        Assert.True(e.FromStore);
    }

    [Fact]
    public void Chromium_ComponentExtensions_AreHidden() =>
        Assert.Null(Extensions.ParseChromium("Edge", "x", Manifest, Locale, new Extensions.ChromiumSettings(true, false, IsComponent: true)));

    [Fact]
    public void Rating_BroadAccessFromStore_IsReview_SideloadedIsHigh()
    {
        var store = Extensions.ParseChromium("Chrome", "abcdefghijklmnopabcdefghijklmnop", Manifest, Locale, null)!;
        Assert.Equal(ExtensionRisk.Review, Extensions.Rate(store, out var why));
        Assert.Contains("read your logins", why);

        var sideloaded = Extensions.ParseChromium("Chrome", "abcdefghijklmnopabcdefghijklmnop", Manifest.Replace("clients2.google.com/service/update2/crx", "evil.example/upd"), Locale, null)!;
        Assert.False(sideloaded.FromStore);
        Assert.Equal(ExtensionRisk.High, Extensions.Rate(sideloaded, out _));
    }

    [Fact]
    public void Rating_KnownGood_IsTrusted()
    {
        var ublock = new BrowserExtension { Browser = "Chrome", Id = "cjpalhdlnbpafiamejdnhcphjbkeiagm", Name = "uBlock Origin", Sites = ["<all_urls>"], FromStore = true };
        Assert.Equal(ExtensionRisk.Trusted, Extensions.Rate(ublock, out _));
    }

    [Fact]
    public void Rating_NarrowAccess_IsLow() =>
        Assert.Equal(ExtensionRisk.Low, Extensions.Rate(new BrowserExtension { Browser = "Chrome", Id = "x", Name = "Site helper", Sites = ["https://example.com/*"] }, out _));

    [Fact]
    public void Chromium_Preferences_GiveEnabledAndStoreFlags()
    {
        const string prefs = """
            { "extensions": { "settings": {
              "aaa": { "location": 1, "from_webstore": true, "state": 1 },
              "bbb": { "location": 4, "disable_reasons": 1 },
              "ccc": { "location": 5 },
              "ddd": { "location": 3 } } } }
            """;
        var s = Extensions.ParseChromiumPreferences(prefs, null);
        Assert.Equal(new Extensions.ChromiumSettings(true, true, false), s["aaa"]);
        Assert.Equal(new Extensions.ChromiumSettings(false, false, false), s["bbb"]);
        Assert.True(s["ccc"].IsComponent);
        Assert.True(s["ddd"].FromStore); // installed by policy / an external installer
    }

    [Fact]
    public void Firefox_ExtensionsJson_IsParsed()
    {
        const string json = """
            { "addons": [
              { "id": "uBlock0@raymondhill.net", "type": "extension", "location": "app-profile", "active": true, "version": "1.60",
                "sourceURI": "https://addons.mozilla.org/firefox/downloads/file/1/ublock.xpi",
                "defaultLocale": { "name": "uBlock Origin" }, "userPermissions": { "permissions": ["webRequest"], "origins": ["<all_urls>"] } },
              { "id": "sideload@x", "type": "extension", "location": "app-profile", "active": true, "sourceURI": "file:///C:/x.xpi",
                "defaultLocale": { "name": "Search Booster" }, "userPermissions": { "permissions": [], "origins": ["<all_urls>"] } },
              { "id": "formautofill@mozilla.org", "type": "extension", "location": "app-builtin", "active": true, "defaultLocale": { "name": "Form Autofill" } },
              { "id": "fr@dictionaries", "type": "dictionary", "location": "app-profile" } ] }
            """;
        var list = Extensions.ParseFirefox(json, "default-release");
        Assert.Equal(["uBlock Origin", "Search Booster"], list.Select(e => e.Name));
        Assert.Equal(ExtensionRisk.Trusted, Extensions.Rate(list[0], out _));
        Assert.Equal(ExtensionRisk.High, Extensions.Rate(list[1], out _));
    }

    // ---- winget ----

    [Fact]
    public void Winget_UpgradeTable_IsParsed()
    {
        string output = "\r   - \r   \\ \r" + """
            Name                               Id                          Version      Available    Source
            ---------------------------------------------------------------------------------------------------
            Google Chrome                      Google.Chrome               130.0.6723.7 131.0.6778.86 winget
            Microsoft Visual Studio Code       Microsoft.VisualStudioCode  1.94.0       1.95.3       winget
            Notepad++ (64-bit x64)             Notepad++.Notepad++         8.6.9        8.7.1        winget
            3 upgrades available.
            """;
        var updates = Winget.ParseUpgradeTable(output);
        Assert.Equal(3, updates.Count);
        Assert.Equal(new AppUpdate("Google Chrome", "Google.Chrome", "130.0.6723.7", "131.0.6778.86", "winget"), updates[0]);
        Assert.Equal("Notepad++.Notepad++", updates[2].Id);
    }

    [Fact]
    public void Winget_LocalisedHeadings_StillParse()
    {
        string output = """
            Name                Id                Version  Verfügbar Quelle
            ------------------------------------------------------------------
            7-Zip 24.07 (x64)   7zip.7zip         24.07    24.08     winget
            2 Aktualisierungen verfügbar.
            """;
        var u = Assert.Single(Winget.ParseUpgradeTable(output));
        Assert.Equal(("7zip.7zip", "24.08"), (u.Id, u.Available));
    }

    [Theory]
    [InlineData("No installed package found matching input criteria.")]
    [InlineData("")]
    public void Winget_NothingToUpgrade_IsEmpty(string output) => Assert.Empty(Winget.ParseUpgradeTable(output));
}
