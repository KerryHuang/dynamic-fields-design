using System.Text.Json;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>跨記錄驗證：同一模組內依組鍵加總或計數，結果必須滿足斷言（設計 §5）。</summary>
public class ModuleCheck : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public long ModuleId { get; set; }
    public bool Active { get; set; } = true;
    public CheckDefinition Definition { get; set; } = new();
}

/// <summary>Function：sum 或 count。Where 是列表篩選條件 JSON。</summary>
public record CheckDefinition(
    List<long> GroupBy, string Function, long? FieldId, JsonElement? Where, CheckAssert Assert, I18nText Message)
{
    public CheckDefinition() : this([], "sum", null, null, new CheckAssert("gte", new CheckLimit(0, null, null)), new I18nText()) { }
}

/// <summary>Op：gte、gt、lte、lt、eq。</summary>
public record CheckAssert(string Op, CheckLimit Limit);

/// <summary>三擇一：Const；Field（被寫入記錄自己的數字欄位）；Via＋Field（經參照欄位指向的記錄的數字欄位）。</summary>
public record CheckLimit(decimal? Const, long? Field, long? Via);
