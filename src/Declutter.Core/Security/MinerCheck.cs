using Declutter.Core.Safety;

namespace Declutter.Core.Security;

/// <summary>
/// Looks for cryptocurrency miners: well-known miner programs, mining command lines and pool
/// connections, and unknown programs quietly using a lot of CPU.
/// </summary>
public static class MinerCheck
{
    public const string Id = "miners";

    /// <summary>Process names (without .exe) of common open-source and commercial miners.</summary>
    internal static readonly HashSet<string> MinerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "xmrig", "xmrig-notls", "xmrig-cuda", "xmr-stak", "xmr-stak-rx", "xmrminer", "cpuminer", "cpuminer-opt", "minerd",
        "ethminer", "nbminer", "t-rex", "trex", "lolminer", "phoenixminer", "gminer", "miner", "teamredminer", "srbminer",
        "srbminer-multi", "nanominer", "bminer", "claymore", "ccminer", "cgminer", "bfgminer", "nicehashminer", "nhqm",
        "excavator", "kawpowminer", "wildrig", "rigel", "bzminer", "onezerominer", "xmrigdaemon", "moneroocean",
    };

    /// <summary>Pieces of a command line that only show up when something is mining.</summary>
    internal static readonly string[] CommandLineMarkers =
    [
        "stratum+tcp://", "stratum+ssl://", "stratum2+tcp://", "stratum1+tcp://", "--donate-level", "--randomx", "-a rx/0",
        "--algo=rx/0", "--cpu-max-threads-hint", "--coin monero", "--coin=monero", "--cuda-loader",
    ];

    /// <summary>Public mining pools; seeing one in a command line is a near-certain miner.</summary>
    internal static readonly string[] PoolDomains =
    [
        "nanopool.org", "supportxmr.com", "minexmr.com", "moneroocean.stream", "hashvault.pro", "c3pool.com", "2miners.com",
        "f2pool.com", "ethermine.org", "herominers.com", "unmineable.com", "nicehash.com", "miningpoolhub.com", "xmrpool.eu",
        "dwarfpool.com", "minergate.com", "prohashing.com", "zpool.ca", "zergpool.com", "woolypooly.com", "kryptex.network",
    ];

    /// <summary>Ports used almost exclusively by mining pools.</summary>
    internal static readonly HashSet<int> PoolPorts = [3333, 4444, 5555, 7777, 14433, 14444, 45560, 45700];

    /// <summary>A program using at least this share of the whole CPU, all the time, stands out.</summary>
    public const double HighCpuPercent = 25;

    public static CheckOutcome Evaluate(IReadOnlyList<ProcessInfo> processes, IReadOnlyList<TcpConnection> connections,
        IReadOnlyDictionary<string, SignatureInfo> signatures, KnownFolders folders)
    {
        var findings = new List<SecurityFinding>();
        var flagged = new HashSet<int>();

        foreach (var p in processes)
        {
            string name = Path.GetFileNameWithoutExtension(p.Name);
            string? marker = p.CommandLine is { } cmd
                ? CommandLineMarkers.FirstOrDefault(m => cmd.Contains(m, StringComparison.OrdinalIgnoreCase)) ??
                  PoolDomains.FirstOrDefault(d => cmd.Contains(d, StringComparison.OrdinalIgnoreCase))
                : null;

            if (MinerNames.Contains(name) || marker != null)
            {
                flagged.Add(p.Pid);
                findings.Add(new SecurityFinding
                {
                    CheckId = Id,
                    Severity = Severity.Critical,
                    Title = $"Crypto-miner running: {p.Name}",
                    Explanation = marker != null
                        ? "This program's settings point at a cryptocurrency mining pool. Unless you set up mining yourself, it is " +
                          "almost certainly malware using your PC to make money for someone else, which slows it down and drains the battery."
                        : "This is the name of a well-known cryptocurrency miner. Unless you installed it yourself, it's likely " +
                          "malware using your PC to make money for someone else.",
                    Evidence = p.CommandLine ?? p.Path,
                    FilePath = p.Path,
                    ProcessId = p.Pid,
                    Actions = Actions(p),
                });
            }
        }

        foreach (var group in connections.Where(c => PoolPorts.Contains(c.RemotePort) && !IsLocal(c.RemoteAddress) && c.Pid > 4)
                     .GroupBy(c => c.Pid))
        {
            if (flagged.Contains(group.Key)) continue;
            var p = processes.FirstOrDefault(x => x.Pid == group.Key);
            if (p != null && p.Path is { } path && Locations.IsProtectedInstallLocation(path, folders) &&
                signatures.TryGetValue(path, out var sig) && sig.IsValid) continue;

            flagged.Add(group.Key);
            var c = group.First();
            string who = p?.Name ?? $"Process {group.Key}";
            findings.Add(new SecurityFinding
            {
                CheckId = Id,
                Severity = Severity.Warning,
                Title = $"{who} is connected to a port mining pools use",
                Explanation = $"It's talking to {c.RemoteAddress} on port {c.RemotePort}, a port that cryptocurrency mining pools commonly use. " +
                              "Some legitimate apps use it too, so check what this program is before acting.",
                Evidence = p?.Path ?? $"{c.RemoteAddress}:{c.RemotePort}",
                FilePath = p?.Path,
                ProcessId = p?.Pid,
                Actions = p == null ? [] : Actions(p),
            });
        }

        foreach (var p in processes.Where(p => p.CpuPercent >= HighCpuPercent && !flagged.Contains(p.Pid) && p.Path != null))
        {
            // Only programs in folders anyone can write to. Windows and Program Files need admin rights to
            // install into, and catalog signatures there can't always be verified, so flagging them would
            // cry wolf about ordinary busy software.
            var sig = signatures.GetValueOrDefault(p.Path!);
            if (sig is { IsValid: true } || !Locations.IsUserWritable(p.Path!, folders)) continue;

            findings.Add(new SecurityFinding
            {
                CheckId = Id,
                Severity = Severity.Warning,
                Title = $"Unknown program using {p.CpuPercent:0}% of your CPU: {p.Name}",
                Explanation = (sig is { IsSigned: false } ? "It isn't digitally signed by its maker" : "It runs from a folder any program can write to") +
                              ", and it's working your processor hard right now. That's what hidden miners look like, though it " +
                              "could also be a game, a video export or a developer tool you started yourself.",
                Evidence = p.Path,
                FilePath = p.Path,
                ProcessId = p.Pid,
                Actions = Actions(p),
            });
        }

        return new CheckOutcome(Id, "No signs of crypto-miners or other programs secretly using your CPU", findings);
    }

    private static IReadOnlyList<FindingAction> Actions(ProcessInfo p) => p.Path == null
        ? [FindingAction.SearchOnline, FindingAction.EndProcess]
        : [FindingAction.ScanWithDefender, FindingAction.ShowFile, FindingAction.SearchOnline, FindingAction.EndProcess];

    internal static bool IsLocal(string address) =>
        address is "127.0.0.1" or "::1" or "0.0.0.0" or "::" or "[::1]" or "[::]" ||
        address.StartsWith("127.", StringComparison.Ordinal);
}
