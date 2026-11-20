namespace DynamicModules.Domain.Localization;

/// <summary>
/// 介面文字的語法白名單（語系包設計 §13.2；i4d1 計畫判斷 3）：只接受一般文字、<c>{名稱}</c>（名稱 <c>[A-Za-z_][A-Za-z0-9_]*</c>）、
/// <c>{'字面'}</c>（可含大括號，例如內建的 <c>{'{{欄位}}'}</c>）。字面量照 vue-i18n message compiler 的規則：跳脫只認 <c>\\</c>、<c>\'</c>、
/// <c>\uXXXX</c>、<c>\UXXXXXX</c>，其他跳脫、位數不足、超過 U+10FFFF、字面量內換行（含 CR、U+2028、U+2029）是語法錯誤；Unicode 跳脫解碼後
/// 仍過 <see cref="LocaleTextSafety.IsForbidden"/>，解出控制字元回控制字元錯誤（審查必修 2）。裸 <c>@</c>、<c>|</c>、<c>%{</c>、未配對或巢狀的大括號是語法錯誤；
/// 名稱不在允許集合裡是佔位符錯誤（少用不擋，Q7）。前端 <c>message-check.ts</c> 跑同一份共用案例（<c>shared/i18n-message-cases.json</c>）。
/// </summary>
public static class UiMessageSyntax
{
    private const int Invalid = -1, ControlChar = -2;

    /// <summary>回拒收原因代碼（<see cref="LocalePackReasons.Syntax"/>、<see cref="LocalePackReasons.Control"/>、<see cref="LocalePackReasons.Placeholder"/>），通過回 null。</summary>
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
                    if (end == ControlChar) return LocalePackReasons.Control;
                    if (end < 0) return LocalePackReasons.Syntax;
                    i = end;
                    continue;
                }
            }
            i++;
        }
        return names.All(allowedNames.Contains) ? null : LocalePackReasons.Placeholder;
    }

    /// <summary>從 <paramref name="start"/> 的 <c>{</c> 讀一段插值或字面，回下一個位置；不合法回 <see cref="Invalid"/>，字面量解出控制字元回 <see cref="ControlChar"/>。</summary>
    private static int ReadBrace(string text, int start, List<string> names)
    {
        var i = start + 1;
        if (i < text.Length && text[i] == '\'') return ReadLiteral(text, i + 1);
        if (i >= text.Length || !IsNameStart(text[i])) return Invalid;
        var nameStart = i;
        while (i < text.Length && IsNamePart(text[i])) i++;
        if (i >= text.Length || text[i] != '}') return Invalid;
        names.Add(text[nameStart..i]);
        return i + 1;
    }

    /// <summary>從開頭單引號之後讀到 <c>'}</c>，回下一個位置。</summary>
    private static int ReadLiteral(string text, int i)
    {
        var control = false;
        while (i < text.Length && text[i] != '\'')
        {
            switch (text[i])
            {
                case '\n' or '\r' or '\u2028' or '\u2029':
                    return Invalid;
                case '\\':
                {
                    var next = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (next is '\\' or '\'') { i += 2; continue; }
                    var digits = next switch { 'u' => 4, 'U' => 6, _ => 0 };
                    if (digits == 0 || i + 2 + digits > text.Length) return Invalid;
                    var hex = text.AsSpan(i + 2, digits);
                    foreach (var h in hex) if (!char.IsAsciiHexDigit(h)) return Invalid;
                    var codePoint = int.Parse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
                    if (codePoint > 0x10FFFF) return Invalid;   // 前端 String.fromCodePoint 會丟例外；代理碼位前端換成 U+FFFD，不擋
                    control |= LocaleTextSafety.IsForbidden(codePoint);
                    i += 2 + digits;
                    continue;
                }
            }
            i++;
        }
        if (i + 1 >= text.Length || text[i + 1] != '}') return Invalid;
        return control ? ControlChar : i + 2;
    }

    private static bool IsNameStart(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    private static bool IsNamePart(char c) => IsNameStart(c) || c is >= '0' and <= '9';
}
