namespace Declutter.Core.Apps;

public enum AppKind
{
    Desktop,
    Store,
}

public enum AppAdvice
{
    Unknown,

    /// <summary>Windows or other apps depend on it (runtimes, drivers, core tools).</summary>
    Required,

    /// <summary>Normal, useful app. Remove only if you're sure.</summary>
    Useful,

    /// <summary>Preinstalled extra most people never use.</summary>
    Bloatware,
}

public sealed class InstalledApp
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AppKind Kind { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateTime? InstallDate { get; init; }
    public long? SizeBytes { get; set; }
    public string? InstallLocation { get; init; }

    /// <summary>Desktop apps: the command Windows runs for "Uninstall".</summary>
    public string? UninstallCommand { get; init; }

    /// <summary>Desktop apps: true if this is a Windows Installer (MSI) product.</summary>
    public bool IsMsi { get; init; }

    /// <summary>Store apps: the package full name used to remove it.</summary>
    public string? PackageFullName { get; init; }

    /// <summary>Store apps: the package name, e.g. "Microsoft.BingNews".</summary>
    public string? PackageName { get; init; }

    public bool RequiresAdmin { get; init; }
    public AppAdvice Advice { get; set; }
    public string? AdviceReason { get; set; }
}
