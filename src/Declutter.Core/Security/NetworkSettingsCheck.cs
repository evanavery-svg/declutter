namespace Declutter.Core.Security;

/// <summary>
/// The hosts file and proxy settings decide where your PC sends web traffic. Malware changes them to
/// block antivirus updates, show its own ads, or snoop on what you browse.
/// </summary>
public static class NetworkSettingsCheck
{
    public const string HostsId = "hosts";
    public const string ProxyId = "proxy";

    private static readonly string[] SensitiveDomains =
    [
        "microsoft.com", "windowsupdate", "windows.com", "live.com", "msftncsi", "defender", "smartscreen",
        "avast", "avg.com", "kaspersky", "mcafee", "norton", "symantec", "bitdefender", "malwarebytes", "eset", "sophos",
        "trendmicro", "avira", "virustotal", "google.com", "bing.com", "facebook.com", "apple.com", "paypal",
        "amazon.", "bank", "chase.com", "wellsfargo", "citi", "hsbc", "barclays",
    ];

    public sealed record HostsEntry(string Address, string HostName);

    public static IReadOnlyList<HostsEntry> ParseHosts(string text)
    {
        var entries = new List<HostsEntry>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var parts = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < parts.Length; i++) entries.Add(new HostsEntry(parts[0], parts[i].ToLowerInvariant()));
        }
        return entries;
    }

    public static CheckOutcome EvaluateHosts(string? hostsText)
    {
        if (hostsText == null) return CheckOutcome.Pass(HostsId, "The hosts file is clean");

        var custom = ParseHosts(hostsText)
            .Where(e => e.HostName is not ("localhost" or "localhost.localdomain" or "ip6-localhost" or "ip6-loopback"))
            .ToList();
        if (custom.Count == 0) return CheckOutcome.Pass(HostsId, "The hosts file is clean");

        var sensitive = custom.Where(e => SensitiveDomains.Any(d => e.HostName.Contains(d))).ToList();
        if (sensitive.Count > 0)
        {
            return new CheckOutcome(HostsId, "", [new SecurityFinding
            {
                CheckId = HostsId,
                Severity = Severity.Critical,
                Title = "The hosts file redirects important websites",
                Explanation = "Windows' hosts file has been edited to send these sites somewhere else. Malware does this to block " +
                              "antivirus and Windows updates or to fake banking and login pages. If you didn't do this yourself, " +
                              "run a full Defender scan, then remove these lines from the hosts file.",
                Evidence = string.Join("\n", sensitive.Take(8).Select(e => $"{e.Address}  {e.HostName}")) +
                           (sensitive.Count > 8 ? $"\n…and {sensitive.Count - 8} more" : ""),
                FilePath = null,
                Actions = [FindingAction.RunQuickScan, FindingAction.OpenWindowsSecurity],
            }]);
        }

        return new CheckOutcome(HostsId, "", [new SecurityFinding
        {
            CheckId = HostsId,
            Severity = Severity.Info,
            Title = $"The hosts file has {custom.Count} custom entr{(custom.Count == 1 ? "y" : "ies")}",
            Explanation = "Ad blockers and developer tools add entries like these. None of them touch security or banking sites.",
            Evidence = string.Join("\n", custom.Take(5).Select(e => $"{e.Address}  {e.HostName}")) + (custom.Count > 5 ? $"\n…and {custom.Count - 5} more" : ""),
        }]);
    }

    public static CheckOutcome EvaluateProxy(bool proxyEnabled, string? proxyServer, string? autoConfigUrl)
    {
        var findings = new List<SecurityFinding>();
        if (proxyEnabled && !string.IsNullOrWhiteSpace(proxyServer) && !IsLoopbackProxy(proxyServer))
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ProxyId,
                Severity = Severity.Warning,
                Title = "Your web traffic goes through a proxy",
                Explanation = "Windows is set to send web traffic through another server. Work and school PCs often do this. " +
                              "If you didn't set it up, malware may be using it to watch or change what you browse. " +
                              "You can turn it off in Settings → Network & internet → Proxy.",
                Evidence = proxyServer,
            });
        }
        if (!string.IsNullOrWhiteSpace(autoConfigUrl))
        {
            findings.Add(new SecurityFinding
            {
                CheckId = ProxyId,
                Severity = Severity.Warning,
                Title = "A proxy setup script is configured",
                Explanation = "Windows downloads a script that decides where your web traffic goes. Common on work PCs; " +
                              "suspicious on a personal one. Check Settings → Network & internet → Proxy.",
                Evidence = autoConfigUrl,
            });
        }
        return new CheckOutcome(ProxyId, "No unexpected proxy is redirecting your web traffic", findings);
    }

    /// <summary>Local proxies (127.0.0.1) are used by legitimate privacy and dev tools; still visible in the evidence.</summary>
    private static bool IsLoopbackProxy(string server) =>
        server.Split(';').All(s => s.Contains("127.0.0.1") || s.Contains("localhost") || s.Contains("[::1]"));
}
