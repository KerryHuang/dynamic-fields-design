using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class Module : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string ProgramCode { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public string? Icon { get; set; }
    /// <summary>選單分區的分組鍵（繁中文字）；不同應用程式同名的分區在選單合成一組。寫入走 <see cref="SetSection"/>。</summary>
    public string? Section { get; set; }
    /// <summary>選單分區的多語系文字；null 的舊資料視為只有繁中（見 <see cref="SectionText"/>）。</summary>
    public I18nText? SectionI18n { get; set; }
    public long? TitleFieldId { get; set; }
    /// <summary>標題樣板（{} 內是欄位 Id，例如 {12} {13}）；null 時標題就是 TitleFieldId 那個欄位的值。見 <see cref="Modules.TitleTemplate"/>。</summary>
    public string? TitleTemplate { get; set; }
    public ModuleStatus Status { get; set; }
    public ModuleKind Kind { get; set; } = ModuleKind.Standalone;
    /// <summary>擴充模組所擴充的既有實體鍵；standalone 為 null。</summary>
    public string? HostEntityKey { get; set; }
    public int Version { get; set; }
    public List<Block> Blocks { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];
    /// <summary>狀態機；沒有就是 null。includeChildren 時載入。</summary>
    public Workflow? Workflow { get; set; }

    /// <summary>
    /// 決定記錄標題的欄位：標題欄位加上標題樣板引用的欄位。這些欄位不能停用、刪除或設為隱藏，
    /// 而且使用者要全部看得到才看得到標題（fail-closed）。
    /// </summary>
    public IReadOnlyList<long> TitleFieldIds() =>
        (TitleFieldId is { } id ? new[] { id } : []).Concat(Modules.TitleTemplate.FieldIds(TitleTemplate)).Distinct().ToList();

    /// <summary>選單分區的多語系文字；沒有分區是 null，SectionI18n 是 null 的舊資料視為 {"zh-TW": Section}。</summary>
    public I18nText? SectionText() =>
        string.IsNullOrEmpty(Section) ? null
        : SectionI18n ?? new I18nText([KeyValuePair.Create(I18nText.FallbackLocale, Section)]);

    /// <summary>設定選單分區：Section 取繁中、SectionI18n 存整個物件；繁中空白視同不分區，兩者都清成 null。</summary>
    public void SetSection(I18nText? text)
    {
        var zh = text?.Values.GetValueOrDefault(I18nText.FallbackLocale);
        if (string.IsNullOrWhiteSpace(zh))
        {
            Section = null;
            SectionI18n = null;
            return;
        }
        Section = zh;
        // 每次給新的實例，EF 才偵測得到 JSON 欄位變更
        SectionI18n = new I18nText(text!.Values);
    }

    /// <summary>metadata 異動時呼叫：版本加一並更新修改稽核。</summary>
    public void TouchVersion(string userId, DateTime now)
    {
        Version++;
        StampModified(userId, now);
    }
}
