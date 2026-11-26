using Declutter.Core.Health;
using Declutter.Core.Reports;

namespace Declutter.Core.Tests;

public class SystemInfoTests
{
    [Theory]
    [InlineData("To be filled by O.E.M.", null)]
    [InlineData("Default string", null)]
    [InlineData("0000000", null)]
    [InlineData("VMware-56 4d 2a", null)]
    [InlineData(" 5CG12345XY ", "5CG12345XY")]
    public void Serial_PlaceholdersAreIgnored(string raw, string? expected) =>
        Assert.Equal(expected, new SystemInfo { SerialNumber = raw }.RealSerial);

    [Theory]
    [InlineData("Dell Inc.", "ABC1234", "https://www.dell.com/support/home/product-support/servicetag/ABC1234/overview")]
    [InlineData("LENOVO", "PF12345", "https://pcsupport.lenovo.com/warrantylookup")]
    [InlineData("HP", null, "https://support.hp.com/checkwarranty")]
    [InlineData("Contoso", "X", null)]
    public void Warranty_PageForManufacturer(string maker, string? serial, string? expected) =>
        Assert.Equal(expected, Warranty.PageFor(maker, serial));

    [Theory]
    [InlineData(17_000_000_000L, 16L << 30)]   // 15.8 GiB visible → sold as 16 GB
    [InlineData(8_400_000_000L, 8L << 30)]
    [InlineData(1_000_000_000L, 1_000_000_000L)]
    public void Memory_RoundsToTheSizeOnTheBox(long visible, long expected) =>
        Assert.Equal(expected, SystemInfoText.RoundToMarketedSize(visible));

    [Fact]
    public void Memory_DescribesSlots()
    {
        var text = SystemInfoText.Memory(new SystemInfo { MemoryBytes = 16L << 30, MemorySpeedMHz = 3200, MemoryModules = 1, MemorySlots = 2 });
        Assert.Contains("16.0 GB", text);
        Assert.Contains("1 of 2 slots used", text);
    }

    [Theory]
    [InlineData(0, 0, 5, "5 minutes")]
    [InlineData(0, 3, 0, "3 hours")]
    [InlineData(1, 1, 0, "1 day, 1 hour")]
    [InlineData(12, 4, 0, "12 days, 4 hours")]
    public void Uptime_IsReadable(int d, int h, int m, string expected) =>
        Assert.Equal(expected, SystemInfoText.Uptime(new TimeSpan(d, h, m, 0)));

    [Fact]
    public void Report_EncodesHtml_AndHidesTheUser()
    {
        var builder = new ReportBuilder(@"C:\Users\mildp", "mildp");
        string html = builder.Build("mildp's laptop", new DateTime(2026, 11, 26, 9, 30, 0), [
            new ReportSection("Startup", [new ReportRow("<script>alert(1)</script>", @"C:\Users\mildp\AppData\Roaming\x.exe", "danger")], "Note for mildp"),
        ]);

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("mildp", html);
        Assert.Contains(@"%USERPROFILE%\AppData\Roaming\x.exe", html);
        Assert.Contains("class=\"danger\"", html);
        Assert.Contains("[user]", html);
    }

    [Fact]
    public void Report_ShortUserNamesArentBlindlyReplaced() =>
        Assert.Equal("Intel Core i7", new ReportBuilder(@"C:\Users\al", "al").Redact("Intel Core i7"));
}
