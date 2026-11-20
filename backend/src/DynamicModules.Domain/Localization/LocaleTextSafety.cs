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
        {
            if (c is '\n' or '\t') continue;
            if (char.IsControl(c) || c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩') return LocalePackReasons.Control;
        }
        return text.Length > maxLength ? LocalePackReasons.TooLong : null;
    }
}
