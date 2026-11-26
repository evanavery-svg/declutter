using System.Collections.ObjectModel;
using Declutter.App.Mvvm;
using Declutter.Core.Health;
using Declutter.Core.Protection;
using Declutter.Windows;
using Declutter.Windows.Health;
using Declutter.Windows.Protection;

namespace Declutter.App.ViewModels;

public sealed class ProtectionViewModel : PageViewModel
{
    private readonly ProtectionCollector _collector;
    private bool _hasRun;
    private string _summary = "";
    private Tone _summaryTone = Tone.Neutral;

    public ProtectionViewModel(AppServices services) : base(services)
    {
        _collector = new ProtectionCollector(services.Folders, services.IsElevated);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
    }

    public override string Title => "Protection";
    public override string Glyph => "";

    public AsyncCommand RefreshCommand { get; }
    public ObservableCollection<ProtectionCardViewModel> Areas { get; } = [];
    public ObservableCollection<ExtensionRowViewModel> Extensions { get; } = [];

    /// <summary>The latest verdicts, for the health score and the report.</summary>
    public IReadOnlyList<ProtectionResult> Results { get; private set; } = [];

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

    public Tone SummaryTone
    {
        get => _summaryTone;
        private set => Set(ref _summaryTone, value);
    }

    public string ExtensionSummary => Extensions.Count == 0
        ? "No browser extensions found."
        : $"{Extensions.Count} extension{(Extensions.Count == 1 ? "" : "s")} across your browsers" +
          (Extensions.Count(e => e.Risk >= ExtensionRisk.Review) is int n and > 0 ? $"; {n} worth a look." : ". Nothing worrying.");

    protected override Task OnFirstVisitAsync() => RefreshAsync();

    public Task RefreshAsync() => RunBusyAsync(async ct =>
    {
        Status = "Checking backups, encryption and updates…";
        var info = await SystemInfoCollector.CollectAsync(ct);
        var apps = await Services.Apps.GetAppsAsync(ct);
        var backup = _collector.ReadBackupAsync(apps, ct);
        var device = _collector.ReadDeviceSecurityAsync(ct);
        var updates = _collector.ReadUpdatesAsync(info.WindowsName, info.WindowsVersion, ct);
        var extensions = ExtensionScanner.ScanAsync(Services.Folders.LocalAppData, Services.Folders.RoamingAppData, ct);
        await Task.WhenAll(backup, device, updates, extensions);

        Results =
        [
            BackupCheck.Evaluate(backup.Result, DateTime.UtcNow),
            DeviceSecurityCheck.Evaluate(device.Result),
            WindowsUpdateCheck.Evaluate(updates.Result, DateTime.UtcNow),
        ];
        Areas.Clear();
        foreach (var r in Results) Areas.Add(new ProtectionCardViewModel(r));

        Extensions.Clear();
        foreach (var e in extensions.Result.Select(e => new ExtensionRowViewModel(e))
                     .OrderByDescending(e => e.Risk == ExtensionRisk.Trusted ? -1 : (int)e.Risk).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            Extensions.Add(e);

        int poor = Results.Count(r => r.Grade == HealthGrade.Poor);
        int fair = Results.Count(r => r.Grade == HealthGrade.Fair) + Extensions.Count(e => e.Risk == ExtensionRisk.High);
        (Summary, SummaryTone) = (poor, fair) switch
        {
            (0, 0) => ("You're well protected: backups, encryption and updates all look good.", Tone.Good),
            (0, _) => ($"Mostly protected. {fair} thing{(fair == 1 ? "" : "s")} could be better.", Tone.Caution),
            _ => ($"{poor} important thing{(poor == 1 ? "" : "s")} to fix below.", Tone.Danger),
        };
        HasRun = true;
        Status = "";
        OnPropertyChanged(nameof(ExtensionSummary));
        Services.Log.Write("Protection", string.Join("; ", Results.Select(r => $"{r.Area}: {r.Summary}")) + $"; {Extensions.Count} extensions");
    });
}

public sealed class ProtectionCardViewModel
{
    public ProtectionCardViewModel(ProtectionResult r)
    {
        Name = r.Area;
        Summary = r.Summary;
        (GradeLabel, GradeTone) = r.Grade switch
        {
            HealthGrade.Good => ("Good", Tone.Good),
            HealthGrade.Fair => ("Could be better", Tone.Caution),
            HealthGrade.Poor => ("Needs attention", Tone.Danger),
            _ => ("Unknown", Tone.Neutral),
        };
        Notes = r.Notes.Select(n => new NoteViewModel(n)).ToList();
        ActionLabel = r.ActionLabel;
        ActionCommand = new RelayCommand(() => SystemTools.Open(r.ActionUri!), () => r.ActionUri != null);
    }

    public string Name { get; }
    public string Summary { get; }
    public string GradeLabel { get; }
    public Tone GradeTone { get; }
    public IReadOnlyList<NoteViewModel> Notes { get; }
    public string? ActionLabel { get; }
    public RelayCommand ActionCommand { get; }
}

public sealed class ExtensionRowViewModel
{
    public ExtensionRowViewModel(BrowserExtension e)
    {
        Extension = e;
        Risk = Core.Protection.Extensions.Rate(e, out string why);
        Why = why;
        (RiskLabel, RiskTone) = Risk switch
        {
            ExtensionRisk.High => ("Check this", Tone.Danger),
            ExtensionRisk.Review => ("Broad access", Tone.Caution),
            ExtensionRisk.Trusted => ("Well known", Tone.Good),
            _ => ("Limited access", Tone.Neutral),
        };
        OpenPageCommand = new RelayCommand(() =>
        {
            if (!ExtensionScanner.OpenExtensionsPage(e.Browser)) SystemTools.Open("https://www.bing.com/search?q=" + Uri.EscapeDataString($"how to remove extension {e.Browser}"));
        });
        string? store = ExtensionScanner.StorePage(e);
        LookUpCommand = new RelayCommand(() => SystemTools.Open(store ?? "https://www.bing.com/search?q=" + Uri.EscapeDataString($"\"{e.Name}\" {e.Browser} extension")));
    }

    public BrowserExtension Extension { get; }

    public RelayCommand ExplainCommand => _explain ??= ExplainCommands.For(() => Core.Explain.ExplainRequest.Of(
        $"{Extension.Browser} browser extension", Extension.Name,
        ("Extension ID", Extension.Id), ("Version", Extension.Version), ("Description", Extension.Description),
        ("From the official store", Extension.FromStore ? "yes" : "no"), ("Turned on", Extension.Enabled ? "yes" : "no"),
        ("Permissions", string.Join(", ", Extension.Permissions.Take(20))), ("Sites it can read", string.Join(", ", Extension.Sites.Take(10))),
        ("Declutter's view", Why)));
    private RelayCommand? _explain;
    public ExtensionRisk Risk { get; }
    public string Name => Extension.Name;
    public string Subtitle => string.Join("  ·  ", new[]
    {
        Extension.Browser + (Extension.Profile is { } p && p != "Default" && Extension.Browser != "Firefox" ? $" ({p})" : ""),
        Extension.Version is { } v ? "v" + v : null,
        Extension.Enabled ? null : "Turned off",
        Extension.FromStore ? null : "Not from the store",
    }.Where(s => s != null));
    public string Why { get; }
    public string RiskLabel { get; }
    public Tone RiskTone { get; }
    public string OpenPageText => $"Open {Extension.Browser} extensions";
    public RelayCommand OpenPageCommand { get; }
    public RelayCommand LookUpCommand { get; }
}
