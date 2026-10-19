using Declutter.Core.Safety;
using Declutter.Core.Startup;

namespace Declutter.Core.Security;

/// <summary>Everything collected from the PC for one security check.</summary>
public sealed class SecurityEvidence
{
    public IReadOnlyList<ProcessInfo> Processes { get; init; } = [];
    public IReadOnlyList<TcpConnection> Connections { get; init; } = [];
    public IReadOnlyList<StartupItem> StartupItems { get; init; } = [];

    /// <summary>Keyed by full path, case-insensitive.</summary>
    public IReadOnlyDictionary<string, SignatureInfo> Signatures { get; init; } = new Dictionary<string, SignatureInfo>(StringComparer.OrdinalIgnoreCase);

    public DefenderStatus? Defender { get; init; }
    public IReadOnlyList<AntivirusProduct> AntivirusProducts { get; init; } = [];
    public IReadOnlyList<DefenderThreat> Threats { get; init; } = [];

    /// <summary>Null when they couldn't be read (needs administrator rights).</summary>
    public DefenderExclusions? Exclusions { get; init; }

    public string? HostsFile { get; init; }
    public bool ProxyEnabled { get; init; }
    public string? ProxyServer { get; init; }
    public string? ProxyAutoConfigUrl { get; init; }
    public IReadOnlyDictionary<string, bool?> FirewallProfiles { get; init; } = new Dictionary<string, bool?>();
    public bool? UacEnabled { get; init; }

    /// <summary>Null when they couldn't be read (needs administrator rights).</summary>
    public IReadOnlyList<WmiConsumer>? WmiConsumers { get; init; }
}

public sealed class SecurityReport
{
    public required IReadOnlyList<CheckOutcome> Outcomes { get; init; }
    public required IReadOnlyList<ProcessInfo> TopCpu { get; init; }

    /// <summary>All findings, most serious first.</summary>
    public IReadOnlyList<SecurityFinding> Findings =>
        Outcomes.SelectMany(o => o.Findings).OrderByDescending(f => f.Severity).ToList();

    public IReadOnlyList<string> Passed => Outcomes.Where(o => o.Passed && o.PassedText.Length > 0).Select(o => o.PassedText).ToList();

    public int CriticalCount => Findings.Count(f => f.Severity == Severity.Critical);
    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);

    public string Headline => (CriticalCount, WarningCount) switch
    {
        (0, 0) => "No signs of viruses, miners or tampering. Your PC looks healthy.",
        (0, 1) => "1 thing is worth a look. It may well be harmless.",
        (0, var w) => $"{w} things are worth a look. They may well be harmless.",
        (1, _) => "1 thing needs your attention.",
        (var c, _) => $"{c} things need your attention.",
    };
}

public static class SecurityAnalyzer
{
    public static SecurityReport Analyze(SecurityEvidence e, KnownFolders folders, DateTime nowUtc) => new()
    {
        Outcomes =
        [
            DefenderCheck.EvaluateProtection(e.Defender, e.AntivirusProducts),
            DefenderCheck.EvaluateThreats(e.Threats, nowUtc),
            MinerCheck.Evaluate(e.Processes, e.Connections, e.Signatures, folders),
            ImpostorCheck.Evaluate(e.Processes, folders),
            PersistenceCheck.Evaluate(e.StartupItems, e.Signatures, folders),
            SystemCheck.EvaluateWmi(e.WmiConsumers),
            DefenderCheck.EvaluateExclusions(e.Exclusions, folders),
            NetworkSettingsCheck.EvaluateHosts(e.HostsFile),
            NetworkSettingsCheck.EvaluateProxy(e.ProxyEnabled, e.ProxyServer, e.ProxyAutoConfigUrl),
            SystemCheck.EvaluateFirewall(e.FirewallProfiles),
            SystemCheck.EvaluateUac(e.UacEnabled),
        ],
        TopCpu = e.Processes.Where(p => p.CpuPercent >= 1).OrderByDescending(p => p.CpuPercent).Take(5).ToList(),
    };

    /// <summary>
    /// Executables whose signatures are worth checking: busy processes, processes in user-writable folders,
    /// processes talking to mining-pool ports, and startup programs outside Windows and Program Files.
    /// </summary>
    public static IReadOnlyList<string> PathsToVerify(IReadOnlyList<ProcessInfo> processes, IReadOnlyList<TcpConnection> connections,
        IReadOnlyList<StartupItem> startup, KnownFolders folders)
    {
        var poolPids = connections.Where(c => MinerCheck.PoolPorts.Contains(c.RemotePort)).Select(c => c.Pid).ToHashSet();
        return processes
            .Where(p => p.Path != null && (p.CpuPercent >= MinerCheck.HighCpuPercent || poolPids.Contains(p.Pid) || Locations.IsUserWritable(p.Path, folders)))
            .Select(p => p.Path!)
            .Concat(startup.Where(s => s.FilePath != null && !s.FileMissing && !Locations.IsProtectedInstallLocation(s.FilePath, folders)).Select(s => s.FilePath!))
            .Where(p => !p.Contains('"'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(150)
            .ToList();
    }
}
