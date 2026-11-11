using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Rules;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>重新比對的結果：Changed＝路線不同；NewFlow 為 null 代表快照裡沒有流程符合（改走備援）；NeedsPick＝新流程含發起人自選；
/// Undecidable 非 null＝輪到的流程條件解析不了，呼叫端回 409（主控裁決 26）。</summary>
public sealed record RouteCheck(bool Changed, FlowSnapshotEntry? NewFlow, bool NeedsPick, FlowSnapshotEntry? Undecidable = null);

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

    /// <summary>
    /// 每次同意的重新比對（設計 §5.5 第 2、3 點）：只看實例的流程快照。
    /// 一般實例：比到的流程或版本不同 → Changed；相同時，目前關卡之前每個 SkipReason = condition 的關卡用目前值重算 when，有任何一個變成立 → Changed。
    /// 備援實例（RouteKind = fallback）：快照裡有流程符合就 Changed（改走那個流程），沒有就照常核准。
    /// </summary>
    public static RouteCheck Recheck(ApprovalInstance inst, IReadOnlyList<ApprovalStageRun> runs, Module module, IReadOnlyDictionary<string, JsonElement?> values,
        FieldTypeRegistry registry, DateOnly today, IReadOnlySet<string> userIds)
    {
        var (match, undecidable) = Match(inst.FlowSetSnapshot, module, values, registry, today, userIds);
        if (undecidable is not null) return new(false, null, false, undecidable);
        static bool Picks(FlowSnapshotEntry? f) => f?.Definition.Stages.Any(s => s.Approvers.Source == ApprovalSources.InitiatorPick) == true;
        if (inst.RouteKind == ApprovalRouteKind.Fallback)
            return match is null ? new(false, null, false) : new(true, match, Picks(match));
        if (match is null) return new(true, null, false);
        if (match.FlowId != inst.FlowId || match.FlowVersionId != inst.FlowVersionId) return new(true, match, Picks(match));

        var current = runs.FirstOrDefault(r => r.Id == inst.CurrentStageRunId)?.Seq ?? int.MaxValue;
        var stages = match.Definition.Stages;
        var reopened = runs.Where(r => r.Seq < current && r.Status == ApprovalStageStatus.Skipped && r.SkipReason == ApprovalSkipReasons.Condition)
            .Any(r => stages.FirstOrDefault(s => s.Code == r.StageCode) is { } s && Holds(s.When, module, values, registry, today, userIds));
        return reopened ? new(true, match, Picks(match)) : new(false, match, false);
    }
}
