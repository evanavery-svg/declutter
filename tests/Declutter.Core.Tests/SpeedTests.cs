using Declutter.Core.Health;
using Declutter.Core.Speed;

namespace Declutter.Core.Tests;

public class SpeedTests
{
    private const string Ns = "xmlns='http://schemas.microsoft.com/win/2004/08/events/event'";

    private static string Boot(string time, int boot, int main = 20000, int post = 10000) =>
        $"<Event {Ns}><System><EventID>100</EventID><TimeCreated SystemTime='{time}'/></System><EventData>" +
        $"<Data Name='BootTime'>{boot}</Data><Data Name='MainPathBootTime'>{main}</Data><Data Name='BootPostBootTime'>{post}</Data></EventData></Event>";

    private static string Slow(int id, string time, string friendly, string file, int ms) =>
        $"<Event {Ns}><System><EventID>{id}</EventID><TimeCreated SystemTime='{time}'/></System><EventData>" +
        $"<Data Name='Name'>{file}</Data><Data Name='FriendlyName'>{friendly}</Data><Data Name='TotalTime'>{ms + 500}</Data><Data Name='DegradationTime'>{ms}</Data></EventData></Event>";

    [Fact]
    public void BootEvents_AreParsed()
    {
        string xml = Boot("2026-11-20T08:00:00.000Z", 45000) + "\r\n" +
                     Slow(101, "2026-11-20T08:00:00.100Z", "Microsoft Teams", "ms-teams.exe", 6200) +
                     Slow(103, "2026-11-20T08:00:00.200Z", "Adobe Acrobat Update Service", "armsvc.exe", 2100) +
                     $"<Event {Ns}><System><EventID>200</EventID><TimeCreated SystemTime='2026-11-20T09:00:00Z'/></System></Event>";

        var (boots, slow) = BootEvents.Parse(xml);

        var b = Assert.Single(boots);
        Assert.Equal(45, b.Seconds);
        Assert.Equal(new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), b.TimeUtc);
        Assert.Equal(2, slow.Count);
        Assert.Equal(("Microsoft Teams", "App", 6200), (slow[0].Name, slow[0].Kind, slow[0].DegradationMs));
        Assert.Equal("Service", slow[1].Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No events were found that match the specified selection criteria.")]
    [InlineData("<Event><broken")]
    public void BootEvents_BadInput_IsEmpty(string text)
    {
        var (boots, slow) = BootEvents.Parse(text);
        Assert.Empty(boots);
        Assert.Empty(slow);
    }

    [Fact]
    public void Summary_UsesTheMedianOfRecentBoots_AndRanksCulprits()
    {
        var now = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);
        var boots = new[] { 30, 32, 200, 31, 35 }.Select((s, i) => new BootRecord(now.AddDays(-10 + i), s * 1000, 0, 0)).ToList();
        var slow = new List<BootSlowdown>
        {
            new(now.AddDays(-1), "Teams", "ms-teams.exe", 6000, "App"),
            new(now.AddDays(-2), "Teams", "ms-teams.exe", 8000, "App"),
            new(now.AddDays(-3), "Tiny", "t.exe", 300, "App"),        // under a second: not worth listing
            new(now.AddDays(-200), "Ancient", "a.exe", 9000, "App"),  // too old
        };

        var summary = BootEvents.Summarize(boots, slow, now);

        Assert.Equal(32, summary.TypicalSeconds); // the 200 s outlier doesn't skew it
        Assert.Equal(35, summary.Latest!.Seconds);
        var culprit = Assert.Single(summary.Culprits);
        Assert.Equal(("Teams", 2, 7.0), (culprit.Name, culprit.Times, culprit.AverageSeconds));
        Assert.Equal("Starts at an OK speed", summary.Verdict);
    }

    [Fact]
    public void Summary_WithNoBoots_SaysSo() =>
        Assert.Equal("No start-up timings recorded yet", BootEvents.Summarize([], [], DateTime.UtcNow).Verdict);

    private static ServiceInfo Svc(string name, string display, string? company = null) =>
        new() { Name = name, DisplayName = display, StartMode = "Auto", Company = company };

    [Theory]
    [InlineData("GoogleUpdaterService140.0", "GoogleUpdater Service (GoogleUpdater 140.0)", "Google LLC", ServiceAdvice.SafeToSetManual)]
    [InlineData("AdobeARMservice", "Adobe Acrobat Update Service", "Adobe", ServiceAdvice.SafeToSetManual)]
    [InlineData("NvTelemetryContainer", "NVIDIA Telemetry Container", "NVIDIA", ServiceAdvice.SafeToSetManual)]
    [InlineData("NVDisplay.ContainerLocalSystem", "NVIDIA Display Container LS", "NVIDIA", ServiceAdvice.Keep)]
    [InlineData("RtkAudioUniversalService", "Realtek Audio Universal Service", "Realtek", ServiceAdvice.Keep)]
    [InlineData("MBAMService", "Malwarebytes Service", "Malwarebytes", ServiceAdvice.Keep)]
    [InlineData("TeamViewer", "TeamViewer", "TeamViewer", ServiceAdvice.Optional)]
    [InlineData("HttpMonitorSvc", "Http Monitor", "Contoso", ServiceAdvice.Unknown)]
    public void Services_AreClassified(string name, string display, string company, ServiceAdvice expected) =>
        Assert.Equal(expected, ServiceAdvisor.Advise(Svc(name, display, company)).Advice);

    [Fact]
    public void Hardware_LowMemoryUnderPressure_SuggestsMoreRam_AndNotesTheFreeSlot()
    {
        var info = new SystemInfo { MemoryBytes = 8L << 30, MemoryFreeBytes = 1L << 30, MemoryModules = 1, MemorySlots = 2 };
        var notes = HardwareAdvisor.Advise(info, systemDriveIsHdd: false, diskUsedFraction: 0.5, typicalBootSeconds: 30);
        Assert.Contains(notes, n => n.Kind == NoteKind.Warning && n.Text.Contains("more RAM"));
        Assert.Contains(notes, n => n.Text.Contains("free memory slot"));
    }

    [Fact]
    public void Hardware_HddAndFullDisk_AreFlagged()
    {
        var info = new SystemInfo { MemoryBytes = 16L << 30, MemoryFreeBytes = 8L << 30 };
        var notes = HardwareAdvisor.Advise(info, systemDriveIsHdd: true, diskUsedFraction: 0.95, typicalBootSeconds: 120);
        Assert.Contains(notes, n => n.Text.Contains("SSD typically makes"));
        Assert.Contains(notes, n => n.Text.Contains("95% full") || n.Text.Contains("95 % full"));
        Assert.Contains(notes, n => n.Kind == NoteKind.Good && n.Text.Contains("plenty"));
    }

    [Fact]
    public void Hardware_SlowBootOnSsd_PointsAtStartupApps() =>
        Assert.Contains(HardwareAdvisor.Advise(new SystemInfo(), false, null, 100), n => n.Text.Contains("Startup page"));
}
