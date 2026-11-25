using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using Declutter.App.Mvvm;
using Declutter.Core.Updates;
using Declutter.Windows;

namespace Declutter.App.ViewModels;

/// <summary>
/// Checks GitHub for a newer Declutter (at most once a day, or when asked) and installs it on request:
/// download → verify → swap the .exe → restart.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly AppServices _services;
    private readonly Updater _updater = new(Http);
    private ReleaseInfo? _latest;
    private bool _isWorking;
    private double _progress;
    private string? _status;

    public UpdateViewModel(AppServices services)
    {
        _services = services;
        CurrentVersion = typeof(UpdateViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);
        CheckCommand = new AsyncCommand(() => CheckAsync(userAsked: true), () => !IsWorking);
        InstallCommand = new AsyncCommand(InstallAsync, () => !IsWorking && IsAvailable);
    }

    public Version CurrentVersion { get; }
    public AsyncCommand CheckCommand { get; }
    public AsyncCommand InstallCommand { get; }

    public bool IsAvailable => _latest != null && Releases.IsNewer(_latest.Version, CurrentVersion);
    public string AvailableText => _latest == null ? "" : $"Version {_latest.Version.ToString(3)} is available";

    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (Set(ref _isWorking, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private static string StampFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Declutter", "last-update-check.txt");

    /// <summary>On startup: removes the previous version left by an update, then checks at most once a day.</summary>
    public async Task StartupAsync()
    {
        if (Environment.ProcessPath is { } exe && IsSelfUpdatable(exe))
        {
            await Task.Delay(TimeSpan.FromSeconds(3)); // let the previous version finish exiting
            SelfReplace.CleanUp(exe);
        }

        try
        {
            if (File.Exists(StampFile) &&
                long.TryParse(await File.ReadAllTextAsync(StampFile), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) &&
                DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromDays(1))
                return;
        }
        catch (IOException)
        {
        }

        await CheckAsync(userAsked: false);
    }

    /// <summary>Asks GitHub for the latest release. Quiet unless <paramref name="userAsked"/>.</summary>
    public async Task CheckAsync(bool userAsked)
    {
        IsWorking = true;
        Status = userAsked ? "Checking for updates…" : null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _latest = await _updater.GetLatestAsync(timeout.Token);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StampFile)!);
                await File.WriteAllTextAsync(StampFile, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
            }

            if (userAsked)
                Status = _latest == null ? "Couldn't reach GitHub to check for updates."
                    : IsAvailable ? null
                    : $"You're up to date (version {CurrentVersion.ToString(3)}).";
            if (IsAvailable) _services.Log.Write("Update", $"{_latest!.Tag} is available (running {CurrentVersion.ToString(3)})");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (userAsked) Status = "Couldn't reach GitHub to check for updates. Are you online?";
        }
        finally
        {
            IsWorking = false;
            OnPropertiesChanged(nameof(IsAvailable), nameof(AvailableText));
        }
    }

    private async Task InstallAsync()
    {
        if (_latest == null) return;
        string? exe = Environment.ProcessPath;
        var asset = Releases.PickAsset(_latest, RuntimeInformation.ProcessArchitecture);

        string notes = string.IsNullOrWhiteSpace(_latest.Notes) ? "" : "\n\n" + Truncate(_latest.Notes.Trim(), 700);
        if (exe == null || asset == null || !IsSelfUpdatable(exe))
        {
            if (_services.Dialogs.Confirm($"Version {_latest.Version.ToString(3)} is available",
                    "Declutter can't update itself from here, but you can download the new version from GitHub." + notes, "Open download page"))
                SystemTools.Open(_latest.PageUrl);
            return;
        }

        if (!_services.Dialogs.Confirm($"Update to version {_latest.Version.ToString(3)}?",
                "Declutter will download the new version from GitHub, check its fingerprint, replace itself and restart. " +
                "Nothing else on your PC changes." + notes, "Update and restart"))
            return;

        IsWorking = true;
        Progress = 0;
        string download = SelfReplace.DownloadPathFor(exe);
        try
        {
            Status = "Downloading…";
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Status = $"Downloading… {p:P0}";
            });
            await _updater.DownloadAsync(asset, download, progress, CancellationToken.None);

            Status = "Installing…";
            SelfReplace.Swap(exe, download);
            _services.Log.Write("Update", $"Updated {CurrentVersion.ToString(3)} → {_latest.Version.ToString(3)}; restarting");

            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _services.Log.Write("Update", "Update failed: " + ex.Message);
            Status = null;
            string reason = ex is UnauthorizedAccessException or IOException
                ? "Windows didn't allow Declutter to replace its own file (it may be in a protected folder)."
                : ex.Message;
            if (_services.Dialogs.Confirm("The update didn't finish", reason + " Nothing was changed. You can download the new version yourself instead.", "Open download page"))
                SystemTools.Open(_latest.PageUrl);
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Only the published single-file Declutter.exe can swap itself (not "dotnet run" builds).</summary>
    private static bool IsSelfUpdatable(string exe) =>
        Path.GetFileName(exe).StartsWith("Declutter", StringComparison.OrdinalIgnoreCase) &&
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !File.Exists(Path.ChangeExtension(exe, ".dll")); // a framework-dependent dev build has Declutter.dll beside it

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
