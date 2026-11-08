using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;

namespace DynamicModules.Domain.Conditions;

public abstract record ConditionNode;

/// <summary>All 為 true 是 all（且），false 是 any（或）。</summary>
public sealed record ConditionGroup(bool All, IReadOnlyList<ConditionNode> Children) : ConditionNode;

/// <summary>
/// Values 是比較值經處理器 Normalize、ToIndex 後的索引值：between 兩個、in／notIn N 個、
/// isEmpty／isNotEmpty、beforeToday 與 withinXxxDays 為空清單；contains／startsWith 是只填 TextValue 的一個值。
/// withinNextDays／withinLastDays 的天數放在 Days。規則條件的 value 可以是 fieldRef：此時 RefField 是被比較的欄位，Values 為空清單。
/// </summary>
public sealed record FieldCondition(
    Field Field, FilterOperator Op, IReadOnlyList<IndexValue> Values, int? Days, Field? RefField = null) : ConditionNode;
