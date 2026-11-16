using System.Text.Json;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 連動事件定義：來源模組的記錄寫入時（新增、修改、狀態轉移），在同一個交易內新增或更新其他模組的記錄。
/// 不含任何業務名詞；情境完全由定義決定。設計見 docs/superpowers/specs/2026-11-08-events-ledger-design.md §4。
/// </summary>
public class ModuleEvent : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public long SourceModuleId { get; set; }
    public bool Active { get; set; } = true;
    public EventDefinition Definition { get; set; } = new();
}

/// <summary>When 是規則條件 JSON（ConditionParser／ConditionEvaluator），對寫入後的來源值求值；null 代表無條件。</summary>
public record EventDefinition(EventTrigger Trigger, JsonElement? When, List<EventEffect> Effects)
{
    public EventDefinition() : this(new EventTrigger("create", null, null, null), null, []) { }
}

/// <summary>On：create、update、transition。FromStates 空代表任一狀態；Watch 空代表任一欄位。</summary>
public record EventTrigger(string On, List<long>? FromStates, long? ToState, List<long>? Watch);

/// <summary>
/// Kind：create、update、reverse、custom。Each：來源的明細模組代號；Via：來源上指向目標的參照欄位（update）；
/// OfEvent／OfEffect：reverse 要沖銷哪個事件效果新增的分錄；Handler／Args：custom 的擴充點。
/// </summary>
public record EventEffect(
    string Code, string Kind, string? TargetModuleKey, string? Each, long? Via, List<EventSet>? Set,
    string? OfEvent, string? OfEffect, string? Handler, JsonElement? Args);

/// <summary>值來源三擇一：From（複製）、Formula（公式）、Const（常數）。AllowHiddenSource：明確允許複製被隱藏的來源欄位。</summary>
public record EventSet(long To, EventFrom? From, EventFormula? Formula, JsonElement? Const, bool AllowHiddenSource);

/// <summary>Side：header 或 line（line 只能用在有 Each 的 create）。</summary>
public record EventFrom(string Side, long FieldId);

public record EventFormula(string Side, string Expr);

/// <summary>事件效果新增或沖銷的事實；唯一索引保證同一事件對同一來源最多生效一次。</summary>
public class EventFact
{
    public long Id { get; set; }
    public string EventKey { get; set; } = "";
    public string EffectCode { get; set; } = "";
    public long SourceRecordId { get; set; }
    public long ItemKey { get; set; }
    public long TargetRecordId { get; set; }
    public string OperationId { get; set; } = "";
    public DateTime CreatedTime { get; set; }
}
