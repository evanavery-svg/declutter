using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Declutter.Core.Startup;

public sealed record ScheduledTaskInfo(string Path, string Name, bool Enabled, string? Command, string? Author);

/// <summary>
/// Parses the output of "schtasks /query /xml": a &lt;Tasks&gt; element holding one &lt;Task&gt; per scheduled task,
/// each preceded by a comment with its path. Only tasks that run when you sign in or when the PC starts
/// are returned, and Windows' own tasks (under \Microsoft\) are left out.
/// </summary>
public static class ScheduledTaskXml
{
    // Each task, optionally preceded by the "<!-- \Path\Name -->" comment schtasks writes before it.
    private static readonly Regex TaskBlock = new(@"(?:<!--(?<path>(?:(?!-->).)*)-->\s*)?(?<task><Task\b.*?</Task>)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static IReadOnlyList<ScheduledTaskInfo> ParseStartupTasks(string xml)
    {
        // Tasks are parsed one at a time, so a single malformed entry can't hide all the others.
        var results = new List<ScheduledTaskInfo>();
        foreach (Match match in TaskBlock.Matches(xml))
        {
            XElement task;
            try
            {
                task = XElement.Parse(match.Groups["task"].Value);
            }
            catch (XmlException)
            {
                continue;
            }
            if (Parse(task, match.Groups["path"].Success ? match.Groups["path"].Value.Trim() : null) is { } info)
                results.Add(info);
        }
        return results;
    }

    private static ScheduledTaskInfo? Parse(XElement task, string? commentPath)
    {
        XNamespace ns = task.Name.Namespace;
        string? path = Text(task.Element(ns + "RegistrationInfo")?.Element(ns + "URI")) ?? commentPath;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.StartsWith('\\')) path = "\\" + path;
        if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) return null;

        bool runsAtStartup = task.Element(ns + "Triggers")?.Elements()
            .Any(t => t.Name.LocalName is "LogonTrigger" or "BootTrigger" && Text(t.Element(ns + "Enabled")) != "false") ?? false;
        if (!runsAtStartup) return null;

        bool enabled = Text(task.Element(ns + "Settings")?.Element(ns + "Enabled")) != "false";

        var exec = task.Element(ns + "Actions")?.Element(ns + "Exec");
        string? command = Text(exec?.Element(ns + "Command"));
        string? args = Text(exec?.Element(ns + "Arguments"));
        if (command != null && !command.StartsWith('"') && command.Contains(' ')) command = $"\"{command}\"";
        string? fullCommand = command == null ? null : string.IsNullOrEmpty(args) ? command : $"{command} {args}";

        string name = path[(path.LastIndexOf('\\') + 1)..];
        return new ScheduledTaskInfo(path, name, enabled, fullCommand, Text(task.Element(ns + "RegistrationInfo")?.Element(ns + "Author")));
    }

    private static string? Text(XElement? e) => string.IsNullOrWhiteSpace(e?.Value) ? null : e.Value.Trim();
}
