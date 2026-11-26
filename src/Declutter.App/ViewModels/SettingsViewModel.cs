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
        ClearHistoryCommand = new RelayCommand(ClearHistory);
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
    public RelayCommand ClearHistoryCommand { get; }

    private void ClearHistory()
    {
        if (!Services.Dialogs.Confirm("Clear history?", "Removes the remembered disk space, start-up times, battery health and scores. The trends on Home start again from today.", "Clear", danger: true))
            return;
        try
        {
            Services.History.Clear();
            Services.Log.Write("Settings", "Cleared history");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Services.Dialogs.Show("Couldn't clear history", ex.Message);
        }
        _main.Home.LoadTrends();
    }
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
            _main.Home.LoadTrends();
        }
    }
}
