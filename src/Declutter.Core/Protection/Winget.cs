namespace Declutter.Core.Protection;

public sealed record AppUpdate(string Name, string Id, string Version, string Available, string Source);

/// <summary>
/// Reads the table printed by "winget upgrade". Column headings are translated into the PC's language, so
/// columns are located by position (from the heading line above the dashes), never by name.
/// </summary>
public static class Winget
{
    public static IReadOnlyList<AppUpdate> ParseUpgradeTable(string output)
    {
        // Progress spinners overwrite themselves with carriage returns; keep what's left after the last one.
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l[(l.LastIndexOf('\r') + 1)..].TrimEnd()).ToList();
        var updates = new List<AppUpdate>();

        for (int i = 1; i < lines.Count; i++)
        {
            if (!IsDashes(lines[i])) continue;
            string header = lines[i - 1];
            var starts = ColumnStarts(header);
            if (starts.Count < 5) continue;

            for (int r = i + 1; r < lines.Count; r++)
            {
                string row = lines[r];
                if (row.Length == 0 || row.Length < starts[^1]) break; // end of this table (a blank line or the summary)
                var cells = new string[starts.Count];
                for (int c = 0; c < starts.Count; c++)
                {
                    int from = Math.Min(starts[c], row.Length);
                    int to = c + 1 < starts.Count ? Math.Min(starts[c + 1], row.Length) : row.Length;
                    cells[c] = row[from..to].Trim();
                }
                string id = cells[1];
                if (id.Length == 0 || id.Contains(' ')) continue; // misaligned row (very wide characters): skip rather than guess
                updates.Add(new AppUpdate(cells[0].TrimEnd('…'), id, cells[2], cells[3], cells[4]));
            }
        }
        return updates;
    }

    private static bool IsDashes(string line) => line.Length >= 10 && line.All(c => c == '-' || c == '─');

    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
        }
        return starts;
    }
}
