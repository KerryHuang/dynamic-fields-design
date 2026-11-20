namespace DynamicModules.Domain.Localization;

/// <summary>
/// 匯入文字的共通安全檢查（語系包設計 §13.2）：拒收 Unicode 雙向控制字元（U+202A–202E、U+2066–2069）與其他控制字元（換行、tab 除外），
/// 以及超過長度上限的句子。介面文字、後端範本、語系自稱都過它。
/// </summary>
public static class LocaleTextSafety
{
    /// <summary>回拒收原因代碼（<see cref="LocalePackReasons.Control"/>、<see cref="LocalePackReasons.TooLong"/>），通過回 null。</summary>
    public static string? Check(string text, int maxLength)
    {
        foreach (var c in text)
            if (IsForbidden(c)) return LocalePackReasons.Control;
        return text.Length > maxLength ? LocalePackReasons.TooLong : null;
    }

    /// <summary>碼位是否被禁止（換行、tab 除外的控制字元與雙向控制字元）；<see cref="UiMessageSyntax"/> 解完字面量的 Unicode 跳脫後也用它（審查必修 2）。</summary>
    public static bool IsForbidden(int codePoint) =>
        codePoint is not ('\n' or '\t')
        && (codePoint is < 0x20 or (>= 0x7F and <= 0x9F) or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069));
}
