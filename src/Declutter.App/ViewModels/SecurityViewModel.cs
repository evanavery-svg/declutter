using System.Collections.ObjectModel;
using Declutter.App.Mvvm;
using Declutter.Core.Security;
using Declutter.Windows;
using Declutter.Windows.Security;

namespace Declutter.App.ViewModels;

public sealed class SecurityViewModel : PageViewModel
{
    private SecurityReport? _report;
    private string _summary = "Checks for viruses, hidden crypto-miners and settings malware likes to change.";
    private Tone _summaryTone = Tone.Neutral;
    private bool _hasRun;
    private string? _defenderMessage;
    private Tone _defenderTone = Tone.Neutral;

    public SecurityViewModel(AppServices services) : base(services)
    {
        RunCheckCommand = new AsyncCommand(RunCheckAsync, () => !IsBusy);
        QuickScanCommand = new AsyncCommand(QuickScanAsync, () => !IsBusy && services.Defender.IsAvailable);
        UpdateDefinitionsCommand = new AsyncCommand(UpdateDefinitionsAsync, () => !IsBusy && services.Defender.IsAvailable);
        OpenWindowsSecurityCommand = new RelayCommand(() => SystemTools.Open("windowsdefender://threat"));
    }

    public override string Title => "Security";
    public override string Glyph => "";

    public AsyncCommand RunCheckCommand { get; }
    public AsyncCommand QuickScanCommand { get; }
    public AsyncCommand UpdateDefinitionsCommand { get; }
    public RelayCommand OpenWindowsSecurityCommand { get; }

    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<string> Passed { get; } = [];
    public ObservableCollection<CpuRowViewModel> TopCpu { get; } = [];

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

    public bool HasRun
    {
        get => _hasRun;
        private set => Set(ref _hasRun, value);
    }

    public string? DefenderMessage
    {
        get => _defenderMessage;
        private set => Set(ref _defenderMessage, value);
    }

    public Tone DefenderTone
    {
        get => _defenderTone;
        private set => Set(ref _defenderTone, value);
    }

    public string PassedTitle => $"{Passed.Count} checks passed";

    protected override Task OnFirstVisitAsync() => RunCheckAsync();

    public Task RunCheckAsync() => RunBusyAsync(CollectAndShowAsync);

    private async Task CollectAndShowAsync(CancellationToken ct)
    {
        var evidence = await Services.Security.CollectAsync(new Progress<string>(s => Status = s), ct);
        _report = SecurityAnalyzer.Analyze(evidence, Services.Folders, DateTime.UtcNow);

        Findings.Clear();
        foreach (var f in _report.Findings) Findings.Add(new FindingViewModel(f, this));
        Passed.Clear();
        foreach (var p in _report.Passed) Passed.Add(p);
        TopCpu.Clear();
        foreach (var p in _report.TopCpu) TopCpu.Add(new CpuRowViewModel(p));

        Summary = _report.Headline;
        SummaryTone = _report.CriticalCount > 0 ? Tone.Danger : _report.WarningCount > 0 ? Tone.Caution : Tone.Good;
        HasRun = true;
        Status = "";
        OnPropertyChanged(nameof(PassedTitle));
        Services.Log.Write("Security", $"Check finished: {_report.CriticalCount} critical, {_report.WarningCount} worth a look, {_report.Passed.Count} passed");
    }

    private Task QuickScanAsync() => RunBusyAsync(async ct =>
    {
        Status = "Microsoft Defender is running a quick scan. This usually takes a few minutes…";
        DefenderMessage = null;
        var outcome = await Services.Defender.QuickScanAsync(ct);
        ShowDefenderOutcome(outcome);
        Status = "";
        await RefreshAfterDefenderAsync(ct);
    });

    private Task UpdateDefinitionsAsync() => RunBusyAsync(async ct =>
    {
        Status = "Updating Microsoft Defender's virus definitions…";
        var outcome = await Services.Defender.UpdateDefinitionsAsync(ct);
        ShowDefenderOutcome(outcome);
        Status = "";
        await RefreshAfterDefenderAsync(ct);
    });

    internal Task ScanFileAsync(FindingViewModel finding) => RunBusyAsync(async ct =>
    {
        Status = $"Microsoft Defender is scanning {Path.GetFileName(finding.Finding.FilePath)}…";
        var outcome = await Services.Defender.ScanPathAsync(finding.Finding.FilePath!, ct);
        Status = "";
        finding.SetResult(outcome.ThreatsFound
            ? "Defender found a threat in this file and is removing it."
            : outcome.Ok ? "Defender scanned this file and found nothing. If you still don't recognise it, search for it online." : outcome.Message,
            outcome.ThreatsFound ? Tone.Danger : outcome.Ok ? Tone.Good : Tone.Caution);
    });

    internal async Task DisableStartupAsync(FindingViewModel finding)
    {
        if (!Services.Dialogs.Confirm("Turn off at startup?",
                $"\"{finding.Title}\" won't run when you sign in any more. The file isn't deleted, and you can turn it back on from the Startup page.",
                "Turn off"))
            return;

        var items = await Services.Startup.GetItemsAsync();
        var item = items.FirstOrDefault(i => i.Id == finding.Finding.StartupItemId);
        if (item == null)
        {
            finding.SetResult("It's no longer in the startup list.", Tone.Neutral);
            return;
        }
        try
        {
            await Services.Startup.SetEnabledAsync(item, false);
            finding.SetResult("Turned off at startup.", Tone.Good);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            finding.SetResult("Couldn't turn it off: " + ex.Message, Tone.Caution);
        }
    }

    internal void EndProcess(FindingViewModel finding)
    {
        if (finding.Finding.ProcessId is not int pid) return;
        if (!Services.Dialogs.Confirm("End this program?",
                $"{finding.Title}\n\nAny unsaved work in it will be lost. This only stops it for now; if it's malware, also run a Defender scan.",
                "End program", danger: true))
            return;
        var (ok, message) = ProcessControl.TryEnd(pid, finding.Finding.FilePath, Services.Folders, Services.Log);
        finding.SetResult(message, ok ? Tone.Good : Tone.Caution);
    }

    private void ShowDefenderOutcome(DefenderOutcome outcome)
    {
        DefenderMessage = outcome.Message;
        DefenderTone = outcome.ThreatsFound ? Tone.Danger : outcome.Ok ? Tone.Good : Tone.Caution;
    }

    // Re-run the check so Defender findings (scan age, threats) reflect what just happened.
    private Task RefreshAfterDefenderAsync(CancellationToken ct) => CollectAndShowAsync(ct);
}

public sealed class FindingViewModel : ObservableObject
{
    private readonly SecurityViewModel _owner;
    private string? _result;
    private Tone _resultTone;

    public FindingViewModel(SecurityFinding finding, SecurityViewModel owner)
    {
        Finding = finding;
        _owner = owner;
        (SeverityLabel, SeverityTone, SeverityGlyph) = finding.Severity switch
        {
            Severity.Critical => ("Needs attention", Tone.Danger, ""),
            Severity.Warning => ("Worth a look", Tone.Caution, ""),
            _ => ("For your information", Tone.Info, ""),
        };

        bool has(FindingAction a) => finding.Actions.Contains(a);
        ScanFileCommand = new AsyncCommand(() => owner.ScanFileAsync(this), () => !owner.IsBusy);
        DisableStartupCommand = new AsyncCommand(() => owner.DisableStartupAsync(this));
        EndProcessCommand = new RelayCommand(() => owner.EndProcess(this));
        ShowFileCommand = new RelayCommand(() => SystemTools.ShowInExplorer(finding.FilePath!));
        SearchCommand = new RelayCommand(() => SystemTools.Open("https://www.bing.com/search?q=" + Uri.EscapeDataString(SearchTerm)));

        CanScanFile = has(FindingAction.ScanWithDefender) && finding.FilePath != null && File.Exists(finding.FilePath);
        CanDisableStartup = has(FindingAction.DisableStartup) && finding.StartupItemId != null;
        CanEndProcess = has(FindingAction.EndProcess) && finding.ProcessId != null;
        CanShowFile = has(FindingAction.ShowFile) && finding.FilePath != null && File.Exists(finding.FilePath);
        CanSearch = has(FindingAction.SearchOnline);
        CanQuickScan = has(FindingAction.RunQuickScan);
        CanUpdateDefinitions = has(FindingAction.UpdateDefinitions);
        CanOpenWindowsSecurity = has(FindingAction.OpenWindowsSecurity);
    }

    public SecurityFinding Finding { get; }
    public string Title => Finding.Title;
    public string Explanation => Finding.Explanation;
    public string? Evidence => Finding.Evidence;
    public string SeverityLabel { get; }
    public Tone SeverityTone { get; }
    public string SeverityGlyph { get; }

    public AsyncCommand ScanFileCommand { get; }
    public AsyncCommand DisableStartupCommand { get; }
    public RelayCommand EndProcessCommand { get; }
    public RelayCommand ShowFileCommand { get; }
    public RelayCommand SearchCommand { get; }
    public AsyncCommand QuickScanCommand => _owner.QuickScanCommand;
    public AsyncCommand UpdateDefinitionsCommand => _owner.UpdateDefinitionsCommand;
    public RelayCommand OpenWindowsSecurityCommand => _owner.OpenWindowsSecurityCommand;

    public bool CanScanFile { get; }
    public bool CanDisableStartup { get; }
    public bool CanEndProcess { get; }
    public bool CanShowFile { get; }
    public bool CanSearch { get; }
    public bool CanQuickScan { get; }
    public bool CanUpdateDefinitions { get; }
    public bool CanOpenWindowsSecurity { get; }

    public string? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public Tone ResultTone
    {
        get => _resultTone;
        private set => Set(ref _resultTone, value);
    }

    internal void SetResult(string message, Tone tone)
    {
        ResultTone = tone;
        Result = message;
    }

    private string SearchTerm => Finding.FilePath is { } path ? $"\"{Path.GetFileName(path)}\" safe or malware" : $"\"{Finding.Title}\"";
}

public sealed class CpuRowViewModel(ProcessInfo process)
{
    public string Name => process.Name;
    public string Path => process.Path ?? "";
    public string CpuText => $"{process.CpuPercent:0}%";
    public double Fraction => Math.Clamp(process.CpuPercent / 100, 0.01, 1);
}
