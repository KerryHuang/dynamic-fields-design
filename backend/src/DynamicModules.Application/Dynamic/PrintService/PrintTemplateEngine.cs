using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicModules.Application.Dynamic.PrintService;

/// <summary>範本裡用到的佔位符。Header：表頭欄位鍵；Loops：明細模組鍵 → 迴圈內用到的欄位鍵；Error：迴圈結構錯誤的說明。</summary>
public sealed record PrintPlaceholders(HashSet<string> Header, Dictionary<string, HashSet<string>> Loops, string? Error);

/// <summary>
/// 範本語法：<c>{{f_12}}</c> 取欄位值；<c>{{_title}}</c>、<c>{{_printedAt}}</c> 是系統值；
/// <c>{{#each 明細模組鍵}}…{{/each}}</c> 對每一列重複一次（不可巢狀），迴圈內 <c>{{_index}}</c> 是從 1 開始的列號。
/// 值一律 HTML 跳脫；範本文字只被掃描一次，值的內容不會再被當成佔位符。找不到的佔位符輸出空字串，不丟例外。
/// </summary>
public static partial class PrintTemplateEngine
{
    [GeneratedRegex(@"\{\{#each\s+([A-Za-z0-9_-]+)\s*\}\}(.*?)\{\{/each\}\}", RegexOptions.Singleline)]
    private static partial Regex Loop();

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex Slot();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    public const string LoopMarkerInTagMessage = "迴圈標記 {{#each}}、{{/each}} 不能放在標籤或屬性裡";

    /// <summary>
    /// 已淨化的範本裡，迴圈標記是否落在標籤內（含屬性值）。迴圈展開是純文字取代，標記若跨過標籤邊界，
    /// 展開後會把前後兩段拼成原本不存在的標籤或屬性，繞過先淨化再展開的順序。
    /// 淨化後的標籤一定良構，屬性值裡的 &lt; &gt; 都已跳脫，所以 &lt;…&gt; 之間就是整個標籤。
    /// </summary>
    public static bool HasLoopMarkerInsideTag(string sanitizedBody) =>
        Tag().Matches(sanitizedBody).Any(m => m.Value.Contains("{{#each", StringComparison.Ordinal) || m.Value.Contains("{{/each", StringComparison.Ordinal));

    [GeneratedRegex(@"\{\{#each\s+([A-Za-z0-9_-]+)\s*\}\}|\{\{(/each)\}\}|\{\{\s*([A-Za-z0-9_]+)\s*\}\}")]
    private static partial Regex Token();

    public static string Render(
        string body, Func<string, string?> header, Func<string, IReadOnlyList<Func<string, string?>>> rowsOf)
    {
        var output = new StringBuilder();
        var last = 0;
        foreach (Match m in Loop().Matches(body))
        {
            output.Append(Fill(body[last..m.Index], header, null));
            var rows = rowsOf(m.Groups[1].Value);
            for (var i = 0; i < rows.Count; i++) output.Append(Fill(m.Groups[2].Value, rows[i], i + 1));
            last = m.Index + m.Length;
        }
        output.Append(Fill(body[last..], header, null));
        return output.ToString();
    }

    private static string Fill(string template, Func<string, string?> lookup, int? index) =>
        Slot().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (key == "_index" && index is { } n) return n.ToString();
            return Encode(lookup(key));
        });

    private static string Encode(string? value) =>
        string.IsNullOrEmpty(value) ? "" : WebUtility.HtmlEncode(value).Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    public static PrintPlaceholders Placeholders(string body)
    {
        var header = new HashSet<string>(StringComparer.Ordinal);
        var loops = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? current = null;
        string? error = null;
        foreach (Match m in Token().Matches(body))
        {
            if (m.Groups[1].Success)
            {
                if (current is not null) { error ??= "迴圈不能巢狀"; continue; }
                current = m.Groups[1].Value;
                loops.TryAdd(current, new HashSet<string>(StringComparer.Ordinal));
            }
            else if (m.Groups[2].Success)
            {
                if (current is null) error ??= "{{/each}} 沒有對應的 {{#each}}";
                current = null;
            }
            else
            {
                var key = m.Groups[3].Value;
                if (key.StartsWith('_')) continue;
                (current is null ? header : loops[current]).Add(key);
            }
        }
        if (current is not null) error ??= "{{#each}} 缺少結尾的 {{/each}}";
        return new PrintPlaceholders(header, loops, error);
    }
}
