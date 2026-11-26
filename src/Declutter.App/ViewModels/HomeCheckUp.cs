using System.Collections.ObjectModel;
using Declutter.App.Mvvm;
using Declutter.Core.Health;
using Declutter.Core.History;
using Declutter.Core.Security;
using Declutter.Core.Util;

namespace Declutter.App.ViewModels;

/// <summary>Home's PC health score (from a one-click check-up) and the history trends.</summary>
public sealed partial class HomeViewModel
{
    private HealthScoreResult? _score;
    private string? _checkUpStatus;
    private IReadOnlyList<HistorySnapshot> _history = [];

    public AsyncCommand CheckUpCommand { get; }
    public RelayCommand FixCommand { get; }
    public ObservableCollection<DeductionViewModel> Deductions { get; } = [];

    public bool HasScore => _score != null;
    public int Score => _score?.Score ?? 0;
    public string ScoreLabel => _score?.Label ?? "";
    public Tone ScoreTone => _score?.Score switch
    {
        null => Tone.Neutral,
        >= 75 => Tone.Good,
        >= 55 => Tone.Caution,
        _ => Tone.Danger,
    };
    public double ScoreFraction => Score / 100.0;
    public string ScoreDetail => _score == null ? ""
        : _score.Deductions.Count == 0 ? "Nothing to fix. Nice work."
        : $"{_score.Deductions.Count} thing{(_score.Deductions.Count == 1 ? "" : "s")} to improve, biggest first:";
    public string? UncheckedText => _score?.Unchecked.Count > 0 ? "Not checked: " + string.Join(", ", _score.Unchecked) + "." : null;

    /// <summary>"Last check-up 3 days ago: 82" when there's an older score in the history.</summary>
    public string LastScoreText
    {
        get
        {
            var last = _history.LastOrDefault(h => h.Score != null);
            if (last == null) return "One click checks security, protection, drive health, speed and updates, then gives your PC a score out of 100 with what to fix first. Nothing is changed.";
            int days = DateOnly.FromDateTime(DateTime.Now).DayNumber - last.Date.DayNumber;
            string when = days switch { 0 => "today", 1 => "yesterday", _ => $"{days} days ago" };
            return $"Last check-up {when}: {last.Score} out of 100. Run it again to see what's changed.";
        }
    }

    public string? CheckUpStatus
    {
        get => _checkUpStatus;
        private set => Set(ref _checkUpStatus, value);
    }

    // ---- Trends -----------------------------------------------------------------------------------

    public bool HasTrends => FreeSpaceTrend.Count >= 2 || ScoreTrend.Count >= 2 || BootTrend.Count >= 2;
    public IReadOnlyList<double> FreeSpaceTrend { get; private set; } = [];
    public IReadOnlyList<double> ScoreTrend { get; private set; } = [];
    public IReadOnlyList<double> BootTrend { get; private set; } = [];
    public bool HasFreeSpaceTrend => FreeSpaceTrend.Count >= 2;
    public bool HasScoreTrend => ScoreTrend.Count >= 2;
    public bool HasBootTrend => BootTrend.Count >= 2;
    public string FreeSpaceTrendText { get; private set; } = "";
    public string ScoreTrendText { get; private set; } = "";
    public string BootTrendText { get; private set; } = "";
    public string? ForecastText { get; private set; }
    public bool KeepsHistory => Services.Settings.KeepHistory;

    internal void LoadTrends()
    {
        _history = Services.History.Load();
        var recent = _history.Where(h => h.Date > DateOnly.FromDateTime(DateTime.Now).AddDays(-90)).ToList();

        var free = recent.Where(h => h.DriveFreeBytes != null).ToList();
        FreeSpaceTrend = free.Select(h => h.DriveFreeBytes!.Value / (double)(1L << 30)).ToList();
        FreeSpaceTrendText = free.Count >= 2 ? Change(free[0].DriveFreeBytes!.Value, free[^1].DriveFreeBytes!.Value, free[0].Date) : "";

        var scores = recent.Where(h => h.Score != null).ToList();
        ScoreTrend = scores.Select(h => (double)h.Score!.Value).ToList();
        ScoreTrendText = scores.Count >= 2 ? $"{scores[0].Score} → {scores[^1].Score} since {scores[0].Date:d MMM}" : "";

        var boots = recent.Where(h => h.BootSeconds != null).ToList();
        BootTrend = boots.Select(h => h.BootSeconds!.Value).ToList();
        BootTrendText = boots.Count >= 2 ? $"{boots[0].BootSeconds:0} s → {boots[^1].BootSeconds:0} s since {boots[0].Date:d MMM}" : "";

        ForecastText = DiskForecast.WeeksUntilFull(_history, DateOnly.FromDateTime(DateTime.Now)) is double weeks ? DiskForecast.Describe(weeks) : null;

        OnPropertiesChanged(nameof(HasTrends), nameof(FreeSpaceTrend), nameof(ScoreTrend), nameof(BootTrend), nameof(HasFreeSpaceTrend),
            nameof(HasScoreTrend), nameof(HasBootTrend), nameof(FreeSpaceTrendText), nameof(ScoreTrendText), nameof(BootTrendText),
            nameof(ForecastText), nameof(LastScoreText), nameof(KeepsHistory));
    }

    private static string Change(long from, long to, DateOnly since)
    {
        long diff = to - from;
        string what = diff switch
        {
            > 0 => $"{ByteSize.Format(diff)} more free",
            < 0 => $"{ByteSize.Format(-diff)} less free",
            _ => "No change",
        };
        return $"{what} than on {since:d MMM}";
    }

    // ---- Check-up ---------------------------------------------------------------------------------

    private PageViewModel? PageByName(string name) => name switch
    {
        "Security" => _main.Security,
        "Protection" => _main.Protection,
        "Junk" => _main.Junk,
        "Health" => _main.Health,
        "Startup" => _main.Startup,
        "Speed" => _main.Speed,
        "AppUpdates" => _main.AppUpdates,
        "ThisPc" => _main.ThisPc,
        _ => null,
    };

    public Task CheckUpAsync() => RunBusyAsync(async ct =>
    {
        var m = _main;
        async Task Step(string what, PageViewModel page, bool done, Func<Task> run)
        {
            CheckUpStatus = what;
            page.MarkVisited();
            while (page.IsBusy) await Task.Delay(200, ct);
            if (!done) await run();
            ct.ThrowIfCancellationRequested();
        }

        await Step("Checking security… (1 of 6)", m.Security, m.Security.HasRun, m.Security.RunCheckAsync);
        await Step("Checking backups, encryption and Windows Update… (2 of 6)", m.Protection, m.Protection.HasRun, m.Protection.RefreshAsync);
        await Step("Checking drive and battery health… (3 of 6)", m.Health, m.Health.HasRun, m.Health.RefreshAsync);
        await Step("Checking start-up speed… (4 of 6)", m.Speed, m.Speed.HasRun, m.Speed.RefreshAsync);
        await Step("Counting startup apps… (5 of 6)", m.Startup, m.Startup.HasRun, m.Startup.RefreshAsync);
        if (!m.AppUpdates.WingetMissing)
            await Step("Looking for app updates… (6 of 6)", m.AppUpdates, m.AppUpdates.HasRun, m.AppUpdates.RefreshAsync);

        m.RefreshDrive();
        _score = HealthScore.Compute(GatherInputs());
        Deductions.Clear();
        foreach (var d in _score.Deductions) Deductions.Add(new DeductionViewModel(d, FixCommand));
        CheckUpStatus = null;
        Services.RecordHistory(new HistorySnapshot { Score = _score.Score });
        Services.Log.Write("Check-up", $"Score {_score.Score} ({_score.Label}): " + string.Join("; ", _score.Deductions.Select(d => $"-{d.Points} {d.Reason}")));
        OnPropertiesChanged(nameof(HasScore), nameof(Score), nameof(ScoreLabel), nameof(ScoreTone), nameof(ScoreFraction), nameof(ScoreDetail),
            nameof(UncheckedText), nameof(LastScoreText));
    });

    internal ScoreInputs GatherInputs()
    {
        var m = _main;
        var findings = m.Security.HasRun ? m.Security.Findings.Select(f => f.Finding).ToList() : null;
        var drives = m.Health.Disks.Select(d => d.Grade).Where(g => g != HealthGrade.Unknown).ToList();
        return new ScoreInputs
        {
            DriveUsedFraction = m.DriveUsedFraction > 0 ? m.DriveUsedFraction : null,
            SecurityCritical = findings?.Count(f => f.Severity == Severity.Critical),
            SecurityWarnings = findings?.Count(f => f.Severity == Severity.Warning),
            Protection = m.Protection.Results.Select(r => (r.Area, r.Grade)).ToList(),
            WorstDrive = drives.Count > 0 ? drives.Max() : null,
            BatteryHealthPercent = m.Health.Batteries.FirstOrDefault(b => b.Percent != null)?.Percent,
            StartupAppsOn = m.Startup.HasRun ? m.Startup.OnCount : null,
            BootSeconds = m.Speed.TypicalBootSeconds,
            OutdatedApps = m.AppUpdates.OutdatedCount,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }
}

public sealed class DeductionViewModel(ScoreDeduction d, RelayCommand fix)
{
    public string Reason => d.Reason;
    public string PointsText => $"−{d.Points}";
    public string Category => d.Category;
    public string Page => d.Page;
    public RelayCommand FixCommand => fix;
    public string FixText => d.Page switch
    {
        "Junk" => "Free up space",
        "AppUpdates" => "Update apps",
        "ThisPc" => "See details",
        _ => "Open " + d.Page,
    };
}
