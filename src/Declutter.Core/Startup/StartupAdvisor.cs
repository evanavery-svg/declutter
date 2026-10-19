namespace Declutter.Core.Startup;

/// <summary>Plain-English advice for common startup programs. First matching rule wins.</summary>
public static class StartupAdvisor
{
    private sealed record Rule(string[] Patterns, StartupAdvice Advice, string Reason);

    private static readonly Rule[] Rules =
    [
        // Things that should keep running.
        new(["securityhealth", "windows security", "windows defender", "msmpeng"], StartupAdvice.Keep,
            "Windows Security. Keep this on."),
        new(["avast", "avg ", "avgui", "bitdefender", "kaspersky", " eset", "egui.exe", "malwarebytes", "norton", "mcafee", "sophos", "webroot", "trend micro"],
            StartupAdvice.Keep, "Security software. Keep it on so it can protect you."),
        new(["realtek", "rtkaud", "rtkngui", "rthdvcpl", "maxxaudio", "waves", "dolby", "nahimic", "sonic studio", "dts audio", "conexant", "smartaudio"],
            StartupAdvice.Keep, "Audio driver helper. Sound or volume keys may stop working without it."),
        new(["synaptics", "syntp", "elantech", "etdctrl", "elan smart", "alps", "touchpad", "wacom", "pen service"],
            StartupAdvice.Keep, "Touchpad or pen driver. Gestures may stop working without it."),
        new(["hotkey", "hkcmd", "fn key", "fnhotkey", "hp quick launch", "osd.exe", "lenovo utility", "asus keyboard", "atk package", "dell instant"],
            StartupAdvice.Keep, "Laptop keyboard/function-key support (brightness, volume, airplane mode)."),
        new(["igfx", "intel graphics", "intel(r) graphics", "persistence module", "radeonsoftware", "amd software", "nvidia display", "nvdisplay"],
            StartupAdvice.Keep, "Graphics driver helper."),
        new(["bluetooth", "btsendto"], StartupAdvice.Keep, "Bluetooth support."),

        // Your call.
        new(["onedrive"], StartupAdvice.Optional, "Keeps your files synced to OneDrive. Turn off only if you don't use OneDrive."),
        new(["dropbox", "google drive", "googledrivefs", "googledrivesync", "icloud", "box drive", "pcloud", "megasync"],
            StartupAdvice.Optional, "Cloud file sync. Turn off if you don't need files synced as soon as you sign in."),
        new(["teams", "slack", "zoom", "whatsapp", "telegram", "signal", "discord", "messenger", "skype for business"],
            StartupAdvice.Optional, "Chat app. Turning it off means no messages until you open it yourself."),
        new(["phone link", "yourphone", "phoneexperiencehost"], StartupAdvice.Optional, "Connects your phone to your PC."),
        new(["logitech", "lghub", "logi options", "razer", "synapse", "icue", "corsair", "steelseries", "hyperx", "armoury crate"],
            StartupAdvice.Optional, "Mouse/keyboard/RGB software. Custom buttons or lighting may need it."),
        new(["nordvpn", "expressvpn", "protonvpn", "surfshark", "openvpn", "wireguard", "tunnelbear"],
            StartupAdvice.Optional, "VPN. Keep it on if you want the VPN active as soon as you sign in."),
        new(["bitwarden", "1password", "lastpass", "dashlane", "keepass"], StartupAdvice.Optional, "Password manager."),
        new(["powertoys", "f.lux", "flux", "sharex", "greenshot", "lightshot", "everything", "autohotkey", "ditto"],
            StartupAdvice.Optional, "Utility you probably installed on purpose. Keep it if you use it all the time."),
        new(["nvidia app", "geforce experience", "nvbackend", "nvcontainer", "shadowplay"], StartupAdvice.Optional,
            "NVIDIA app / overlay. Only needed for game recording, overlays and driver notifications."),
        new(["docker"], StartupAdvice.Optional, "Docker Desktop. Only needed if you run containers."),

        // Rarely needed at startup.
        new(["microsoftedgeautolaunch", "msedge.exe"], StartupAdvice.SafeToDisable,
            "Pre-loads Edge in the background. Edge still opens normally without it."),
        new(["spotify"], StartupAdvice.SafeToDisable, "Opens Spotify at sign-in. It works fine when you open it yourself."),
        new(["steam", "epicgameslauncher", "epic games", "battle.net", "origin.exe", "eadesktop", "ea app", "ubisoft", "upc.exe", "gog galaxy", "rockstar", "riot client"],
            StartupAdvice.SafeToDisable, "Game launcher. Games still work, and the launcher opens when you need it."),
        new(["skype"], StartupAdvice.SafeToDisable, "Opens Skype at sign-in."),
        new(["cortana"], StartupAdvice.SafeToDisable, "Cortana is retired on Windows 11."),
        new(["adobearm", "acrobat update", "adobe updater", "adobegcinvoker", "adobe genuine", "acrotray", "adobe collaboration"],
            StartupAdvice.SafeToDisable, "Adobe updater/helper. Adobe apps check for updates when you open them."),
        new(["creative cloud", "adobe desktop service", "ccxprocess"], StartupAdvice.SafeToDisable,
            "Adobe Creative Cloud. Opens when you launch an Adobe app."),
        new(["ituneshelper", "quicktime", "jusched", "java update", "sunjavaupdatesched"], StartupAdvice.SafeToDisable,
            "Old helper/updater that doesn't need to run all the time."),
        new(["utorrent", "bittorrent", "qbittorrent", "vuze", "frostwire"], StartupAdvice.SafeToDisable, "Torrent app."),
        new(["browser assistant", "opera gx", "opera stable", "brave", "vivaldi", "chrome.exe", "firefox"], StartupAdvice.SafeToDisable,
            "Browser helper or auto-start. The browser opens normally without it."),
        new(["ccleaner", "wondershare", "cyberlink", "youcam", "remindme", "grammarly", "booking", "mcafee webadvisor", "wildtangent"],
            StartupAdvice.SafeToDisable, "Doesn't need to run all the time."),
        new(["update", "updater", "upgrade"], StartupAdvice.SafeToDisable,
            "Update checker. The app can check for updates when you open it."),
    ];

    public static (StartupAdvice Advice, string Reason) Advise(string name, string? command, string? publisher, bool fileMissing)
    {
        if (fileMissing)
            return (StartupAdvice.SafeToDisable, "The program this starts no longer exists. It's a leftover from an uninstalled app.");

        string exe = command is null ? "" : Path.GetFileName(Util.CommandLine.ExtractExecutable(command));
        string haystack = $" {name} {exe} {publisher} ".ToLowerInvariant();

        foreach (var rule in Rules)
        {
            if (rule.Patterns.Any(haystack.Contains))
                return (rule.Advice, rule.Reason);
        }

        return (StartupAdvice.Unknown, "Not on our list. If you don't recognise it, search the name online before turning it off.");
    }
}
