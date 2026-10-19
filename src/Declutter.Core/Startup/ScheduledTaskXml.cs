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
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static IReadOnlyList<ScheduledTaskInfo> ParseStartupTasks(string xml)
    {
        int start = xml.IndexOf("<Tasks", StringComparison.Ordinal);
        if (start < 0) return [];

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml[start..], LoadOptions.None);
        }
        catch (XmlException)
        {
            return [];
        }

        var results = new List<ScheduledTaskInfo>();
        string? pendingPath = null;
        foreach (var node in doc.Root!.Nodes())
        {
            if (node is XComment comment)
            {
                pendingPath = comment.Value.Trim();
                continue;
            }
            if (node is not XElement task || task.Name.LocalName != "Task") continue;

            string? path = Text(task.Element(Ns + "RegistrationInfo")?.Element(Ns + "URI")) ?? pendingPath;
            pendingPath = null;
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!path.StartsWith('\\')) path = "\\" + path;
            if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;

            bool runsAtStartup = task.Element(Ns + "Triggers")?.Elements()
                .Any(t => t.Name.LocalName is "LogonTrigger" or "BootTrigger" && Text(t.Element(Ns + "Enabled")) != "false") ?? false;
            if (!runsAtStartup) continue;

            bool enabled = Text(task.Element(Ns + "Settings")?.Element(Ns + "Enabled")) != "false";

            var exec = task.Element(Ns + "Actions")?.Element(Ns + "Exec");
            string? command = Text(exec?.Element(Ns + "Command"));
            string? args = Text(exec?.Element(Ns + "Arguments"));
            if (command != null && !command.StartsWith('"') && command.Contains(' ')) command = $"\"{command}\"";
            string? fullCommand = command == null ? null : string.IsNullOrEmpty(args) ? command : $"{command} {args}";

            string name = path[(path.LastIndexOf('\\') + 1)..];
            results.Add(new ScheduledTaskInfo(path, name, enabled, fullCommand, Text(task.Element(Ns + "RegistrationInfo")?.Element(Ns + "Author"))));
        }
        return results;
    }

    private static string? Text(XElement? e) => string.IsNullOrWhiteSpace(e?.Value) ? null : e.Value.Trim();
}
