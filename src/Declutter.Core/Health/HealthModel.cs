namespace Declutter.Core.Health;

public enum HealthGrade
{
    Unknown,
    Good,
    Fair,
    Poor,
}

public enum NoteKind
{
    Info,
    Good,
    Warning,
    Critical,
}

public sealed record HealthNote(NoteKind Kind, string Text);

/// <summary>A verdict with a one-line summary and the reasons behind it.</summary>
public sealed record HealthAssessment(HealthGrade Grade, string Summary, IReadOnlyList<HealthNote> Notes);

/// <summary>One physical drive, as reported by Windows' storage subsystem (Get-PhysicalDisk).</summary>
public sealed record DiskInfo
{
    public required string Name { get; init; }

    /// <summary>"SSD", "HDD", "SCM" or "Unspecified".</summary>
    public string MediaType { get; init; } = "Unspecified";

    /// <summary>"NVMe", "SATA", "USB", "SAS", "RAID"…</summary>
    public string BusType { get; init; } = "";

    public long SizeBytes { get; init; }

    /// <summary>Windows' own verdict: "Healthy", "Warning" or "Unhealthy" (includes SMART failure prediction).</summary>
    public string HealthStatus { get; init; } = "";

    /// <summary>Share of the drive's rated write endurance already used (SSDs only), 0–100+.</summary>
    public int? WearPercent { get; init; }

    public int? TemperatureC { get; init; }
    public int? TemperatureMaxC { get; init; }
    public long? PowerOnHours { get; init; }
    public long? ReadErrorsUncorrected { get; init; }
    public long? WriteErrorsUncorrected { get; init; }

    /// <summary>True for the drive Windows itself runs from.</summary>
    public bool IsSystemDisk { get; init; }

    public bool IsSsd => MediaType.Equals("SSD", StringComparison.OrdinalIgnoreCase) || BusType.Equals("NVMe", StringComparison.OrdinalIgnoreCase);
    public bool IsHdd => MediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One battery, from Windows' battery report (powercfg /batteryreport).</summary>
public sealed record BatteryInfo
{
    public required string Name { get; init; }
    public string? Manufacturer { get; init; }
    public string? Chemistry { get; init; }

    /// <summary>Capacity when new, in mWh.</summary>
    public long DesignCapacityMWh { get; init; }

    /// <summary>What a full charge holds today, in mWh.</summary>
    public long FullChargeCapacityMWh { get; init; }

    public int? CycleCount { get; init; }

    /// <summary>Full charge today as a share of when new, capped at 100.</summary>
    public double? HealthPercent => DesignCapacityMWh > 0 && FullChargeCapacityMWh > 0
        ? Math.Min(100, FullChargeCapacityMWh * 100.0 / DesignCapacityMWh)
        : null;
}
