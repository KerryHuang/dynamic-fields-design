using System.Text.RegularExpressions;

namespace DynamicModules.Domain.Localization;

/// <summary>
/// 語系代碼格式守衛（語系包設計 §17.4 M1）：只認正規形式「語言[-文字][-地區]」，例 <c>ja</c>、<c>zh-TW</c>、<c>zh-Hant-HK</c>、<c>es-419</c>。
/// 語言 2–3 個小寫字母、文字 4 個字母首字大寫、地區 2 個大寫字母或 3 位數字。這是技術限制（可寫死），不判斷「有沒有安裝」。
/// </summary>
public static partial class LocaleCode
{
    public const int MaxLength = 35;

    // 結尾用 \z 不用 $：.NET 的 $ 會讓「ja\n」通過（存得進去卻用 ja 取不到）
    [GeneratedRegex("^[a-z]{2,3}(-[A-Z][a-z]{3})?(-([A-Z]{2}|[0-9]{3}))?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Canonical();

    public static bool IsValid(string? code) => code is { Length: > 0 and <= MaxLength } && Canonical().IsMatch(code);

    /// <summary>把大小寫不對的代碼轉成正規形式（<c>JA</c> → <c>ja</c>、<c>zh-hant-hk</c> → <c>zh-Hant-HK</c>）；分隔符不是 <c>-</c> 或結構不對回 false。</summary>
    public static bool TryNormalize(string? raw, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var parts = raw.Trim().Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            parts[i] = i == 0 ? p.ToLowerInvariant()
                : p.Length == 4 ? char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()
                : p.ToUpperInvariant();
        }
        var normalized = string.Join('-', parts);
        if (!IsValid(normalized)) return false;
        code = normalized;
        return true;
    }
}
