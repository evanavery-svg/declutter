namespace Declutter.Core.Speed;

public enum ServiceAdvice
{
    Unknown,

    /// <summary>Drivers, security software, licensing: leave alone.</summary>
    Keep,

    /// <summary>Useful for some people (phone sync, remote access).</summary>
    Optional,

    /// <summary>Updaters, telemetry and helpers that work fine starting only when needed.</summary>
    SafeToSetManual,
}

/// <summary>A Windows service installed by an app (Windows' own services are never listed).</summary>
public sealed record ServiceInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>"Auto", "Manual", "Disabled".</summary>
    public required string StartMode { get; init; }

    public bool DelayedAutoStart { get; init; }
    public bool Running { get; init; }
    public string? Path { get; init; }
    public string? Company { get; init; }
    public string? Description { get; init; }
    public bool IsAutomatic => StartMode.Equals("Auto", StringComparison.OrdinalIgnoreCase);
}

public static class ServiceAdvisor
{
    private sealed record Rule(string[] Patterns, ServiceAdvice Advice, string Reason);

    // Matched against "name display-name company", lower case. First match wins.
    private static readonly Rule[] Rules =
    [
        new(["defender", "antivirus", "avast", "avg ", "bitdefender", "kaspersky", "eset", "malwarebytes", "norton", "mcafee", "sophos",
             "webroot", "trend micro", "crowdstrike", "sentinel"],
            ServiceAdvice.Keep, "Security software. It needs to run all the time to protect you."),
        new(["realtek", "intel(r)", "intel ", "nvidia display", "nvdisplay", "amd external events", "amd crash", "synaptics", "elan smart",
             "elantech", "dolby", "waves", "nahimic", "thunderbolt", "bluetooth", "wacom", "fingerprint", "goodix", "smart card"],
            ServiceAdvice.Keep, "Hardware or driver support. Devices may stop working properly without it."),
        new(["clicktorunsvc", "office click-to-run", "licens", "flexnet", "sentinel", "codemeter", "vmware", "virtualbox", "hyper-v", "docker"],
            ServiceAdvice.Keep, "Needed by an app you use (licensing, virtual machines or Office)."),

        new(["teamviewer", "anydesk", "splashtop", "logmein", "rustdesk", "remote utilities", "screenconnect"],
            ServiceAdvice.Optional, "Remote-access software: it lets someone connect to this PC. Keep it only if you use it; if you don't recognise it, look into it."),
        new(["apple mobile device", "itunes", "bonjour"], ServiceAdvice.Optional, "Needed to connect an iPhone or iPad over USB. Otherwise it can be set to Manual."),
        new(["onedrive", "dropbox service", "google drive", "icloud"], ServiceAdvice.Optional, "Cloud sync. Keep it if you rely on files syncing in the background."),

        new(["gupdate", "googleupdater", "google update", "edgeupdate", "microsoft edge update", "brave update", "bravesoftware update",
             "adobearmservice", "adobe acrobat update", "adobeupdateservice", "dropbox update", "dbupdate", "mozillamaintenance",
             "lghub updater", "logitech g hub updater", "asusupdatecheck", "ccleaner", "avast update", "wondershare", "razer game manager",
             "updater", "update service", "updateservice", "autoupdate"],
            ServiceAdvice.SafeToSetManual, "An updater. Set to Manual, it still runs when the app needs it, just not all day."),
        new(["telemetry", "analytics", "nvtelemetry", "hptouchpointanalytics", "hp analytics", "hpappHelpercap", "hpdiagscap", "hpsysinfocap",
             "customer experience", "diagnostic", "agmservice", "agsservice", "adobe genuine"],
            ServiceAdvice.SafeToSetManual, "Usage reporting or a background helper. It isn't needed for the app to work."),
        new(["steam client service", "epic online services", "ea background", "eabackgroundservice", "origin client", "origin web helper", "ubisoft", "battle.net", "rockstar"],
            ServiceAdvice.SafeToSetManual, "Game launcher helper. It starts when you open the launcher."),
    ];

    public static (ServiceAdvice Advice, string Reason) Advise(ServiceInfo s)
    {
        string hay = $"{s.Name} {s.DisplayName} {s.Company}".ToLowerInvariant();
        foreach (var rule in Rules)
        {
            if (rule.Patterns.Any(p => hay.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return (rule.Advice, rule.Reason);
        }
        return (ServiceAdvice.Unknown, "Not on our list. Leave it as it is unless you know what it does.");
    }
}
