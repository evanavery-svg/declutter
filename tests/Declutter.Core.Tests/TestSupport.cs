using Declutter.Core.Junk;
using Declutter.Core.Safety;
using Declutter.Core.Util;

namespace Declutter.Core.Tests;

/// <summary>A throwaway Windows-shaped folder tree (C:\Windows, C:\Users\user\AppData, …) under the temp dir.</summary>
public sealed class FakeMachine : IDisposable
{
    public FakeMachine()
    {
        Root = Path.Combine(Path.GetTempPath(), "declutter-tests-" + Guid.NewGuid().ToString("N"));
        Folders = KnownFolders.CreateUnder(Root);
        foreach (var dir in new[] { Folders.WindowsDir, Folders.ProgramFiles, Folders.ProgramData, Folders.LocalAppData,
                     Folders.RoamingAppData, Folders.TempDir, Folders.Documents!, Folders.Downloads!, Folders.Desktop! })
            Directory.CreateDirectory(dir);
        Guard = new SafetyGuard(Folders);
    }

    public string Root { get; }
    public KnownFolders Folders { get; }
    public SafetyGuard Guard { get; }

    public string WriteFile(string path, int bytes = 100, byte fill = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, bytes).ToArray());
        return path;
    }

    public JunkContext Context(DateTime? now = null, bool elevated = true) =>
        new(Guard, elevated, new FixedClock(now ?? DateTime.UtcNow), NullActivityLog.Instance);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FixedClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; } = utcNow;
}

public sealed class RecordingLog : IActivityLog
{
    public List<string> Lines { get; } = [];
    public void Write(string area, string message) => Lines.Add($"[{area}] {message}");
}
