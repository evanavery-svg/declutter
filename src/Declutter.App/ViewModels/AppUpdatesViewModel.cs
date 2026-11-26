using System.Collections.ObjectModel;
using Declutter.App.Mvvm;
using Declutter.Core.Protection;
using Declutter.Windows;
using Declutter.Windows.Protection;

namespace Declutter.App.ViewModels;

public sealed class AppUpdatesViewModel : PageViewModel
{
    private readonly AppUpdater _updater;
    private bool _hasRun;
    private string _summary = "";

    public AppUpdatesViewModel(AppServices services) : base(services)
    {
        _updater = new AppUpdater(services.Log);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        UpdateSelectedCommand = new AsyncCommand(() => UpdateAsync(Rows.Where(r => r.IsSelected).ToList()), () => !IsBusy && Rows.Any(r => r.IsSelected));
        UpdateAllCommand = new AsyncCommand(() => UpdateAsync(Rows.Where(r => !r.Done).ToList()), () => !IsBusy && Rows.Any(r => !r.Done));
        GetWingetCommand = new RelayCommand(() => SystemTools.Open("ms-windows-store://pdp/?productid=9NBLGGH4NNS1"));
    }

    public override string Title => "App updates";
    public override string Glyph => "";

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand UpdateSelectedCommand { get; }
    public AsyncCommand UpdateAllCommand { get; }
    public RelayCommand GetWingetCommand { get; }
    public ObservableCollection<AppUpdateRowViewModel> Rows { get; } = [];
    public bool WingetMissing => !_updater.IsAvailable;

    /// <summary>The number of apps with an update waiting, for the health score.</summary>
    public int? OutdatedCount { get; private set; }

    public bool HasRun
    {
        get => _hasRun;
        private set => Set(ref _hasRun, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    protected override Task OnFirstVisitAsync() => RefreshAsync();

    public Task RefreshAsync() => RunBusyAsync(async ct =>
    {
        if (WingetMissing)
        {
            Summary = "The app updater uses Windows' built-in package manager (winget), which isn't installed on this PC.";
            HasRun = true;
            return;
        }
        Status = "Asking winget which apps have updates… (this can take a minute the first time)";
        var updates = await _updater.ListAsync(ct);
        Rows.Clear();
        foreach (var u in updates.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)) Rows.Add(new AppUpdateRowViewModel(u));
        OutdatedCount = updates.Count;
        Summary = updates.Count == 0
            ? "All the apps winget knows about are up to date."
            : $"{updates.Count} app{(updates.Count == 1 ? " has an update" : "s have updates")}. Updates fix security holes as well as bugs, so keeping apps current matters.";
        HasRun = true;
        Status = "";
    });

    private Task UpdateAsync(IReadOnlyList<AppUpdateRowViewModel> rows) => RunBusyAsync(async ct =>
    {
        if (rows.Count == 0) return;
        if (!Services.Dialogs.Confirm(rows.Count == 1 ? $"Update {rows[0].Name}?" : $"Update {rows.Count} apps?",
                "Each app is downloaded from its publisher through winget and installed quietly. Close the apps first; an app that's open may not update.\n\n" +
                string.Join("\n", rows.Take(15).Select(r => $"•  {r.Name}  {r.Update.Version} → {r.Update.Available}")), "Update"))
            return;

        int ok = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            Status = $"Updating {row.Name} ({i + 1} of {rows.Count})…";
            row.State = "Updating…";
            string? problem = await _updater.UpgradeAsync(row.Update, ct);
            if (problem == null)
            {
                ok++;
                row.MarkDone();
            }
            else
            {
                row.State = "Didn't update: " + problem;
            }
        }
        Status = "";
        Summary = $"Updated {ok} of {rows.Count}." + (ok < rows.Count ? " Some need the app closed first, or must be updated from inside the app." : "");
    });
}

public sealed class AppUpdateRowViewModel(AppUpdate update) : ObservableObject
{
    private bool _isSelected;
    private string? _state;
    private bool _done;

    public AppUpdate Update { get; } = update;
    public string Name => Update.Name;
    public string Versions => $"{Update.Version}  →  {Update.Available}";
    public string Source => Update.Id;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value && !_done);
    }

    public string? State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    public bool Done => _done;
    public bool CanSelect => !_done;

    internal void MarkDone()
    {
        _done = true;
        IsSelected = false;
        State = "Updated ✓";
        OnPropertiesChanged(nameof(Done), nameof(CanSelect));
    }
}
