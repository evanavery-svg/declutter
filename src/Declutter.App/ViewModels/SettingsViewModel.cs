using Declutter.App.Mvvm;
using Declutter.Windows;

namespace Declutter.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    public SettingsViewModel(AppServices services, MainViewModel main) : base(services)
    {
        _main = main;
        var current = Enum.TryParse<ThemeMode>(services.Settings.Theme, out var t) ? t : ThemeMode.System;
        Theme = new ChoiceSet<ThemeMode>(current, (ThemeMode.System, "Match Windows"), (ThemeMode.Light, "Light"), (ThemeMode.Dark, "Dark"));
        Theme.Changed += (_, _) =>
        {
            WindowTheme.SetMode(Theme.Selected);
            Services.UpdateSettings(s => s with { Theme = Theme.Selected.ToString() });
        };
        OpenDataFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(AppServices.DataFolder);
            SystemTools.Open(AppServices.DataFolder);
        });
        OpenProjectCommand = new RelayCommand(() => SystemTools.Open("https://github.com/" + Core.Updates.Releases.Owner + "/" + Core.Updates.Releases.Repository + "#readme"));
    }

    public override string Title => "Settings";
    public override string Glyph => "";

    public ChoiceSet<ThemeMode> Theme { get; }
    public UpdateViewModel Updates => _main.Updates;
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public string Version => _main.Version;
    public bool IsHighContrast => WindowTheme.IsHighContrast;

    public bool CheckForUpdates
    {
        get => Services.Settings.CheckForUpdates;
        set
        {
            Services.UpdateSettings(s => s with { CheckForUpdates = value });
            OnPropertyChanged();
        }
    }

    public bool KeepHistory
    {
        get => Services.Settings.KeepHistory;
        set
        {
            Services.UpdateSettings(s => s with { KeepHistory = value });
            OnPropertyChanged();
        }
    }
}
