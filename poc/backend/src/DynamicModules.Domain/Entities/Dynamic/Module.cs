using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class Module : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string ProgramCode { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public string? Icon { get; set; }
    public string? Section { get; set; }
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

    /// <summary>metadata 異動時呼叫：版本加一並更新修改稽核。</summary>
    public void TouchVersion(string userId, DateTime now)
    {
        Version++;
        StampModified(userId, now);
    }
}
