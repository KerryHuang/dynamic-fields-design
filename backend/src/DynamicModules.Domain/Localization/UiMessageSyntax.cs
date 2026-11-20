namespace DynamicModules.Domain.Localization;

/// <summary>
/// 介面文字的語法白名單（語系包設計 §13.2；i4d1 計畫判斷 3）：只接受一般文字、<c>{名稱}</c>（名稱 <c>[A-Za-z_][A-Za-z0-9_]*</c>）、
/// <c>{'字面'}</c>（字面內不含 <c>'</c>，可含大括號，例如內建的 <c>{'{{欄位}}'}</c>）。裸 <c>@</c>、<c>|</c>、<c>%{</c>、未配對或巢狀的大括號是語法錯誤；
/// 名稱不在允許集合裡是佔位符錯誤（少用不擋，Q7）。前端 <c>message-check.ts</c> 跑同一份共用案例（<c>shared/i18n-message-cases.json</c>）。
/// </summary>
public static class UiMessageSyntax
{
    /// <summary>回拒收原因代碼（<see cref="LocalePackReasons.Syntax"/>、<see cref="LocalePackReasons.Placeholder"/>），通過回 null。</summary>
    public static string? Check(string text, IReadOnlySet<string> allowedNames)
    {
        var names = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '@' or '|' or '}':
                    return LocalePackReasons.Syntax;
                case '%' when i + 1 < text.Length && text[i + 1] == '{':
                    return LocalePackReasons.Syntax;
                case '{':
                {
                    var end = ReadBrace(text, i, names);
                    if (end < 0) return LocalePackReasons.Syntax;
                    i = end;
                    continue;
                }
            }
            i++;
        }
        return names.All(allowedNames.Contains) ? null : LocalePackReasons.Placeholder;
    }

    /// <summary>從 <paramref name="start"/> 的 <c>{</c> 讀一段插值或字面，回下一個位置；不合法回 -1。</summary>
    private static int ReadBrace(string text, int start, List<string> names)
    {
        var i = start + 1;
        if (i < text.Length && text[i] == '\'')
        {
            var close = text.IndexOf('\'', i + 1);
            return close >= 0 && close + 1 < text.Length && text[close + 1] == '}' ? close + 2 : -1;
        }
        if (i >= text.Length || !IsNameStart(text[i])) return -1;
        var nameStart = i;
        while (i < text.Length && IsNamePart(text[i])) i++;
        if (i >= text.Length || text[i] != '}') return -1;
        names.Add(text[nameStart..i]);
        return i + 1;
    }

    private static bool IsNameStart(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    private static bool IsNamePart(char c) => IsNameStart(c) || c is >= '0' and <= '9';
}
