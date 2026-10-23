using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace TkgPda.Api;

internal static class OperationalReportXlsx
{
    internal enum CellKind { Text, Number, DateTime, Duration, Percent }
    internal sealed record Cell(object? Value, CellKind Kind = CellKind.Text);
    internal sealed record Sheet(
        string Name,
        IReadOnlyList<string> Headers,
        IEnumerable<IReadOnlyList<Cell>> Rows);

    internal static IResult File(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<Cell>> rows,
        string fileName) =>
        File([new Sheet("Rapor", headers, rows)], fileName);

    internal static IResult File(IReadOnlyList<Sheet> sheets, string fileName)
    {
        if (sheets.Count == 0) throw new ArgumentException("En az bir çalışma sayfası gereklidir.", nameof(sheets));

        var namedSheets = NormalizeSheetNames(sheets);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml", ContentTypes(namedSheets.Count));
            Write(archive, "_rels/.rels", RootRelationships);
            Write(archive, "xl/workbook.xml", Workbook(namedSheets));
            Write(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships(namedSheets.Count));
            Write(archive, "xl/styles.xml", Styles);
            for (var index = 0; index < namedSheets.Count; index++)
            {
                var sheet = namedSheets[index];
                Write(archive, $"xl/worksheets/sheet{index + 1}.xml", Worksheet(sheet.Headers, sheet.Rows));
            }
        }

        return Results.File(
            output.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    internal static Cell Text(object? value) => new(value);
    internal static Cell Number(long? value) => new(value, CellKind.Number);
    internal static Cell Date(DateTimeOffset? value) => new(value, CellKind.DateTime);
    internal static Cell Duration(long? seconds) => new(seconds, CellKind.Duration);
    internal static Cell Percent(double? value) => new(value, CellKind.Percent);

    private static IReadOnlyList<Sheet> NormalizeSheetNames(IReadOnlyList<Sheet> sheets)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<Sheet>(sheets.Count);
        foreach (var sheet in sheets)
        {
            var baseName = SafeSheetName(sheet.Name);
            var name = baseName;
            var suffix = 2;
            while (!used.Add(name))
            {
                var suffixText = $" ({suffix++})";
                name = baseName[..Math.Min(baseName.Length, 31 - suffixText.Length)] + suffixText;
            }

            normalized.Add(sheet with { Name = name });
        }

        return normalized;
    }

    private static string SafeSheetName(string name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "Rapor" : name.Trim();
        foreach (var invalid in new[] { ':', '\\', '/', '?', '*', '[', ']' }) value = value.Replace(invalid, '-');
        value = value.Trim('\'');
        if (value.Length == 0) value = "Rapor";
        return value[..Math.Min(value.Length, 31)];
    }

    private static string Worksheet(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<Cell>> rows)
    {
        if (headers.Count == 0) throw new ArgumentException("En az bir sütun gereklidir.", nameof(headers));

        var xml = new StringBuilder(64 * 1024);
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        xml.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        xml.Append("<sheetFormatPr defaultRowHeight=\"18\"/><cols>");
        for (var index = 0; index < headers.Count; index++)
        {
            var width = Math.Clamp(headers[index].Length + 5, 12, 28);
            xml.Append(CultureInfo.InvariantCulture, $"<col min=\"{index + 1}\" max=\"{index + 1}\" width=\"{width}\" customWidth=\"1\"/>");
        }
        xml.Append("</cols><sheetData>");
        xml.Append("<row r=\"1\">");
        for (var column = 0; column < headers.Count; column++)
            AppendTextCell(xml, Reference(column, 1), headers[column], style: 1);
        xml.Append("</row>");

        var rowNumber = 2;
        foreach (var row in rows)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
            for (var column = 0; column < headers.Count; column++)
            {
                var cell = column < row.Count ? row[column] : new Cell(null);
                AppendCell(xml, Reference(column, rowNumber), cell);
            }
            xml.Append("</row>");
            rowNumber++;
        }

        xml.Append("</sheetData>");
        xml.Append(CultureInfo.InvariantCulture, $"<autoFilter ref=\"A1:{ColumnName(headers.Count - 1)}{Math.Max(1, rowNumber - 1)}\"/>");
        xml.Append("</worksheet>");
        return xml.ToString();
    }

    private static void AppendCell(StringBuilder xml, string reference, Cell cell)
    {
        if (cell.Value is null)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"/>");
            return;
        }

        switch (cell.Kind)
        {
            case CellKind.Number:
                xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"><v>{Convert.ToInt64(cell.Value, CultureInfo.InvariantCulture)}</v></c>");
                break;
            case CellKind.DateTime:
                var local = TimeZoneInfo.ConvertTime((DateTimeOffset)cell.Value, ReportCsv.Zone);
                xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" s=\"2\"><v>{local.DateTime.ToOADate():0.############}</v></c>");
                break;
            case CellKind.Duration:
                var seconds = Convert.ToInt64(cell.Value, CultureInfo.InvariantCulture);
                xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" s=\"3\"><v>{seconds / 86400d:0.############}</v></c>");
                break;
            case CellKind.Percent:
                var percent = Convert.ToDouble(cell.Value, CultureInfo.InvariantCulture);
                xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" s=\"4\"><v>{percent:0.############}</v></c>");
                break;
            default:
                AppendTextCell(xml, reference, Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? "", 0);
                break;
        }
    }

    private static void AppendTextCell(StringBuilder xml, string reference, string value, int style)
    {
        var escaped = SecurityElement.Escape(value) ?? "";
        xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{escaped}</t></is></c>");
    }

    private static string Reference(int zeroBasedColumn, int row) =>
        ColumnName(zeroBasedColumn) + row.ToString(CultureInfo.InvariantCulture);

    private static string ColumnName(int zeroBasedColumn)
    {
        var column = zeroBasedColumn + 1;
        var name = "";
        while (column > 0)
        {
            column--;
            name = (char)('A' + column % 26) + name;
            column /= 26;
        }

        return name;
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ContentTypes(int sheetCount)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        xml.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        xml.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        xml.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        xml.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        for (var index = 1; index <= sheetCount; index++)
            xml.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        xml.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        xml.Append("</Types>");
        return xml.ToString();
    }

    private static string Workbook(IReadOnlyList<Sheet> sheets)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        xml.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (var index = 0; index < sheets.Count; index++)
        {
            var name = SecurityElement.Escape(sheets[index].Name) ?? "Rapor";
            xml.Append(CultureInfo.InvariantCulture, $"<sheet name=\"{name}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>");
        }
        xml.Append("</sheets></workbook>");
        return xml.ToString();
    }

    private static string WorkbookRelationships(int sheetCount)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        xml.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var index = 1; index <= sheetCount; index++)
            xml.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{index}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index}.xml\"/>");
        xml.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        xml.Append("</Relationships>");
        return xml.ToString();
    }

    private const string RootRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="3"><numFmt numFmtId="164" formatCode="dd.mm.yyyy hh:mm:ss"/><numFmt numFmtId="165" formatCode="[h]:mm:ss"/><numFmt numFmtId="166" formatCode="0%"/></numFmts>
          <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts>
          <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF0B5975"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="5"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="1" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}
