using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Declutter.App.Mvvm;
using Declutter.Core.Startup;
using Declutter.Windows;

namespace Declutter.App.ViewModels;

public enum StartupFilter
{
    All,
    On,
    Suggested,
}

public sealed class StartupViewModel : PageViewModel
{
    private string _summary = "";

    public StartupViewModel(AppServices services) : base(services)
    {
        Items = [];
        Filter = new ChoiceSet<StartupFilter>(StartupFilter.All,
            (StartupFilter.All, "All"), (StartupFilter.On, "Turned on"), (StartupFilter.Suggested, "Safe to turn off"));
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is StartupItemViewModel i && Filter.Selected switch
        {
            StartupFilter.On => i.IsOn,
            StartupFilter.Suggested => i.IsOn && i.Item.Advice == StartupAdvice.SafeToDisable,
            _ => true,
        };
        Filter.Changed += (_, _) => View.Refresh();

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        DisableSuggestedCommand = new AsyncCommand(DisableSuggestedAsync, () => !IsBusy && SuggestedCount > 0);
        OpenTaskManagerCommand = new RelayCommand(() => SystemTools.Open("taskmgr.exe"));
    }

    public override string Title => "Startup";
    public override string Glyph => "";

    public ObservableCollection<StartupItemViewModel> Items { get; }
    public ICollectionView View { get; }
    public ChoiceSet<StartupFilter> Filter { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand DisableSuggestedCommand { get; }
    public RelayCommand OpenTaskManagerCommand { get; }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public int SuggestedCount => Items.Count(i => i.IsOn && i.CanToggle && i.Item.Advice == StartupAdvice.SafeToDisable);
    public bool HasLockedItems => !IsElevated && Items.Any(i => i.Item.RequiresAdmin);

    protected override Task OnFirstVisitAsync() => RefreshAsync();

    public Task RefreshAsync() => RunBusyAsync(async ct =>
    {
        Status = "Reading startup programs…";
        var items = await Services.Startup.GetItemsAsync(ct);
        Items.Clear();
        foreach (var item in items) Items.Add(new StartupItemViewModel(item, this));
        Status = "";
        UpdateSummary();
    });

    internal async Task<bool> ToggleAsync(StartupItemViewModel vm, bool enable)
    {
        try
        {
            await Services.Startup.SetEnabledAsync(vm.Item, enable);
            UpdateSummary();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or System.Security.SecurityException)
        {
            Services.Dialogs.Show($"Couldn't change \"{vm.Name}\"", ex.Message);
            return false;
        }
    }

    private Task DisableSuggestedAsync() => RunBusyAsync(async _ =>
    {
        var targets = Items.Where(i => i.IsOn && i.CanToggle && i.Item.Advice == StartupAdvice.SafeToDisable).ToList();
        string list = string.Join("\n", targets.Select(t => "•  " + t.Name));
        if (!Services.Dialogs.Confirm($"Turn off {targets.Count} startup apps?",
                "These apps won't open automatically when you sign in. They aren't uninstalled, and you can turn them back on here at any time.\n\n" + list,
                "Turn off"))
            return;

        foreach (var t in targets)
        {
            Status = $"Turning off {t.Name}…";
            await t.SetEnabledAsync(false);
        }
        Status = "";
    });

    private void UpdateSummary()
    {
        int on = Items.Count(i => i.IsOn);
        int suggested = SuggestedCount;
        Summary = $"{on} of {Items.Count} startup apps are turned on." +
                  (suggested > 0 ? $" {suggested} of them are safe to turn off." : " Nothing obvious left to turn off.");
        OnPropertiesChanged(nameof(SuggestedCount), nameof(HasLockedItems));
        View.Refresh();
    }
}

public sealed class StartupItemViewModel : ObservableObject
{
    private readonly StartupViewModel _owner;
    private bool _busy;

    public StartupItemViewModel(StartupItem item, StartupViewModel owner)
    {
        Item = item;
        _owner = owner;
        (AdviceLabel, AdviceTone) = Advice.For(item.Advice);
        ShowFileCommand = new RelayCommand(() => SystemTools.ShowInExplorer(item.FilePath!), () => item.FilePath != null && !item.FileMissing);
    }

    public StartupItem Item { get; }
    public string Name => Item.Name;

    public string Subtitle => string.Join("  ·  ",
        new[] { Item.Description, Item.Publisher }.Where(s => !string.IsNullOrWhiteSpace(s) && !s!.Equals(Item.Name, StringComparison.OrdinalIgnoreCase)).Distinct());

    public string AdviceLabel { get; }
    public Tone AdviceTone { get; }
    public string? AdviceReason => Item.AdviceReason;
    public string SourceText => Item.SourceDescription;
    public string Command => Item.Command;
    public bool FileMissing => Item.FileMissing;
    public RelayCommand ShowFileCommand { get; }

    public bool CanToggle => !_busy && Item.LockedReason == null && (!Item.RequiresAdmin || _owner.IsElevated);

    public string? LockText => Item.LockedReason
        ?? (Item.RequiresAdmin && !_owner.IsElevated ? "Needs administrator rights to change" : null);

    public string StateText => Item.Enabled ? "On" : "Off";

    /// <summary>Bound to the toggle switch: flipping it applies the change immediately.</summary>
    public bool IsOn
    {
        get => Item.Enabled;
        set
        {
            if (value != Item.Enabled) _ = SetEnabledAsync(value);
        }
    }

    internal async Task SetEnabledAsync(bool enabled)
    {
        _busy = true;
        OnPropertyChanged(nameof(CanToggle));
        try
        {
            await _owner.ToggleAsync(this, enabled);
        }
        finally
        {
            _busy = false;
            OnPropertiesChanged(nameof(IsOn), nameof(StateText), nameof(CanToggle));
        }
    }
}
