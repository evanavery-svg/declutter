using System.Windows;
using Declutter.App.Mvvm;
using Declutter.Core.Util;
using Declutter.Windows;

namespace Declutter.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private PageViewModel _currentPage;
    private SystemTools.DriveSpace? _drive;

    public MainViewModel(AppServices services)
    {
        Services = services;
        Home = new HomeViewModel(services, this);
        Security = new SecurityViewModel(services);
        Junk = new JunkViewModel(services);
        Startup = new StartupViewModel(services);
        Files = new FilesViewModel(services);
        Apps = new AppsViewModel(services);
        Pages = [Home, Security, Junk, Startup, Files, Apps];
        _currentPage = Home;

        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        OpenLogCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(services.Log.Directory);
            SystemTools.Open(services.Log.Directory);
        });
        services.DiskChanged += (_, _) => RefreshDrive();
        RefreshDrive();
    }

    public AppServices Services { get; }
    public HomeViewModel Home { get; }
    public SecurityViewModel Security { get; }
    public JunkViewModel Junk { get; }
    public StartupViewModel Startup { get; }
    public FilesViewModel Files { get; }
    public AppsViewModel Apps { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public SessionStats Stats => Services.Stats;
    public bool IsElevated => Services.IsElevated;
    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        set
        {
            if (value != null && Set(ref _currentPage, value)) _ = value.NavigatedToAsync();
        }
    }

    public string Version => "Version " + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    public string DriveName => _drive == null ? "Disk" : $"Drive {_drive.Name.TrimEnd('\\')}";
    public double DriveUsedFraction => _drive?.UsedFraction ?? 0;
    public string DriveText => _drive == null ? "" : $"{ByteSize.Format(_drive.Free)} free of {ByteSize.Format(_drive.Total)}";
    public bool DriveIsLow => _drive != null && _drive.UsedFraction > 0.9;

    public void RefreshDrive()
    {
        _drive = SystemTools.GetDriveSpace(Services.Folders.SystemDrive);
        OnPropertiesChanged(nameof(DriveName), nameof(DriveUsedFraction), nameof(DriveText), nameof(DriveIsLow));
        Home.DriveChanged();
    }

    private void RestartAsAdmin()
    {
        if (SystemTools.RestartElevated())
            Application.Current.Shutdown();
    }
}

public sealed class HomeViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private string? _restorePointMessage;
    private Tone _restorePointTone = Tone.Neutral;

    public HomeViewModel(AppServices services, MainViewModel main) : base(services)
    {
        _main = main;
        GoCommand = new RelayCommand(p => { if (p is PageViewModel page) _main.CurrentPage = page; });
        RestorePointCommand = new AsyncCommand(CreateRestorePointAsync, () => !IsBusy);
    }

    public override string Title => "Home";
    public override string Glyph => "";

    public MainViewModel Main => _main;
    public RelayCommand GoCommand { get; }
    public AsyncCommand RestorePointCommand { get; }

    public string Greeting
    {
        get
        {
            int hour = DateTime.Now.Hour;
            string part = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
            return $"{part}, {Environment.UserName}";
        }
    }

    public string DriveHeadline => _main.DriveIsLow
        ? "Your drive is almost full. Start with the Junk Cleaner and the Files tool."
        : "Here's how your PC is doing.";

    public string? RestorePointMessage
    {
        get => _restorePointMessage;
        private set => Set(ref _restorePointMessage, value);
    }

    public Tone RestorePointTone
    {
        get => _restorePointTone;
        private set => Set(ref _restorePointTone, value);
    }

    internal void DriveChanged() => OnPropertyChanged(nameof(DriveHeadline));

    private Task CreateRestorePointAsync() => RunBusyAsync(async ct =>
    {
        RestorePointMessage = "Creating a restore point. This can take a minute…";
        RestorePointTone = Tone.Info;
        var outcome = await SystemTools.CreateRestorePointAsync(Services.Log, ct);
        RestorePointMessage = outcome.Message;
        RestorePointTone = outcome.Created ? Tone.Good : Tone.Caution;
    });
}
