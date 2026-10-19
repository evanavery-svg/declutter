using Declutter.Core.Startup;
using Declutter.Core.Util;

namespace Declutter.Core.Tests;

public class StartupTests
{
    [Fact]
    public void StartupApproved_MissingValue_MeansEnabled() => Assert.True(StartupApproved.IsEnabled(null));

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void StartupApproved_ReadsFlag(byte[] value, bool enabled) => Assert.Equal(enabled, StartupApproved.IsEnabled(value));

    [Fact]
    public void StartupApproved_EncodeRoundTrips()
    {
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        var off = StartupApproved.Encode(false, now);
        var on = StartupApproved.Encode(true, now);

        Assert.Equal(12, off.Length);
        Assert.False(StartupApproved.IsEnabled(off));
        Assert.Equal(now.ToFileTimeUtc(), BitConverter.ToInt64(off, 4));
        Assert.True(StartupApproved.IsEnabled(on));
        Assert.Equal(new byte[12] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, on);
    }

    [Fact]
    public void StartupApproved_SubKeys()
    {
        Assert.Equal("Run", StartupApproved.SubKeyFor(StartupSource.RegistryUser));
        Assert.Equal("Run32", StartupApproved.SubKeyFor(StartupSource.RegistryMachine32));
        Assert.Equal("StartupFolder", StartupApproved.SubKeyFor(StartupSource.StartupFolderAllUsers));
        Assert.Throws<ArgumentOutOfRangeException>(() => StartupApproved.SubKeyFor(StartupSource.ScheduledTask));
    }

    [Fact]
    public void StoreStartupState_Toggles()
    {
        Assert.True(StoreStartupState.IsEnabled(StoreStartupState.Enabled));
        Assert.False(StoreStartupState.IsEnabled(StoreStartupState.DisabledByUser));
        Assert.False(StoreStartupState.CanToggle(StoreStartupState.DisabledByPolicy));
        Assert.Equal(StoreStartupState.DisabledByUser, StoreStartupState.For(false));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", "C:\\Program Files\\App\\app.exe", "--minimized")]
    [InlineData("C:\\Program Files\\App\\app.exe --minimized", "C:\\Program Files\\App\\app.exe", "--minimized")]
    [InlineData("C:\\Program Files\\App\\app.exe", "C:\\Program Files\\App\\app.exe", "")]
    [InlineData("C:\\Tools\\run.cmd now C:\\x\\other.exe", "C:\\Tools\\run.cmd", "now C:\\x\\other.exe")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL", "rundll32.exe", "shell32.dll,Control_RunDLL")]
    [InlineData("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}", "MsiExec.exe", "/X{12345678-1234-1234-1234-123456789012}")]
    [InlineData("\"C:\\unterminated\\app.exe", "C:\\unterminated\\app.exe", "")]
    [InlineData("   ", "", "")]
    public void CommandLine_Splits(string input, string file, string args)
    {
        var (f, a) = CommandLine.Split(input);
        Assert.Equal(file, f);
        Assert.Equal(args, a);
    }

    [Theory]
    [InlineData("SecurityHealth", "%windir%\\system32\\SecurityHealthSystray.exe", StartupAdvice.Keep)]
    [InlineData("RtkAudUService", "\"C:\\Windows\\System32\\DriverStore\\RtkAudUService64.exe\" -background", StartupAdvice.Keep)]
    [InlineData("OneDrive", "\"C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe\" /background", StartupAdvice.Optional)]
    [InlineData("Spotify", "\"C:\\Users\\me\\AppData\\Roaming\\Spotify\\Spotify.exe\" --autostart", StartupAdvice.SafeToDisable)]
    [InlineData("MicrosoftEdgeAutoLaunch_0A1B2C", "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --no-startup-window", StartupAdvice.SafeToDisable)]
    [InlineData("Steam", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent", StartupAdvice.SafeToDisable)]
    [InlineData("Discord", "C:\\Users\\me\\AppData\\Local\\Discord\\Update.exe --processStart Discord.exe", StartupAdvice.Optional)]
    [InlineData("ResetHelper", "C:\\Tools\\ResetHelper.exe", StartupAdvice.Unknown)]
    [InlineData("Contoso Widget", "C:\\Contoso\\widget.exe", StartupAdvice.Unknown)]
    public void Advisor_ClassifiesCommonPrograms(string name, string command, StartupAdvice expected) =>
        Assert.Equal(expected, StartupAdvisor.Advise(name, command, null, fileMissing: false).Advice);

    [Fact]
    public void Advisor_MissingFile_IsSafeToDisable() =>
        Assert.Equal(StartupAdvice.SafeToDisable, StartupAdvisor.Advise("SecurityHealth", "x.exe", null, fileMissing: true).Advice);
}
