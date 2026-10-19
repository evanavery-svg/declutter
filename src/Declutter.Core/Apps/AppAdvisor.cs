namespace Declutter.Core.Apps;

/// <summary>Flags preinstalled extras and protects things Windows or other apps depend on.</summary>
public static class AppAdvisor
{
    private sealed record Rule(string[] Patterns, AppAdvice Advice, string Reason);

    // Store packages, matched against the package name (e.g. "Microsoft.BingNews"). Prefix match.
    private static readonly Rule[] StoreRules =
    [
        new(["Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
             "Microsoft.AAD.", "Microsoft.AccountsControl", "Microsoft.LockApp",
             "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI", "Microsoft.WindowsAppRuntime", "Microsoft.VCLibs",
             "Microsoft.UI.Xaml", "Microsoft.NET.", "Microsoft.Services.Store", "Microsoft.ApplicationCompatibilityEnhancements",
             "Microsoft.HEIFImageExtension", "Microsoft.HEVCVideoExtension", "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions",
             "Microsoft.WebpImageExtension", "Microsoft.RawImageExtension", "Microsoft.AV1VideoExtension", "Microsoft.MPEG2VideoExtension",
             "Microsoft.AVCEncoderVideoExtension", "Microsoft.DolbyAudioExtensions", "Microsoft.WidgetsPlatformRuntime"],
            AppAdvice.Required, "Part of Windows, the Store, or a media format other apps rely on."),
        new(["MicrosoftWindows.Client.WebExperience"], AppAdvice.Bloatware,
            "Widgets board (weather and news on the taskbar). Remove it if you never open Widgets."),

        new(["Microsoft.BingNews"], AppAdvice.Bloatware, "Microsoft News. Preinstalled news reader."),
        new(["Microsoft.BingWeather"], AppAdvice.Bloatware, "MSN Weather. Preinstalled."),
        new(["Microsoft.BingSearch"], AppAdvice.Bloatware, "Bing Search app. Preinstalled."),
        new(["Microsoft.GetHelp"], AppAdvice.Bloatware, "Get Help. Preinstalled support app."),
        new(["Microsoft.Getstarted"], AppAdvice.Bloatware, "Tips. Preinstalled."),
        new(["Microsoft.MicrosoftSolitaireCollection"], AppAdvice.Bloatware, "Solitaire, with ads. Preinstalled."),
        new(["Microsoft.MicrosoftOfficeHub"], AppAdvice.Bloatware, "\"Microsoft 365 (Office)\" launcher. Not Office itself."),
        new(["Microsoft.People"], AppAdvice.Bloatware, "People. Preinstalled."),
        new(["Microsoft.PowerAutomateDesktop"], AppAdvice.Bloatware, "Power Automate. Preinstalled automation tool."),
        new(["Microsoft.Todos"], AppAdvice.Bloatware, "Microsoft To Do. Preinstalled."),
        new(["Microsoft.WindowsFeedbackHub"], AppAdvice.Bloatware, "Feedback Hub. Preinstalled."),
        new(["Microsoft.WindowsMaps"], AppAdvice.Bloatware, "Maps. Preinstalled, and Microsoft is retiring it."),
        new(["Microsoft.ZuneVideo"], AppAdvice.Bloatware, "Movies & TV. Preinstalled."),
        new(["Microsoft.549981C3F5F10"], AppAdvice.Bloatware, "Cortana. Retired on Windows 11."),
        new(["Microsoft.MixedReality.Portal"], AppAdvice.Bloatware, "Mixed Reality Portal. Only for VR headsets."),
        new(["Microsoft.Microsoft3DViewer", "Microsoft.3DBuilder", "Microsoft.Print3D"], AppAdvice.Bloatware, "3D viewer/builder. Preinstalled."),
        new(["Microsoft.SkypeApp"], AppAdvice.Bloatware, "Skype. Retired by Microsoft."),
        new(["MicrosoftTeams", "Microsoft.Teams"], AppAdvice.Bloatware, "Teams (free/personal). Preinstalled chat app."),
        new(["Microsoft.OutlookForWindows"], AppAdvice.Bloatware, "New Outlook. Remove it if you use another email app."),
        new(["Microsoft.Windows.DevHome"], AppAdvice.Bloatware, "Dev Home. Developer dashboard, preinstalled."),
        new(["Clipchamp.Clipchamp"], AppAdvice.Bloatware, "Clipchamp video editor. Preinstalled."),
        new(["Microsoft.GamingApp"], AppAdvice.Bloatware, "Xbox app. Keep it if you play PC Game Pass games."),
        new(["Microsoft.XboxApp", "Microsoft.XboxGameOverlay", "Microsoft.XboxSpeechToTextOverlay"], AppAdvice.Bloatware, "Old Xbox components."),
        new(["Microsoft.XboxGamingOverlay"], AppAdvice.Bloatware, "Xbox Game Bar (Win+G). Keep it if you record gameplay."),
        new(["Microsoft.YourPhone"], AppAdvice.Bloatware, "Phone Link. Keep it if you connect your phone to your PC."),
        new(["MicrosoftCorporationII.MicrosoftFamily"], AppAdvice.Bloatware, "Microsoft Family Safety. Preinstalled."),
        new(["Microsoft.Wallet", "Microsoft.Messaging", "Microsoft.OneConnect", "Microsoft.Office.OneNote", "Microsoft.Office.Sway", "Microsoft.MSPaint"],
            AppAdvice.Bloatware, "Old preinstalled Microsoft app."),
        new(["SpotifyAB.SpotifyMusic"], AppAdvice.Bloatware, "Spotify, preinstalled by the manufacturer. Keep it if you use it."),
        new(["Disney.", "AmazonVideo.PrimeVideo", "BytedancePte.Ltd.TikTok", "Facebook.", "FACEBOOK.", "Instagram", "king.com.",
             "5A894077.McAfeeSecurity", "4DF9E0F8.Netflix", "Netflix", "AdobeSystemsIncorporated.AdobeExpress", "ROBLOXCORPORATION",
             "22364Disney", "Twitter", "PricelinePartnerNetwork", "AmazonVideo", "Duolingo", "flaregamesGmbH", "Playtika", "G5Entertainment",
             "WildTangentGames", "A278AB0D.", "828B5831.", "26720RandomSaladGamesLLC", "Booking.com", "E046963F.LenovoCompanion",
             "AD2F1837.HP", "DellInc.DellCustomerConnect", "DellInc.PartnerPromo", "Acer", "B9ECED6F.ASUS"],
            AppAdvice.Bloatware, "Preinstalled by the manufacturer or Microsoft as a promotion."),

        new(["Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.Windows.Photos",
             "Microsoft.WindowsTerminal", "Microsoft.WindowsCamera", "Microsoft.WindowsSoundRecorder", "Microsoft.WindowsAlarms",
             "Microsoft.ZuneMusic", "Microsoft.MicrosoftStickyNotes", "Microsoft.PowerToys", "MicrosoftCorporationII.QuickAssist",
             "Microsoft.OneDriveSync", "Microsoft.Edge", "Microsoft.WindowsCommunicationsApps", "MicrosoftCorporationII.WindowsSubsystemForLinux"],
            AppAdvice.Useful, "Built-in Windows tool. Most people keep it."),

        // Anything else that ships as part of Windows itself.
        new(["Microsoft.Windows.", "MicrosoftWindows."], AppAdvice.Required, "Part of Windows."),
    ];

    // Desktop programs, matched against "name publisher" (lower case, substring).
    private static readonly Rule[] DesktopRules =
    [
        new(["microsoft visual c++", "microsoft .net", "microsoft windows desktop runtime", "microsoft asp.net", "windows app runtime",
             "webview2", "microsoft update health tools", "microsoft gameinput", "directx", "vulkan run time", "openal",
             "microsoft xna"],
            AppAdvice.Required, "A runtime other programs need to work. Removing it can break apps."),
        new(["microsoft edge", "microsoft onedrive", "windows pc health check"], AppAdvice.Useful, "Part of a normal Windows 11 setup."),
        new(["java(tm)", "java 8 update", "java se"], AppAdvice.Useful, "Java. Only needed if an app you use requires it."),

        new(["mcafee", "norton", "avast free", "avg antivirus", "totalav", "shield antivirus"], AppAdvice.Bloatware,
            "Often a preinstalled trial antivirus. Windows Security already protects you, so remove it unless you pay for it."),
        new(["expressvpn", "dropbox promotion", "booking.com", "wildtangent", "candy crush", "cyberlink", "wps office",
             "power2go", "powerdvd", "youcam", "acer jumpstart", "acer collection", "asus giftbox", "dell digital delivery",
             "dell customer connect", "hp jumpstart", "hp documentation", "hp wolf security", "hp sure", "lenovo welcome",
             "mirkat", "amazon assistant", "ask toolbar", "conduit", "driver booster", "pc app store", "segurazo",
             "reimage", "pc optimizer", "advanced systemcare", "wise care", "web companion", "opera browser assistant"],
            AppAdvice.Bloatware, "Preinstalled trial, promotion or \"optimizer\" you probably don't need."),
        new(["driver", "realtek", "intel(r) management engine", "intel(r) serial io", "intel(r) chipset", "intel(r) rapid storage",
             "intel® ", "nvidia graphics", "nvidia physx", "nvidia hd audio", "amd chipset", "amd software", "radeon", "synaptics",
             "elan microelectronics", "elan smart", "elantech", "dolby", "waves maxxaudio", "thunderbolt", "bluetooth"],
            AppAdvice.Required, "Driver or hardware support software. Removing it can break sound, graphics, touchpad or Wi-Fi."),
        new(["hp support assistant", "dell supportassist", "lenovo vantage", "myasus", "acer care center", "armoury crate", "dell command"],
            AppAdvice.Useful, "Manufacturer support app. Handy for driver and BIOS updates, but optional."),
    ];

    public static (AppAdvice Advice, string? Reason) Advise(InstalledApp app)
    {
        if (app.Kind == AppKind.Store && app.PackageName is { } package)
        {
            foreach (var rule in StoreRules)
            {
                if (rule.Patterns.Any(p => package.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    return (rule.Advice, rule.Reason);
            }
            return (AppAdvice.Unknown, null);
        }

        string haystack = $"{app.Name} {app.Publisher}".ToLowerInvariant();
        foreach (var rule in DesktopRules)
        {
            if (rule.Patterns.Any(haystack.Contains))
                return (rule.Advice, rule.Reason);
        }
        return (AppAdvice.Unknown, null);
    }
}
