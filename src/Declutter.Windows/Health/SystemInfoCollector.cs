using Declutter.Core.Health;
using Declutter.Windows.Security;
using Microsoft.Win32;

namespace Declutter.Windows.Health;

/// <summary>Reads what's inside this PC from WMI and the registry. Read-only and fast.</summary>
public static class SystemInfoCollector
{
    public static Task<SystemInfo> CollectAsync(CancellationToken ct) => Task.Run(() =>
    {
        var cs = Wmi.Query(@"root\cimv2", "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem")?.FirstOrDefault();
        var os = Wmi.Query(@"root\cimv2", "SELECT Caption, Version, BuildNumber, InstallDate, FreePhysicalMemory FROM Win32_OperatingSystem")?.FirstOrDefault();
        var bios = Wmi.Query(@"root\cimv2", "SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS")?.FirstOrDefault();
        var cpus = Wmi.Query(@"root\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor") ?? [];
        var modules = Wmi.Query(@"root\cimv2", "SELECT Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory") ?? [];
        var arrays = Wmi.Query(@"root\cimv2", "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray") ?? [];
        var gpus = Wmi.Query(@"root\cimv2", "SELECT Name, AdapterRAM FROM Win32_VideoController") ?? [];
        var disks = Wmi.Query(@"root\cimv2", "SELECT Model, Size FROM Win32_DiskDrive") ?? [];
        ct.ThrowIfCancellationRequested();

        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string? displayVersion = cv?.GetValue("DisplayVersion") as string;
        string? build = os?.Str("BuildNumber");
        if (build != null && cv?.GetValue("UBR") is int ubr) build += "." + ubr;

        int speed = modules.Select(m => (int)(m.Long("ConfiguredClockSpeed") ?? m.Long("Speed") ?? 0)).DefaultIfEmpty(0).Max();
        int slots = (int)arrays.Sum(a => a.Long("MemoryDevices") ?? 0);

        return new SystemInfo
        {
            Manufacturer = Clean(cs?.Str("Manufacturer")),
            Model = Clean(cs?.Str("Model")),
            SerialNumber = Clean(bios?.Str("SerialNumber")),
            BiosVersion = Clean(bios?.Str("SMBIOSBIOSVersion")),
            BiosDate = bios?.Date("ReleaseDate"),
            WindowsName = Clean(os?.Str("Caption"))?.Replace("Microsoft ", "", StringComparison.Ordinal),
            WindowsVersion = displayVersion,
            WindowsBuild = build,
            WindowsInstalled = os?.Date("InstallDate"),
            Processor = Clean(cpus.FirstOrDefault()?.Str("Name")),
            Cores = cpus.Count > 0 ? (int)cpus.Sum(c => c.Long("NumberOfCores") ?? 0) : null,
            Threads = cpus.Count > 0 ? (int)cpus.Sum(c => c.Long("NumberOfLogicalProcessors") ?? 0) : null,
            MemoryBytes = modules.Count > 0 ? modules.Sum(m => m.Long("Capacity") ?? 0) : cs?.Long("TotalPhysicalMemory"),
            MemoryFreeBytes = os?.Long("FreePhysicalMemory") is long kb ? kb * 1024 : null,
            MemoryModules = modules.Count > 0 ? modules.Count : null,
            MemorySlots = slots > 0 ? slots : null,
            MemorySpeedMHz = speed > 0 ? speed : null,
            Graphics = gpus.Where(g => Clean(g.Str("Name")) != null)
                .Select(g => new GpuInfo(Clean(g.Str("Name"))!, g.Long("AdapterRAM") is long ram and > 0 ? ram : null))
                .ToList(),
            Drives = disks.Where(d => d.Long("Size") is > 0)
                .Select(d => new DriveSummary(Clean(d.Str("Model")) ?? "Drive", d.Long("Size")!.Value))
                .ToList(),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }, ct);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
