using System.Diagnostics;
using System.Globalization;
using Declutter.Core.Apps;
using Declutter.Core.Protection;
using Declutter.Core.Safety;
using Declutter.Core.Util;
using Declutter.Windows.Security;
using Microsoft.Win32;

namespace Declutter.Windows.Protection;

/// <summary>Gathers backup, device-security and Windows Update facts. Read-only.</summary>
public sealed class ProtectionCollector(KnownFolders folders, bool isElevated)
{
    public Task<BackupEvidence> ReadBackupAsync(IReadOnlyList<InstalledApp> installedApps, CancellationToken ct) => Task.Run(() =>
    {
        var personal = new Dictionary<string, string?> { ["Desktop"] = folders.Desktop, ["Documents"] = folders.Documents, ["Pictures"] = folders.Pictures };
        var inOneDrive = personal.Where(kv => kv.Value != null)
            .ToDictionary(kv => kv.Key, kv => folders.OneDriveFolders.Any(o => PathUtil.IsSameOrUnder(kv.Value!, o)));

        DateTime? fileHistory = null;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\FileHistory"))
        {
            if (key?.GetValue("ProtectedUpToTime") is long ft && ft > 0)
            {
                try { fileHistory = DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { }
            }
        }
        bool fileHistoryConfigured = File.Exists(Path.Combine(folders.LocalAppData, "Microsoft", "Windows", "FileHistory", "Configuration", "Config1.xml"));

        var backupApps = installedApps
            .Where(a => BackupCheck.KnownBackupApps.Any(k => a.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        return new BackupEvidence
        {
            FoldersInOneDrive = inOneDrive,
            OneDriveRunning = IsRunning("OneDrive"),
            FileHistoryLastBackupUtc = fileHistory,
            FileHistoryConfigured = fileHistoryConfigured || fileHistory != null,
            BackupApps = backupApps,
        };
    }, ct);

    public Task<DeviceSecurityEvidence> ReadDeviceSecurityAsync(CancellationToken ct) => Task.Run(() =>
    {
        bool? encrypted = null;
        if (isElevated)
        {
            string drive = folders.SystemDrive.TrimEnd('\\');
            var volume = Wmi.Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{drive}'")?.FirstOrDefault();
            encrypted = volume?.Long("ProtectionStatus") switch { 1 => true, 0 => false, _ => null };
        }

        bool? secureBoot = null;
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
        {
            if (key?.GetValue("UEFISecureBootEnabled") is int sb) secureBoot = sb == 1;
        }

        bool? tpm = null;
        if (isElevated)
        {
            var rows = Wmi.Query(@"root\CIMV2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue FROM Win32_Tpm");
            if (rows != null) tpm = rows.Any(r => r.Bool("IsEnabled_InitialValue") == true);
        }

        // Laptop-style chassis types (portable, notebook, tablet, convertible…), or a battery.
        var chassis = Wmi.Query(@"root\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure")?.FirstOrDefault();
        bool laptop = chassis?.TryGetValue("ChassisTypes", out var types) == true && types is ushort[] t &&
                      t.Any(c => c is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32);
        laptop |= Wmi.Query(@"root\cimv2", "SELECT Name FROM Win32_Battery")?.Count > 0;

        return new DeviceSecurityEvidence { DriveEncrypted = encrypted, SecureBoot = secureBoot, TpmPresent = tpm, IsLaptop = laptop };
    }, ct);

    public async Task<UpdateEvidence> ReadUpdatesAsync(string? windowsName, string? windowsVersion, CancellationToken ct)
    {
        // Windows Update's own history (dates are UTC). Operation 1 = install, ResultCode 2 = succeeded.
        const string script =
            "$s = New-Object -ComObject Microsoft.Update.Session; $h = $s.CreateUpdateSearcher(); $n = $h.GetTotalHistoryCount();" +
            "$r = if ($n -gt 0) { $h.QueryHistory(0, [Math]::Min($n, 100)) | Where-Object { $_.ResultCode -eq 2 -and $_.Operation -eq 1 } |" +
            " Sort-Object Date -Descending | Select-Object -First 1 | ForEach-Object { [pscustomobject]@{ D = $_.Date.ToString('yyyy-MM-ddTHH:mm:ss'); T = [string]$_.Title } } }";
        DateTime? last = null;
        using (var doc = await PowerShellJson.RunAsync(script, null, TimeSpan.FromSeconds(60), ct))
        {
            if (doc?.RootElement.EnumerateArray().FirstOrDefault() is { ValueKind: System.Text.Json.JsonValueKind.Object } e &&
                DateTime.TryParse(e.Str("D"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                last = d;
        }

        bool pending =
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is { } a && Dispose(a) ||
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is { } b && Dispose(b);

        return new UpdateEvidence
        {
            LastSuccessfulUpdateUtc = last,
            RestartPending = pending,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            WindowsName = windowsName,
            WindowsVersion = windowsVersion,
        };
    }

    private static bool Dispose(RegistryKey key)
    {
        key.Dispose();
        return true;
    }

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }
}
