namespace Declutter.Core.Startup;

public enum StartupSource
{
    RegistryUser,
    RegistryMachine,
    RegistryMachine32,
    StartupFolderUser,
    StartupFolderAllUsers,
    ScheduledTask,
    StoreApp,
}

public enum StartupAdvice
{
    Unknown,

    /// <summary>Drivers, security software, input devices: leave these on.</summary>
    Keep,

    /// <summary>Useful for some people (cloud sync, chat): your call.</summary>
    Optional,

    /// <summary>Launchers, updaters and helpers that don't need to start with Windows.</summary>
    SafeToDisable,
}

public sealed class StartupItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required StartupSource Source { get; init; }
    public required bool Enabled { get; set; }

    /// <summary>The registry value name, shortcut file name, task path or package task key used to toggle it.</summary>
    public required string Key { get; init; }

    public string? FilePath { get; init; }
    public bool FileMissing { get; init; }
    public string? Publisher { get; init; }
    public bool RequiresAdmin { get; init; }
    public StartupAdvice Advice { get; init; }
    public string? AdviceReason { get; init; }

    public string SourceDescription => Source switch
    {
        StartupSource.RegistryUser => "Registry (your account)",
        StartupSource.RegistryMachine => "Registry (all users)",
        StartupSource.RegistryMachine32 => "Registry (all users, 32-bit)",
        StartupSource.StartupFolderUser => "Startup folder (your account)",
        StartupSource.StartupFolderAllUsers => "Startup folder (all users)",
        StartupSource.ScheduledTask => "Scheduled task at sign-in",
        StartupSource.StoreApp => "Store app",
        _ => Source.ToString(),
    };
}
