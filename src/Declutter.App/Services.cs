using Declutter.Core.History;
using Declutter.App.Mvvm;
using Declutter.Core.Settings;
using Declutter.Core.Safety;
using Declutter.Core.Util;
using Declutter.Windows;
using Declutter.Windows.Security;

namespace Declutter.App;

/// <summary>Everything the pages share, created once at startup.</summary>
public sealed class AppServices
{
    public AppServices(IDialogs dialogs, Func<IntPtr> ownerWindow, SettingsStore settingsStore)
    {
        SettingsStore = settingsStore;
        Settings = settingsStore.Load();
        Folders = WindowsEnvironment.DetectKnownFolders();
        Guard = new SafetyGuard(Folders);
        IsElevated = WindowsEnvironment.IsElevated;
        Log = new FileActivityLog(Path.Combine(Folders.LocalAppData, "Declutter", "Logs"));
        Dialogs = dialogs;
        OwnerWindow = ownerWindow;
        Startup = new StartupManager(Log, IsElevated);
        Apps = new InstalledApps(Log, IsElevated);
        Recycler = new ShellRecycler(ownerWindow);
        Security = new SecurityCollector(Startup, Folders, IsElevated);
        Defender = new DefenderClient(Log);
        Reports = new ReportService(this);
        History = new HistoryStore(Path.Combine(DataFolder, "history.json"));
    }

    /// <summary>Local daily history for the Home trends; written only while Settings → Keep history is on.</summary>
    public HistoryStore History { get; }

    /// <summary>Raised after something new is recorded, so Home can redraw its trends.</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>Adds today's readings to the history (merged with anything else recorded today).</summary>
    public void RecordHistory(HistorySnapshot reading)
    {
        if (!Settings.KeepHistory) return;
        try
        {
            History.Record(reading with { Date = DateOnly.FromDateTime(DateTime.Now) });
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("History", "Couldn't save history: " + ex.Message);
        }
    }

    public SettingsStore SettingsStore { get; }

    /// <summary>Current settings; change with <see cref="UpdateSettings"/> so they're saved.</summary>
    public AppSettings Settings { get; private set; }

    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        Settings = change(Settings);
        try
        {
            SettingsStore.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Settings", "Couldn't save settings: " + ex.Message);
        }
    }

    /// <summary>Where Declutter keeps its own files (settings, logs, history).</summary>
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Declutter");

    public KnownFolders Folders { get; }
    public SafetyGuard Guard { get; }
    public bool IsElevated { get; }
    public FileActivityLog Log { get; }
    public IDialogs Dialogs { get; }
    public Func<IntPtr> OwnerWindow { get; }
    public StartupManager Startup { get; }
    public InstalledApps Apps { get; }
    public ShellRecycler Recycler { get; }
    public SecurityCollector Security { get; }
    public DefenderClient Defender { get; }
    public ReportService Reports { get; }
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
