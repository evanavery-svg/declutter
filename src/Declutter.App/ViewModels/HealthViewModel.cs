using System.Collections.ObjectModel;
using Declutter.App.Mvvm;
using Declutter.Core.Health;
using Declutter.Core.Util;
using Declutter.Windows;
using Declutter.Windows.Health;

namespace Declutter.App.ViewModels;

public sealed class HealthViewModel : PageViewModel
{
    private readonly HealthCollector _collector;
    private bool _hasRun;
    private bool _countersNeedAdmin;
    private string? _batteryMessage;
    private bool _hasBattery;

    public HealthViewModel(AppServices services) : base(services)
    {
        _collector = new HealthCollector(services.IsElevated);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        BatteryReportCommand = new AsyncCommand(OpenBatteryReportAsync, () => !IsBusy);
    }

    public override string Title => "Health";
    public override string Glyph => "";

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand BatteryReportCommand { get; }
    public ObservableCollection<HealthCardViewModel> Disks { get; } = [];
    public ObservableCollection<HealthCardViewModel> Batteries { get; } = [];

    public bool HasRun
    {
        get => _hasRun;
        private set => Set(ref _hasRun, value);
    }

    public bool CountersNeedAdmin
    {
        get => _countersNeedAdmin;
        private set => Set(ref _countersNeedAdmin, value);
    }

    /// <summary>"This PC doesn't have a battery", or the current charge.</summary>
    public string? BatteryMessage
    {
        get => _batteryMessage;
        private set => Set(ref _batteryMessage, value);
    }

    /// <summary>True when the PC has a battery, even if Windows gave no capacity details for it.</summary>
    public bool HasBattery
    {
        get => _hasBattery;
        private set => Set(ref _hasBattery, value);
    }

    protected override Task OnFirstVisitAsync() => RefreshAsync();

    public Task RefreshAsync() => RunBusyAsync(async ct =>
    {
        Status = "Reading drive and battery health…";
        var snapshot = await _collector.CollectAsync(ct);

        Disks.Clear();
        foreach (var d in snapshot.Disks)
        {
            string subtitle = string.Join("  ·  ", new[] { d.IsSystemDisk ? "Windows drive" : null, d.BusType.Length > 0 ? d.BusType : null }
                .Where(s => s != null));
            Disks.Add(new HealthCardViewModel(d.Name, subtitle, HealthAnalyzer.AssessDisk(d), null));
        }

        Batteries.Clear();
        foreach (var b in snapshot.Batteries)
        {
            var assessment = HealthAnalyzer.AssessBattery(b);
            string subtitle = string.Join("  ·  ", new[] { b.Manufacturer, b.Chemistry }.Where(s => !string.IsNullOrWhiteSpace(s)));
            Batteries.Add(new HealthCardViewModel(b.Name, subtitle, assessment, b.HealthPercent));
        }

        BatteryMessage = snapshot.Battery switch
        {
            null => "This PC doesn't have a battery.",
            { ChargePercent: int c } now => $"Charged to {c}% right now · {(now.PluggedIn ? "plugged in" : "on battery")}",
            _ => null,
        };
        if (snapshot.Battery != null && Batteries.Count == 0)
            BatteryMessage = "Windows didn't report details for this battery. The full battery report may have more.";

        CountersNeedAdmin = snapshot.CountersNeedAdmin;
        HasBattery = snapshot.Battery != null || Batteries.Count > 0;
        HasRun = true;
        Status = "";
        if (Batteries.FirstOrDefault(b => b.Percent != null)?.Percent is double batteryHealth)
            Services.RecordHistory(new Core.History.HistorySnapshot { BatteryHealthPercent = batteryHealth });
        Services.Log.Write("Health", $"Checked {Disks.Count} drive(s) and {Batteries.Count} battery(ies): " +
                                     string.Join("; ", Disks.Concat(Batteries).Select(c => $"{c.Name}: {c.GradeLabel}")));
    });

    private Task OpenBatteryReportAsync() => RunBusyAsync(async ct =>
    {
        Status = "Creating Windows' battery report…";
        string? page = await HealthCollector.CreateBatteryReportPageAsync(ct);
        Status = "";
        if (page != null) SystemTools.Open(page);
        else Services.Dialogs.Show("No battery report", "Windows couldn't create a battery report on this PC.");
    });
}

public sealed class HealthCardViewModel
{
    public HealthCardViewModel(string name, string subtitle, HealthAssessment assessment, double? percent)
    {
        Name = name;
        Subtitle = subtitle;
        Summary = assessment.Summary;
        Percent = percent;
        Grade = assessment.Grade;
        (GradeLabel, GradeTone) = assessment.Grade switch
        {
            HealthGrade.Good => ("Good", Tone.Good),
            HealthGrade.Fair => ("Fair", Tone.Caution),
            HealthGrade.Poor => ("Needs attention", Tone.Danger),
            _ => ("Unknown", Tone.Neutral),
        };
        Notes = assessment.Notes.Select(n => new NoteViewModel(n)).ToList();
    }

    public string Name { get; }
    public string Subtitle { get; }
    public string Summary { get; }
    public HealthGrade Grade { get; }
    public string GradeLabel { get; }
    public Tone GradeTone { get; }
    public IReadOnlyList<NoteViewModel> Notes { get; }

    /// <summary>Battery health 0–100 (null for drives).</summary>
    public double? Percent { get; }
    public double Fraction => (Percent ?? 0) / 100;
    public bool HasPercent => Percent != null;
    public string PercentText => Percent is double p ? $"{p:0}%" : "";
}

public sealed class NoteViewModel(HealthNote note)
{
    public string Text => note.Text;

    public (string Glyph, Tone Tone) Look => note.Kind switch
    {
        NoteKind.Good => ("", Tone.Good),
        NoteKind.Warning => ("", Tone.Caution),
        NoteKind.Critical => ("", Tone.Danger),
        _ => ("", Tone.Neutral),
    };

    public string Glyph => Look.Glyph;
    public Tone Tone => Look.Tone;
}
