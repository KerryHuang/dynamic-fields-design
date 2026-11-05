using System.Text;

namespace DynamicModules.Application.Dynamic.ExportService;

/// <summary>CSV：UTF-8 含 BOM、CRLF、RFC 4180 引號規則。</summary>
public static class CsvWriter
{
    public static byte[] Write(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, header);
        foreach (var row in rows) AppendRow(sb, row);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>公式注入防護：以 = + - @ 開頭的文字前面加 '。</summary>
    public static string EscapeFormula(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> row)
    {
        for (var i = 0; i < row.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var v = row[i];
            sb.Append(v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v);
        }
        sb.Append("\r\n");
    }
}
