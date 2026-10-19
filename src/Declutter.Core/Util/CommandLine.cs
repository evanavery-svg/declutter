namespace Declutter.Core.Util;

public static class CommandLine
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat", ".cmd", ".lnk", ".scr"];

    /// <summary>
    /// Splits a Windows command line into program and arguments, handling the common unquoted form
    /// "C:\Program Files\App\app.exe --flag" that registry entries often use.
    /// </summary>
    public static (string FileName, string Arguments) Split(string commandLine)
    {
        string cmd = commandLine.Trim();
        if (cmd.Length == 0) return ("", "");

        if (cmd[0] == '"')
        {
            int close = cmd.IndexOf('"', 1);
            if (close < 0) return (cmd.Trim('"'), "");
            return (cmd[1..close], cmd[(close + 1)..].Trim());
        }

        // Unquoted: the program ends at the earliest executable extension followed by a space or the end.
        int bestEnd = -1;
        foreach (var ext in ExecutableExtensions)
        {
            int start = 0;
            while (true)
            {
                int i = cmd.IndexOf(ext, start, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                int end = i + ext.Length;
                if (end == cmd.Length || cmd[end] == ' ')
                {
                    if (bestEnd < 0 || end < bestEnd) bestEnd = end;
                    break;
                }
                start = end;
            }
        }
        if (bestEnd > 0) return (cmd[..bestEnd], cmd[bestEnd..].Trim());

        int space = cmd.IndexOf(' ');
        return space < 0 ? (cmd, "") : (cmd[..space], cmd[(space + 1)..].Trim());
    }

    /// <summary>The program a command line runs, with %VARIABLES% expanded.</summary>
    public static string ExtractExecutable(string commandLine) =>
        Split(Environment.ExpandEnvironmentVariables(commandLine)).FileName;
}
