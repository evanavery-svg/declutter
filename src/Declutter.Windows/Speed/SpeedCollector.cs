using System.Diagnostics;
using System.Text.Json;
using Declutter.Core.Speed;
using Declutter.Core.Util;
using Declutter.Windows.Security;

namespace Declutter.Windows.Speed;

/// <summary>Start-up timings, third-party services, and reversible service changes.</summary>
public sealed class SpeedCollector(string dataFolder, string windowsDir, IActivityLog log, bool isElevated)
{
    private string ChangesFile => Path.Combine(dataFolder, "services-changed.json");

    /// <summary>Null when the log can't be read (it needs administrator rights).</summary>
    public async Task<BootSummary?> ReadBootsAsync(CancellationToken ct)
    {
        if (!isElevated) return null;
        try
        {
            var result = await ProcessRunner.RunDetectingEncodingAsync(ProcessRunner.SystemTool("wevtutil.exe"),
                "qe Microsoft-Windows-Diagnostics-Performance/Operational /q:\"*[System[(EventID>=100 and EventID<=110)]]\" /c:400 /rd:true /f:xml", ct);
            if (result.ExitCode != 0) return null;
            var (boots, slowdowns) = BootEvents.Parse(result.Output);
            return BootEvents.Summarize(boots, slowdowns, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Services installed by apps that start automatically, plus any Declutter has switched to Manual.</summary>
    public Task<IReadOnlyList<ServiceInfo>> ReadServicesAsync(CancellationToken ct) => Task.Run<IReadOnlyList<ServiceInfo>>(() =>
    {
        var changed = LoadChanges();
        var rows = Wmi.Query(@"root\cimv2", "SELECT Name, DisplayName, StartMode, State, PathName, Description, DelayedAutoStart FROM Win32_Service") ?? [];
        var services = new List<ServiceInfo>();
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            string? name = r.Str("Name"), path = r.Str("PathName");
            if (name == null || path == null) continue;
            string exe = CommandLine.ExtractExecutable(path);
            if (!Path.IsPathFullyQualified(exe) || PathUtil.IsSameOrUnder(exe, windowsDir)) continue; // Windows' own services

            string mode = r.Str("StartMode") ?? "";
            if (!mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) && !changed.ContainsKey(name)) continue;

            string? company = null;
            try
            {
                if (File.Exists(exe)) company = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            services.Add(new ServiceInfo
            {
                Name = name,
                DisplayName = r.Str("DisplayName") ?? name,
                StartMode = mode,
                DelayedAutoStart = r.Bool("DelayedAutoStart") == true,
                Running = string.Equals(r.Str("State"), "Running", StringComparison.OrdinalIgnoreCase),
                Path = exe,
                Company = string.IsNullOrWhiteSpace(company) ? null : company,
                Description = r.Str("Description"),
            });
        }
        return services.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }, ct);

    public bool WasChangedByDeclutter(string serviceName) => LoadChanges().ContainsKey(serviceName);

    /// <summary>Sets a service to start only when needed. Remembers how it was so it can be undone.</summary>
    public async Task SetManualAsync(ServiceInfo service, CancellationToken ct)
    {
        RequireAdmin();
        string original = service.DelayedAutoStart ? "delayed-auto" : "auto";
        await RunScAsync($"config \"{service.Name}\" start= demand", ct);
        var changes = LoadChanges();
        changes.TryAdd(service.Name, original);
        SaveChanges(changes);
        log.Write("Speed", $"Set service \"{service.DisplayName}\" ({service.Name}) to Manual (was {original})");
    }

    /// <summary>Puts a service back the way it was before Declutter changed it.</summary>
    public async Task UndoAsync(ServiceInfo service, CancellationToken ct)
    {
        RequireAdmin();
        var changes = LoadChanges();
        string original = changes.GetValueOrDefault(service.Name, "auto");
        await RunScAsync($"config \"{service.Name}\" start= {original}", ct);
        changes.Remove(service.Name);
        SaveChanges(changes);
        log.Write("Speed", $"Restored service \"{service.DisplayName}\" ({service.Name}) to {original}");
    }

    private void RequireAdmin()
    {
        if (!isElevated) throw new UnauthorizedAccessException("Changing services needs administrator rights.");
    }

    private static async Task RunScAsync(string args, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(ProcessRunner.SystemTool("sc.exe"), args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Windows refused the change: " + (result.Output + result.Error).Trim());
    }

    private Dictionary<string, string> LoadChanges()
    {
        try
        {
            return File.Exists(ChangesFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ChangesFile)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void SaveChanges(Dictionary<string, string> changes)
    {
        Directory.CreateDirectory(dataFolder);
        File.WriteAllText(ChangesFile, JsonSerializer.Serialize(changes, new JsonSerializerOptions { WriteIndented = true }));
    }
}
