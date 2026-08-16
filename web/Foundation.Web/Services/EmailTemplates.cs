using System.Net;
using System.Text;
using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// Subject-line placeholders and the HTML bodies for both kinds of email.
/// Bodies are built here rather than in Razor views because the schedule worker
/// runs outside a request and has no view engine.
/// </summary>
public static class EmailTemplates
{
    /// <summary>Substitutes {placeholder} tokens, case-insensitively. Unknown
    /// tokens are left as typed so a mistyped one is visible in the subject
    /// instead of silently vanishing.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
            result = System.Text.RegularExpressions.Regex.Replace(
                result, "\\{" + System.Text.RegularExpressions.Regex.Escape(key) + "\\}",
                value.Replace("$", "$$"),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return result;
    }

    // ===== Scheduled report =====

    public static Dictionary<string, string> ScheduleTokens(
        ReportSchedule schedule, string companyName,
        DateTime localStart, DateTime localEndExclusive, int loadCount) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["report"] = schedule.Name,
            ["company"] = companyName,
            ["start"] = localStart.ToString("MM/dd/yyyy"),
            ["end"] = localEndExclusive.AddDays(-1).ToString("MM/dd/yyyy"),
            ["period"] = WeighReportWorkbook.DateLabel(localStart, localEndExclusive),
            ["count"] = loadCount.ToString()
        };

    public static string ScheduleSubject(
        ReportSchedule schedule, string companyName,
        DateTime localStart, DateTime localEndExclusive, int loadCount)
    {
        var tokens = ScheduleTokens(schedule, companyName, localStart, localEndExclusive, loadCount);
        var template = string.IsNullOrWhiteSpace(schedule.SubjectTemplate)
            ? "{company} — {report} — {period}"
            : schedule.SubjectTemplate;
        return Fill(template, tokens);
    }

    public static string ScheduleBody(
        ReportSchedule schedule, string companyName,
        DateTime localStart, DateTime localEndExclusive,
        List<WeighRow> rows, string attachmentName)
    {
        var sb = new StringBuilder();
        sb.Append(BodyOpen());
        sb.Append($"<h2 style=\"margin:0 0 4px;font-size:18px;\">{Esc(companyName)}</h2>");
        sb.Append($"<div style=\"color:#666;margin-bottom:16px;\">{Esc(schedule.Name)} — {Esc(WeighReportWorkbook.DateLabel(localStart, localEndExclusive))}</div>");

        if (rows.Count == 0)
        {
            sb.Append("<p>No loads were weighed in this period.</p>");
        }
        else
        {
            var totalLbs = rows.Sum(r => (long)r.Transaction.NetWeight);
            sb.Append($"<p><strong>{rows.Count:N0}</strong> load{(rows.Count == 1 ? "" : "s")}, ");
            sb.Append($"<strong>{totalLbs:N0} lbs</strong> net.</p>");

            // Per-commodity totals mirror the workbook's Summary sheet so the
            // recipient gets the headline without opening the attachment.
            var byCommodity = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Transaction.Commodity)
                    ? "(No Commodity)" : r.Transaction.Commodity!,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            sb.Append(TableOpen());
            sb.Append(Th("Commodity") + Th("Loads", true) + Th("Net (lbs)", true) + Th("Net (Units)", true) + "</tr>");
            foreach (var g in byCommodity)
            {
                var list = g.ToList();
                sb.Append("<tr>");
                sb.Append(Td(g.Key));
                sb.Append(Td(list.Count.ToString("N0"), true));
                sb.Append(Td(list.Sum(x => (long)x.Transaction.NetWeight).ToString("N0"), true));
                sb.Append(Td($"{list.Sum(x => x.NetUnits):N2} {Esc(list[0].UnitName)}", true));
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            sb.Append($"<p style=\"color:#666;\">Full detail is attached as <strong>{Esc(attachmentName)}</strong>.</p>");
        }

        sb.Append(Footer());
        sb.Append(BodyClose());
        return sb.ToString();
    }

    // ===== Per-load email =====

    public static Dictionary<string, string> LoadTokens(WeighRow row, string companyName) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ticket"] = row.Transaction.Ticket,
            ["customer"] = row.Transaction.Customer ?? "",
            ["commodity"] = row.Transaction.Commodity ?? "",
            ["carrier"] = row.Transaction.Carrier ?? "",
            ["truck"] = row.Transaction.TruckId ?? "",
            ["net"] = row.Transaction.NetWeight.ToString("N0"),
            ["units"] = $"{row.NetUnits:N2} {row.UnitName}",
            ["company"] = companyName
        };

    public static string LoadSubject(LoadEmailRule rule, WeighRow row, string companyName)
    {
        var template = string.IsNullOrWhiteSpace(rule.SubjectTemplate)
            ? "{company} — Ticket {ticket}"
            : rule.SubjectTemplate;
        return Fill(template, LoadTokens(row, companyName));
    }

    /// <summary>
    /// The load detail as an HTML table. Rows come from the same column set the
    /// scheduled workbook uses, minus the ones that only make sense in a
    /// spreadsheet, so a field hidden in Setup → Fields stays hidden here.
    /// </summary>
    public static string LoadBody(WeighRow row, string companyName, List<WeighColumn> columns)
    {
        var sb = new StringBuilder();
        sb.Append(BodyOpen());
        sb.Append($"<h2 style=\"margin:0 0 4px;font-size:18px;\">{Esc(companyName)}</h2>");
        sb.Append($"<div style=\"color:#666;margin-bottom:16px;\">Ticket {Esc(row.Transaction.Ticket)}</div>");

        sb.Append(TableOpen());
        foreach (var col in columns)
        {
            var value = col.Value(row);
            if (value == null || (value is string s && s.Length == 0)) continue;

            var text = col.Kind switch
            {
                WeighColumnKind.DateTime => ((DateTime)value).ToString("MM/dd/yyyy h:mm tt"),
                WeighColumnKind.Weight => $"{Convert.ToDouble(value):N0}",
                WeighColumnKind.Quantity => value is string q ? q : $"{Convert.ToDouble(value):N2}",
                _ => value.ToString() ?? ""
            };

            sb.Append("<tr>");
            sb.Append($"<td style=\"padding:6px 12px;border-bottom:1px solid #eee;color:#666;white-space:nowrap;\">{Esc(col.Header)}</td>");
            sb.Append($"<td style=\"padding:6px 12px;border-bottom:1px solid #eee;font-weight:600;\">{Esc(text)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");

        sb.Append(Footer());
        sb.Append(BodyClose());
        return sb.ToString();
    }

    // ===== Shared chrome =====

    private static string BodyOpen() =>
        "<div style=\"font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;font-size:14px;color:#222;\">";

    private static string BodyClose() => "</div>";

    private static string TableOpen() =>
        "<table cellspacing=\"0\" cellpadding=\"0\" style=\"border-collapse:collapse;margin:8px 0;\"><tr>";

    private static string Th(string text, bool right = false) =>
        $"<th style=\"padding:6px 12px;border-bottom:2px solid #1F4E79;text-align:{(right ? "right" : "left")};\">{Esc(text)}</th>";

    private static string Td(string text, bool right = false) =>
        $"<td style=\"padding:6px 12px;border-bottom:1px solid #eee;text-align:{(right ? "right" : "left")};\">{Esc(text)}</td>";

    private static string Footer() =>
        "<p style=\"color:#999;font-size:12px;margin-top:20px;\">Sent automatically by Foundation. "
        + "To change or stop these emails, edit the schedule under Setup → Email.</p>";

    private static string Esc(string? value) => WebUtility.HtmlEncode(value ?? "");
}
