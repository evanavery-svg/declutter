using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Declutter.App.Mvvm;
using Declutter.Core.Apps;
using Declutter.Core.Util;
using Declutter.Windows;

namespace Declutter.App.ViewModels;

public enum AppsFilter
{
    All,
    Suggested,
    Desktop,
    Store,
}

public enum AppsSort
{
    Size,
    Name,
    Date,
}

public sealed class AppsViewModel : PageViewModel
{
    private string _search = "";
    private string _summary = "";
    private CancellationTokenSource? _measuring;

    public AppsViewModel(AppServices services) : base(services)
    {
        Filter = new ChoiceSet<AppsFilter>(AppsFilter.All,
            (AppsFilter.All, "All"), (AppsFilter.Suggested, "Suggested to remove"), (AppsFilter.Desktop, "Programs"), (AppsFilter.Store, "Store apps"));
        Sort = new ChoiceSet<AppsSort>(AppsSort.Size, (AppsSort.Size, "Size"), (AppsSort.Name, "Name"), (AppsSort.Date, "Install date"));
        View = CollectionViewSource.GetDefaultView(Apps);
        View.Filter = Matches;
        Filter.Changed += (_, _) => View.Refresh();
        Sort.Changed += (_, _) => ApplySort();
        ApplySort();

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        UninstallSelectedCommand = new AsyncCommand(() => UninstallAsync(Apps.Where(a => a.IsSelected).ToList()), () => !IsBusy && SelectedCount > 0);
    }

    public override string Title => "Apps";
    public override string Glyph => "";

    public ObservableCollection<AppRowViewModel> Apps { get; } = [];
    public ICollectionView View { get; }
    public ChoiceSet<AppsFilter> Filter { get; }
    public ChoiceSet<AppsSort> Sort { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand UninstallSelectedCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) View.Refresh();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public int SelectedCount => Apps.Count(a => a.IsSelected);
    public string UninstallButtonText => SelectedCount > 0 ? $"Uninstall {SelectedCount} selected" : "Uninstall selected";

    internal void SelectionChanged() => OnPropertiesChanged(nameof(SelectedCount), nameof(UninstallButtonText));

    private bool Matches(object o)
    {
        if (o is not AppRowViewModel a) return false;
        bool filter = Filter.Selected switch
        {
            AppsFilter.Suggested => a.App.Advice == AppAdvice.Bloatware,
            AppsFilter.Desktop => a.App.Kind == AppKind.Desktop,
            AppsFilter.Store => a.App.Kind == AppKind.Store,
            _ => true,
        };
        return filter && (Search.Length == 0 ||
                          a.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase) ||
                          (a.App.Publisher?.Contains(Search, StringComparison.CurrentCultureIgnoreCase) ?? false));
    }

    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            switch (Sort.Selected)
            {
                case AppsSort.Size:
                    View.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.SizeSort), ListSortDirection.Descending));
                    break;
                case AppsSort.Date:
                    View.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.DateSort), ListSortDirection.Descending));
                    break;
            }
            View.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.Name), ListSortDirection.Ascending));
        }
    }

    protected override Task OnFirstVisitAsync() => RefreshAsync();

    public Task RefreshAsync() => RunBusyAsync(async ct =>
    {
        _measuring?.Cancel();
        Status = "Finding installed apps…";
        var apps = await Services.Apps.GetAppsAsync(ct);

        Apps.Clear();
        foreach (var app in apps) Apps.Add(new AppRowViewModel(app, this));
        int suggested = apps.Count(a => a.Advice == AppAdvice.Bloatware);
        Summary = $"{apps.Count} apps installed." + (suggested > 0 ? $" {suggested} are preinstalled extras you could remove." : "");
        Status = "";
        SelectionChanged();
        _ = MeasureSizesAsync();
    });

    private async Task MeasureSizesAsync()
    {
        _measuring = new CancellationTokenSource();
        var byApp = Apps.ToDictionary(a => a.App);
        try
        {
            await InstalledApps.MeasureMissingSizesAsync(byApp.Keys, app => App.RunOnUi(() => byApp[app].SizeMeasured()), _measuring.Token);
            View.Refresh();
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal Task UninstallAsync(IReadOnlyList<AppRowViewModel> rows) => RunBusyAsync(async ct =>
    {
        if (rows.Count == 0) return;
        bool anyRequired = rows.Any(r => r.App.Advice == AppAdvice.Required);
        bool anyDesktop = rows.Any(r => r.App.Kind == AppKind.Desktop);
        string list = string.Join("\n", rows.Take(15).Select(r => "•  " + r.Name)) + (rows.Count > 15 ? $"\n…and {rows.Count - 15} more" : "");
        string message = list +
            (anyDesktop ? "\n\nPrograms open their own uninstaller. Follow its steps, one program at a time." : "") +
            (anyRequired ? "\n\nWarning: Windows or other apps depend on at least one of these. Removing it can stop things working." : "");
        string title = rows.Count == 1 ? $"Uninstall {rows[0].Name}?" : $"Uninstall {rows.Count} apps?";
        if (!Services.Dialogs.Confirm(title, message, "Uninstall", danger: true)) return;

        var failures = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            Status = rows.Count == 1 ? $"Uninstalling {row.Name}…" : $"Uninstalling {row.Name} ({i + 1} of {rows.Count})…";
            var outcome = await Services.Apps.UninstallAsync(row.App, ct);
            if (outcome.Removed)
                Apps.Remove(row);
            else
                failures.Add($"•  {row.Name}: {outcome.Message}");
        }

        Status = "";
        SelectionChanged();
        Services.NotifyDiskChanged();
        if (failures.Count > 0) Services.Dialogs.Show("Some apps weren't removed", string.Join("\n", failures));
    });
}

public sealed class AppRowViewModel : ObservableObject
{
    private readonly AppsViewModel _owner;
    private bool _isSelected;

    public AppRowViewModel(InstalledApp app, AppsViewModel owner)
    {
        App = app;
        _owner = owner;
        (AdviceLabel, AdviceTone) = Advice.For(app.Advice);
        UninstallCommand = new AsyncCommand(() => owner.UninstallAsync([this]), () => !owner.IsBusy && CanUninstall);
    }

    public InstalledApp App { get; }
    public string Name => App.Name;

    public string Subtitle => string.Join("  ·  ", new[]
    {
        App.Publisher,
        string.IsNullOrWhiteSpace(App.Version) ? null : "Version " + App.Version,
        App.Kind == AppKind.Store ? "Store app" : null,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string AdviceLabel { get; }
    public Tone AdviceTone { get; }
    public bool HasAdvice => AdviceLabel.Length > 0;
    public string? AdviceReason => App.AdviceReason;

    public string SizeText => App.SizeBytes is { } b ? ByteSize.Format(b) : "";
    public long SizeSort => App.SizeBytes ?? -1;
    public string DateText => App.InstallDate is { } d ? "Installed " + d.ToString("d MMM yyyy") : "";
    public DateTime DateSort => App.InstallDate ?? DateTime.MinValue;

    public bool CanUninstall => App.Kind == AppKind.Store || !string.IsNullOrWhiteSpace(App.UninstallCommand);

    /// <summary>Runtimes and drivers can't be batch-selected; uninstalling them individually shows a warning.</summary>
    public bool CanSelect => CanUninstall && App.Advice != AppAdvice.Required && (!App.RequiresAdmin || _owner.IsElevated);

    public string? AdminNote => App.RequiresAdmin && !_owner.IsElevated ? "Needs administrator rights" : null;

    public AsyncCommand UninstallCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value && CanSelect)) _owner.SelectionChanged();
        }
    }

    internal void SizeMeasured() => OnPropertiesChanged(nameof(SizeText), nameof(SizeSort));
}
