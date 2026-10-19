using Declutter.Core.Apps;

namespace Declutter.Core.Tests;

public class AppsTests
{
    private const string Guid1 = "{12345678-1234-1234-1234-123456789012}";

    private static Dictionary<string, object?> Values(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Parse_ReadsAllFields()
    {
        var app = UninstallEntryParser.Parse("Contoso", Values(
            ("DisplayName", "Contoso Studio "), ("Publisher", "Contoso Ltd"), ("DisplayVersion", "2.1"),
            ("InstallDate", "20240315"), ("EstimatedSize", 2048), ("UninstallString", "\"C:\\Program Files\\Contoso\\uninst.exe\""),
            ("InstallLocation", "\"C:\\Program Files\\Contoso\"")), machineWide: true)!;

        Assert.Equal("Contoso Studio", app.Name);
        Assert.Equal("Contoso Ltd", app.Publisher);
        Assert.Equal(new DateTime(2024, 3, 15), app.InstallDate);
        Assert.Equal(2048 * 1024L, app.SizeBytes);
        Assert.Equal("C:\\Program Files\\Contoso", app.InstallLocation);
        Assert.True(app.RequiresAdmin);
        Assert.Equal(AppKind.Desktop, app.Kind);
    }

    [Theory]
    [InlineData("SystemComponent", 1)]
    [InlineData("ParentKeyName", "OfficeParent")]
    [InlineData("ReleaseType", "Security Update")]
    public void Parse_HidesComponentsAndUpdates(string key, object value) =>
        Assert.Null(UninstallEntryParser.Parse("X", Values(("DisplayName", "Thing"), ("UninstallString", "u.exe"), (key, value)), false));

    [Fact]
    public void Parse_HidesKbUpdatesAndNamelessEntries()
    {
        Assert.Null(UninstallEntryParser.Parse("X", Values(("DisplayName", "Update for Windows (KB5034441)"), ("UninstallString", "u.exe")), false));
        Assert.Null(UninstallEntryParser.Parse("X", Values(("UninstallString", "u.exe")), false));
        Assert.Null(UninstallEntryParser.Parse("X", Values(("DisplayName", "No uninstaller")), false));
    }

    [Fact]
    public void Msi_InstallSwitch_IsTurnedIntoUninstall()
    {
        var app = UninstallEntryParser.Parse(Guid1, Values(("DisplayName", "Thing"), ("WindowsInstaller", 1),
            ("UninstallString", $"MsiExec.exe /I{Guid1}")), true)!;

        Assert.True(app.IsMsi);
        Assert.Equal($"MsiExec.exe /X{Guid1}", app.UninstallCommand);
    }

    [Fact]
    public void Msi_WithoutUninstallString_UsesProductCode()
    {
        var app = UninstallEntryParser.Parse(Guid1, Values(("DisplayName", "Thing"), ("WindowsInstaller", 1)), true)!;
        Assert.Equal($"MsiExec.exe /X{Guid1}", app.UninstallCommand);
    }

    [Fact]
    public void NonMsi_KeepsItsOwnUninstaller()
    {
        var app = UninstallEntryParser.Parse("Thing", Values(("DisplayName", "Thing"), ("UninstallString", "\"C:\\x\\unins000.exe\" /SILENT")), false)!;
        Assert.Equal("\"C:\\x\\unins000.exe\" /SILENT", app.UninstallCommand);
    }

    [Theory]
    [InlineData("20231201", 2023, 12, 1)]
    [InlineData("2023-12-01", 2023, 12, 1)]
    [InlineData("12/1/2023", 2023, 12, 1)]
    public void InstallDate_Formats(string raw, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), UninstallEntryParser.ParseInstallDate(raw));

    [Theory]
    [InlineData("garbage")]
    [InlineData("19000101")]
    [InlineData(null)]
    public void InstallDate_Invalid(string? raw) => Assert.Null(UninstallEntryParser.ParseInstallDate(raw));

    [Fact]
    public void Deduplicate_KeepsOneEntryPerNameAndVersion()
    {
        var a = UninstallEntryParser.Parse("A", Values(("DisplayName", "Tool"), ("DisplayVersion", "1"), ("UninstallString", "u")), true)!;
        var b = UninstallEntryParser.Parse("B", Values(("DisplayName", "tool"), ("DisplayVersion", "1"), ("UninstallString", "u"), ("EstimatedSize", 5)), false)!;
        var c = UninstallEntryParser.Parse("C", Values(("DisplayName", "Tool"), ("DisplayVersion", "2"), ("UninstallString", "u")), true)!;

        var result = UninstallEntryParser.Deduplicate([a, b, c]);

        Assert.Equal(2, result.Count);
        Assert.Contains(b, result);
    }

    [Theory]
    [InlineData("Microsoft.BingNews", AppAdvice.Bloatware)]
    [InlineData("Clipchamp.Clipchamp", AppAdvice.Bloatware)]
    [InlineData("Microsoft.Windows.DevHome", AppAdvice.Bloatware)]
    [InlineData("MicrosoftWindows.Client.WebExperience", AppAdvice.Bloatware)]
    [InlineData("king.com.CandyCrushSaga", AppAdvice.Bloatware)]
    [InlineData("Microsoft.Windows.Photos", AppAdvice.Useful)]
    [InlineData("Microsoft.WindowsCalculator", AppAdvice.Useful)]
    [InlineData("Microsoft.WindowsStore", AppAdvice.Required)]
    [InlineData("Microsoft.Windows.ShellExperienceHost", AppAdvice.Required)]
    [InlineData("Microsoft.HEVCVideoExtension", AppAdvice.Required)]
    [InlineData("Contoso.SomeApp", AppAdvice.Unknown)]
    public void Advisor_StoreApps(string package, AppAdvice expected)
    {
        var app = new InstalledApp { Id = package, Name = package, Kind = AppKind.Store, PackageName = package };
        Assert.Equal(expected, AppAdvisor.Advise(app).Advice);
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Corporation", AppAdvice.Required)]
    [InlineData("Realtek Audio Driver", "Realtek Semiconductor Corp.", AppAdvice.Required)]
    [InlineData("Driver Booster 11", "IObit", AppAdvice.Bloatware)]
    [InlineData("McAfee LiveSafe", "McAfee, LLC", AppAdvice.Bloatware)]
    [InlineData("Dell SupportAssist", "Dell Inc.", AppAdvice.Useful)]
    [InlineData("Microsoft Edge", "Microsoft Corporation", AppAdvice.Useful)]
    [InlineData("VLC media player", "VideoLAN", AppAdvice.Unknown)]
    public void Advisor_DesktopApps(string name, string publisher, AppAdvice expected)
    {
        var app = new InstalledApp { Id = name, Name = name, Publisher = publisher, Kind = AppKind.Desktop };
        Assert.Equal(expected, AppAdvisor.Advise(app).Advice);
    }
}
