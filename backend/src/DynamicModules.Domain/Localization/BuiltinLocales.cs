namespace DynamicModules.Domain.Localization;

/// <summary>內建語系：代碼、自稱（不隨目前語系翻譯）、Quasar 語言包名稱。</summary>
public sealed record BuiltinLocale(string Code, string NativeName, string QuasarLang);

/// <summary>
/// 內建語系（語系包設計 §4.1 的程式內嵌 manifest）：隨程式出貨、一定已安裝，啟動時同步成 <c>Dynamic_Locale</c> 的 builtin 列。
/// 與 <c>shared/i18n/&lt;code&gt;/locale.json</c> 一致，由 <c>BuiltinLocalesFixtureTests</c> 與啟動自檢把關；不讀資源（i4b 計畫判斷 3）。
/// </summary>
public static class BuiltinLocales
{
    public static IReadOnlyList<BuiltinLocale> All { get; } =
    [
        new("zh-TW", "繁體中文", "zh-TW"),
        new("en-US", "English", "en-US"),
        new("th", "ไทย", "th"),
        new("vi", "Tiếng Việt", "vi"),
    ];

    public static IReadOnlyList<string> Codes { get; } = All.Select(l => l.Code).ToArray();

    public static bool Contains(string code) => Codes.Contains(code);
}
