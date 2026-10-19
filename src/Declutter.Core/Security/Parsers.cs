using System.Globalization;

namespace Declutter.Core.Security;

public static class Parsers
{
    /// <summary>
    /// Parses "netstat -ano" output. Only TCP lines are used; the column order (proto, local, remote,
    /// state, PID) is the same in every Windows language, only the state word is translated.
    /// </summary>
    public static IReadOnlyList<TcpConnection> ParseNetstat(string output)
    {
        var connections = new List<TcpConnection>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) continue;
            if (!TrySplitEndpoint(parts[1], out var localAddr, out int localPort)) continue;
            if (!TrySplitEndpoint(parts[2], out var remoteAddr, out int remotePort)) continue;
            connections.Add(new TcpConnection(localAddr, localPort, remoteAddr, remotePort, parts[3], pid));
        }
        return connections;
    }

    /// <summary>"192.168.1.5:443" or "[2001:db8::1]:443".</summary>
    internal static bool TrySplitEndpoint(string endpoint, out string address, out int port)
    {
        address = "";
        port = 0;
        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0) return false;
        address = endpoint[..colon];
        if (address.StartsWith('[') && address.EndsWith(']')) address = address[1..^1];
        return int.TryParse(endpoint[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
    }

    /// <summary>"CN=Contoso Ltd, O=Contoso Ltd, L=Redmond" → "Contoso Ltd".</summary>
    public static string? SignerName(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        foreach (var part in SplitDistinguishedName(subject))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return part[3..].Trim().Trim('"');
        }
        return subject;
    }

    /// <summary>Splits "CN=\"Bar, Inc\", O=Foo" on commas that aren't inside quotes.</summary>
    private static IEnumerable<string> SplitDistinguishedName(string dn)
    {
        int start = 0;
        bool quoted = false;
        for (int i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '"') quoted = !quoted;
            else if (dn[i] == ',' && !quoted)
            {
                yield return dn[start..i].Trim();
                start = i + 1;
            }
        }
        yield return dn[start..].Trim();
    }

    /// <summary>A process's share of the whole machine's CPU over a sampling window.</summary>
    public static double CpuPercent(TimeSpan processorTimeUsed, TimeSpan elapsed, int processorCount) =>
        elapsed <= TimeSpan.Zero || processorCount <= 0 ? 0
            : Math.Clamp(processorTimeUsed.TotalMilliseconds / (elapsed.TotalMilliseconds * processorCount) * 100, 0, 100);
}
