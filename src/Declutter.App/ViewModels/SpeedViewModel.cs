using System.Collections.ObjectModel;
using Declutter.App.Mvvm;
using Declutter.Core.Health;
using Declutter.Core.Speed;
using Declutter.Windows;
using Declutter.Windows.Health;
using Declutter.Windows.Speed;

namespace Declutter.App.ViewModels;

public sealed class SpeedViewModel : PageViewModel
{
    private readonly SpeedCollector _collector;
    private BootSummary? _boot;
    private bool _hasRun;

    public SpeedViewModel(AppServices services) : base(services)
    {
        _collector = new SpeedCollector(AppServices.DataFolder, services.Folders.WindowsDir, services.Log, services.IsElevated);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        ManualAllCommand = new AsyncCommand(SetAllSuggestedManualAsync, () => !IsBusy && ServiceRows.Any(s => s.CanSetManual));
    }

    public override string Title => "Speed";
    public override string Glyph => "";

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ManualAllCommand { get; }
    public ObservableCollection<ServiceRowViewModel> ServiceRows { get; } = [];
    public ObservableCollection<BootCulprit> Culprits { get; } = [];
    public ObservableCollection<NoteViewModel> Hardware { get; } = [];

    public bool HasRun
    {
        get => _hasRun;
        private set => Set(ref _hasRun, value);
    }

    public bool BootNeedsAdmin => HasRun && !IsElevated;
    public bool HasBootData => _boot?.Latest != null;
    public string BootHeadline => _boot?.TypicalSeconds is double t ? $"Usually ready in {t:0} seconds" : "No start-up timings yet";
    public string BootVerdict => _boot?.Verdict ?? "";
    public string BootDetail => _boot?.Latest is { } last
        ? $"Last start: {last.Seconds:0} seconds, on {last.TimeUtc.ToLocalTime():d MMM} at {last.TimeUtc.ToLocalTime():HH:mm}. Windows times this itself each time it starts."
        : "Windows records a timing after each full start (not after waking from sleep). Restart once and check again.";
    public IReadOnlyList<double> BootTrend => _boot?.Recent.Select(b => b.Seconds).ToList() ?? [];
    public bool HasCulprits => Culprits.Count > 0;
    public int SuggestedCount => ServiceRows.Count(s => s.CanSetManual);

    protected override Task OnFirstVisitAsync() => RefreshAsync();

    public Task RefreshAsync() => RunBusyAsync(async ct =>
    {
        Status = "Reading start-up timings…";
        var boot = _collector.ReadBootsAsync(ct);
        Status = "Looking at background services…";
        var services = _collector.ReadServicesAsync(ct);
        var info = SystemInfoCollector.CollectAsync(ct);
        var health = new HealthCollector(Services.IsElevated).CollectAsync(ct);
        await Task.WhenAll(boot, services, info, health);

        _boot = boot.Result;
        Culprits.Clear();
        foreach (var c in _boot?.Culprits ?? []) Culprits.Add(c);

        ServiceRows.Clear();
        foreach (var s in services.Result
                     .Select(s => new ServiceRowViewModel(s, _collector.WasChangedByDeclutter(s.Name), this))
                     .OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            ServiceRows.Add(s);

        var space = SystemTools.GetDriveSpace(Services.Folders.SystemDrive);
        bool hdd = health.Result.Disks.FirstOrDefault(d => d.IsSystemDisk)?.IsHdd == true;
        Hardware.Clear();
        foreach (var n in HardwareAdvisor.Advise(info.Result, hdd, space?.UsedFraction, _boot?.TypicalSeconds)) Hardware.Add(new NoteViewModel(n));

        HasRun = true;
        Status = "";
        OnPropertiesChanged(nameof(BootNeedsAdmin), nameof(HasBootData), nameof(BootHeadline), nameof(BootVerdict), nameof(BootDetail),
            nameof(BootTrend), nameof(HasCulprits), nameof(SuggestedCount));
    });

    internal async Task ToggleAsync(ServiceRowViewModel row)
    {
        try
        {
            if (row.ChangedByDeclutter) await _collector.UndoAsync(row.Service, CancellationToken.None);
            else await _collector.SetManualAsync(row.Service, CancellationToken.None);
            row.ChangedByDeclutter = !row.ChangedByDeclutter;
            OnPropertyChanged(nameof(SuggestedCount));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Services.Dialogs.Show($"Couldn't change \"{row.Name}\"", ex.Message);
        }
    }

    private Task SetAllSuggestedManualAsync() => RunBusyAsync(async ct =>
    {
        var targets = ServiceRows.Where(s => s.CanSetManual).ToList();
        if (!Services.Dialogs.Confirm($"Set {targets.Count} services to Manual?",
                "They'll start only when their app needs them, instead of running all day. Nothing is uninstalled, and you can undo each one here.\n\n" +
                string.Join("\n", targets.Select(t => "•  " + t.Name)), "Set to Manual"))
            return;
        foreach (var t in targets)
        {
            Status = $"Changing {t.Name}…";
            await ToggleAsync(t);
        }
        Status = "Done. The changes take full effect after your next restart.";
    });
}

public sealed class ServiceRowViewModel : ObservableObject
{
    private readonly SpeedViewModel _owner;
    private bool _changed;

    public ServiceRowViewModel(ServiceInfo service, bool changedByDeclutter, SpeedViewModel owner)
    {
        Service = service;
        _owner = owner;
        _changed = changedByDeclutter;
        (Advice, AdviceReason) = ServiceAdvisor.Advise(service);
        (AdviceLabel, AdviceTone) = Advice switch
        {
            ServiceAdvice.Keep => ("Keep", Tone.Good),
            ServiceAdvice.Optional => ("Your choice", Tone.Info),
            ServiceAdvice.SafeToSetManual => ("Safe to set to Manual", Tone.Caution),
            _ => ("Not sure", Tone.Neutral),
        };
        ToggleCommand = new AsyncCommand(() => owner.ToggleAsync(this), () => !owner.IsBusy && (ChangedByDeclutter || Advice != ServiceAdvice.Keep));
    }

    public ServiceInfo Service { get; }
    public string Name => Service.DisplayName;
    public string Subtitle => string.Join("  ·  ", new[] { Service.Company, Service.Running ? "Running now" : "Not running" }.Where(s => s != null));
    public ServiceAdvice Advice { get; }
    public string AdviceReason { get; }
    public string AdviceLabel { get; }
    public Tone AdviceTone { get; }
    public AsyncCommand ToggleCommand { get; }

    public bool ChangedByDeclutter
    {
        get => _changed;
        set
        {
            if (Set(ref _changed, value)) OnPropertiesChanged(nameof(ButtonText), nameof(StateText), nameof(CanSetManual));
        }
    }

    public bool CanSetManual => !ChangedByDeclutter && Advice == ServiceAdvice.SafeToSetManual && _owner.IsElevated;
    public string ButtonText => ChangedByDeclutter ? "Undo" : "Set to Manual";
    public string StateText => ChangedByDeclutter ? "Manual (changed by Declutter)" : "Starts automatically";
    public int SortOrder => Advice switch { ServiceAdvice.SafeToSetManual => 0, ServiceAdvice.Unknown => 1, ServiceAdvice.Optional => 2, _ => 3 };
}
