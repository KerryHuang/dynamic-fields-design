namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>語系的來源：內建（隨程式出貨，啟動時同步）或匯入。</summary>
public enum LocaleOrigin { Builtin, Installed }

/// <summary>
/// 已安裝的語系（表 <c>Dynamic_Locale</c>，語系包設計 §4.1）：只存「裝了什麼、長什麼樣」，不存開不開——啟用、預設、必填仍是三個語系政策。
/// 不存的欄位（T19，沒有行為或入口的欄位不放）：<c>Direction</c>（本版只收 ltr）、<c>IntlLocale</c>（代碼本身就是 BCP 47）、<c>SortOrder</c>
/// （順序固定為內建在前、匯入依安裝時間）。<see cref="ModifiedUtc"/> 不可空（新增時等於建立時間），是語系快照版本戳記的一部分。
/// </summary>
public class InstalledLocale
{
    public string Code { get; set; } = "";
    /// <summary>自稱（「日本語」），不隨目前語系翻譯。</summary>
    public string NativeName { get; set; } = "";
    /// <summary>Quasar 語言包；空＝用 en-US 的。</summary>
    public string? QuasarLang { get; set; }
    /// <summary>上層語系（缺字時先退到這裡）。完整性由安裝驗證與移除守衛負責，不設外鍵。</summary>
    public string? FallbackCode { get; set; }
    public LocaleOrigin Origin { get; set; }
    public string? CreatedUserId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? ModifiedUserId { get; set; }
    public DateTime ModifiedUtc { get; set; }
}
