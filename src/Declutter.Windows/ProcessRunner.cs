using System.Diagnostics;
using System.Text;

namespace Declutter.Windows;

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal static class ProcessRunner
{
    /// <summary>Runs a console tool hidden and captures its output.</summary>
    public static async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct = default, Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
        CleanEnvironment(psi);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    /// <summary>Runs a tool and decodes its output whatever encoding it wrote (schtasks may use UTF-16).</summary>
    public static async Task<ProcessResult> RunDetectingEncodingAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        CleanEnvironment(psi);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {fileName}.");
        using var buffer = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await copy;
        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, Core.Util.TextDecoding.Decode(buffer.ToArray()), await error);
    }

    /// <summary>
    /// If Declutter was started from PowerShell 7, its PSModulePath is inherited and makes Windows
    /// PowerShell 5.1 load incompatible modules (Get-AuthenticodeSignature, Checkpoint-Computer fail).
    /// Without the variable, Windows PowerShell uses its own defaults.
    /// </summary>
    private static void CleanEnvironment(ProcessStartInfo psi)
    {
        if (Path.GetFileName(psi.FileName).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            psi.Environment.Remove("PSModulePath");
    }

    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);
}
