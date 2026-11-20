using System.Globalization;

namespace DynamicModules.Domain.Localization;

/// <summary>
/// 後端訊息範本的語法白名單（語系包設計 §9.3；i4d1 計畫判斷 3）：只收 <c>{n}</c>（n 是非負整數）與跳脫 <c>{{</c>、<c>}}</c>；
/// 對齊與格式子句（<c>{0,5}</c>、<c>{0:D3}</c>）拒收；參數編號集合要和基準範本（內建 zh-TW）相同；最後以假參數實跑 <see cref="string.Format(IFormatProvider, string, object[])"/>。
/// </summary>
public static class ServerTemplateSyntax
{
    /// <summary>回拒收原因代碼（<see cref="LocalePackReasons.Syntax"/>、<see cref="LocalePackReasons.FormatClause"/>、<see cref="LocalePackReasons.Placeholder"/>），通過回 null。</summary>
    public static string? Check(string text, string baseTemplate)
    {
        var error = Scan(text, out var args);
        if (error is not null) return error;
        Scan(baseTemplate, out var baseArgs);
        if (!args.SetEquals(baseArgs)) return LocalePackReasons.Placeholder;
        try
        {
            var fake = new object[args.Count == 0 ? 0 : args.Max() + 1];
            Array.Fill(fake, "x");
            _ = string.Format(CultureInfo.InvariantCulture, text, fake);
            return null;
        }
        catch (FormatException) { return LocalePackReasons.Syntax; }
    }

    private static string? Scan(string text, out HashSet<int> args)
    {
        args = [];
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{') { i += 2; continue; }
                var j = i + 1;
                while (j < text.Length && char.IsAsciiDigit(text[j])) j++;
                if (j == i + 1 || j >= text.Length) return LocalePackReasons.Syntax;
                if (text[j] is ',' or ':') return LocalePackReasons.FormatClause;
                if (text[j] != '}') return LocalePackReasons.Syntax;
                if (!int.TryParse(text.AsSpan(i + 1, j - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 99)
                    return LocalePackReasons.Syntax;
                args.Add(n);
                i = j + 1;
                continue;
            }
            if (c == '}')
            {
                if (i + 1 < text.Length && text[i + 1] == '}') { i += 2; continue; }
                return LocalePackReasons.Syntax;
            }
            i++;
        }
        return null;
    }
}
