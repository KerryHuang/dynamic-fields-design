using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>清單查詢：Filter 為 null 表示不篩選；Keyword 對 Title 做 contains；Today 供 withinXxxDays 使用；OwnerUserIds 為 null 表示不限擁有者，空集合表示什麼都看不到。</summary>
public sealed record RecordQuery(
    long ModuleId, ConditionNode? Filter, IReadOnlyList<RecordSort> Sorts, string? Keyword,
    DateOnly Today, int Page, int Size, IReadOnlySet<string>? OwnerUserIds = null);

public sealed record RecordSort(Field Field, bool Descending);
