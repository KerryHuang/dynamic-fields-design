using ClosedXML.Excel;

namespace DynamicModules.Application.Dynamic.ExportService;

/// <summary>xlsx：一個工作表；Cell 為 decimal 的寫成數字儲存格，其餘寫文字。</summary>
public static class XlsxWriter
{
    public static byte[] Write(string sheetName, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object>> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SafeSheetName(sheetName));
        for (var c = 0; c < header.Count; c++) ws.Cell(1, c + 1).Value = header[c];
        ws.Row(1).Style.Font.Bold = true;
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                var cell = ws.Cell(r, c + 1);
                if (row[c] is decimal d) cell.Value = d;
                else cell.Value = (string)row[c]; // 文字儲存格不會被 Excel 當公式
            }
            r++;
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>工作表名稱：去掉 []:*?/\、截到 31 字元；空字串退回 Sheet1。</summary>
    private static string SafeSheetName(string name)
    {
        var clean = new string(name.Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray());
        if (clean.Length > 31) clean = clean[..31];
        return clean.Length == 0 ? "Sheet1" : clean;
    }
}
