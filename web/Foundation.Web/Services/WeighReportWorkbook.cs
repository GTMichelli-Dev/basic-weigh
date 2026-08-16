using ClosedXML.Excel;
using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// Writes the .xlsx that scheduled reports attach. Columns come from
/// WeighReportColumns (the enabled ticket fields plus net pounds and the
/// commodity's own unit); this class only handles layout and formatting.
///
/// With GroupCommoditiesIntoSheets on, the workbook leads with a Summary sheet
/// of per-commodity totals and gives each commodity its own sheet. Off, it's a
/// single "Loads" sheet.
/// </summary>
public static class WeighReportWorkbook
{
    private const string HeaderFill = "#1F4E79";
    private const string TitleFill = "#DCE6F1";

    public static byte[] Build(
        string reportName,
        string companyName,
        DateTime localStart,
        DateTime localEndExclusive,
        List<WeighRow> rows,
        List<WeighColumn> columns,
        bool groupCommoditiesIntoSheets)
    {
        using var wb = new XLWorkbook();

        if (groupCommoditiesIntoSheets)
        {
            var groups = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Transaction.Commodity)
                    ? "(No Commodity)"
                    : r.Transaction.Commodity!,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AddSummarySheet(wb, reportName, companyName, localStart, localEndExclusive, groups);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Summary" };
            foreach (var group in groups)
            {
                var sheet = wb.Worksheets.Add(UniqueSheetName(group.Key, used));
                WriteSheet(sheet, group.Key, companyName, localStart, localEndExclusive,
                    group.ToList(), columns);
            }

            // A grouped report with no loads still needs one sheet to be a
            // valid workbook (Send When Empty).
            if (groups.Count == 0)
            {
                var sheet = wb.Worksheets.Add("Loads");
                WriteSheet(sheet, reportName, companyName, localStart, localEndExclusive,
                    rows, columns);
            }
        }
        else
        {
            var sheet = wb.Worksheets.Add("Loads");
            WriteSheet(sheet, reportName, companyName, localStart, localEndExclusive, rows, columns);
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteSheet(
        IXLWorksheet sheet,
        string title,
        string companyName,
        DateTime localStart,
        DateTime localEndExclusive,
        List<WeighRow> rows,
        List<WeighColumn> columns)
    {
        var lastCol = Math.Max(columns.Count, 1);

        sheet.Cell(1, 1).Value = companyName;
        sheet.Range(1, 1, 1, lastCol).Merge().Style
            .Font.SetBold().Font.SetFontSize(14)
            .Fill.SetBackgroundColor(XLColor.FromHtml(TitleFill));

        sheet.Cell(2, 1).Value = $"{title} — {DateLabel(localStart, localEndExclusive)}";
        sheet.Range(2, 1, 2, lastCol).Merge().Style
            .Font.SetItalic()
            .Fill.SetBackgroundColor(XLColor.FromHtml(TitleFill));

        const int headerRow = 4;
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = sheet.Cell(headerRow, c + 1);
            cell.Value = columns[c].Header;
            cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White)
                .Fill.SetBackgroundColor(XLColor.FromHtml(HeaderFill))
                .Alignment.SetWrapText();
        }

        var r = headerRow + 1;
        foreach (var row in rows)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                SetValue(cell, columns[c], row);
            }
            r++;
        }

        var lastDataRow = r - 1;

        if (rows.Count > 0)
        {
            // Total the numeric columns. Text and timestamps get no total, and
            // Unit is deliberately blank — mixed units don't sum.
            var totalRow = lastDataRow + 1;
            sheet.Cell(totalRow, 1).Value = "Total";
            sheet.Cell(totalRow, 1).Style.Font.SetBold();

            for (var c = 0; c < columns.Count; c++)
            {
                if (columns[c].Kind is not (WeighColumnKind.Weight or WeighColumnKind.Quantity)) continue;
                var letter = sheet.Cell(headerRow, c + 1).Address.ColumnLetter;
                var cell = sheet.Cell(totalRow, c + 1);
                cell.FormulaA1 = $"SUM({letter}{headerRow + 1}:{letter}{lastDataRow})";
                cell.Style.Font.SetBold().NumberFormat.SetFormat(NumberFormat(columns[c].Kind));
            }

            sheet.Range(totalRow, 1, totalRow, lastCol).Style
                .Border.SetTopBorder(XLBorderStyleValues.Thin);

            sheet.Range(headerRow, 1, lastDataRow, lastCol).SetAutoFilter();
        }
        else
        {
            sheet.Cell(headerRow + 1, 1).Value = "No loads were weighed in this period.";
            sheet.Range(headerRow + 1, 1, headerRow + 1, lastCol).Merge().Style
                .Font.SetItalic().Font.SetFontColor(XLColor.Gray);
        }

        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns().AdjustToContents(headerRow, Math.Max(headerRow, lastDataRow), 8, 42);
    }

    private static void SetValue(IXLCell cell, WeighColumn column, WeighRow row)
    {
        var value = column.Value(row);
        if (value == null) return;

        switch (column.Kind)
        {
            case WeighColumnKind.DateTime:
                cell.Value = (DateTime)value;
                cell.Style.DateFormat.Format = "mm/dd/yyyy hh:mm AM/PM";
                break;
            case WeighColumnKind.Weight:
                cell.Value = Convert.ToDouble(value);
                cell.Style.NumberFormat.Format = NumberFormat(column.Kind);
                break;
            case WeighColumnKind.Quantity:
                // Custom text fields typed as numeric can still hold text.
                if (value is string text) cell.Value = text;
                else
                {
                    cell.Value = Convert.ToDouble(value);
                    cell.Style.NumberFormat.Format = NumberFormat(column.Kind);
                }
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }

    private static string NumberFormat(WeighColumnKind kind) =>
        kind == WeighColumnKind.Weight ? "#,##0" : "#,##0.00";

    /// <summary>Per-commodity totals sheet that leads a grouped workbook, so the
    /// recipient sees the whole period before drilling into a commodity.</summary>
    private static void AddSummarySheet(
        XLWorkbook wb,
        string reportName,
        string companyName,
        DateTime localStart,
        DateTime localEndExclusive,
        List<IGrouping<string, WeighRow>> groups)
    {
        var sheet = wb.Worksheets.Add("Summary");

        sheet.Cell(1, 1).Value = companyName;
        sheet.Range(1, 1, 1, 5).Merge().Style
            .Font.SetBold().Font.SetFontSize(14)
            .Fill.SetBackgroundColor(XLColor.FromHtml(TitleFill));

        sheet.Cell(2, 1).Value = $"{reportName} — {DateLabel(localStart, localEndExclusive)}";
        sheet.Range(2, 1, 2, 5).Merge().Style
            .Font.SetItalic()
            .Fill.SetBackgroundColor(XLColor.FromHtml(TitleFill));

        var headers = new[] { "Commodity", "Loads", "Net (lbs)", "Unit", "Net (Units)" };
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = sheet.Cell(4, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White)
                .Fill.SetBackgroundColor(XLColor.FromHtml(HeaderFill));
        }

        var r = 5;
        foreach (var group in groups)
        {
            var list = group.ToList();
            sheet.Cell(r, 1).Value = group.Key;
            sheet.Cell(r, 2).Value = list.Count;
            sheet.Cell(r, 3).Value = list.Sum(x => (double)x.Transaction.NetWeight);
            sheet.Cell(r, 3).Style.NumberFormat.Format = "#,##0";
            sheet.Cell(r, 4).Value = list[0].UnitName;
            sheet.Cell(r, 5).Value = Math.Round(list.Sum(x => x.NetUnits), 2);
            sheet.Cell(r, 5).Style.NumberFormat.Format = "#,##0.00";
            r++;
        }

        if (groups.Count > 0)
        {
            sheet.Cell(r, 1).Value = "Total";
            sheet.Cell(r, 2).FormulaA1 = $"SUM(B5:B{r - 1})";
            sheet.Cell(r, 3).FormulaA1 = $"SUM(C5:C{r - 1})";
            sheet.Cell(r, 3).Style.NumberFormat.Format = "#,##0";
            // Net (Units) is intentionally not totalled here — bushels and tons
            // in one column don't add up to anything meaningful.
            sheet.Range(r, 1, r, 5).Style
                .Font.SetBold()
                .Border.SetTopBorder(XLBorderStyleValues.Thin);
        }
        else
        {
            sheet.Cell(5, 1).Value = "No loads were weighed in this period.";
            sheet.Cell(5, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
        }

        sheet.Columns().AdjustToContents(4, Math.Max(4, r), 10, 42);
    }

    /// <summary>Excel sheet names cap at 31 chars, can't contain : \ / ? * [ ]
    /// and must be unique — commodity names satisfy none of that reliably.</summary>
    private static string UniqueSheetName(string raw, HashSet<string> used)
    {
        var clean = new string(raw.Select(c => ":\\/?*[]".Contains(c) ? '-' : c).ToArray()).Trim();
        if (clean.Length == 0) clean = "Sheet";
        if (clean.Length > 31) clean = clean[..31];

        var name = clean;
        var n = 2;
        while (!used.Add(name))
        {
            var suffix = $" ({n++})";
            name = clean.Length + suffix.Length > 31
                ? clean[..(31 - suffix.Length)] + suffix
                : clean + suffix;
        }
        return name;
    }

    /// <summary>"08/15/2026" for a single day, "08/09/2026 – 08/15/2026" for a
    /// range. The end bound is exclusive, so the label shows the day before.</summary>
    public static string DateLabel(DateTime localStart, DateTime localEndExclusive)
    {
        var lastDay = localEndExclusive.Date.AddDays(-1);
        return lastDay <= localStart.Date
            ? localStart.ToString("MM/dd/yyyy")
            : $"{localStart:MM/dd/yyyy} – {lastDay:MM/dd/yyyy}";
    }
}
