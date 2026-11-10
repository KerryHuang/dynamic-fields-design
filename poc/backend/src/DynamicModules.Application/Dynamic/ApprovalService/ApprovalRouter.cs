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
    /// <summary>
    /// 依 Priority 升冪、只取 Active，用 entry.Definition.When 對 values 求值，取第一個成立的；都不成立回 (null, null)。
    /// 輪到的流程條件解析不了（引用的欄位或選項已不在）→ 回那個流程當 Undecidable：不能當成立（可能選到關卡較少的流程），
    /// 也不能當不成立（可能落到 direct），由呼叫端回 409（fail closed，主控裁決）。
    /// </summary>
    public static (FlowSnapshotEntry? Flow, FlowSnapshotEntry? Undecidable) Match(IReadOnlyList<FlowSnapshotEntry> snapshot, Module module,
        IReadOnlyDictionary<string, JsonElement?> values, FieldTypeRegistry registry, DateOnly today, IReadOnlySet<string> userIds)
    {
        foreach (var e in snapshot.Where(e => e.Active).OrderBy(e => e.Priority).ThenBy(e => e.FlowId))
        {
            if (e.Definition.When is not { ValueKind: JsonValueKind.Object } w) return (e, null);
            var parsed = ConditionParser.Parse(w, module, registry, ConditionUsage.Rule, userIds);
            if (parsed.Error is not null) return (null, e);
            // Node 為 null 且無 Error＝空群組（沒有條件），視為成立
            if (parsed.Node is null || ConditionEvaluator.Evaluate(parsed.Node, values, registry, today)) return (e, null);
        }
        return (null, null);
    }

    /// <summary>
    /// 關卡條件成立嗎？null 一律成立。條件解析不了（引用的欄位已不在）時視為成立：關卡不跳過，寧可多簽一關（設計 §1.4）。
    /// </summary>
    public static bool Holds(JsonElement? when, Module module, IReadOnlyDictionary<string, JsonElement?> values,
        FieldTypeRegistry registry, DateOnly today, IReadOnlySet<string> userIds)
    {
        if (when is not { ValueKind: JsonValueKind.Object } w) return true;
        var parsed = ConditionParser.Parse(w, module, registry, ConditionUsage.Rule, userIds);
        return parsed.Node is null || ConditionEvaluator.Evaluate(parsed.Node, values, registry, today);
    }
}
