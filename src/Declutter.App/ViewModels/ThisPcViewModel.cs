using System.Text;
using System.Windows;
using Declutter.App.Mvvm;
using Declutter.Core.Health;
using Declutter.Core.Util;
using Declutter.Windows;
using Declutter.Windows.Health;

namespace Declutter.App.ViewModels;

public sealed record InfoRow(string Label, string Value, string? Hint = null);

public sealed class ThisPcViewModel : PageViewModel
{
    private SystemInfo? _info;
    private IReadOnlyList<InfoRow> _rows = [];
    private string? _restartTip;
    private string? _reportStatus;

    public ThisPcViewModel(AppServices services) : base(services)
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        CopyCommand = new RelayCommand(Copy, () => _info != null);
        WarrantyCommand = new RelayCommand(OpenWarranty, () => WarrantyPage != null);
        CopySerialCommand = new RelayCommand(() => Clipboard.SetText(_info!.RealSerial!), () => _info?.RealSerial != null);
        ReportCommand = new AsyncCommand(CreateReportAsync, () => !IsBusy);
    }

    public override string Title => "This PC";
    public override string Glyph => "";

    public AsyncCommand RefreshCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand WarrantyCommand { get; }
    public RelayCommand CopySerialCommand { get; }
    public AsyncCommand ReportCommand { get; }

    public IReadOnlyList<InfoRow> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    public string Heading => _info == null ? "This PC" : $"{_info.Manufacturer} {_info.Model}".Trim();
    public string? Serial => _info?.RealSerial;
    public string? WarrantyPage => _info == null ? null : Warranty.PageFor(_info.Manufacturer, _info.RealSerial);
    public bool HasWarrantyPage => WarrantyPage != null;

    public string? RestartTip
    {
        get => _restartTip;
        private set => Set(ref _restartTip, value);
    }

    public string? ReportStatus
    {
        get => _reportStatus;
        private set => Set(ref _reportStatus, value);
    }

    protected override Task OnFirstVisitAsync() => RefreshAsync();

    public Task RefreshAsync() => RunBusyAsync(async ct =>
    {
        Status = "Reading system details…";
        _info = await SystemInfoCollector.CollectAsync(ct);
        Rows = BuildRows(_info);
        RestartTip = _info.Uptime is { TotalDays: > 7 } up
            ? $"Windows hasn't fully restarted in {SystemInfoText.Uptime(up)}. \"Shut down\" doesn't count, because Fast Startup resumes where it left off. " +
              "A Restart clears out memory leaks and finishes updates, and often makes a sluggish PC feel new again."
            : null;
        Status = "";
        OnPropertiesChanged(nameof(Heading), nameof(Serial), nameof(WarrantyPage), nameof(HasWarrantyPage));
    });

    private static IReadOnlyList<InfoRow> BuildRows(SystemInfo i)
    {
        var rows = new List<InfoRow>
        {
            new("Windows", $"{i.WindowsName} {i.WindowsVersion}".Trim(), i.WindowsBuild is { } b ? $"Build {b}" : null),
            new("Processor", i.Processor ?? "Unknown", i.Cores is int c ? $"{c} cores, {i.Threads} threads" : null),
            new("Memory (RAM)", SystemInfoText.Memory(i),
                i.MemoryUsedFraction is double used ? $"{used:P0} in use right now" : null),
        };
        foreach (var g in i.Graphics)
            rows.Add(new("Graphics", g.Name, g.MemoryBytes is long m && m >= 256L << 20 ? $"{ByteSize.Format(m)} video memory" : null));
        foreach (var d in i.Drives)
            rows.Add(new("Drive", d.Model, ByteSize.Format(d.SizeBytes)));
        if (i.Uptime is { } up) rows.Add(new("Running since last restart", SystemInfoText.Uptime(up)));
        if (i.WindowsInstalled is { } installed) rows.Add(new("Windows installed", installed.ToLocalTime().ToString("d MMMM yyyy")));
        if (i.BiosVersion != null) rows.Add(new("BIOS / firmware", i.BiosVersion, i.BiosDate is { } d ? d.ToString("MMMM yyyy") : null));
        return rows;
    }

    private void Copy()
    {
        var text = new StringBuilder(Heading).AppendLine();
        if (Serial != null) text.AppendLine("Serial number: " + Serial);
        foreach (var r in Rows) text.AppendLine($"{r.Label}: {r.Value}" + (r.Hint is null ? "" : $" ({r.Hint})"));
        Clipboard.SetText(text.ToString());
        Status = "Copied to the clipboard.";
    }

    private void OpenWarranty()
    {
        if (Serial != null) Clipboard.SetText(Serial);
        SystemTools.Open(WarrantyPage!);
        Status = Serial != null ? "Your serial number is copied. Paste it into the warranty page if it asks." : "";
    }

    private Task CreateReportAsync() => RunBusyAsync(async ct =>
    {
        ReportStatus = null;
        string file = await Services.Reports.CreateAsync(new Progress<string>(s => Status = s), ct);
        Status = "";
        ReportStatus = $"Saved to {Path.GetFileName(Path.GetDirectoryName(file))}\\{Path.GetFileName(file)} in your Documents. It opens in your browser; press Ctrl+P to save it as a PDF.";
        SystemTools.Open(file);
    });
}
