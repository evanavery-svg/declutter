using System.Globalization;

namespace Declutter.Core.Util;

/// <summary>Records everything the app changes, so the user can always see what happened.</summary>
public interface IActivityLog
{
    void Write(string area, string message);
}

public sealed class NullActivityLog : IActivityLog
{
    public static readonly NullActivityLog Instance = new();
    public void Write(string area, string message) { }
}

/// <summary>Appends to one log file per day, e.g. %LOCALAPPDATA%\Declutter\Logs\2026-10-19.log.</summary>
public sealed class FileActivityLog : IActivityLog
{
    private readonly object _gate = new();
    private readonly IClock _clock;

    public FileActivityLog(string directory, IClock? clock = null)
    {
        Directory = directory;
        _clock = clock ?? SystemClock.Instance;
    }

    public string Directory { get; }

    public string CurrentFile =>
        Path.Combine(Directory, _clock.UtcNow.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    public void Write(string area, string message)
    {
        string line = $"{_clock.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm:ss}  [{area}]  {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(CurrentFile, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never break a cleanup.
            }
        }
    }
}
