using System.Text;
using System.Text.Json;

namespace Declutter.Windows;

/// <summary>
/// Runs a Windows PowerShell script and returns what it left in <c>$r</c>, as JSON. The script is passed
/// encoded (no quoting problems) and the JSON goes through a temp file as UTF-8 (no console code-page
/// problems). Any failure returns null: callers treat missing data as "unknown", never as an error.
/// </summary>
internal static class PowerShellJson
{
    public static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <param name="script">PowerShell that assigns its result to <c>$r</c>. If <paramref name="inputLines"/> is given, <c>$in</c> holds the path of a UTF-8 file containing them.</param>
    public static async Task<JsonDocument?> RunAsync(string script, IReadOnlyList<string>? inputLines, TimeSpan timeout, CancellationToken ct)
    {
        string input = Path.Combine(Path.GetTempPath(), $"declutter-ps-{Guid.NewGuid():N}.txt");
        string output = Path.ChangeExtension(input, ".json");
        try
        {
            if (inputLines != null) await File.WriteAllLinesAsync(input, inputLines, new UTF8Encoding(false), ct);
            string full =
                "$ErrorActionPreference='SilentlyContinue';" +
                $"$in = '{input.Replace("'", "''")}';" +
                script +
                $";ConvertTo-Json -InputObject @($r) -Compress -Depth 4 | Set-Content -LiteralPath '{output.Replace("'", "''")}' -Encoding UTF8";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            await ProcessRunner.RunAsync(PowerShellPath, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", limit.Token);

            if (!File.Exists(output)) return null;
            var doc = JsonDocument.Parse(await File.ReadAllTextAsync(output, ct));
            if (doc.RootElement.ValueKind == JsonValueKind.Array) return doc;
            doc.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    public static string? Str(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long? Long(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
