using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Rules;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>依快照比對流程（設計 §5.2、§5.5）。</summary>
public static class ApprovalRouter
{
    /// <summary>依 Priority 升冪、只取 Active，用 entry.Definition.When 對 values 求值，取第一個成立的；都不成立回 null。</summary>
    public static FlowSnapshotEntry? Match(IReadOnlyList<FlowSnapshotEntry> snapshot, Module module, IReadOnlyDictionary<string, JsonElement?> values,
        FieldTypeRegistry registry, DateOnly today, IReadOnlySet<string> userIds) =>
        snapshot.Where(e => e.Active).OrderBy(e => e.Priority).ThenBy(e => e.FlowId)
            .FirstOrDefault(e => Holds(e.Definition.When, module, values, registry, today, userIds));

    /// <summary>
    /// 條件成立嗎？null 一律成立。條件解析不了（引用的欄位已不在）時視為成立：寧可多簽，也不能掉到 direct 或少一關（設計 §1.4、§5.2）。
    /// </summary>
    public static bool Holds(JsonElement? when, Module module, IReadOnlyDictionary<string, JsonElement?> values,
        FieldTypeRegistry registry, DateOnly today, IReadOnlySet<string> userIds)
    {
        if (when is not { ValueKind: JsonValueKind.Object } w) return true;
        var parsed = ConditionParser.Parse(w, module, registry, ConditionUsage.Rule, userIds);
        return parsed.Node is null || ConditionEvaluator.Evaluate(parsed.Node, values, registry, today);
    }
}
