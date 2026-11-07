using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicModules.Application.Dynamic.PrintService;

/// <summary>
/// 列印範本的 HTML 淨化器。做法是白名單重建：把輸入切成標籤與文字，只把白名單內的標籤與屬性<b>重新輸出</b>，
/// 其餘一律丟掉，而不是用正規式去剔除壞東西，所以怪異的拼法（大小寫、換行、NUL、實體編碼、不完整標籤）
/// 不是被「漏接」，而是根本沒有辦法被輸出。輸出是良構的 HTML，瀏覽器解析結果與這裡的理解一致。
/// <list type="bullet">
/// <item>script、style、iframe、object、svg、math 等整個元素連內容一起丟；不認得的標籤只丟標籤、留文字。</item>
/// <item>所有事件屬性（on*）、style 以外的未知屬性一律不輸出；連結只放行 http、https、mailto。</item>
/// <item>style 屬性只留白名單屬性且值只含字母數字與少數符號（沒有括號與引號，所以沒有 url()、expression()）。</item>
/// <item>沒有 img：列印範本不載入任何外部資源。</item>
/// </list>
/// 輸出可以重複淨化（idempotent）；{{…}} 佔位符是普通文字，原樣保留。
/// </summary>
public static partial class PrintTemplateSanitizer
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "div", "span", "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "b", "em", "i", "u", "s", "small", "sub", "sup",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col", "ul", "ol", "li", "blockquote", "pre", "code",
        "section", "header", "footer", "a",
    };

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "br", "hr", "col" };

    /// <summary>整個元素（含內容）丟掉。raw-text 類元素的內容瀏覽器不會當標籤解析，所以要整段略過。</summary>
    private static readonly HashSet<string> DropWithContent = new(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "object", "embed", "noscript", "template", "svg", "math", "head", "title",
        "textarea", "xmp", "plaintext", "noembed", "noframes", "applet", "select", "option", "button",
    };

    private static readonly HashSet<string> StyleProperties = new(StringComparer.Ordinal)
    {
        "text-align", "font-weight", "font-size", "font-style", "text-decoration", "width", "height", "min-height", "color",
        "background-color", "border", "border-top", "border-bottom", "border-left", "border-right", "border-collapse",
        "padding", "margin", "line-height", "vertical-align", "page-break-inside", "page-break-before", "page-break-after",
    };

    [GeneratedRegex(@"^[A-Za-z0-9_\- {}#]{1,100}$")]
    private static partial Regex ClassValue();

    [GeneratedRegex(@"^\d{1,4}(px|%)?$")]
    private static partial Regex Length();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex SmallNumber();

    [GeneratedRegex(@"^[A-Za-z0-9#%.,\s\-]{1,60}$")]
    private static partial Regex StyleValue();

    [GeneratedRegex(@"^&(#\d{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex Entity();

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = StripControlChars(html);
        var n = s.Length;
        var output = new StringBuilder(n);
        var open = new List<string>();
        var i = 0;
        while (i < n)
        {
            var c = s[i];
            if (c != '<')
            {
                AppendTextChar(output, s, ref i);
                continue;
            }
            if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
            {
                var end = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }
            if (i + 1 < n && s[i + 1] is '!' or '?')
            {
                var end = s.IndexOf('>', i);
                i = end < 0 ? n : end + 1;
                continue;
            }
            var isEnd = i + 1 < n && s[i + 1] == '/';
            var nameStart = i + (isEnd ? 2 : 1);
            if (nameStart >= n || !IsAsciiLetter(s[nameStart]))
            {
                output.Append("&lt;");
                i++;
                continue;
            }
            var p = nameStart;
            while (p < n && !char.IsWhiteSpace(s[p]) && s[p] is not ('/' or '>')) p++;
            var name = s[nameStart..p].ToLowerInvariant();
            var attributes = ReadAttributes(s, ref p, out var selfClosing, out var terminated);
            if (!terminated)
            {
                i = n; // 沒有結尾的標籤：丟掉其餘內容，不當文字輸出
                continue;
            }
            i = p;

            if (isEnd)
            {
                CloseTag(output, open, name);
                continue;
            }
            if (DropWithContent.Contains(name))
            {
                if (!selfClosing) i = SkipPast(s, i, name);
                continue;
            }
            if (!Allowed.Contains(name)) continue;

            output.Append('<').Append(name);
            AppendAttributes(output, name, attributes);
            output.Append('>');
            if (!Void.Contains(name)) open.Add(name);
        }
        for (var k = open.Count - 1; k >= 0; k--) output.Append("</").Append(open[k]).Append('>');
        return output.ToString();
    }

    /// <summary>NUL 與其他控制字元（Tab、換行、回車除外）在瀏覽器裡可能被忽略，先全部拿掉，避免拿它們拆字繞過。</summary>
    private static string StripControlChars(string html)
    {
        var sb = new StringBuilder(html.Length);
        foreach (var ch in html)
            if (ch >= ' ' && ch != '\u007f' || ch is '\t' or '\n' or '\r') sb.Append(ch);
        return sb.ToString();
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static void AppendTextChar(StringBuilder output, string s, ref int i)
    {
        switch (s[i])
        {
            case '>': output.Append("&gt;"); break;
            case '&':
                var m = Entity().Match(s, i, Math.Min(40, s.Length - i));
                if (m.Success) { output.Append(m.Value); i += m.Length; return; }
                output.Append("&amp;");
                break;
            default: output.Append(s[i]); break;
        }
        i++;
    }

    private static void CloseTag(StringBuilder output, List<string> open, string name)
    {
        var at = open.LastIndexOf(name);
        if (at < 0) return;
        for (var k = open.Count - 1; k >= at; k--) output.Append("</").Append(open[k]).Append('>');
        open.RemoveRange(at, open.Count - at);
    }

    /// <summary>略過 raw-text 元素的內容與結尾標籤；找不到結尾就略過到輸入結束。</summary>
    private static int SkipPast(string s, int from, string name)
    {
        var close = s.IndexOf("</" + name, from, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return s.Length;
        var gt = s.IndexOf('>', close);
        return gt < 0 ? s.Length : gt + 1;
    }

    private static List<(string Name, string Value)> ReadAttributes(string s, ref int p, out bool selfClosing, out bool terminated)
    {
        var attrs = new List<(string, string)>();
        var n = s.Length;
        selfClosing = false;
        while (p < n)
        {
            while (p < n && (char.IsWhiteSpace(s[p]) || s[p] == '/'))
            {
                if (s[p] == '/') selfClosing = true;
                p++;
            }
            if (p >= n) break;
            if (s[p] == '>') { p++; terminated = true; return attrs; }
            selfClosing = false;
            var start = p;
            while (p < n && !char.IsWhiteSpace(s[p]) && s[p] is not ('=' or '>' or '/')) p++;
            var name = s[start..p].ToLowerInvariant();
            while (p < n && char.IsWhiteSpace(s[p])) p++;
            var value = "";
            if (p < n && s[p] == '=')
            {
                p++;
                while (p < n && char.IsWhiteSpace(s[p])) p++;
                if (p < n && s[p] is '"' or '\'')
                {
                    var quote = s[p];
                    var close = s.IndexOf(quote, p + 1);
                    if (close < 0) { p = n; break; }
                    value = s[(p + 1)..close];
                    p = close + 1;
                }
                else
                {
                    var vs = p;
                    while (p < n && !char.IsWhiteSpace(s[p]) && s[p] != '>') p++;
                    value = s[vs..p];
                }
            }
            if (name.Length > 0) attrs.Add((name, value));
        }
        terminated = false;
        return attrs;
    }

    private static void AppendAttributes(StringBuilder output, string tag, List<(string Name, string Value)> attributes)
    {
        var seen = new HashSet<string>();
        var hasHref = false;
        foreach (var (name, raw) in attributes)
        {
            if (!seen.Add(name)) continue;
            var value = WebUtility.HtmlDecode(raw);
            string? safe = name switch
            {
                "class" => ClassValue().IsMatch(value) ? value : null,
                "style" => SafeStyle(value),
                "title" when tag == "a" => value.Length <= 200 ? value : null,
                "href" when tag == "a" => SafeHref(value),
                "colspan" or "rowspan" when tag is "td" or "th" => SmallNumber().IsMatch(value) ? value : null,
                "span" when tag is "col" or "colgroup" => SmallNumber().IsMatch(value) ? value : null,
                "align" when tag is "td" or "th" or "table" or "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" =>
                    value.ToLowerInvariant() is "left" or "center" or "right" or "justify" ? value.ToLowerInvariant() : null,
                "valign" when tag is "td" or "th" => value.ToLowerInvariant() is "top" or "middle" or "bottom" ? value.ToLowerInvariant() : null,
                "width" when tag is "td" or "th" or "table" or "col" => Length().IsMatch(value) ? value : null,
                "border" or "cellpadding" or "cellspacing" when tag == "table" => SmallNumber().IsMatch(value) ? value : null,
                _ => null,
            };
            if (safe is null) continue;
            if (name == "href") hasHref = true;
            output.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(safe)).Append('"');
        }
        if (hasHref) output.Append(" rel=\"noopener noreferrer\"");
    }

    private static string? SafeHref(string value)
    {
        // 實體解碼後再拿掉空白與控制字元，才判斷 scheme：擋 "java\tscript:"、"jav&#x61;script:" 這類拆字
        var probe = new string(value.Where(ch => !char.IsWhiteSpace(ch) && !char.IsControl(ch)).ToArray()).ToLowerInvariant();
        return probe.StartsWith("http://", StringComparison.Ordinal) || probe.StartsWith("https://", StringComparison.Ordinal)
            || probe.StartsWith("mailto:", StringComparison.Ordinal)
            ? value.Trim() : null;
    }

    private static string? SafeStyle(string value)
    {
        var kept = new List<string>();
        foreach (var declaration in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            var property = declaration[..colon].Trim().ToLowerInvariant();
            var v = declaration[(colon + 1)..].Trim();
            if (StyleProperties.Contains(property) && StyleValue().IsMatch(v)) kept.Add($"{property}:{v}");
        }
        return kept.Count == 0 ? null : string.Join(';', kept);
    }
}
