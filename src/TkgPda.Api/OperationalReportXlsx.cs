using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace TkgPda.Api;

internal static class OperationalReportXlsx
{
    internal enum CellKind { Text, Number, DateTime, Duration }
    internal sealed record Cell(object? Value, CellKind Kind = CellKind.Text);

    internal static IResult File(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<Cell>> rows,
        string fileName)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml", ContentTypes);
            Write(archive, "_rels/.rels", RootRelationships);
            Write(archive, "xl/workbook.xml", Workbook);
            Write(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships);
            Write(archive, "xl/styles.xml", Styles);
            Write(archive, "xl/worksheets/sheet1.xml", Worksheet(headers, rows));
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

    private static string Worksheet(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<Cell>> rows)
    {
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

        var lastColumn = Reference(headers.Count - 1, 1).TrimEnd('1');
        xml.Append("</sheetData>");
        xml.Append(CultureInfo.InvariantCulture, $"<autoFilter ref=\"A1:{lastColumn}{Math.Max(1, rowNumber - 1)}\"/>");
        xml.Append("</worksheet>");
        return xml.ToString();
    }

    private static void AppendCell(StringBuilder xml, string reference, Cell cell)
    {
        if (cell.Value is null) { xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"/>"); return; }
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

    private static string Reference(int zeroBasedColumn, int row)
    {
        var column = zeroBasedColumn + 1;
        var name = "";
        while (column > 0)
        {
            column--;
            name = (char)('A' + column % 26) + name;
            column /= 26;
        }
        return name + row.ToString(CultureInfo.InvariantCulture);
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
        </Types>
        """;
    private const string RootRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;
    private const string Workbook = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets><sheet name="Rapor" sheetId="1" r:id="rId1"/></sheets>
        </workbook>
        """;
    private const string WorkbookRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;
    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="2"><numFmt numFmtId="164" formatCode="dd.mm.yyyy hh:mm:ss"/><numFmt numFmtId="165" formatCode="[h]:mm:ss"/></numFmts>
          <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts>
          <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF0B5975"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="4"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="1" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}
