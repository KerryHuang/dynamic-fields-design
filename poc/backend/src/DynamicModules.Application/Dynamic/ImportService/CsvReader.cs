using System.Text;

namespace DynamicModules.Application.Dynamic.ImportService;

/// <summary>CSV 的資料列；<see cref="Line"/> 是起始行號（表頭為第 1 行）。</summary>
public sealed record CsvRow(int Line, IReadOnlyList<string> Cells);

public sealed record CsvReadResult(string? Error, IReadOnlyList<string> Header, IReadOnlyList<CsvRow> Rows);

/// <summary>CSV 讀取（CsvWriter 的反向）：嚴格 UTF-8、略過 BOM 與空白列、RFC 4180 引號。</summary>
public static class CsvReader
{
    public const int MaxRows = 1000;

    public static CsvReadResult Read(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (ArgumentException)
        {
            return Fail("請把檔案存成 UTF-8 的 CSV");
        }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var rows = new List<CsvRow>();
        var cells = new List<string>();
        var field = new StringBuilder();
        var line = 1;       // 目前讀到的行號
        var rowLine = 1;    // 目前這筆的起始行號
        var inQuotes = false;
        var afterQuote = false; // 剛關掉引號，下一個字元必須是分隔符或換行

        void EndRow()
        {
            cells.Add(field.ToString());
            field.Clear();
            if (cells.Any(c => c.Length > 0)) rows.Add(new CsvRow(rowLine, cells));
            cells = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') { inQuotes = false; afterQuote = true; }
                else
                {
                    field.Append(c);
                    if (c == '\n' || (c == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n'))) line++;
                }
                continue;
            }

            if (c is ',' or '\r' or '\n')
            {
                afterQuote = false;
                if (c == ',') { cells.Add(field.ToString()); field.Clear(); continue; }
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRow();
                line++;
                rowLine = line;
                continue;
            }
            if (afterQuote) return Fail($"第 {rowLine} 行格式不正確");
            if (c == '"' && field.Length == 0) inQuotes = true;
            else field.Append(c);
        }

        if (inQuotes) return Fail($"第 {rowLine} 行格式不正確");
        if (field.Length > 0 || cells.Count > 0 || afterQuote) EndRow();

        if (rows.Count == 0 || rows[0].Line != 1) return Fail("檔案沒有表頭");
        if (rows.Count - 1 > MaxRows) return Fail("一次最多匯入 1000 筆");
        return new CsvReadResult(null, rows[0].Cells, rows.Skip(1).ToList());
    }

    private static CsvReadResult Fail(string error) => new(error, [], []);
}
