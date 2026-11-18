using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>清單查詢：Filter 為 null 表示不篩選；Keyword 對 Title 做 contains；Today 供 withinXxxDays 使用；OwnerUserIds 為 null 表示不限擁有者，空集合表示什麼都看不到；
/// Approval 為 null 表示不看簽核（簽核設計 §13.4.1）。</summary>
public sealed record RecordQuery(
    long ModuleId, ConditionNode? Filter, IReadOnlyList<RecordSort> Sorts, string? Keyword,
    DateOnly Today, int Page, int Size, IReadOnlySet<string>? OwnerUserIds = null, ApprovalListFilter? Approval = null);

/// <summary>清單的簽核系統篩選：Pending＝有進行中的簽核實例，None＝沒有（簽核設計 §13.4.1）。以實例表子查詢現算，不是記錄上的欄位。</summary>
public enum ApprovalListFilter { Pending, None }

/// <summary>ByTitle：依持久化的 Record.Title 排序（模組有標題樣板時，標題欄位代表組好的標題）。</summary>
public sealed record RecordSort(Field Field, bool Descending, bool ByTitle = false);

/// <summary>組鍵的一個欄位；Value 為 null 代表「這個欄位沒有值」。</summary>
public sealed record AggregateGroupKey(Field Field, IndexValue? Value);

/// <summary>SumField 為 null 時只數筆數。Where 與列表篩選共用同一套條件轉 SQL。</summary>
public sealed record AggregateQuery(
    long ModuleId, IReadOnlyList<AggregateGroupKey> Group, ConditionNode? Where, Field? SumField, DateOnly Today);

/// <summary>
/// Count：組內筆數；ScaledSum：SumField 的 ScaledValue 加總（沒有任何列時為 0）；
/// MissingScaled：有索引列卻沒有 ScaledValue 的筆數（大於 0 代表索引不完整，呼叫端要 fail-closed）。
/// </summary>
public sealed record AggregateResult(long Count, long ScaledSum, long MissingScaled);
