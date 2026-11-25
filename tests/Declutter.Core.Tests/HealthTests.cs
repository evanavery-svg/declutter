using Declutter.Core.Health;

namespace Declutter.Core.Tests;

public class HealthTests
{
    private static DiskInfo Ssd(string health = "Healthy", int? wear = 5, int? temp = 38) => new()
    {
        Name = "Samsung SSD 980", MediaType = "SSD", BusType = "NVMe", SizeBytes = 512L << 30, HealthStatus = health,
        WearPercent = wear, TemperatureC = temp, TemperatureMaxC = 70, PowerOnHours = 4380, IsSystemDisk = true,
    };

    [Fact]
    public void HealthySsd_IsGood()
    {
        var a = HealthAnalyzer.AssessDisk(Ssd());
        Assert.Equal(HealthGrade.Good, a.Grade);
        Assert.Contains("NVMe SSD", a.Summary);
        Assert.Contains(a.Notes, n => n.Text.Contains("5% of its rated lifetime"));
        Assert.Contains(a.Notes, n => n.Text.Contains("183 days"));
    }

    [Fact]
    public void UnhealthyDrive_IsPoor_WithBackupAdvice()
    {
        var a = HealthAnalyzer.AssessDisk(Ssd("Unhealthy"));
        Assert.Equal(HealthGrade.Poor, a.Grade);
        Assert.Contains(a.Notes, n => n.Kind == NoteKind.Critical && n.Text.Contains("Back up"));
    }

    [Theory]
    [InlineData("Warning", 5, 38, HealthGrade.Fair)]
    [InlineData("Healthy", 85, 38, HealthGrade.Fair)]
    [InlineData("Healthy", 100, 38, HealthGrade.Poor)]
    [InlineData("Healthy", 5, 75, HealthGrade.Fair)]
    public void WarningsDowngradeTheGrade(string health, int wear, int temp, HealthGrade expected) =>
        Assert.Equal(expected, HealthAnalyzer.AssessDisk(Ssd(health, wear, temp)).Grade);

    [Fact]
    public void UncorrectedErrors_AreAWarning()
    {
        var a = HealthAnalyzer.AssessDisk(Ssd() with { ReadErrorsUncorrected = 3 });
        Assert.Equal(HealthGrade.Fair, a.Grade);
        Assert.Contains(a.Notes, n => n.Text.Contains("3 read/write errors"));
    }

    [Fact]
    public void ZeroTemperature_MeansNotReported()
    {
        var a = HealthAnalyzer.AssessDisk(Ssd(temp: 0));
        Assert.DoesNotContain(a.Notes, n => n.Text.Contains("°C"));
    }

    [Fact]
    public void SystemHardDrive_GetsTheSsdTip()
    {
        var hdd = new DiskInfo { Name = "WDC WD10", MediaType = "HDD", BusType = "SATA", SizeBytes = 1L << 40, HealthStatus = "Healthy", IsSystemDisk = true };
        var a = HealthAnalyzer.AssessDisk(hdd);
        Assert.Contains("Hard drive", a.Summary);
        Assert.Contains(a.Notes, n => n.Text.Contains("SSD is the single biggest speed-up"));
    }

    [Fact]
    public void UnknownStatus_WithNoOtherData_IsNotCalledGood() =>
        Assert.Equal(HealthGrade.Unknown, HealthAnalyzer.AssessDisk(new DiskInfo { Name = "USB stick", HealthStatus = "Unknown" }).Grade);

    [Fact]
    public void TemperatureMax_ThatIsJustTheHottestRecorded_IsNotUsedAsALimit()
    {
        // Drive at 41°C whose "max" is its own record of 41°C: normal, not hot.
        var a = HealthAnalyzer.AssessDisk(Ssd(temp: 41) with { TemperatureMaxC = 41 });
        Assert.Equal(HealthGrade.Good, a.Grade);
        Assert.Contains(a.Notes, n => n.Text.Contains("41°C, which is normal"));
    }

    [Fact]
    public void TemperatureMax_ThatIsARealLimit_IsUsed()
    {
        var a = HealthAnalyzer.AssessDisk(Ssd(temp: 66) with { TemperatureMaxC = 65 });
        Assert.Equal(HealthGrade.Fair, a.Grade);
        Assert.Contains(a.Notes, n => n.Text.Contains("limit 65°C"));
    }

    [Fact]
    public void NoData_IsUnknown() =>
        Assert.Equal(HealthGrade.Unknown, HealthAnalyzer.AssessDisk(new DiskInfo { Name = "Virtual Disk" }).Grade);

    [Theory]
    [InlineData(50000, 47000, HealthGrade.Good, "94%")]
    [InlineData(50000, 35000, HealthGrade.Fair, "70%")]
    [InlineData(50000, 25000, HealthGrade.Poor, "50%")]
    [InlineData(50000, 52000, HealthGrade.Good, "100%")]
    public void Battery_GradesByRemainingCapacity(long design, long full, HealthGrade grade, string percent)
    {
        var a = HealthAnalyzer.AssessBattery(new BatteryInfo { Name = "B", DesignCapacityMWh = design, FullChargeCapacityMWh = full, CycleCount = 210 });
        Assert.Equal(grade, a.Grade);
        Assert.Contains(percent, a.Summary);
        Assert.Contains(a.Notes, n => n.Text.Contains("210 charge cycles"));
    }

    [Fact]
    public void Battery_WithoutCapacity_IsUnknown() =>
        Assert.Equal(HealthGrade.Unknown, HealthAnalyzer.AssessBattery(new BatteryInfo { Name = "B" }).Grade);

    [Fact]
    public void BatteryReport_IsParsed()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <SystemInformation><ComputerName>LAPTOP</ComputerName></SystemInformation>
              <Batteries>
                <Battery>
                  <Id>DELL 1VX1H21</Id>
                  <Manufacturer>SMP</Manufacturer>
                  <Chemistry>LiP</Chemistry>
                  <DesignCapacity>54000</DesignCapacity>
                  <FullChargeCapacity>48600</FullChargeCapacity>
                  <CycleCount>187</CycleCount>
                </Battery>
              </Batteries>
              <RuntimeEstimates><FullChargeCapacity><ActiveRuntime>PT5H</ActiveRuntime></FullChargeCapacity></RuntimeEstimates>
            </BatteryReport>
            """;
        var b = Assert.Single(BatteryReportParser.Parse(xml));
        Assert.Equal("DELL 1VX1H21", b.Name);
        Assert.Equal(54000, b.DesignCapacityMWh);
        Assert.Equal(48600, b.FullChargeCapacityMWh);
        Assert.Equal(187, b.CycleCount);
        Assert.Equal(90, b.HealthPercent!.Value, 3);
    }

    [Theory]
    [InlineData("<BatteryReport><Batteries /></BatteryReport>")]
    [InlineData("not xml")]
    public void BatteryReport_WithoutBatteries_IsEmpty(string xml) => Assert.Empty(BatteryReportParser.Parse(xml));
}
