using System.Buffers.Binary;

namespace Declutter.Core.Startup;

/// <summary>
/// The enable/disable flag Task Manager and Settings use for Run-key and Startup-folder items, stored in
/// HKCU|HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run,Run32,StartupFolder}.
/// Each value is 12 bytes: a little-endian DWORD flag (even = enabled, odd = disabled) followed by the
/// FILETIME it was disabled. Using the same mechanism means changes show up in Task Manager too, and the
/// program's own startup entry is never deleted — turning it back on restores it exactly.
/// </summary>
public static class StartupApproved
{
    public const string UserKeyBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>A missing or malformed value means the item runs (Windows' own behaviour).</summary>
    public static bool IsEnabled(byte[]? value) => value is not { Length: >= 1 } || (value[0] & 1) == 0;

    public static byte[] Encode(bool enabled, DateTime utcNow)
    {
        var data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
        }
        else
        {
            data[0] = 0x03;
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), utcNow.ToFileTimeUtc());
        }
        return data;
    }

    public static string SubKeyFor(StartupSource source) => source switch
    {
        StartupSource.RegistryUser or StartupSource.RegistryMachine => "Run",
        StartupSource.RegistryMachine32 => "Run32",
        StartupSource.StartupFolderUser or StartupSource.StartupFolderAllUsers => "StartupFolder",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Not controlled by StartupApproved."),
    };
}

/// <summary>Store-app startup tasks keep a StartupTaskState DWORD named "State".</summary>
public static class StoreStartupState
{
    public const int Disabled = 0;
    public const int DisabledByUser = 1;
    public const int Enabled = 2;
    public const int DisabledByPolicy = 3;
    public const int EnabledByPolicy = 4;

    public static bool IsEnabled(int state) => state is Enabled or EnabledByPolicy;
    public static bool CanToggle(int state) => state is Disabled or DisabledByUser or Enabled;
    public static int For(bool enabled) => enabled ? Enabled : DisabledByUser;
}
