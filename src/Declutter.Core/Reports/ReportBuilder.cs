using System.Globalization;
using System.Net;
using System.Text;

namespace Declutter.Core.Reports;

public sealed record ReportRow(string Label, string Value, string? Tone = null);

public sealed record ReportSection(string Title, IReadOnlyList<ReportRow> Rows, string? Note = null);

/// <summary>
/// Builds a single, self-contained HTML page (no scripts, no external files) that can be emailed or printed
/// to PDF. Every piece of text is HTML-encoded, and the user's own folder path and user name are replaced
/// so the report can be shared without revealing them.
/// </summary>
public sealed class ReportBuilder(string userProfile, string userName)
{
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string result = text;
        if (userProfile.Length > 3) result = result.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (userName.Length >= 3) result = result.Replace(userName, "[user]", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public string Build(string title, DateTime generatedLocal, IReadOnlyList<ReportSection> sections)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(E(Redact(title))).Append("</title><style>")
            .Append("body{font-family:'Segoe UI',system-ui,sans-serif;margin:32px auto;max-width:860px;padding:0 16px;color:#1a1a1a;background:#fff}")
            .Append("h1{font-size:26px;margin:0}h2{font-size:17px;margin:28px 0 8px;border-bottom:1px solid #ddd;padding-bottom:6px}")
            .Append(".sub{color:#5d5d5d;margin:4px 0 0}table{border-collapse:collapse;width:100%}")
            .Append("td{padding:6px 8px;vertical-align:top;border-bottom:1px solid #f0f0f0}td.l{width:34%;color:#5d5d5d}")
            .Append(".good{color:#0e700e;font-weight:600}.caution{color:#7a4a00;font-weight:600}.danger{color:#a4262c;font-weight:600}")
            .Append(".note{color:#5d5d5d;font-size:13px;margin:6px 0 0}.foot{color:#8a8a8a;font-size:12px;margin-top:32px}")
            .Append("@media print{body{margin:0}}")
            .Append("</style></head><body>");

        html.Append("<h1>").Append(E(Redact(title))).Append("</h1>")
            .Append("<p class=\"sub\">Created by Declutter on ")
            .Append(E(generatedLocal.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture))).Append("</p>");

        foreach (var section in sections)
        {
            html.Append("<h2>").Append(E(Redact(section.Title))).Append("</h2><table>");
            foreach (var row in section.Rows)
            {
                string cls = row.Tone is "good" or "caution" or "danger" ? $" class=\"{row.Tone}\"" : "";
                html.Append("<tr><td class=\"l\">").Append(E(Redact(row.Label))).Append("</td><td")
                    .Append(cls).Append('>').Append(E(Redact(row.Value))).Append("</td></tr>");
            }
            html.Append("</table>");
            if (section.Note != null) html.Append("<p class=\"note\">").Append(E(Redact(section.Note))).Append("</p>");
        }

        html.Append("<p class=\"foot\">Personal folder paths and the Windows user name have been hidden. ")
            .Append("To save as PDF, press Ctrl+P and choose \u201cMicrosoft Print to PDF\u201d.</p></body></html>");
        return html.ToString();
    }
}
