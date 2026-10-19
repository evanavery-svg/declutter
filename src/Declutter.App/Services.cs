using Declutter.App.Mvvm;
using Declutter.Core.Safety;
using Declutter.Core.Util;
using Declutter.Windows;

namespace Declutter.App;

/// <summary>Everything the pages share, created once at startup.</summary>
public sealed class AppServices
{
    public AppServices(IDialogs dialogs, Func<IntPtr> ownerWindow)
    {
        Folders = WindowsEnvironment.DetectKnownFolders();
        Guard = new SafetyGuard(Folders);
        IsElevated = WindowsEnvironment.IsElevated;
        Log = new FileActivityLog(Path.Combine(Folders.LocalAppData, "Declutter", "Logs"));
        Dialogs = dialogs;
        OwnerWindow = ownerWindow;
        Startup = new StartupManager(Log, IsElevated);
        Apps = new InstalledApps(Log, IsElevated);
        Recycler = new ShellRecycler(ownerWindow);
    }

    public KnownFolders Folders { get; }
    public SafetyGuard Guard { get; }
    public bool IsElevated { get; }
    public FileActivityLog Log { get; }
    public IDialogs Dialogs { get; }
    public Func<IntPtr> OwnerWindow { get; }
    public StartupManager Startup { get; }
    public InstalledApps Apps { get; }
    public ShellRecycler Recycler { get; }
    public SessionStats Stats { get; } = new();

    /// <summary>Raised after anything that changes free disk space.</summary>
    public event EventHandler? DiskChanged;

    public void NotifyDiskChanged() => DiskChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class SessionStats : ObservableObject
{
    private long _bytesFreed;

    public long BytesFreed
    {
        get => _bytesFreed;
        private set
        {
            if (Set(ref _bytesFreed, value)) OnPropertiesChanged(nameof(Text), nameof(HasFreed));
        }
    }

    public bool HasFreed => BytesFreed > 0;
    public string Text => $"Freed this session: {ByteSize.Format(BytesFreed)}";

    public void Add(long bytes) => BytesFreed += Math.Max(0, bytes);
}

public interface IDialogs
{
    /// <summary>Asks before doing something; returns true only if the user chose <paramref name="confirmText"/>.</summary>
    bool Confirm(string title, string message, string confirmText, bool danger = false);

    void Show(string title, string message);
}
