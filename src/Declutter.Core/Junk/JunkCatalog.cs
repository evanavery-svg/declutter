using Declutter.Core.Safety;

namespace Declutter.Core.Junk;

/// <summary>
/// Every folder the Junk Cleaner knows about. Each one holds data that Windows or the owning app
/// recreates on demand; nothing here is a user's own document or a setting.
/// </summary>
public static class JunkCatalog
{
    private static readonly TimeSpan TwoDays = TimeSpan.FromDays(2);

    public static IReadOnlyList<FileJunkSource> Create(KnownFolders f)
    {
        string local = f.LocalAppData;
        string roaming = f.RoamingAppData;
        string win = f.WindowsDir;
        var sources = new List<FileJunkSource>();

        if (f.SafeTempDir is { } temp)
        {
            sources.Add(new FileJunkSource("temp-user", "Temporary files",
                "Leftovers from installers and apps in your temp folder. Files used in the last 2 days are kept.",
                [new JunkRule(temp) { MinAge = TwoDays }]));
        }

        sources.Add(new FileJunkSource("temp-windows", "Windows temporary files",
            "Temp files created by Windows and system services. Files used in the last 2 days are kept.",
            [new JunkRule(Path.Combine(win, "Temp")) { MinAge = TwoDays, RequiresAdmin = true }]));

        sources.Add(new FileJunkSource("windows-update", "Windows Update downloads",
            "Update packages that have already been installed. Windows downloads again anything it still needs.",
            [new JunkRule(Path.Combine(win, "SoftwareDistribution", "Download")) { MinAge = TimeSpan.FromDays(10), RequiresAdmin = true }]));

        sources.Add(new FileJunkSource("delivery-optimization", "Delivery Optimization cache",
            "Copies of updates kept to share with other PCs. Safe to remove.",
            [
                new JunkRule(Path.Combine(win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")) { RequiresAdmin = true },
                new JunkRule(Path.Combine(win, "SoftwareDistribution", "DeliveryOptimization")) { RequiresAdmin = true },
            ]));

        sources.Add(new FileJunkSource("crash-dumps", "Crash dumps & error reports",
            "Memory dumps and reports saved when an app or Windows crashed. Only useful for debugging.",
            [
                new JunkRule(Path.Combine(local, "CrashDumps")),
                new JunkRule(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive")),
                new JunkRule(Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue")),
                new JunkRule(Path.Combine(f.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive")) { RequiresAdmin = true },
                new JunkRule(Path.Combine(f.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue")) { RequiresAdmin = true },
                new JunkRule(Path.Combine(win, "Minidump")) { RequiresAdmin = true },
                new JunkRule(Path.Combine(win, "LiveKernelReports")) { SearchPattern = "*.dmp", RequiresAdmin = true },
                new JunkRule(win) { SearchPattern = "MEMORY.DMP", Recursive = false, RemoveEmptyFolders = false, RequiresAdmin = true },
            ]));

        sources.Add(new FileJunkSource("thumbnails", "Thumbnail & icon cache",
            "Small preview pictures Explorer saves. They are rebuilt as you browse, so folders may show previews slowly at first.",
            [
                new JunkRule(Path.Combine(local, "Microsoft", "Windows", "Explorer")) { SearchPattern = "thumbcache_*.db", Recursive = false, RemoveEmptyFolders = false },
                new JunkRule(Path.Combine(local, "Microsoft", "Windows", "Explorer")) { SearchPattern = "iconcache_*.db", Recursive = false, RemoveEmptyFolders = false },
            ])
        {
            SelectedByDefault = false,
            Warning = "Files Explorer is using are skipped.",
        });

        sources.Add(Chromium("browser-edge", "Microsoft Edge cache", Path.Combine(local, "Microsoft", "Edge", "User Data"),
            new BlockingProcess("msedge", "Microsoft Edge")));
        sources.Add(Chromium("browser-chrome", "Google Chrome cache", Path.Combine(local, "Google", "Chrome", "User Data"),
            new BlockingProcess("chrome", "Google Chrome")));
        sources.Add(Chromium("browser-brave", "Brave cache", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
            new BlockingProcess("brave", "Brave")));
        sources.Add(Chromium("browser-vivaldi", "Vivaldi cache", Path.Combine(local, "Vivaldi", "User Data"),
            new BlockingProcess("vivaldi", "Vivaldi")));

        sources.Add(new FileJunkSource("browser-opera", "Opera cache",
            "Saved copies of web pages and images. Your history, passwords and logins are not touched.",
            [
                new JunkRule(Path.Combine(local, "Opera Software", "Opera Stable", "Cache")),
                new JunkRule(Path.Combine(local, "Opera Software", "Opera GX Stable", "Cache")),
            ])
        { BlockingProcesses = [new BlockingProcess("opera", "Opera")] });

        sources.Add(new FileJunkSource("browser-firefox", "Firefox cache",
            "Saved copies of web pages and images. Your history, passwords and logins are not touched.",
            [
                new JunkRule(Path.Combine(local, "Mozilla", "Firefox", "Profiles", "*", "cache2")),
                new JunkRule(Path.Combine(local, "Mozilla", "Firefox", "Profiles", "*", "startupCache")),
            ])
        { BlockingProcesses = [new BlockingProcess("firefox", "Firefox")] });

        sources.Add(new FileJunkSource("app-caches", "App caches (Discord, Teams, VS Code, Steam)",
            "Web caches kept by chat and developer apps. Your messages, settings and games are not touched.",
            [
                .. ChromiumAppCache(Path.Combine(roaming, "discord")),
                .. ChromiumAppCache(Path.Combine(roaming, "Microsoft", "Teams")),
                .. ChromiumAppCache(Path.Combine(roaming, "Code")),
                new JunkRule(Path.Combine(roaming, "Code", "CachedData")),
                new JunkRule(Path.Combine(roaming, "Code", "CachedExtensionVSIXs")),
                new JunkRule(Path.Combine(local, "Steam", "htmlcache")),
            ])
        {
            BlockingProcesses =
            [
                new BlockingProcess("Discord", "Discord"),
                new BlockingProcess("Teams", "Microsoft Teams (classic)"),
                new BlockingProcess("Code", "Visual Studio Code"),
                new BlockingProcess("steamwebhelper", "Steam"),
            ],
        });

        sources.Add(new FileJunkSource("shader-cache", "Graphics shader cache",
            "Pre-compiled graphics data for games and apps. It's rebuilt automatically, so games may stutter briefly the first time you play them.",
            [
                new JunkRule(Path.Combine(local, "D3DSCache")),
                new JunkRule(Path.Combine(local, "NVIDIA", "DXCache")),
                new JunkRule(Path.Combine(local, "NVIDIA", "GLCache")),
                new JunkRule(Path.Combine(local, "AMD", "DxCache")),
                new JunkRule(Path.Combine(local, "AMD", "DxcCache")),
                new JunkRule(Path.Combine(local, "AMD", "GLCache")),
                new JunkRule(Path.Combine(local, "AMD", "VkCache")),
                new JunkRule(Path.Combine(f.AppDataRoot, "LocalLow", "Intel", "ShaderCache")),
            ])
        { SelectedByDefault = false });

        return sources;
    }

    private static FileJunkSource Chromium(string id, string name, string userData, BlockingProcess process) =>
        new(id, name, "Saved copies of web pages and images. Your history, passwords, bookmarks and logins are not touched.",
            [
                new JunkRule(Path.Combine(userData, "*", "Cache")),
                new JunkRule(Path.Combine(userData, "*", "Code Cache")),
                new JunkRule(Path.Combine(userData, "*", "GPUCache")),
                new JunkRule(Path.Combine(userData, "*", "DawnCache")),
                new JunkRule(Path.Combine(userData, "*", "DawnGraphiteCache")),
                new JunkRule(Path.Combine(userData, "*", "DawnWebGPUCache")),
                new JunkRule(Path.Combine(userData, "ShaderCache")),
                new JunkRule(Path.Combine(userData, "GrShaderCache")),
                new JunkRule(Path.Combine(userData, "GraphiteDawnCache")),
            ])
        { BlockingProcesses = [process] };

    private static IEnumerable<JunkRule> ChromiumAppCache(string appData) =>
    [
        new JunkRule(Path.Combine(appData, "Cache")),
        new JunkRule(Path.Combine(appData, "Code Cache")),
        new JunkRule(Path.Combine(appData, "GPUCache")),
    ];
}
