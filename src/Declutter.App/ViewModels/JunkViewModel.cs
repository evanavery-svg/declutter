using System.Collections.ObjectModel;
using System.Text;
using Declutter.App.Mvvm;
using Declutter.Core.Junk;
using Declutter.Core.Util;
using Declutter.Windows;

namespace Declutter.App.ViewModels;

public sealed class JunkViewModel : PageViewModel
{
    private string _summary = "Scan to see how much space you can free up.";
    private Tone _summaryTone = Tone.Neutral;
    private bool _hasScanned;

    public JunkViewModel(AppServices services) : base(services)
    {
        IEnumerable<IJunkSource> sources = [.. JunkCatalog.Create(services.Folders), new RecycleBinSource(services.OwnerWindow)];
        Categories = new ObservableCollection<JunkCategoryViewModel>(sources.Select(s => new JunkCategoryViewModel(s, this, services.IsElevated)));
        ScanCommand = new AsyncCommand(ScanAsync, () => !IsBusy);
        CleanCommand = new AsyncCommand(CleanAsync, () => !IsBusy && SelectedBytes > 0);
        OpenStorageSettingsCommand = new RelayCommand(() => SystemTools.Open("ms-settings:storagesense"));
    }

    public override string Title => "Junk Cleaner";
    public override string Glyph => "";

    public ObservableCollection<JunkCategoryViewModel> Categories { get; }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand CleanCommand { get; }
    public RelayCommand OpenStorageSettingsCommand { get; }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public Tone SummaryTone
    {
        get => _summaryTone;
        private set => Set(ref _summaryTone, value);
    }

    public bool HasScanned
    {
        get => _hasScanned;
        private set => Set(ref _hasScanned, value);
    }

    public long SelectedBytes => Categories.Where(c => c.IsSelected && c.CanClean).Sum(c => c.Bytes);
    public string CleanButtonText => SelectedBytes > 0 ? $"Clean {ByteSize.Format(SelectedBytes)}" : "Clean";

    internal void SelectionChanged() => OnPropertiesChanged(nameof(SelectedBytes), nameof(CleanButtonText));

    private JunkContext Context => new(Services.Guard, Services.IsElevated, SystemClock.Instance, Services.Log);

    protected override Task OnFirstVisitAsync() => ScanAsync();

    public Task ScanAsync() => RunBusyAsync(async ct =>
    {
        Status = "Looking for junk files…";
        foreach (var c in Categories) c.BeginScan();

        await Task.WhenAll(Categories.Select(c => c.ScanAsync(Context, ct)));

        var running = SystemTools.RunningProcesses(Categories.SelectMany(c => c.Source.BlockingProcesses).Select(b => b.ProcessName));
        foreach (var c in Categories) c.UpdateBlockers(running);

        long total = Categories.Where(c => c.CanClean).Sum(c => c.Bytes);
        Summary = total > 0
            ? $"Found {ByteSize.Format(total)} you can safely remove."
            : "Nothing to clean right now. Your PC is already tidy.";
        SummaryTone = total > 0 ? Tone.Info : Tone.Good;
        HasScanned = true;
        Status = "";
        SelectionChanged();
    });

    public Task CleanAsync() => RunBusyAsync(async ct =>
    {
        var selected = Categories.Where(c => c.IsSelected && c.CanClean && c.Bytes > 0).ToList();
        if (selected.Count == 0) return;

        var message = new StringBuilder("These files will be permanently deleted. Windows and your apps recreate them when needed. Your documents, photos and settings are not affected.\n\n");
        foreach (var c in selected) message.AppendLine($"•  {c.Name}: {c.SizeText}");
        if (selected.Any(c => c.Source is RecycleBinSource))
            message.AppendLine("\nThe Recycle Bin will be emptied. That can't be undone.");
        if (!Services.Dialogs.Confirm($"Clean {ByteSize.Format(selected.Sum(c => c.Bytes))}?", message.ToString(), "Clean now", danger: false))
            return;

        long freed = 0;
        int skipped = 0;
        foreach (var category in selected)
        {
            Status = $"Cleaning {category.Name}…";
            var result = await category.CleanAsync(Context, ct);
            freed += result.BytesFreed;
            skipped += result.Skipped;
        }

        Services.Stats.Add(freed);
        Services.NotifyDiskChanged();

        Status = "Checking what's left…";
        await Task.WhenAll(selected.Select(c => c.ScanAsync(Context, ct)));

        Summary = $"Freed {ByteSize.Format(freed)}." + (skipped > 0
            ? $" {skipped:N0} files were in use and were left alone. They'll be cleaned next time."
            : "");
        SummaryTone = Tone.Good;
        Status = "";
        SelectionChanged();
    });
}

public sealed class JunkCategoryViewModel : ObservableObject
{
    private readonly JunkViewModel _owner;
    private readonly bool _elevated;
    private bool _isSelected;
    private bool _isWorking;
    private JunkScanResult? _scan;
    private string? _blockers;

    public JunkCategoryViewModel(IJunkSource source, JunkViewModel owner, bool elevated)
    {
        Source = source;
        _owner = owner;
        _elevated = elevated;
        _isSelected = source.SelectedByDefault;
    }

    public IJunkSource Source { get; }
    public string Name => Source.Name;
    public string Description => Source.Description;
    public string? Warning => Source.Warning;

    public bool NeedsAdmin => Source.RequiresAdmin && !_elevated;
    public bool CanClean => !NeedsAdmin && !_isWorking;
    public long Bytes => _scan?.Bytes ?? 0;
    public bool IsEmpty => _scan != null && _scan.Bytes == 0 && !NeedsAdmin;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) _owner.SelectionChanged();
        }
    }

    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (Set(ref _isWorking, value)) OnPropertiesChanged(nameof(CanClean), nameof(SizeText));
        }
    }

    public string SizeText => NeedsAdmin ? "Needs admin"
        : IsWorking ? "…"
        : _scan == null ? "—"
        : _scan.Bytes == 0 ? "Clean"
        : ByteSize.Format(_scan.Bytes);

    public string? Detail => _scan is { Count: > 0 } s && Source is not RecycleBinSource
        ? $"{s.Count:N0} files" + (_scan.Note is { } n ? " · " + n : "")
        : _scan?.Note;

    public string? Blockers
    {
        get => _blockers;
        private set => Set(ref _blockers, value);
    }

    internal void BeginScan() => IsWorking = true;

    internal async Task ScanAsync(JunkContext context, CancellationToken ct)
    {
        IsWorking = true;
        try
        {
            _scan = await Source.ScanAsync(context, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _scan = new JunkScanResult([], 0, 0, "Couldn't scan this location.");
        }
        finally
        {
            IsWorking = false;
            OnPropertiesChanged(nameof(Bytes), nameof(SizeText), nameof(Detail), nameof(IsEmpty));
        }
    }

    internal async Task<JunkCleanResult> CleanAsync(JunkContext context, CancellationToken ct)
    {
        if (_scan == null) return new JunkCleanResult();
        IsWorking = true;
        try
        {
            return await Source.CleanAsync(_scan, context, null, ct);
        }
        finally
        {
            IsWorking = false;
        }
    }

    internal void UpdateBlockers(IReadOnlySet<string> running)
    {
        var open = Source.BlockingProcesses.Where(b => running.Contains(b.ProcessName)).Select(b => b.DisplayName).ToList();
        Blockers = open.Count == 0 ? null : $"Close {string.Join(" and ", open)} first to clean everything.";
    }
}
