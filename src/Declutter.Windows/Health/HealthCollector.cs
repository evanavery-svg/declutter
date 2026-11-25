using Declutter.Core.Health;
using Declutter.Windows.Security;

namespace Declutter.Windows.Health;

public sealed record BatteryNow(int? ChargePercent, bool PluggedIn);

public sealed class HealthSnapshot
{
    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
    public IReadOnlyList<BatteryInfo> Batteries { get; init; } = [];

    /// <summary>Null on PCs without a battery (desktops).</summary>
    public BatteryNow? Battery { get; init; }

    /// <summary>Wear, temperature and error counters are only readable with administrator rights.</summary>
    public bool CountersNeedAdmin { get; init; }
}

/// <summary>Reads drive and battery health from Windows' own tools. Read-only.</summary>
public sealed class HealthCollector(bool isElevated)
{
    // Get-PhysicalDisk gives Windows' verdict (it includes the drive's own SMART failure prediction);
    // Get-StorageReliabilityCounter adds wear, temperature, hours and error counts (admin only).
    private const string DiskScript =
        "$sys = $null; try { $sys = (Get-Partition -DriveLetter ($env:SystemDrive.Substring(0,1)) -ErrorAction Stop).DiskNumber } catch {};" +
        "$r = foreach ($d in Get-PhysicalDisk) {" +
        "  $c = $null; try { $c = $d | Get-StorageReliabilityCounter -ErrorAction Stop } catch {};" +
        "  [pscustomobject]@{" +
        "    Name = [string]$d.FriendlyName; Media = [string]$d.MediaType; Bus = [string]$d.BusType; Size = $d.Size;" +
        "    Health = [string]$d.HealthStatus; Wear = $c.Wear; Temp = $c.Temperature; TempMax = $c.TemperatureMax;" +
        "    Hours = $c.PowerOnHours; ReadErr = $c.ReadErrorsUncorrected; WriteErr = $c.WriteErrorsUncorrected;" +
        "    System = ($null -ne $sys -and [string]$d.DeviceId -eq [string]$sys)" +
        "  }" +
        "}";

    public async Task<HealthSnapshot> CollectAsync(CancellationToken ct)
    {
        var disks = ReadDisksAsync(ct);
        var batteries = ReadBatteryReportAsync(ct);
        var now = Task.Run(ReadBatteryNow, ct);
        await Task.WhenAll(disks, batteries, now);

        return new HealthSnapshot
        {
            Disks = disks.Result,
            Batteries = now.Result == null ? [] : batteries.Result,
            Battery = now.Result,
            CountersNeedAdmin = !isElevated,
        };
    }

    private static async Task<IReadOnlyList<DiskInfo>> ReadDisksAsync(CancellationToken ct)
    {
        using var doc = await PowerShellJson.RunAsync(DiskScript, null, TimeSpan.FromSeconds(60), ct);
        if (doc == null) return [];

        return doc.RootElement.EnumerateArray()
            .Select(e => new DiskInfo
            {
                Name = e.Str("Name") is { Length: > 0 } n ? n.Trim() : "Drive",
                MediaType = e.Str("Media") ?? "Unspecified",
                BusType = e.Str("Bus") ?? "",
                SizeBytes = e.Long("Size") ?? 0,
                HealthStatus = e.Str("Health") ?? "",
                WearPercent = e.Long("Wear") is long w and >= 0 and <= 255 ? (int)w : null,
                TemperatureC = e.Long("Temp") is long t and > 0 and < 150 ? (int)t : null,
                TemperatureMaxC = e.Long("TempMax") is long tm and > 0 and < 150 ? (int)tm : null,
                PowerOnHours = e.Long("Hours") is long h and > 0 ? h : null,
                ReadErrorsUncorrected = e.Long("ReadErr"),
                WriteErrorsUncorrected = e.Long("WriteErr"),
                IsSystemDisk = e.Bool("System") == true,
            })
            .OrderByDescending(d => d.IsSystemDisk)
            .ToList();
    }

    private static async Task<IReadOnlyList<BatteryInfo>> ReadBatteryReportAsync(CancellationToken ct)
    {
        string file = Path.Combine(Path.GetTempPath(), $"declutter-battery-{Guid.NewGuid():N}.xml");
        try
        {
            await ProcessRunner.RunAsync(ProcessRunner.SystemTool("powercfg.exe"), $"/batteryreport /xml /output \"{file}\"", ct);
            return File.Exists(file) ? BatteryReportParser.Parse(await File.ReadAllTextAsync(file, ct)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static BatteryNow? ReadBatteryNow()
    {
        var row = Wmi.Query(@"root\cimv2", "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery")?.FirstOrDefault();
        if (row == null) return null;
        int? charge = row.Long("EstimatedChargeRemaining") is long c and >= 0 and <= 100 ? (int)c : null;
        // 1 = discharging, 4/5 = low/critical on battery; everything else means mains power.
        bool plugged = row.Long("BatteryStatus") is not (1 or 4 or 5);
        return new BatteryNow(charge, plugged);
    }

    /// <summary>Creates Windows' detailed battery report (an HTML page) and returns its path.</summary>
    public static async Task<string?> CreateBatteryReportPageAsync(CancellationToken ct)
    {
        string file = Path.Combine(Path.GetTempPath(), "Declutter battery report.html");
        try
        {
            var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("powercfg.exe"), $"/batteryreport /output \"{file}\"", ct);
            return result.ExitCode == 0 && File.Exists(file) ? file : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
