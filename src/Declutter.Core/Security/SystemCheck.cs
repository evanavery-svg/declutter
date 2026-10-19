namespace Declutter.Core.Security;

public sealed record WmiConsumer(string Name, string Kind, string? Command);

/// <summary>Windows' built-in safety switches, and a hiding place advanced malware likes.</summary>
public static class SystemCheck
{
    public const string FirewallId = "firewall";
    public const string UacId = "uac";
    public const string WmiId = "wmi";

    /// <param name="profiles">Firewall profile name → enabled (null = not configured, which means on).</param>
    public static CheckOutcome EvaluateFirewall(IReadOnlyDictionary<string, bool?> profiles)
    {
        var off = profiles.Where(p => p.Value == false).Select(p => p.Key).ToList();
        if (off.Count == 0) return CheckOutcome.Pass(FirewallId, "Windows Firewall is on");
        return new CheckOutcome(FirewallId, "", [new SecurityFinding
        {
            CheckId = FirewallId,
            Severity = Severity.Warning,
            Title = $"Windows Firewall is off ({string.Join(", ", off)})",
            Explanation = "The firewall blocks unwanted connections from the network. Unless another security app manages it, " +
                          "turn it back on in Windows Security → Firewall & network protection.",
            Actions = [FindingAction.OpenWindowsSecurity],
        }]);
    }

    public static CheckOutcome EvaluateUac(bool? enabled)
    {
        if (enabled != false) return CheckOutcome.Pass(UacId, "User Account Control is on");
        return new CheckOutcome(UacId, "", [new SecurityFinding
        {
            CheckId = UacId,
            Severity = Severity.Warning,
            Title = "User Account Control is switched off",
            Explanation = "Programs can make system changes without asking you. Turn it back on: search the Start menu for " +
                          "\"Change User Account Control settings\" and move the slider up (a restart is needed).",
        }]);
    }

    public static CheckOutcome EvaluateWmi(IReadOnlyList<WmiConsumer>? consumers)
    {
        if (consumers == null) return CheckOutcome.Pass(WmiId, "No hidden WMI startup commands");
        var findings = consumers.Where(c => !IsKnownBenign(c)).Select(c => new SecurityFinding
        {
            CheckId = WmiId,
            Severity = PersistenceCheck.DescribeScriptTrick(c.Command) != null || c.Kind == "ActiveScriptEventConsumer" ? Severity.Critical : Severity.Warning,
            Title = $"Hidden command runs through WMI: {c.Name}",
            Explanation = "WMI event subscriptions can run commands automatically without appearing in Startup or Task Manager. " +
                          "Very few normal programs use them, and fileless malware loves them. Search the name online; if it isn't " +
                          "from software you know, run a full Defender scan.",
            Evidence = c.Command,
            Actions = [FindingAction.SearchOnline, FindingAction.RunQuickScan],
        }).ToList();
        return new CheckOutcome(WmiId, "No hidden WMI startup commands", findings);
    }

    /// <summary>Microsoft's own test consumer ships with some Windows versions.</summary>
    private static bool IsKnownBenign(WmiConsumer c) =>
        c.Name == "BVTConsumer" && c.Command?.Contains("KernCap.vbs", StringComparison.OrdinalIgnoreCase) == true;
}
