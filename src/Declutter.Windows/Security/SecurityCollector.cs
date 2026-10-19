using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Declutter.Core.Safety;
using Declutter.Core.Security;
using Declutter.Core.Startup;
using Microsoft.Win32;

namespace Declutter.Windows.Security;

/// <summary>Gathers everything the security checks look at. Read-only: nothing here changes the PC.</summary>
public sealed class SecurityCollector(StartupManager startup, KnownFolders folders, bool isElevated)
{
    private const string DefenderNs = @"root\Microsoft\Windows\Defender";

    /// <summary>How long CPU use is measured. Long enough to see a miner, short enough not to bore anyone.</summary>
    public TimeSpan CpuSampleWindow { get; init; } = TimeSpan.FromSeconds(4);

    public async Task<SecurityEvidence> CollectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Watching what's using your processor…");
        var processes = await Task.Run(() => SampleProcesses(ct), ct);

        progress?.Report("Checking network connections…");
        var connections = await ReadConnectionsAsync(ct);

        progress?.Report("Checking startup programs…");
        IReadOnlyList<StartupItem> startupItems;
        try
        {
            startupItems = await startup.GetItemsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            startupItems = [];
        }

        progress?.Report("Checking digital signatures…");
        var signatures = await VerifySignaturesAsync(SecurityAnalyzer.PathsToVerify(processes, connections, startupItems, folders), ct);

        progress?.Report("Asking Microsoft Defender…");
        return await Task.Run(() =>
        {
            var (proxyOn, proxyServer, pac) = ReadProxy();
            return new SecurityEvidence
            {
                Processes = processes,
                Connections = connections,
                StartupItems = startupItems,
                Signatures = signatures,
                Defender = ReadDefenderStatus(),
                AntivirusProducts = ReadAntivirusProducts(),
                Threats = ReadThreats(),
                Exclusions = ReadExclusions(),
                HostsFile = ReadHostsFile(),
                ProxyEnabled = proxyOn,
                ProxyServer = proxyServer,
                ProxyAutoConfigUrl = pac,
                FirewallProfiles = ReadFirewall(),
                UacEnabled = ReadUac(),
                WmiConsumers = isElevated ? ReadWmiConsumers() : null,
            };
        }, ct);
    }

    // --- Processes ---------------------------------------------------------------------------------

    private IReadOnlyList<ProcessInfo> SampleProcesses(CancellationToken ct)
    {
        var details = (Wmi.Query(@"root\cimv2", "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process") ?? [])
            .Select(r => (Pid: (int)(r.Long("ProcessId") ?? -1), Name: r.Str("Name") ?? "", Path: r.Str("ExecutablePath"), Cmd: r.Str("CommandLine")))
            .Where(p => p.Pid > 4)
            .GroupBy(p => p.Pid)
            .ToDictionary(g => g.Key, g => g.First());

        int self = Environment.ProcessId;
        var before = CpuTimes();
        var clock = Stopwatch.StartNew();
        ct.WaitHandle.WaitOne(CpuSampleWindow);
        ct.ThrowIfCancellationRequested();
        var after = CpuTimes();
        var elapsed = clock.Elapsed;

        var result = new List<ProcessInfo>();
        foreach (var (pid, d) in details)
        {
            if (pid == self) continue;
            double cpu = before.TryGetValue(pid, out var t0) && after.TryGetValue(pid, out var t1) && t1 >= t0
                ? Parsers.CpuPercent(t1 - t0, elapsed, Environment.ProcessorCount)
                : 0;
            result.Add(new ProcessInfo(pid, d.Name, d.Path, d.Cmd, cpu));
        }

        // If WMI was unavailable, fall back to what System.Diagnostics can see (no command lines).
        if (details.Count == 0)
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id <= 4 || p.Id == self) continue;
                    double cpu = before.TryGetValue(p.Id, out var t0) && after.TryGetValue(p.Id, out var t1) && t1 >= t0
                        ? Parsers.CpuPercent(t1 - t0, elapsed, Environment.ProcessorCount) : 0;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch (Exception) { }
                    result.Add(new ProcessInfo(p.Id, p.ProcessName + ".exe", path, null, cpu));
                }
            }
        }
        return result;
    }

    private static Dictionary<int, TimeSpan> CpuTimes()
    {
        var times = new Dictionary<int, TimeSpan>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    times[p.Id] = p.TotalProcessorTime;
                }
                catch (Exception)
                {
                    // protected processes: no access to their CPU time
                }
            }
        }
        return times;
    }

    // --- Network -----------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<TcpConnection>> ReadConnectionsAsync(CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("NETSTAT.EXE"), "-ano", ct);
            return Parsers.ParseNetstat(result.Output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    // --- Signatures --------------------------------------------------------------------------------

    /// <summary>
    /// Uses PowerShell's Get-AuthenticodeSignature, which (unlike a plain certificate read) also understands
    /// catalog-signed files, so Windows' own programs show as signed.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, SignatureInfo>> VerifySignaturesAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var map = new Dictionary<string, SignatureInfo>(StringComparer.OrdinalIgnoreCase);
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count == 0) return map;

        string input = Path.Combine(Path.GetTempPath(), $"declutter-sig-{Guid.NewGuid():N}.txt");
        string output = Path.ChangeExtension(input, ".json");
        try
        {
            await File.WriteAllLinesAsync(input, existing, new UTF8Encoding(false), ct);
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                $"$r = foreach ($p in [IO.File]::ReadAllLines('{input.Replace("'", "''")}')) {{" +
                "  $s = Get-AuthenticodeSignature -LiteralPath $p;" +
                "  [pscustomobject]@{ P = $p; S = [string]$s.Status; C = $(if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { $null }) }" +
                "};" +
                $"ConvertTo-Json -InputObject @($r) -Compress | Set-Content -LiteralPath '{output.Replace("'", "''")}' -Encoding UTF8";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await ProcessRunner.RunAsync(PowerShellPath, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeout.Token);

            if (!File.Exists(output)) return map;
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(output, ct));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string? path = e.TryGetProperty("P", out var p) ? p.GetString() : null;
                string? status = e.TryGetProperty("S", out var s) ? s.GetString() : null;
                string? signer = e.TryGetProperty("C", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (path != null && !string.IsNullOrEmpty(status)) map[path] = new SignatureInfo(status, Parsers.SignerName(signer));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Signatures are extra evidence; without them the checks simply stay quieter.
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
        return map;
    }

    internal Task<IReadOnlyDictionary<string, SignatureInfo>> VerifyForTestsAsync(IReadOnlyList<string> paths) =>
        VerifySignaturesAsync(paths, CancellationToken.None);

    private static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // --- Defender & Security Center -----------------------------------------------------------------

    private static DefenderStatus? ReadDefenderStatus()
    {
        var row = Wmi.Query(DefenderNs, "SELECT * FROM MSFT_MpComputerStatus")?.FirstOrDefault();
        if (row == null) return null;
        return new DefenderStatus
        {
            AntivirusEnabled = row.Bool("AntivirusEnabled") ?? false,
            RealTimeProtectionEnabled = row.Bool("RealTimeProtectionEnabled") ?? false,
            SignatureAgeDays = row.Days("AntivirusSignatureAge"),
            QuickScanAgeDays = row.Days("QuickScanAge"),
            FullScanAgeDays = row.Days("FullScanAge"),
            RunningMode = row.Str("AMRunningMode"),
        };
    }

    private static IReadOnlyList<AntivirusProduct> ReadAntivirusProducts() =>
        (Wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct") ?? [])
            .Where(r => r.Str("displayName") != null)
            .Select(r => AntivirusProduct.FromProductState(r.Str("displayName")!, (uint)(r.Long("productState") ?? 0)))
            .ToList();

    private static IReadOnlyList<DefenderThreat> ReadThreats()
    {
        var names = (Wmi.Query(DefenderNs, "SELECT ThreatID, ThreatName FROM MSFT_MpThreat") ?? [])
            .Where(r => r.Long("ThreatID") != null)
            .GroupBy(r => r.Long("ThreatID")!.Value)
            .ToDictionary(g => g.Key, g => g.First().Str("ThreatName") ?? "Unknown threat");

        return (Wmi.Query(DefenderNs, "SELECT ThreatID, ThreatStatusID, Resources, InitialDetectionTime FROM MSFT_MpThreatDetection") ?? [])
            .Select(r => new DefenderThreat(
                names.GetValueOrDefault(r.Long("ThreatID") ?? -1, "Unknown threat"),
                (int)(r.Long("ThreatStatusID") ?? 0),
                r.Strings("Resources"),
                r.Date("InitialDetectionTime")))
            .ToList();
    }

    private static DefenderExclusions? ReadExclusions()
    {
        var row = Wmi.Query(DefenderNs, "SELECT ExclusionPath, ExclusionProcess, ExclusionExtension FROM MSFT_MpPreference")?.FirstOrDefault();
        if (row == null) return null;
        string[] paths = row.Strings("ExclusionPath"), processes = row.Strings("ExclusionProcess"), extensions = row.Strings("ExclusionExtension");

        // Non-administrators get a placeholder ("N/A: Must be an administrator to view exclusions").
        if (paths.Concat(processes).Concat(extensions).Any(v => v.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))) return null;
        return new DefenderExclusions(paths, processes, extensions);
    }

    // --- Windows settings ----------------------------------------------------------------------------

    private static string? ReadHostsFile()
    {
        try
        {
            return File.ReadAllText(Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (bool Enabled, string? Server, string? Pac) ReadProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return (key?.GetValue("ProxyEnable") is int on && on != 0, key?.GetValue("ProxyServer") as string, key?.GetValue("AutoConfigURL") as string);
    }

    private static IReadOnlyDictionary<string, bool?> ReadFirewall() =>
        (Wmi.Query(@"root\StandardCimv2", "SELECT Name, Enabled FROM MSFT_NetFirewallProfile") ?? [])
            .Where(r => r.Str("Name") != null)
            .GroupBy(r => r.Str("Name")!)
            .ToDictionary(g => g.Key, g => g.First().Long("Enabled") switch { 0 => (bool?)false, 1 => true, _ => null });

    private static bool? ReadUac()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        return key?.GetValue("EnableLUA") is int v ? v != 0 : null;
    }

    private static IReadOnlyList<WmiConsumer>? ReadWmiConsumers()
    {
        var commandLine = Wmi.Query(@"root\subscription", "SELECT Name, CommandLineTemplate, ExecutablePath FROM CommandLineEventConsumer");
        var scripts = Wmi.Query(@"root\subscription", "SELECT Name, ScriptText, ScriptFileName FROM ActiveScriptEventConsumer");
        if (commandLine == null && scripts == null) return null;

        return (commandLine ?? [])
            .Select(r => new WmiConsumer(r.Str("Name") ?? "(unnamed)", "CommandLineEventConsumer", r.Str("CommandLineTemplate") ?? r.Str("ExecutablePath")))
            .Concat((scripts ?? []).Select(r => new WmiConsumer(r.Str("Name") ?? "(unnamed)", "ActiveScriptEventConsumer",
                r.Str("ScriptFileName") ?? Truncate(r.Str("ScriptText"), 300))))
            .ToList();
    }

    private static string? Truncate(string? s, int max) => s == null || s.Length <= max ? s : s[..max] + "…";
}
