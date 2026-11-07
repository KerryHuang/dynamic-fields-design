using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 列印範本：宣告在模組上，一個模組可有多份。Body 是淨化過的 HTML 文字，內含 <c>{{欄位鍵}}</c> 佔位符與
/// <c>{{#each 明細模組鍵}}…{{/each}}</c> 迴圈，由設計器以純文字編輯；列印時由後端依使用者的有效欄位權限算值、跳脫後填入。
/// </summary>
public class PrintTemplate : AuditableEntity
{
    public long Id { get; set; }
    public long ModuleId { get; set; }
    /// <summary>模組內唯一，建立後不改。</summary>
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public string Body { get; set; } = "";
    public bool Active { get; set; } = true;
}
