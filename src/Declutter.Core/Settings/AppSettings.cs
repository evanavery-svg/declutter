using System.Text.Json;
using System.Text.Json.Serialization;

namespace Declutter.Core.Settings;

/// <summary>Everything the user can change on the Settings page. Stored only on this PC.</summary>
public sealed record AppSettings
{
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; init; } = "System";

    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Keep a history of checks so trends (disk space, boot time, battery) can be shown.</summary>
    public bool KeepHistory { get; init; } = true;

    /// <summary>The Anthropic API key for "Explain this", encrypted for this Windows user (DPAPI), base64.</summary>
    public string? ProtectedApiKey { get; init; }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. A missing or damaged file gives defaults.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes to a temp file then swaps it in, so a crash mid-save can't leave a half-written file.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, Path, overwrite: true);
    }
}
