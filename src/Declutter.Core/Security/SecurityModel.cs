namespace Declutter.Core.Security;

public enum Severity
{
    /// <summary>For your information (e.g. "Defender removed 2 threats last week").</summary>
    Info,

    /// <summary>Worth a look: could be legitimate, could be trouble.</summary>
    Warning,

    /// <summary>A strong sign of malware or of protection being switched off.</summary>
    Critical,
}

public enum FindingAction
{
    ShowFile,
    SearchOnline,
    ScanWithDefender,
    DisableStartup,
    EndProcess,
    RunQuickScan,
    UpdateDefinitions,
    OpenWindowsSecurity,
}

public sealed record SecurityFinding
{
    public required string CheckId { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; init; }

    /// <summary>The raw detail behind the finding: a path, command line or setting value.</summary>
    public string? Evidence { get; init; }

    public string? FilePath { get; init; }
    public int? ProcessId { get; init; }
    public string? StartupItemId { get; init; }
    public IReadOnlyList<FindingAction> Actions { get; init; } = [];
}

/// <summary>The result of one check: either findings, or the reassuring line shown when it passed.</summary>
public sealed record CheckOutcome(string Id, string PassedText, IReadOnlyList<SecurityFinding> Findings)
{
    public bool Passed => Findings.All(f => f.Severity == Severity.Info);

    public static CheckOutcome Pass(string id, string passedText) => new(id, passedText, []);
}

/// <summary>A running process, as seen during the check.</summary>
public sealed record ProcessInfo(int Pid, string Name, string? Path, string? CommandLine, double CpuPercent);

/// <summary>Authenticode signature of a file. <see cref="Status"/> is PowerShell's SignatureStatus name.</summary>
public sealed record SignatureInfo(string Status, string? Signer)
{
    public bool IsSigned => !Status.Equals("NotSigned", StringComparison.OrdinalIgnoreCase);
    public bool IsValid => Status.Equals("Valid", StringComparison.OrdinalIgnoreCase);
}

public sealed record TcpConnection(string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State, int Pid);
